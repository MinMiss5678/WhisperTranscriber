using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WhisperGUI;

public partial class MainWindow : Window
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _lastSrtPath;
    private string? _reviewSrtPath;
    private DispatcherTimer? _loadingTimer;
    private DateTime _loadingStart;

    private static readonly string RepoRoot = DetectRepoRoot();

    private static string DetectRepoRoot()
    {
        // Release: EXE 與 WhisperTranscriber.py 同目錄
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "WhisperTranscriber.py")))
            return AppContext.BaseDirectory;
        // Development: bin\Debug\net10.0-windows\ 往上四層
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\.."));
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhisperGUI", "settings.json");

    private const string OutputDirPlaceholder = "（與輸入檔案相同目錄）";

    public MainWindow() => InitializeComponent();

    // ── Drag & Drop ───────────────────────────────────────────────────────────

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            FilePathBox.Text = files[0];
    }

    // ── Browse ────────────────────────────────────────────────────────────────

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "媒體檔案|*.mp3;*.mp4;*.wav;*.m4a;*.mkv;*.avi;*.flac;*.ogg;*.ts;*.mts;*.m2ts|所有檔案|*.*",
            Title  = "選擇音訊或視訊檔案"
        };
        if (dlg.ShowDialog() == true)
            FilePathBox.Text = dlg.FileName;
    }

    private void BrowseOutputDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "選擇輸出資料夾" };
        if (dlg.ShowDialog() == true)
        {
            OutputDirBox.Text       = dlg.FolderName;
            OutputDirBox.Foreground = Brushes.Black;
        }
    }

    private void ClearOutputDir_Click(object sender, RoutedEventArgs e)
    {
        OutputDirBox.Text       = OutputDirPlaceholder;
        OutputDirBox.Foreground = Brushes.Gray;
    }

    // ── Translation toggle ────────────────────────────────────────────────────

    private void TranslateCheck_Changed(object sender, RoutedEventArgs e)
    {
        bool on = TranslateCheck.IsChecked == true;
        TranslateLangCombo.IsEnabled      = on;
        TranslateBackendCombo.IsEnabled   = on;
        if (TranslatePromptBox is not null) TranslatePromptBox.IsEnabled = on;
        UpdateApiKeyVisibility();
    }

    private void TranslateBackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateApiKeyVisibility();

    private void UpdateApiKeyVisibility()
    {
        if (TranslateCheck is null || ApiKeyLabel is null || ClaudeApiKeyBox is null) return;
        bool on = TranslateCheck.IsChecked == true;
        string backend = ((ComboBoxItem)TranslateBackendCombo.SelectedItem).Tag?.ToString() ?? "";
        bool needKey = on && backend != "googletrans" && backend != "claude-cli";
        var vis = needKey ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyLabel.Content        = backend == "gemini-flash" ? "Gemini Key" : "API Key";
        ApiKeyLabel.Visibility     = vis;
        ClaudeApiKeyBox.Visibility = vis;
    }

    // ── Settings persistence ──────────────────────────────────────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        OutputDirBox.Text       = OutputDirPlaceholder;
        OutputDirBox.Foreground = Brushes.Gray;
        LoadSettings();

        string pythonExe = Path.Combine(RepoRoot, "venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe))
        {
            var setup = new SetupWindow(RepoRoot) { Owner = this };
            setup.ShowDialog();
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        => SaveSettings();

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            if (s is null) return;

            SelectComboByContent(ModelCombo, s.Model);
            SelectComboByTag(LanguageCombo, s.Language);
            SelectComboByTag(DeviceCombo, s.Device);
            SelectComboByTag(ComputeTypeCombo, s.ComputeType);
            PromptBox.Text        = s.InitialPrompt;
            MergeCheck.IsChecked  = s.Merge;
            VadCheck.IsChecked    = s.VadFilter;
            TranslateCheck.IsChecked = s.Translate;
            SelectComboByTag(TranslateLangCombo, s.TranslateLang);
            SelectComboByTag(TranslateBackendCombo, s.TranslateBackend);
            ClaudeApiKeyBox.Text      = s.ClaudeApiKey;
            TranslatePromptBox.Text   = s.TranslatePrompt;
            SelectComboByTag(ChannelCombo, s.Channel);

            if (!string.IsNullOrWhiteSpace(s.OutputDir))
            {
                OutputDirBox.Text       = s.OutputDir;
                OutputDirBox.Foreground = Brushes.Black;
            }
            if (!string.IsNullOrWhiteSpace(s.LastFile) && File.Exists(s.LastFile))
                FilePathBox.Text = s.LastFile;
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var s = new AppSettings
            {
                Model         = ((ComboBoxItem)ModelCombo.SelectedItem).Content.ToString()!,
                Language      = ((ComboBoxItem)LanguageCombo.SelectedItem).Tag?.ToString() ?? "",
                Device        = ((ComboBoxItem)DeviceCombo.SelectedItem).Tag?.ToString() ?? "cuda",
                ComputeType   = ((ComboBoxItem)ComputeTypeCombo.SelectedItem).Tag?.ToString() ?? "float16",
                InitialPrompt = PromptBox.Text,
                Merge         = MergeCheck.IsChecked == true,
                VadFilter     = VadCheck.IsChecked == true,
                Translate        = TranslateCheck.IsChecked == true,
                TranslateLang    = ((ComboBoxItem)TranslateLangCombo.SelectedItem).Tag?.ToString() ?? "zh-TW",
                TranslateBackend = ((ComboBoxItem)TranslateBackendCombo.SelectedItem).Tag?.ToString() ?? "googletrans",
                ClaudeApiKey     = ClaudeApiKeyBox.Text.Trim(),
                TranslatePrompt  = TranslatePromptBox.Text.Trim(),
                Channel          = ((ComboBoxItem)ChannelCombo.SelectedItem).Tag?.ToString() ?? "mix",
                OutputDir     = OutputDirBox.Text == OutputDirPlaceholder ? "" : OutputDirBox.Text,
                LastFile      = FilePathBox.Text,
            };
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void SelectComboByContent(ComboBox cb, string content)
    {
        foreach (ComboBoxItem item in cb.Items)
            if (item.Content?.ToString() == content) { cb.SelectedItem = item; return; }
    }

    private static void SelectComboByTag(ComboBox cb, string tag)
    {
        foreach (ComboBoxItem item in cb.Items)
            if (item.Tag?.ToString() == tag) { cb.SelectedItem = item; return; }
    }

    // ── Device → auto-switch compute type ────────────────────────────────────

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MergeCheck is null) return;
        string lang = ((ComboBoxItem)LanguageCombo.SelectedItem).Tag?.ToString() ?? "";
        bool isJa = lang == "ja";
        if (!isJa)
        {
            MergeCheck.IsChecked = false;
            MergeCheck.IsEnabled = false;
            MergeCheck.ToolTip   = "語意合併僅支援日文";
        }
        else
        {
            MergeCheck.IsEnabled = true;
            MergeCheck.ToolTip   = "語意合併（僅日文有效）";
        }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComputeTypeCombo is null) return;
        bool isCpu = ((ComboBoxItem)DeviceCombo.SelectedItem).Tag?.ToString() == "cpu";
        if (isCpu) SelectComboByTag(ComputeTypeCombo, "int8");
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComputeTypeCombo is null) return;
        bool isDistil = ((ComboBoxItem)ModelCombo.SelectedItem).Content?.ToString() == "distil-large-v3";
        foreach (ComboBoxItem item in ComputeTypeCombo.Items)
            item.IsEnabled = !(isDistil && item.Tag?.ToString() == "float32");
        if (isDistil && ((ComboBoxItem)ComputeTypeCombo.SelectedItem).Tag?.ToString() == "float32")
            SelectComboByTag(ComputeTypeCombo, "float16");
    }

    // ── Transcription ─────────────────────────────────────────────────────────

    private async void StartTranscription_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FilePathBox.Text))
        {
            MessageBox.Show("請先選擇音訊或視訊檔案。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string model        = ((ComboBoxItem)ModelCombo.SelectedItem).Content.ToString()!;
        string language     = ((ComboBoxItem)LanguageCombo.SelectedItem).Tag?.ToString() ?? "";
        string device       = ((ComboBoxItem)DeviceCombo.SelectedItem).Tag?.ToString() ?? "cuda";
        string computeType  = ((ComboBoxItem)ComputeTypeCombo.SelectedItem).Tag?.ToString() ?? "float16";
        string prompt       = PromptBox.Text.Trim();
        bool   merge        = MergeCheck.IsChecked == true;
        bool   vad          = VadCheck.IsChecked == true;
        const int beamSize  = 5;
        bool   translate     = TranslateCheck.IsChecked == true;
        string translateLang = ((ComboBoxItem)TranslateLangCombo.SelectedItem).Tag?.ToString() ?? "zh-TW";
        string translateBackend = ((ComboBoxItem)TranslateBackendCombo.SelectedItem).Tag?.ToString() ?? "googletrans";
        string claudeApiKey     = ClaudeApiKeyBox.Text.Trim();
        string translatePrompt  = TranslatePromptBox.Text.Trim();
        string channel          = ((ComboBoxItem)ChannelCombo.SelectedItem).Tag?.ToString() ?? "mix";

        bool needsKey = translate && translateBackend != "googletrans" && translateBackend != "claude-cli";
        if (needsKey && string.IsNullOrWhiteSpace(claudeApiKey))
        {
            string providerName = translateBackend == "gemini-flash" ? "Gemini" : "Claude";
            MessageBox.Show($"使用 {providerName} 翻譯需要填入 API Key。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetRunning(false);
            return;
        }
        string audioFile    = FilePathBox.Text;

        string outputDir = OutputDirBox.Text == OutputDirPlaceholder
            ? Path.GetDirectoryName(audioFile)!
            : OutputDirBox.Text;
        string outputFile = Path.Combine(outputDir,
            Path.GetFileNameWithoutExtension(audioFile) + ".srt");

        SetRunning(true);
        LogBox.Clear();
        OpenSrtButton.Visibility = Visibility.Collapsed;
        PlayButton.Visibility    = Visibility.Collapsed;
        ProgressBar.Value        = 0;
        StatusText.Text          = "正在啟動...";
        StatusText.Foreground    = Brushes.Gray;
        _lastSrtPath             = null;
        _reviewSrtPath           = null;
        _cts = new CancellationTokenSource();

        try
        {
            await RunTranscription(audioFile, outputFile, model, language, device,
                computeType, prompt, merge, vad, beamSize,
                translate, translateLang, translateBackend, claudeApiKey,
                translatePrompt, channel, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text       = "已取消";
            StatusText.Foreground = Brushes.Gray;
            AppendLog("[已取消]");
        }
        catch (Exception ex)
        {
            StatusText.Text       = "錯誤：" + ex.Message;
            StatusText.Foreground = Brushes.Red;
            AppendLog($"[錯誤] {ex}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void StopTranscription_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        try { _process?.Kill(entireProcessTree: true); } catch { }
    }

    private static readonly string[] _subtitleEditPaths =
    [
        @"C:\Program Files\Subtitle Edit\SubtitleEdit.exe",
        @"C:\Program Files (x86)\Subtitle Edit\SubtitleEdit.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Programs\Subtitle Edit\SubtitleEdit.exe"),
    ];

    private void OpenSrtButton_Click(object sender, RoutedEventArgs e)
    {
        string? target = _reviewSrtPath ?? _lastSrtPath;
        if (target is null || !File.Exists(target)) return;
        string? se = Array.Find(_subtitleEditPaths, File.Exists);
        if (se is not null)
            Process.Start(new ProcessStartInfo(se) { Arguments = $"\"{target}\"", UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        string audioFile = FilePathBox.Text;
        if (!string.IsNullOrWhiteSpace(audioFile) && File.Exists(audioFile))
            Process.Start(new ProcessStartInfo(audioFile) { UseShellExecute = true });
    }

    private async Task RunTranscription(string audioFile, string outputFile,
        string model, string language, string device, string computeType,
        string prompt, bool merge, bool vad, int beamSize,
        bool translate, string translateLang, string translateBackend, string claudeApiKey,
        string translatePrompt, string channel, CancellationToken ct)
    {
        string pythonExe  = Path.Combine(RepoRoot, "venv", "Scripts", "python.exe");
        string scriptPath = Path.Combine(RepoRoot, "WhisperTranscriber.py");

        if (!File.Exists(pythonExe))
        {
            AppendLog($"[錯誤] 找不到 Python 虛擬環境：{pythonExe}");
            StatusText.Text       = "錯誤：找不到 venv/Scripts/python.exe";
            StatusText.Foreground = Brushes.Red;
            return;
        }

        var sb = new StringBuilder($"\"{scriptPath}\"");
        sb.Append($" --model {model}");
        sb.Append($" --file \"{audioFile}\"");
        sb.Append($" --output \"{outputFile}\"");
        sb.Append($" --device {device}");
        sb.Append($" --compute-type {computeType}");
        sb.Append($" --beam-size {beamSize}");
        if (!string.IsNullOrEmpty(language))
            sb.Append($" --language {language}");
        if (!string.IsNullOrWhiteSpace(prompt))
            sb.Append($" --initial-prompt \"{prompt.Replace("\"", "\\\"")}\"");
        if (channel != "mix") sb.Append($" --channel {channel}");
        if (merge)     sb.Append(" --merge");
        if (vad)       sb.Append(" --vad-filter");
        if (translate)
        {
            sb.Append(" --translate");
            sb.Append($" --translate-lang {translateLang}");
            sb.Append($" --translate-backend {translateBackend}");
            if (!string.IsNullOrWhiteSpace(claudeApiKey))
            {
                if (translateBackend == "gemini-flash")
                    sb.Append($" --gemini-api-key \"{claudeApiKey}\"");
                else
                    sb.Append($" --claude-api-key \"{claudeApiKey}\"");
            }
            if (!string.IsNullOrWhiteSpace(translatePrompt))
                sb.Append($" --translate-prompt \"{translatePrompt.Replace("\"", "\\\"")}\"");
        }

        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            Arguments              = sb.ToString(),
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            WorkingDirectory       = RepoRoot,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.Exited += (_, _) => exitTcs.TrySetResult(_process.ExitCode);

        _process.Start();

        string translateNote = translate ? $"  翻譯:{translateLang}" : "";
        AppendLog($"[啟動] {Path.GetFileName(audioFile)}  模型:{model}  語言:{(string.IsNullOrEmpty(language) ? "auto" : language)}  Beam:{beamSize}{(vad ? "  VAD:on" : "")}{translateNote}");

        var stdoutTask = ConsumeStreamAsync(_process.StandardOutput, isStdout: true);
        var stderrTask = ConsumeStreamAsync(_process.StandardError, isStdout: false);

        using var reg = ct.Register(() =>
        {
            try { _process?.Kill(entireProcessTree: true); } catch { }
            exitTcs.TrySetCanceled();
        });

        int exitCode = await exitTcs.Task;
        await Task.WhenAll(stdoutTask, stderrTask);
        _process.Dispose();
        _process = null;

        if (exitCode == 0)
        {
            ProgressBar.Value     = 100;
            StatusText.Foreground = Brushes.DarkGreen;
            if (_lastSrtPath is null) _lastSrtPath = outputFile;
            StatusText.Text          = $"完成！→ {_lastSrtPath}";
            AppendLog($"[完成] SRT 檔案：{_lastSrtPath}");
            OpenSrtButton.Content    = "Subtitle Edit";
            OpenSrtButton.Visibility = Visibility.Visible;
            PlayButton.Visibility    = Visibility.Visible;
        }
        else
        {
            StatusText.Text       = $"轉錄失敗（退出碼 {exitCode}）";
            StatusText.Foreground = Brushes.Red;
        }
    }

    private async Task ConsumeStreamAsync(StreamReader reader, bool isStdout)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line is null) break;

            if (isStdout && line.StartsWith("MODEL_LOADING:"))
            {
                string modelName = line[14..];
                Dispatcher.Invoke(() =>
                {
                    _loadingStart = DateTime.Now;
                    _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _loadingTimer.Tick += (_, _) =>
                    {
                        int elapsed = (int)(DateTime.Now - _loadingStart).TotalSeconds;
                        string hint = elapsed > 30 ? "  （首次執行需下載模型約 3 GB，請耐心等候）" : "";
                        StatusText.Text      = $"載入 {modelName} 模型中… {elapsed} 秒{hint}";
                        StatusText.Foreground = Brushes.Gray;
                    };
                    _loadingTimer.Start();
                });
            }
            else if (isStdout && line.StartsWith("MODEL_LOADED:"))
            {
                Dispatcher.Invoke(() =>
                {
                    _loadingTimer?.Stop();
                    _loadingTimer = null;
                    StatusText.Text      = "模型載入完成，開始辨識...";
                    StatusText.Foreground = Brushes.Gray;
                });
            }
            else if (isStdout && line.StartsWith("PROGRESS:"))
            {
                var parts = line[9..].Split('/');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double cur) &&
                    double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double total) &&
                    total > 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ProgressBar.Value     = cur / total * 100;
                        StatusText.Foreground = Brushes.Gray;
                        StatusText.Text       = $"轉錄中… {FormatDuration(cur)} / {FormatDuration(total)}";
                    });
                }
            }
            else if (isStdout && line.StartsWith("CHANNEL:"))
            {
                string ch = line[8..] == "left" ? "左聲道" : "右聲道";
                Dispatcher.Invoke(() =>
                {
                    ProgressBar.Value = 0;
                    StatusText.Text   = $"轉錄 {ch}...";
                    StatusText.Foreground = Brushes.Gray;
                    AppendLog($"[聲道] 開始處理 {ch}");
                });
            }
            else if (isStdout && line.StartsWith("SRT_REVIEW:"))
            {
                string path = line[11..];
                Dispatcher.Invoke(() =>
                {
                    _reviewSrtPath = path;
                    AppendLog($"[校對檔] {path}");
                });
            }
            else if (isStdout && line.StartsWith("SRT:"))
            {
                string path = line[4..];
                Dispatcher.Invoke(() =>
                {
                    _lastSrtPath = path;
                    AppendLog($"[輸出] {path}");
                });
            }
            else if (isStdout && line.StartsWith("TRANSLATE:"))
            {
                var parts = line[10..].Split('/');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int cur) &&
                    int.TryParse(parts[1], out int total) &&
                    total > 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ProgressBar.Value     = (double)cur / total * 100;
                        StatusText.Foreground = Brushes.Gray;
                        StatusText.Text       = $"翻譯中… {cur} / {total} 段";
                    });
                }
            }
            else
            {
                Dispatcher.Invoke(() => AppendLog(line));
            }
        }
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private void AppendLog(string text)
    {
        LogBox.AppendText(text + "\n");
        LogBox.ScrollToEnd();
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled  = !running;
        StartButton.Content    = running ? "轉錄中..." : "開始轉錄";
        StopButton.IsEnabled   = running;
        FilePathBox.IsEnabled  = !running;
        if (running) StatusText.Foreground = Brushes.Gray;
    }
}

file sealed class AppSettings
{
    public string Model         { get; set; } = "large-v3";
    public string Language      { get; set; } = "ja";
    public string Device        { get; set; } = "cuda";
    public string ComputeType   { get; set; } = "float16";
    public string InitialPrompt { get; set; } = "以下是日文的ASMR";
    public bool   Merge         { get; set; } = false;
    public bool   VadFilter     { get; set; } = false;
    public bool   Translate     { get; set; } = false;
    public string TranslateLang    { get; set; } = "zh-TW";
    public string TranslateBackend { get; set; } = "googletrans";
    public string ClaudeApiKey     { get; set; } = "";
    public string TranslatePrompt  { get; set; } = "日文 ASMR 字幕，保持自然、輕柔、親密的口語語氣，符合 ASMR 風格";
    public string Channel          { get; set; } = "mix";
    public string OutputDir     { get; set; } = "";
    public string LastFile      { get; set; } = "";
}
