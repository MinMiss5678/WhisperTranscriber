import argparse
import asyncio
import os
import sys
import time

sys.stdout.reconfigure(encoding='utf-8')

from faster_whisper import WhisperModel
from sentence_transformers import SentenceTransformer
from sklearn.metrics.pairwise import cosine_similarity


def parse_args():
    parser = argparse.ArgumentParser(description='Whisper 語音轉字幕')
    parser.add_argument('--model', default='large-v3',
                        choices=['tiny', 'base', 'small', 'medium',
                                 'large-v1', 'large-v2', 'large-v3', 'distil-large-v3'])
    parser.add_argument('--file', required=True, nargs='+')
    parser.add_argument('--output', default=None)
    parser.add_argument('--language', default=None)
    parser.add_argument('--device', default='cuda', choices=['cuda', 'cpu'])
    parser.add_argument('--compute-type', default='float16', dest='compute_type')
    parser.add_argument('--beam-size', default=5, type=int, dest='beam_size')
    parser.add_argument('--initial-prompt', default='', dest='initial_prompt')
    parser.add_argument('--merge', action='store_true')
    parser.add_argument('--vad-filter', action='store_true', dest='vad_filter')
    parser.add_argument('--translate', action='store_true')
    parser.add_argument('--translate-lang', default='zh-TW', dest='translate_lang')
    parser.add_argument('--translate-backend', default='googletrans',
                        choices=['googletrans', 'claude-cli', 'claude-haiku', 'claude-sonnet',
                                 'gemini-flash'],
                        dest='translate_backend')
    parser.add_argument('--claude-api-key', default='', dest='claude_api_key')
    parser.add_argument('--gemini-api-key', default='', dest='gemini_api_key')
    parser.add_argument('--translate-prompt', default='日文 ASMR 字幕，保持自然、輕柔、親密的口語語氣，符合 ASMR 風格', dest='translate_prompt')
    parser.add_argument('--channel', default='mix',
                        choices=['mix', 'left', 'right', 'split'],
                        dest='channel')
    parser.add_argument('--segments-out', default=None, dest='segments_out',
                        help='Save segments JSON after transcription (pipeline stage 1)')
    parser.add_argument('--segments-in', default=None, dest='segments_in',
                        help='Load segments JSON and translate only, skip transcription (pipeline stage 2)')
    return parser.parse_args()


def seconds_to_hms(seconds):
    h = int(seconds // 3600)
    m = int((seconds % 3600) // 60)
    s = int(seconds % 60)
    return f"{h:02}:{m:02}:{s:02}"


def convert_seconds_to_srt_time(total_seconds):
    h = int(total_seconds // 3600)
    m = int((total_seconds % 3600) // 60)
    s = total_seconds % 60
    s_int = int(s)
    ms = int((s - s_int) * 1000)
    return f"{h:02}:{m:02}:{s_int:02},{ms:03}"


_BATCH_SEP = " ||| "
_BATCH_SIZE = 10


async def _translate_batch(translator, texts, dest):
    """批次翻譯一組文字，利用前後語境提升準確度。失敗時逐段 fallback。"""
    joined = _BATCH_SEP.join(texts)
    for attempt in range(3):
        try:
            result = await translator.translate(joined, dest=dest)
            if result and result.text:
                parts = [p.strip() for p in result.text.split("|||")]
                if len(parts) == len(texts):
                    return parts
        except Exception:
            if attempt < 2:
                await asyncio.sleep(1)

    # fallback：逐段翻譯
    out = []
    for text in texts:
        try:
            r = await translator.translate(text, dest=dest)
            out.append(r.text if r and r.text else text)
        except Exception:
            out.append(text)
        await asyncio.sleep(0.1)
    return out


async def _translate_all(segments, target_lang):
    from googletrans import Translator
    translator = Translator()
    total = len(segments)
    for batch_start in range(0, total, _BATCH_SIZE):
        batch = segments[batch_start:batch_start + _BATCH_SIZE]
        texts = [seg['text'] for seg in batch]
        translations = await _translate_batch(translator, texts, target_lang)
        for i, (seg, tr) in enumerate(zip(batch, translations)):
            seg['translation'] = tr
            idx = batch_start + i + 1
            print(f"TRANSLATE:{idx}/{total}", flush=True)
            print(f"[翻譯 {idx}/{total}] {tr}", flush=True)
    return segments


def translate_segments(segments, target_lang, backend='googletrans', claude_api_key='', gemini_api_key='', translate_prompt=''):
    if backend == 'claude-cli':
        return _translate_with_claude_cli(segments, target_lang, translate_prompt)
    if backend in ('claude-haiku', 'claude-sonnet'):
        _model = 'claude-haiku-4-5-20251001' if backend == 'claude-haiku' else 'claude-sonnet-4-6'
        return _translate_with_claude(segments, target_lang, claude_api_key, _model, translate_prompt)
    if backend == 'gemini-flash':
        return _translate_with_gemini(segments, target_lang, gemini_api_key, translate_prompt)
    return asyncio.run(_translate_all(segments, target_lang))


def _translate_with_claude_cli(segments, target_lang, translate_prompt=''):
    """全段一次送入 Claude CLI，利用完整語境翻譯。
    超過安全字元上限（~400K chars）時自動切成數批。"""
    import re
    import subprocess

    lang_display = _LANG_NAMES.get(target_lang, target_lang)
    total = len(segments)

    # 估算總字元數，決定是否需要分批
    total_chars = sum(len(s['text']) for s in segments)
    # 400K chars ≈ 120K tokens，留足回應空間
    if total_chars <= 400_000:
        batches = [segments]
    else:
        chunk_size = max(1, int(len(segments) * 400_000 / total_chars))
        batches = [segments[i:i + chunk_size] for i in range(0, total, chunk_size)]

    translated_count = 0
    for batch_idx, batch in enumerate(batches):
        numbered = '\n'.join(f"{i+1}. {seg['text']}" for i, seg in enumerate(batch))

        style_note = f"\n{translate_prompt}" if translate_prompt.strip() else ""
        prompt = (
            f"Translate these subtitle segments to {lang_display}.{style_note}\n"
            f"Return ONLY numbered translations, same format, no explanations.\n\n"
            f"{numbered}"
        )

        # 每段預留 3 秒，最少 120 秒
        timeout_secs = max(120, len(batch) * 3)
        batch_label = f"批次 {batch_idx+1}/{len(batches)}" if len(batches) > 1 else "全段"
        print(f"[Claude CLI] 送出 {batch_label}（{len(batch)} 段），等待回應（最多 {timeout_secs}s）...", flush=True)

        try:
            result = subprocess.run(
                ['claude', '-p', '--output-format', 'text'],
                input=prompt,
                capture_output=True,
                text=True,
                encoding='utf-8',
                timeout=timeout_secs,
            )
            if result.returncode != 0:
                raise RuntimeError(result.stderr.strip())

            pattern = re.compile(r'^\d+[\.\)]\s*(.+)$')
            translations = [
                pattern.match(l.strip()).group(1)
                for l in result.stdout.strip().split('\n')
                if l.strip() and pattern.match(l.strip())
            ]

            if len(translations) == len(batch):
                for seg, tr in zip(batch, translations):
                    seg['translation'] = tr
                    translated_count += 1
                    print(f"TRANSLATE:{translated_count}/{total}", flush=True)
                    print(f"[翻譯 {translated_count}/{total}] {tr}", flush=True)
                continue

            print(f"[警告] CLI 回傳 {len(translations)}/{len(batch)} 段", flush=True)
            print(f"[Claude 回應] {result.stdout.strip()[:300]}", flush=True)
            _googletrans_fallback(batch, translated_count, total, target_lang, "Claude CLI 段數不符")
            translated_count += len(batch)
            continue

        except Exception as e:
            print(f"[Claude CLI 錯誤] {e}", flush=True)
            _googletrans_fallback(batch, translated_count, total, target_lang, "Claude CLI 例外")
            translated_count += len(batch)

    return segments


_LANG_NAMES = {
    'zh-TW': '繁體中文（Traditional Chinese）',
    'zh-CN': '簡體中文（Simplified Chinese）',
    'en':    'English',
    'ko':    '한국어（Korean）',
    'ja':    '日本語（Japanese）',
}


def _googletrans_fallback(chunk, chunk_start, total, target_lang, label):
    """用 googletrans 補翻單批，並在 log 記錄哪段使用了 fallback。"""
    start_idx = chunk_start + 1
    end_idx   = chunk_start + len(chunk)
    print(f"[Fallback] 第 {start_idx}–{end_idx} 段改用 googletrans 補翻（{label}）", flush=True)
    fallback_segs = asyncio.run(_translate_all(chunk, target_lang))
    for i, seg in enumerate(fallback_segs):
        chunk[i]['translation'] = seg.get('translation', seg['text'])
        print(f"TRANSLATE:{chunk_start + i + 1}/{total}", flush=True)


def _translate_with_claude(segments, target_lang, api_key, model, translate_prompt=''):
    import re
    import anthropic

    client = anthropic.Anthropic(api_key=api_key)
    lang_display = _LANG_NAMES.get(target_lang, target_lang)
    total = len(segments)
    chunk_size = 150  # safe per-call limit

    for chunk_start in range(0, total, chunk_size):
        chunk = segments[chunk_start:chunk_start + chunk_size]
        numbered = '\n'.join(f"{i+1}. {seg['text']}" for i, seg in enumerate(chunk))

        style_note = f"\n{translate_prompt}" if translate_prompt.strip() else ""
        prompt = (
            f"你是專業字幕翻譯，負責將字幕翻譯為{lang_display}。{style_note}\n"
            f"只輸出編號翻譯，格式與輸入相同，不加任何說明或空行。\n\n"
            f"{numbered}"
        )

        try:
            message = client.messages.create(
                model=model,
                max_tokens=4096,
                messages=[{"role": "user", "content": prompt}],
            )
            response_text = message.content[0].text
            pattern = re.compile(r'^\d+[\.\)、]\s*(.+)$')
            translations = [
                pattern.match(line).group(1)
                for line in response_text.strip().split('\n')
                if line.strip() and pattern.match(line.strip())
            ]
            if len(translations) == len(chunk):
                for i, (seg, tr) in enumerate(zip(chunk, translations)):
                    seg['translation'] = tr
                    idx = chunk_start + i + 1
                    print(f"TRANSLATE:{idx}/{total}", flush=True)
                    print(f"[翻譯 {idx}/{total}] {tr}", flush=True)
            else:
                print(f"[警告] Claude 回傳 {len(translations)}/{len(chunk)} 段", flush=True)
                _googletrans_fallback(chunk, chunk_start, total, target_lang, "Claude 段數不符")
        except Exception as e:
            print(f"[警告] Claude 例外：{e}", flush=True)
            _googletrans_fallback(chunk, chunk_start, total, target_lang, f"Claude 例外")

    return segments


def _translate_with_gemini(segments, target_lang, api_key, translate_prompt=''):
    import re
    import google.genai as genai

    client = genai.Client(api_key=api_key)
    lang_display = _LANG_NAMES.get(target_lang, target_lang)
    total = len(segments)
    chunk_size = 150

    for chunk_start in range(0, total, chunk_size):
        chunk = segments[chunk_start:chunk_start + chunk_size]
        numbered = '\n'.join(f"{i+1}. {seg['text']}" for i, seg in enumerate(chunk))

        style_note = f"\n{translate_prompt}" if translate_prompt.strip() else ""
        prompt = (
            f"你是專業字幕翻譯，負責將字幕翻譯為{lang_display}。{style_note}\n"
            f"只輸出編號翻譯，格式與輸入相同，不加任何說明或空行。\n\n"
            f"{numbered}"
        )

        try:
            response = client.models.generate_content(
                model='gemini-3.1-flash-lite',
                contents=prompt,
            )
            response_text = response.text
            pattern = re.compile(r'^\d+[\.\)、]\s*(.+)$')
            translations = [
                pattern.match(line).group(1)
                for line in response_text.strip().split('\n')
                if line.strip() and pattern.match(line.strip())
            ]
            if len(translations) == len(chunk):
                for i, (seg, tr) in enumerate(zip(chunk, translations)):
                    seg['translation'] = tr
                    idx = chunk_start + i + 1
                    print(f"TRANSLATE:{idx}/{total}", flush=True)
                    print(f"[翻譯 {idx}/{total}] {tr}", flush=True)
            else:
                print(f"[警告] Gemini 回傳 {len(translations)}/{len(chunk)} 段", flush=True)
                _googletrans_fallback(chunk, chunk_start, total, target_lang, "Gemini 段數不符")
        except Exception as e:
            print(f"[警告] Gemini 例外：{e}", flush=True)
            _googletrans_fallback(chunk, chunk_start, total, target_lang, "Gemini 例外")

    return segments


_sentence_model = None

def _get_sentence_model():
    global _sentence_model
    if _sentence_model is None:
        _sentence_model = SentenceTransformer('sonoisa/sentence-bert-base-ja-mean-tokens-v2')
    return _sentence_model


def merged_segments_with_model(segments):
    merged = []
    prev = None
    sentence_model = _get_sentence_model()
    similarity_threshold = 0.7
    min_display_duration = 1.0
    max_duration = 6.0
    max_segment_gap = 1.0
    max_chars_per_second = 15
    max_chars_per_segment = 45

    for seg in segments:
        if seg["end"] - seg["start"] > max_duration:
            if prev:
                merged.append(prev)
            merged.append(seg)
            prev = None
            continue

        if prev:
            embeddings = sentence_model.encode([prev["text"], seg["text"]])
            sim = cosine_similarity([embeddings[0]], [embeddings[1]])[0][0]
            gap = seg["start"] - prev["end"]
            proposed_duration = seg["end"] - prev["start"]
            proposed_text = prev["text"] + " " + seg["text"]
            chars_per_sec = len(proposed_text) / proposed_duration if proposed_duration > 0 else float('inf')

            if (sim > similarity_threshold and
                    gap <= max_segment_gap and
                    min_display_duration <= proposed_duration <= max_duration and
                    chars_per_sec <= max_chars_per_second and
                    len(proposed_text) <= max_chars_per_segment):
                prev["text"] += " " + seg["text"]
                prev["end"] = seg["end"]
                continue
            else:
                merged.append(prev)

        prev = seg

    if prev:
        merged.append(prev)
    return merged


import re as _re

_HAS_CJK = _re.compile(r'[　-鿿＀-￯぀-ヿ]')
_LATIN_ONLY = _re.compile(r'[a-zA-Z]{3,}')
_ARTIFACT_RE = _re.compile(r'た\d+|[-ɏ]')


def _is_hallucination(text):
    stripped = text.strip()
    if not stripped:
        return True
    if _ARTIFACT_RE.search(stripped):
        return True
    # Text with Japanese characters is valid even if it contains English loanwords
    if _LATIN_ONLY.search(stripped) and not _HAS_CJK.search(stripped):
        return True
    return False


def write_srt_file(output_file, segments, review=False):
    with open(output_file, "w", encoding="utf-8") as f:
        for idx, seg in enumerate(segments, 1):
            f.write(f"{idx}\n")
            f.write(f"{convert_seconds_to_srt_time(seg['start'])} --> {convert_seconds_to_srt_time(seg['end'])}\n")
            has_real_translation = 'translation' in seg and seg['translation'] != seg['text']
            is_combined = '\n' in seg['text']   # L+R 合併條目

            if review:
                if has_real_translation:
                    f.write(f"{seg['text']}\n{seg['translation']}\n\n")
                else:
                    f.write(f"{seg['text']}\n\n")
            else:
                if is_combined:
                    f.write(f"{seg['translation'] if has_real_translation else seg['text']}\n\n")
                elif has_real_translation:
                    f.write(f"{seg['translation']}\n\n")
                else:
                    f.write(f"{seg['text']}\n\n")


def _extract_channel(input_file, channel):
    """用 PyAV 抽取指定聲道為臨時 mono wav，不需要 ffmpeg 執行檔。"""
    import av
    import numpy as np
    import tempfile
    import wave

    channel_idx = 0 if channel == 'left' else 1

    container = av.open(input_file)
    audio_stream = next((s for s in container.streams if s.type == 'audio'), None)
    if audio_stream is None:
        raise RuntimeError("找不到音訊串流")

    sample_rate = audio_stream.sample_rate
    resampler = av.AudioResampler(format='s16p', layout='stereo', rate=sample_rate)

    samples = []
    for frame in container.decode(audio_stream):
        for rf in resampler.resample(frame):
            arr = rf.to_ndarray()   # shape: (2, n_samples)
            samples.append(arr[channel_idx].copy())
    container.close()

    if not samples:
        raise RuntimeError("無法讀取音訊資料")

    audio = np.concatenate(samples)  # int16

    tmp = tempfile.NamedTemporaryFile(suffix='.wav', delete=False)
    tmp.close()
    with wave.open(tmp.name, 'wb') as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(sample_rate)
        wf.writeframes(audio.tobytes())
    return tmp.name


def _transcribe_file(audio_path, model, args):
    """轉錄單一音訊檔，回傳 extracted segments list。"""
    transcribe_kwargs = dict(
        beam_size=args.beam_size,
        word_timestamps=False,
        condition_on_previous_text=False,
    )
    if args.initial_prompt:
        transcribe_kwargs['initial_prompt'] = args.initial_prompt
    if args.language:
        transcribe_kwargs['language'] = args.language
    if args.vad_filter:
        transcribe_kwargs['vad_filter'] = True
        transcribe_kwargs['vad_parameters'] = dict(min_silence_duration_ms=500)

    segments, info = model.transcribe(audio_path, **transcribe_kwargs)
    print(f"偵測語言: {info.language}（信心度: {info.language_probability:.2f}）", flush=True)

    extracted = []
    last_progress = 0.0
    for seg in segments:
        if _is_hallucination(seg.text):
            print(f"[過濾] {seconds_to_hms(seg.start)} 幻覺字幕: {seg.text.strip()}", flush=True)
        else:
            extracted.append({"start": seg.start, "end": seg.end, "text": seg.text})
        if seg.end > last_progress:
            last_progress = seg.end
            print(f"PROGRESS:{seg.end:.2f}/{info.duration:.2f}", flush=True)
            if not _is_hallucination(seg.text):
                print(f"[{seconds_to_hms(seg.end)}/{seconds_to_hms(info.duration)}] {seg.text}", flush=True)
    return extracted


def _post_process(extracted, args):
    """合併 + 翻譯。"""
    if args.merge:
        print("合併字幕段落中...", flush=True)
        extracted = merged_segments_with_model(extracted)
    if args.translate:
        print(f"開始翻譯（{args.translate_lang} / {args.translate_backend}）...", flush=True)
        extracted = translate_segments(
            extracted, args.translate_lang,
            backend=args.translate_backend,
            claude_api_key=args.claude_api_key,
            gemini_api_key=args.gemini_api_key,
            translate_prompt=args.translate_prompt,
        )
    return extracted


def _pair_channels(segs):
    """把時間重疊的 L/R 段落合成同一條字幕（雙行）；不重疊的保持獨立。"""
    result = []
    i = 0
    while i < len(segs):
        cur = segs[i]
        if i + 1 < len(segs):
            nxt = segs[i + 1]
            overlap = cur['end'] > nxt['start'] and cur.get('_ch') != nxt.get('_ch')
            if overlap:
                combined = {
                    'start': min(cur['start'], nxt['start']),
                    'end':   max(cur['end'],   nxt['end']),
                    'text':  cur['text'] + '\n' + nxt['text'],
                }
                if 'translation' in cur and 'translation' in nxt:
                    combined['translation'] = cur['translation'] + '\n' + nxt['translation']
                result.append(combined)
                i += 2
                continue
        result.append(cur)
        i += 1
    return result


def _process_single_file(input_file, output_file, model, args):
    if args.channel == 'split':
        all_segs = []
        for ch, prefix in (('left', '[L]'), ('right', '[R]')):
            label = '左聲道' if ch == 'left' else '右聲道'
            print(f"CHANNEL:{ch}", flush=True)
            print(f"--- 開始轉錄 {label} ---", flush=True)
            tmp = _extract_channel(input_file, ch)
            try:
                extracted = _transcribe_file(tmp, model, args)
            finally:
                os.unlink(tmp)
            if args.merge:
                print(f"合併 {label} 字幕段落中...", flush=True)
                extracted = merged_segments_with_model(extracted)
            for seg in extracted:
                tagged = dict(seg)
                tagged['_ch']     = ch
                tagged['_prefix'] = prefix
                all_segs.append(tagged)

        all_segs.sort(key=lambda s: s['start'])
        if args.translate:
            print(f"開始翻譯（{args.translate_lang} / {args.translate_backend}）...", flush=True)
            all_segs = translate_segments(
                all_segs, args.translate_lang,
                backend=args.translate_backend,
                claude_api_key=args.claude_api_key,
                gemini_api_key=args.gemini_api_key,
                translate_prompt=args.translate_prompt,
            )

        for seg in all_segs:
            p = seg.get('_prefix', '')
            seg['text'] = f"{p} {seg['text'].strip()}"
            if 'translation' in seg:
                seg['translation'] = f"{p} {seg['translation'].strip()}"

        all_segs = _pair_channels(all_segs)
        write_srt_file(output_file, all_segs)
        print(f"SRT:{output_file}", flush=True)
        if args.translate:
            base, ext = os.path.splitext(output_file)
            review_out = base + '_review' + ext
            write_srt_file(review_out, all_segs, review=True)
            print(f"SRT_REVIEW:{review_out}", flush=True)

    else:
        print("開始語音辨識...", flush=True)
        if args.channel in ('left', 'right'):
            label = '左聲道' if args.channel == 'left' else '右聲道'
            print(f"[聲道] 抽取 {label}...", flush=True)
            tmp = _extract_channel(input_file, args.channel)
            try:
                extracted = _transcribe_file(tmp, model, args)
            finally:
                os.unlink(tmp)
        else:
            extracted = _transcribe_file(input_file, model, args)

        if args.merge:
            print("合併字幕段落中...", flush=True)
            extracted = merged_segments_with_model(extracted)
        if args.segments_out:
            import json as _json
            with open(args.segments_out, 'w', encoding='utf-8') as _f:
                _json.dump(extracted, _f, ensure_ascii=False)
            print(f"SEGMENTS_SAVED:{args.segments_out}", flush=True)
        else:
            if args.translate:
                print(f"開始翻譯（{args.translate_lang} / {args.translate_backend}）...", flush=True)
                extracted = translate_segments(
                    extracted, args.translate_lang,
                    backend=args.translate_backend,
                    claude_api_key=args.claude_api_key,
                    gemini_api_key=args.gemini_api_key,
                    translate_prompt=args.translate_prompt,
                )
        write_srt_file(output_file, extracted)
        print(f"SRT:{output_file}", flush=True)
        if args.translate and not args.segments_out:
            base, ext = os.path.splitext(output_file)
            review_file = base + '_review' + ext
            write_srt_file(review_file, extracted, review=True)
            print(f"SRT_REVIEW:{review_file}", flush=True)



def _run_translate_only(args):
    """Stage 2: load segments from JSON, translate, write SRT. No model loading."""
    import json as _json
    with open(args.segments_in, encoding='utf-8') as _f:
        extracted = _json.load(_f)

    total_files = len(args.file)
    input_file = args.file[0]
    output_file = (args.output if total_files == 1 and args.output
                   else os.path.splitext(input_file)[0] + ".srt")

    if args.translate:
        print(f"開始翻譯（{args.translate_lang} / {args.translate_backend}）...", flush=True)
        extracted = translate_segments(
            extracted, args.translate_lang,
            backend=args.translate_backend,
            claude_api_key=args.claude_api_key,
            gemini_api_key=args.gemini_api_key,
            translate_prompt=args.translate_prompt,
        )

    write_srt_file(output_file, extracted)
    print(f"SRT:{output_file}", flush=True)

    if args.translate:
        base, ext = os.path.splitext(output_file)
        review_file = base + '_review' + ext
        write_srt_file(review_file, extracted, review=True)
        print(f"SRT_REVIEW:{review_file}", flush=True)

if __name__ == '__main__':
    args = parse_args()
    if args.segments_in:
        # Stage 2: translate-only, no model loading needed
        # Validate file list so output path can be derived
        missing = [f for f in args.file if not os.path.isfile(f)]
        if missing:
            for f in missing:
                print(f"[錯誤] 找不到檔案：{f}", flush=True)
            sys.exit(1)
        _run_translate_only(args)
        sys.exit(0)


    # Validate all input files before spending time loading the model
    missing = [f for f in args.file if not os.path.isfile(f)]
    if missing:
        for f in missing:
            print(f"[錯誤] 找不到檔案：{f}", flush=True)
        sys.exit(1)

    print(f"MODEL_LOADING:{args.model}", flush=True)
    print(f"載入 {args.model} 模型...", flush=True)
    t0 = time.time()
    try:
        model = WhisperModel(args.model, device=args.device, compute_type=args.compute_type)
    except RuntimeError as e:
        msg = str(e)
        if 'out of memory' in msg.lower():
            print("[錯誤] 顯示卡記憶體不足，請關閉其他佔用 GPU 的程式後重試。", flush=True)
        elif 'no cuda' in msg.lower() or 'cuda' in msg.lower():
            print("[錯誤] 找不到 NVIDIA 顯示卡或 CUDA 驅動，請確認驅動已安裝，或改用 --device cpu --compute-type int8。", flush=True)
        else:
            print(f"[錯誤] 模型載入失敗：{msg}", flush=True)
        sys.exit(1)
    load_secs = time.time() - t0
    print(f"MODEL_LOADED:{load_secs:.1f}", flush=True)
    print(f"模型載入完成，耗時 {int(load_secs // 60)} 分 {int(load_secs % 60)} 秒", flush=True)

    files = args.file
    total_files = len(files)

    t0 = time.time()
    for file_idx, input_file in enumerate(files):
        if total_files > 1:
            print(f"FILE:{file_idx + 1}/{total_files}", flush=True)
            print(f"--- [{file_idx + 1}/{total_files}] {input_file} ---", flush=True)

        output_file = (args.output if total_files == 1 and args.output
                       else os.path.splitext(input_file)[0] + ".srt")

        file_t0 = time.time()
        try:
            _process_single_file(input_file, output_file, model, args)
        except Exception as e:
            print(f"[錯誤] {input_file}: {e}", flush=True)
            continue

        file_elapsed = time.time() - file_t0
        fm, fs = divmod(int(file_elapsed), 60)
        if total_files > 1:
            print(f"[{file_idx + 1}/{total_files}] 完成，耗時 {fm} 分 {fs} 秒", flush=True)

    elapsed = time.time() - t0
    m, s = divmod(int(elapsed), 60)
    if total_files > 1:
        print(f"全部完成，共 {total_files} 個檔案，耗時 {m} 分 {s} 秒", flush=True)
    else:
        print(f"轉錄完成，耗時 {m} 分 {s} 秒", flush=True)
