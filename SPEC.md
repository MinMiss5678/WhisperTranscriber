# Whisper Transcriber — Technical Specification

## 架構概覽

```
WhisperGUI.exe  (WPF, .NET 10)
      │
      │  subprocess (stdin/stdout/stderr)
      ▼
WhisperTranscriber.py  (Python, venv)
      │
      ├── faster-whisper   (轉錄)
      ├── av               (聲道抽取)
      ├── sentence-transformers  (字幕合併)
      └── googletrans / anthropic / google-genai  (翻譯)
```

GUI 不直接操作 Python 函式庫，全部透過 subprocess 呼叫 Python 腳本。兩端透過 stdout 協定溝通。

---

## GUI ↔ Python stdout 通訊協定

Python 腳本輸出兩類訊息：**控制訊息**與**日誌訊息**。

### 控制訊息（GUI 解析，不顯示於 LogBox）

| 格式 | 範例 | 說明 |
|---|---|---|
| `MODEL_LOADING:<model>` | `MODEL_LOADING:large-v3` | 模型開始載入，GUI 啟動每秒計時器顯示等待秒數 |
| `MODEL_LOADED:<secs>` | `MODEL_LOADED:12.3` | 模型載入完成，GUI 停止計時器 |
| `PROGRESS:<cur>/<total>` | `PROGRESS:123.45/3600.00` | 轉錄進度（秒），更新進度條 |
| `TRANSLATE:<n>/<total>` | `TRANSLATE:42/300` | 翻譯進度（段落數），更新進度條 |
| `SRT:<path>` | `SRT:C:\...\output.srt` | 主要輸出 SRT 路徑 |
| `SRT_REVIEW:<path>` | `SRT_REVIEW:C:\...\output_review.srt` | 校對版 SRT 路徑（原文+譯文） |
| `CHANNEL:<ch>` | `CHANNEL:left` | 雙聲道模式下切換聲道，重置進度條 |
| `SEGMENTS_SAVED:<path>` | `SEGMENTS_SAVED:tmp.json` | Pipeline stage 1：segments JSON 已儲存 |
| `FILE:<n>/<total>` | `FILE:2/5` | 批次模式下目前處理第幾個檔案 |

### 日誌訊息（顯示於 LogBox）

所有不符合上述前綴的 stdout 行，以及全部 stderr，都視為日誌原樣顯示。

---

## Python 腳本管線

### 單聲道模式（`--channel mix|left|right`）

```
載入模型
  → [left/right] _extract_channel()  抽取單聲道臨時 wav
  → _transcribe_file()               轉錄，輸出 PROGRESS: 行
  → [--merge] merged_segments_with_model()
  → [--translate] translate_segments()
  → write_srt_file()                 輸出 SRT:
  → [--translate] write_srt_file(review=True)  輸出 SRT_REVIEW:
```

### 雙聲道分離模式（`--channel split`）

```
載入模型
  → left 聲道：_extract_channel() → _transcribe_file() → [--merge]
  → right 聲道：_extract_channel() → _transcribe_file() → [--merge]
  → 合流排序（依 start 時間）
  → [--translate] translate_segments()  （統一翻譯，送入乾淨原文）
  → 貼前綴 [L]/[R]
  → _pair_channels()  重疊段落合成雙行字幕
  → write_srt_file()        輸出 SRT:
  → [--translate] write_srt_file(review=True)  輸出 SRT_REVIEW:
```

---

### Pipeline 模式（stage 1 + stage 2）

當 GUI 佇列有 2+ 個檔案且翻譯啟用時，GUI 改用兩段式呼叫：

```
Stage 1（--segments-out）：載入模型 → 轉錄 → [merge] → 儲存 segments JSON → 輸出 SRT（無翻譯）
Stage 2（--segments-in）： 不載入模型 → 讀取 segments JSON → 翻譯 → 輸出 SRT
```

Stage 1 結束後 Stage 2 作為背景 Task 啟動，同時 Stage 1 開始處理下一個檔案。

---

## 模組說明

### `_extract_channel(input_file, channel)`

使用 PyAV（不依賴 ffmpeg 執行檔）將立體聲音訊抽取為單聲道 wav。

- 輸出格式：16-bit PCM，mono，原始取樣率
- 回傳：臨時檔案路徑（呼叫端負責 `os.unlink`）
- 錯誤：找不到音訊串流時拋 `RuntimeError`

### `_transcribe_file(audio_path, model, args)`

- 輸出每段後立即 flush `PROGRESS:` 行（GUI 即時更新）
- 呼叫 `_is_hallucination()` 過濾異常字幕
- 回傳 `list[dict]`，每筆 `{"start": float, "end": float, "text": str}`

### `_is_hallucination(text)`

過濾條件（任一符合即丟棄）：

| 模式 | 說明 |
|---|---|
| 空白字串 | 無內容 |
| 3+ 拉丁字母且無 CJK 字元 | 純英文幻覺（如 "Thank you for watching"）；日英混用（如 `YouTubeで`）不過濾 |
| `た\d+` | `た20` 類數字計數器幻覺 |
| `[-ɏ]` | Latin Extended 字元（如土耳其語無點 i） |

### `_get_sentence_model()`

Module-level lazy singleton，首次呼叫時載入 `sonoisa/sentence-bert-base-ja-mean-tokens-v2`，後續呼叫回傳快取實例。`split` 模式下左右聲道各呼叫一次 `merged_segments_with_model()`，模型只載入一次。

### `merged_segments_with_model(segments)`

**僅支援日文**（模型為日文專用 BERT）。使用 `_get_sentence_model()` 計算餘弦相似度，逐對比較相鄰段落。

合併條件（全部成立才合併）：

| 條件 | 閾值 |
|---|---|
| 餘弦相似度 | ≥ 0.7 |
| 段落間隔 | ≤ 1.0 秒 |
| 合併後顯示時長 | 1.0–6.0 秒 |
| 合併後字元顯示速率 | ≤ 15 字/秒 |
| 合併後總字元數 | ≤ 45 |
| 單段時長（pre-check） | ≤ 6.0 秒，否則直接輸出不合併 |

### `translate_segments(segments, target_lang, backend, claude_api_key, gemini_api_key, translate_prompt, gemini_model)`

| Backend | 實作 | 備註 |
|---|---|---|
| `googletrans` | `asyncio` + `googletrans.Translator` | 批次 10 段，分隔符 ` \|\|\| `，失敗逐段 fallback |
| `claude-cli` | `subprocess` 呼叫 `claude -p` | 需 Claude 訂閱；全段一次送入，超過 400K chars 自動切批 |
| `claude-haiku` | Anthropic SDK，`claude-haiku-4-5-20251001` | 每批 150 段 |
| `claude-sonnet` | Anthropic SDK，`claude-sonnet-4-6` | 每批 150 段 |
| `gemini-flash` | Google GenAI SDK，預設 `gemini-3.1-flash-lite` | 每批 150 段；模型由 `--gemini-model` 指定；有免費額度（500 RPD） |

翻譯結果寫入 `seg['translation']`，原文 `seg['text']` 不變。

`translate_prompt` 為風格提示詞，附加在系統 prompt 末尾。預設值 `日文 ASMR 字幕，保持自然、輕柔、親密的口語語氣，符合 ASMR 風格`；空字串則省略。對 `googletrans` 後端無效。

**自動 Fallback**：LLM 後端（claude-haiku/sonnet/cli、gemini-flash）若拋例外或回傳段數不符，自動改用 googletrans 補翻該批，並在 log 輸出：
```
[Fallback] 第 X–Y 段改用 googletrans 補翻（原因）
```
其餘批次不受影響，繼續走 LLM。

### `write_srt_file(output_file, segments, review=False)`

| 情境 | 輸出內容 |
|---|---|
| 無翻譯 | 原文 |
| 有翻譯，`review=False` | 譯文 |
| 有翻譯，`review=True` | 原文 + 譯文（雙行） |

### `_pair_channels(segs)`

雙聲道分離後，將時間重疊的 L/R 段落合成單一條目（`text` 以 `\n` 分隔）。非重疊段落保持獨立。

---

## GUI 架構

### 路徑解析

```csharp
RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\.."));
pythonExe  = Path.Combine(RepoRoot, "venv", "Scripts", "python.exe");
scriptPath = Path.Combine(RepoRoot, "WhisperTranscriber.py");
```

`AppContext.BaseDirectory` 在開發時為 `bin\Debug\net10.0-windows\`，四層上去即 repo 根目錄。

### 設定持久化

路徑：`%AppData%\WhisperGUI\settings.json`

儲存欄位：Model、Language、Device、ComputeType、InitialPrompt、Merge、VadFilter、Translate、TranslateLang、TranslateBackend、ClaudeApiKey、TranslatePrompt、Channel、NotifyOnComplete、OutputDir。

`BeamSize` 已從 GUI 移除，固定傳 `--beam-size 5`；CLI 仍可覆蓋。

`ClaudeApiKey` 欄位同時用於 Gemini Key：backend 為 `gemini-flash` 時，GUI 以 `--gemini-api-key` 傳入相同值；`AppSettings` 中無獨立 Gemini Key 欄位。

`GeminiModel`（`AppSettings`）：`gemini-flash` 後端選取時，GUI 顯示「Gemini 模型」輸入欄，允許用戶自訂模型 ID（預設 `gemini-3.1-flash-lite`），以 `--gemini-model` 傳入 Python。

視窗關閉時寫入，載入時讀取。讀取失敗（格式錯誤、不存在）靜默忽略。

### 首次設定精靈（SetupWindow）

`Window_Loaded` 偵測 `venv/Scripts/python.exe` 是否存在：
- 存在 → 正常啟動
- 不存在 → 以 `Owner = this` 開啟 `SetupWindow`（modal）

`SetupWindow` 執行 `setup.bat /nopause`，串流 stdout/stderr 至 log box，完成後啟用「完成」按鈕。`setup.bat` 支援 `/nopause` 參數略過 `pause` 指令（供 GUI 呼叫）。

`MODEL_LOADING:` 計時器超過 30 秒時，StatusText 加注「首次執行需下載模型約 3 GB，請耐心等候」提示。

### 語言限制合併

`LanguageCombo_SelectionChanged` 在語言切換時更新 `MergeCheck`：
- `ja` → `IsEnabled = true`
- 其他（含 auto）→ `IsChecked = false`、`IsEnabled = false`、`ToolTip = "語意合併僅支援日文"`

### 非同步執行模型

`StartTranscription_Click` 為 `async void`，`RunTranscription` / `RunTranscriptionOnly` / `RunTranslationOnly` 為 `async Task`。

stdout/stderr 各由獨立 `ConsumeStreamAsync` 讀取（避免死鎖）。Process 結束透過 `TaskCompletionSource<int>` 通知，不使用 `WaitForExit`。

取消：`CancellationTokenSource` → `_process.Kill(entireProcessTree: true)`。

**Pipeline 模式**（translate 啟用且佇列 2+ 檔案時）：`RunTranscriptionOnly` 完成後立即以背景 Task 啟動 `RunTranslationOnly`，同時開始下一筆轉錄。翻譯 Task 以 `ContinueWith` 回報 `QueueStatus.Done / Error`，並清理臨時 segments JSON。

**Pipeline 模式**（translate 啟用且佇列 2+ 檔案時）： 完成後立即啟動  作為背景 Task，同時開始下一筆轉錄。翻譯 Task 以  回報 ，並清理臨時 segments JSON。

---

## 新增功能指引

### 新增翻譯後端

1. `WhisperTranscriber.py`：`--translate-backend` choices 加入新值，`translate_segments()` 加 `if backend == '...'` 分支（接收 `translate_prompt` 並傳入後端函式）
2. `MainWindow.xaml`：`TranslateBackendCombo` 加 `<ComboBoxItem Tag="...">`
3. `UpdateApiKeyVisibility()`：視需要調整 API Key 顯示邏輯（label 文字、`ClaudeApiKeyBox` visibility）
4. `RunTranscription`：若新後端使用不同 API Key 參數名，在 `sb.Append` 區塊加對應 `--xxx-api-key` 分支
5. `AppSettings`：`TranslateBackend` 預設值視需要調整

### 新增 stdout 控制訊息

1. Python 端：`print(f"NEWPREFIX:{value}", flush=True)`
2. C# 端：`ConsumeStreamAsync` 加 `else if (line.StartsWith("NEWPREFIX:"))` 分支

### 新增模型限制（compute type）

`ModelCombo_SelectionChanged` 負責依模型名稱動態 disable 不支援的精度選項，並自動 fallback。
新增有限制的模型時，在此方法加判斷即可：

```csharp
bool isDistil = ((ComboBoxItem)ModelCombo.SelectedItem).Content?.ToString() == "distil-large-v3";
// 對 float32 disable；若已選則切回 float16
```

目前限制：

| 模型 | 不支援 |
|---|---|
| `distil-large-v3` | `float32` |
