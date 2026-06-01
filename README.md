# Whisper Transcriber

將音訊/視訊檔案轉錄為 SRT 字幕，支援翻譯與雙聲道分離。使用 [faster-whisper](https://github.com/SYSTRAN/faster-whisper) 驅動，提供 CLI 與 WPF GUI 兩種使用方式。

## 功能

- 多模型支援（tiny → large-v3、distil-large-v3）
- CUDA GPU 加速，CPU 亦可運作
- VAD 靜音過濾，減少幻覺字幕
- 語意相似度字幕合併（Japanese BERT，僅日文）
- 雙聲道分離（Binaural ASMR 左/右聲道分別轉錄，輸出含 `[L]`/`[R]` 標示的單一 SRT）
- 翻譯：googletrans（免費）、Gemini 2.5 Flash、Claude Haiku/Sonnet API、Claude CLI
- 翻譯時同時輸出含原文+譯文的校對版 SRT
- 批次佇列 + Pipeline 模式：第 N 筆翻譯與第 N+1 筆轉錄同時進行

## 環境需求

- Windows 10 / 11
- NVIDIA GPU（建議；CPU 亦可但速度慢約 10 倍）
- Python 3.10+
- .NET 10 Runtime（僅 GUI 需要）

## 安裝

雙擊 `setup.bat`，自動完成所有安裝步驟：

- Python 3.12（未安裝時透過 winget 自動下載）
- .NET 10 Runtime（未安裝時透過 winget 自動下載）
- Python 虛擬環境與所有套件

首次執行會自動從 HuggingFace Hub 下載模型（large-v3 約 3 GB）。

## 使用方式

### GUI

開啟 `WhisperGUI/` 專案以 Rider 或 Visual Studio 編譯後執行，或使用已編譯的 `WhisperGUI.exe`。

支援拖放音訊/視訊檔案，設定將自動儲存於 `%AppData%\WhisperGUI\settings.json`。

### CLI

```bash
source venv/Scripts/activate

# 基本轉錄（日文）
python WhisperTranscriber.py --file audio_files/input.mp3 --language ja

# 翻譯為繁體中文（免費）
python WhisperTranscriber.py --file input.mp4 --language ja --translate --translate-lang zh-TW

# 使用 Claude API 翻譯
python WhisperTranscriber.py --file input.mp4 --language ja \
  --translate --translate-backend claude-haiku --claude-api-key sk-ant-...

# 雙聲道分離（Binaural ASMR）
python WhisperTranscriber.py --file input.mp4 --language ja --channel split --translate

# CPU 模式
python WhisperTranscriber.py --file input.mp3 --device cpu --compute-type int8
```

### CLI 參數一覽

| 參數 | 預設值 | 說明 |
|---|---|---|
| `--file` | （必填）| 輸入音訊/視訊檔案路徑 |
| `--output` | 同輸入目錄 | 輸出 SRT 路徑 |
| `--model` | `large-v3` | 模型大小（tiny/base/small/medium/large-v1/v2/v3/distil-large-v3） |
| `--language` | 自動偵測 | 來源語言（`ja`、`zh`、`en`、`ko`…） |
| `--device` | `cuda` | `cuda` 或 `cpu` |
| `--compute-type` | `float16` | `float16`（GPU）、`int8`（CPU） |
| `--beam-size` | `5` | Beam search 寬度（1–10） |
| `--initial-prompt` | | 提示詞（如「以下是日文的ASMR」） |
| `--merge` | 關 | 啟用語意相似度字幕合併（僅日文） |
| `--vad-filter` | 關 | 啟用 VAD 靜音過濾 |
| `--channel` | `mix` | `mix`、`left`、`right`、`split` |
| `--translate` | 關 | 啟用翻譯 |
| `--translate-lang` | `zh-TW` | 目標語言 |
| `--translate-backend` | `googletrans` | `googletrans`、`claude-cli`、`claude-haiku`、`claude-sonnet`、`gemini-flash` |
| `--claude-api-key` | | Anthropic API Key（claude-haiku/sonnet 需要） |
| `--gemini-api-key` | | Google API Key（gemini-flash 需要，Google AI Studio 申請，有免費額度） |
| `--translate-prompt` | ASMR 風格 | LLM 翻譯風格提示詞（LLM 後端有效，googletrans 忽略） |

## 輸出檔案

| 情境 | 輸出檔案 |
|---|---|
| 一般轉錄 | `<input>.srt` |
| 翻譯 | `<input>.srt`（純譯文）、`<input>_review.srt`（原文+譯文） |

## 翻譯後端

| 後端 | 費用 | 說明 |
|---|---|---|
| googletrans | 免費 | 不需申請，速度快，品質普通 |
| Gemini 2.5 Flash | 免費額度 | 需 [Google AI Studio](https://aistudio.google.com/) API Key；10 RPM / 1,500 RPD 免費 |
| Claude Haiku API | 付費 | 需 [Anthropic Console](https://console.anthropic.com/) API Key |
| Claude Sonnet API | 付費 | 品質最佳 |
| Claude CLI | 需訂閱 | 需安裝 Claude 桌面版 |

LLM 後端失敗或回傳段數不符時，自動 fallback 至 googletrans 補翻該批。

## 字幕合併條件（`--merge`）

使用 `sonoisa/sentence-bert-base-ja-mean-tokens-v2` 計算語意相似度，同時滿足以下條件才合併：

- 餘弦相似度 ≥ 0.7
- 兩段間隔 ≤ 1.0 秒
- 合併後顯示時長 1.0–6.0 秒
- 合併後字元顯示速率 ≤ 15 字/秒
- 合併後總字元數 ≤ 45

## 授權

本專案採用 [GNU GPL v3](LICENSE) 授權。商業分發須附原始碼。
