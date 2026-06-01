# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Running the Script

```bash
# Activate the virtual environment first (Windows)
source venv/Scripts/activate

# Run the transcription (requires --file argument)
python WhisperTranscriber.py --file audio_files/input.mp3
```

Test suite: `venv/Scripts/python -m pytest tests/` (unit tests in `tests/test_whisper.py`).

## Project Structure

- `WhisperTranscriber.py` — main CLI script
- `WhisperGUI/` — WPF GUI frontend (C#) that spawns the Python script as a subprocess

## Dependencies

Dependencies are listed in `requirements.txt`. Install with `pip install -r requirements.txt` while the venv is active. Key packages: `faster-whisper`, `torch`, `sentence-transformers`, `ctranslate2`, `av`, `googletrans`, `anthropic`, `google-genai`. Install new packages with `pip install <package>` then update `requirements.txt`.

## Architecture

### CLI script: `WhisperTranscriber.py`

Accepts argparse arguments. The pipeline is:

1. **Validate inputs** — checks all `--file` paths exist before loading the model
2. **Load model** — `WhisperModel(args.model, device=args.device, compute_type=args.compute_type)` from `faster-whisper`; CUDA OOM and device errors emit user-friendly Chinese messages
3. **Channel extraction** (optional) — `_extract_channel()` uses PyAV to extract left/right mono channel without ffmpeg binary; `split` mode transcribes both channels separately then merges
4. **Transcribe** — `_transcribe_file()` returns segments; emits `PROGRESS:cur/total` lines to stdout for GUI progress bar; filters hallucinations via `_is_hallucination()`
5. **Merge segments** (optional, `--merge`) — `merged_segments_with_model()` uses `sentence-transformers` cosine similarity
6. **Translate** (optional, `--translate`) — supports `googletrans`, `claude-cli`, `claude-haiku`, `claude-sonnet`, `gemini-flash` backends
7. **Write SRT** — `write_srt_file()` outputs `<filename>.srt`; with `--translate` also writes a `_review` SRT with both original and translation

**Pipeline mode** (two-stage, used by GUI batch queue):
- Stage 1: `--segments-out PATH` — transcribes and saves segments JSON, skips translation
- Stage 2: `--segments-in PATH` — loads segments JSON and translates only, does NOT load Whisper model

### GUI: `WhisperGUI/`

WPF app (C#, .NET 10). Locates `venv/Scripts/python.exe` relative to `AppContext.BaseDirectory` (4 levels up). Spawns `WhisperTranscriber.py` and parses `PROGRESS:`, `TRANSLATE:`, `SRT:`, `SRT_REVIEW:`, `CHANNEL:`, `SEGMENTS_SAVED:` protocol lines from stdout.

**Batch queue**: `QueueItems` (`ObservableCollection<QueueItem>`) holds files with status `Pending / Transcribing / Translating / Done / Error`. When translation is enabled and 2+ files are queued, the GUI uses pipeline mode: `RunTranscriptionOnly` (stage 1) and `RunTranslationOnly` (stage 2) overlap GPU transcription with API translation.

Settings persisted to `%AppData%\WhisperGUI\settings.json`.

## Key CLI Arguments

| Argument | Default | Purpose |
|---|---|---|
| `--model` | `large-v3` | Whisper model variant |
| `--file` | (required) | Input audio/video file path |
| `--language` | auto-detect | Target language (`ja` for Japanese) |
| `--device` | `cuda` | `cuda` or `cpu` |
| `--compute-type` | `float16` | `float16`, `int8`, etc. |
| `--beam-size` | 5 | Beam search width |
| `--merge` | off | Enable segment merging |
| `--vad-filter` | off | Enable VAD filtering |
| `--channel` | `mix` | `mix`, `left`, `right`, `split` |
| `--translate` | off | Enable translation |
| `--translate-lang` | `zh-TW` | Target language for translation |
| `--translate-backend` | `googletrans` | `googletrans`, `claude-cli`, `claude-haiku`, `claude-sonnet`, `gemini-flash` |
| `--claude-api-key` | — | Required for claude-haiku/sonnet backends |
| `--gemini-api-key` | — | Required for gemini-flash backend |
| `--segments-out` | — | Pipeline stage 1: save segments JSON after transcription |
| `--segments-in` | — | Pipeline stage 2: load segments JSON and translate only |

## Hardware Requirements

CUDA GPU required for practical use. First run auto-downloads the `large-v3` model (~3 GB) via HuggingFace Hub. To run on CPU, change `--device cpu --compute-type int8`.

## Segment Merging Logic

`merged_segments_with_model()` merges two adjacent segments when **all** of the following hold:
- Cosine similarity between their sentence embeddings ≥ 0.7
- Time gap between end of first and start of second ≤ 1.0 s
- Combined display duration is between 1.0–6.0 s
- Combined character display rate ≤ 15 chars/sec
- Combined character count ≤ 45

The sentence embedding model used is `sonoisa/sentence-bert-base-ja-mean-tokens-v2` (Japanese BERT).

## Hallucination Filter

`_is_hallucination()` filters out bad Whisper output. Rules:
- Empty / whitespace-only → filtered
- Contains `た\d+` or Latin Extended chars (e.g. `ɏ`) → filtered
- Contains 3+ consecutive Latin letters **and** no CJK characters → filtered (pure-English hallucination)
- Japanese text mixed with English loanwords (e.g. `YouTubeで`, `ASMRの動画`) → **kept**
