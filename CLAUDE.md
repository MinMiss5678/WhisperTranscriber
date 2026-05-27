# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Running the Script

```bash
# Activate the virtual environment first (Windows)
source venv/Scripts/activate

# Run the transcription (requires --file argument)
python whisper-transcriber.py --file audio_files/input.mp3
```

No build system or test suite exists.

## Project Structure

- `whisper-transcriber.py` — main CLI script
- `WhisperGUI/` — WPF GUI frontend (C#) that spawns the Python script as a subprocess

## Dependencies

There is no `requirements.txt`. All packages are installed directly into `venv/`. Key packages: `faster-whisper`, `torch`, `sentence-transformers`, `ctranslate2`, `av`, `googletrans`, `anthropic`. Install new packages with `pip install <package>` while the venv is active.

## Architecture

### CLI script: `whisper-transcriber.py`

Accepts argparse arguments. The pipeline is:

1. **Load model** — `WhisperModel(args.model, device=args.device, compute_type=args.compute_type)` from `faster-whisper`
2. **Channel extraction** (optional) — `_extract_channel()` uses PyAV to extract left/right mono channel without ffmpeg binary; `split` mode transcribes both channels separately then merges
3. **Transcribe** — `_transcribe_file()` returns segments; emits `PROGRESS:cur/total` lines to stdout for GUI progress bar; filters hallucinations via `_is_hallucination()`
4. **Merge segments** (optional, `--merge`) — `merged_segments_with_model()` uses `sentence-transformers` cosine similarity
5. **Translate** (optional, `--translate`) — supports `googletrans`, `claude-cli`, `claude-haiku`, `claude-sonnet` backends
6. **Write SRT** — `write_srt_file()` outputs `<filename>.srt`; with `--translate` also writes a `_review` SRT with both original and translation

### GUI: `WhisperGUI/`

WPF app (C#, .NET). Locates `venv/Scripts/python.exe` relative to `AppContext.BaseDirectory` (4 levels up). Spawns `whisper-transcriber.py` and parses `PROGRESS:`, `TRANSLATE:`, `SRT:`, `SRT_REVIEW:`, `CHANNEL:` protocol lines from stdout.

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
| `--translate-backend` | `googletrans` | `googletrans`, `claude-cli`, `claude-haiku`, `claude-sonnet` |
| `--claude-api-key` | — | Required for claude-haiku/sonnet backends |

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
