using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Brushes = System.Windows.Media.Brushes;
using ComboBox = System.Windows.Controls.ComboBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace WhisperGUI;

public partial class MainWindow : Window
{
    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _lastSrtPath;
    private string? _reviewSrtPath;
    private string? _lastAudioFile;
    private DispatcherTimer? _loadingTimer;
    private DateTime _loadingStart;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private string? _subtitleEditExe;

    public ObservableCollection<QueueItem> QueueItems { get; } = new();

    private static readonly string RepoRoot = DetectRepoRoot();

    private static string DetectRepoRoot()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "WhisperTranscriber.py")))
            return AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\.."));
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhisperGUI", "settings.json");

    private const string OutputDirPlaceholder = "（與輸入檔案相同目錄）";

    public MainWindow()
    {
        InitializeComponent();
        QueueList.ItemsSource = QueueItems;
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon    = System.Drawing.SystemIcons.Application,
            Visible = false,
        };
    }

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
            AddFilesToQueue(files);
    }

    private void QueueList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            AddFilesToQueue(files);
    }

    private void AddFilesToQueue(string[] paths)
    {
        var existingPaths = QueueItems.Select(q => q.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (File.Exists(path) && !existingPaths.Contains(path))
            {
                QueueItems.Add(new QueueItem { FilePath = path });
                existingPaths.Add(path);
            }
        }
    }

    // ── Queue buttons ─────────────────────────────────────────────────────────

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter      = "媒體檔案|*.mp3;*.mp4;*.wav;*.m4a;*.mkv;*.avi;*.flac;*.ogg;*.ts;*.mts;*.m2ts|所有檔案|*.*",
            Title       = "選擇音訊或視訊檔案",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true)
            AddFilesToQueue(dlg.FileNames);
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        var toRemove = QueueList.SelectedItems.Cast<QueueItem>()
            .Where(q => q.Status != QueueStatus.Transcribing && q.Status != QueueStatus.Translating)
            .ToList();
        foreach (var item in toRemove)
            QueueItems.Remove(item);
    }

    private void ClearDone_Click(object sender, RoutedEventArgs e)
    {
        var toRemove = QueueItems
            .Where(q => q.Status == QueueStatus.Done || q.Status == QueueStatus.Error)
            .ToList();
        foreach (var item in toRemove)
            QueueItems.Remove(item);
    }

    // ── Browse ────────────────────────────────────────────────────────────────

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
        TranslateLangCombo.IsEnabled    = on;
        TranslateBackendCombo.IsEnabled = on;
        if (TranslatePromptBox is not null) TranslatePromptBox.IsEnabled = on;
        if (PresetAsmrButton   is not null) PresetAsmrButton.IsEnabled   = on;
        if (PresetDramaButton  is not null) PresetDramaButton.IsEnabled  = on;
        UpdateApiKeyVisibility();
    }

    private const string PromptAsmr  = "日文 ASMR 字幕，保持自然、輕柔、親密的口語語氣，符合 ASMR 風格";
    private const string PromptDrama = "日劇字幕翻譯，保留角色說話語氣和情感，敬語與普通話語氣的區別要維持，翻譯自然流暢。";

    private void PresetAsmr_Click(object sender, RoutedEventArgs e)  => TranslatePromptBox.Text = PromptAsmr;
    private void PresetDrama_Click(object sender, RoutedEventArgs e) => TranslatePromptBox.Text = PromptDrama;

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
    {
        SaveSettings();
        _notifyIcon?.Dispose();
    }

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
            PromptBox.Text           = s.InitialPrompt;
            MergeCheck.IsChecked     = s.Merge;
            VadCheck.IsChecked       = s.VadFilter;
            TranslateCheck.IsChecked = s.Translate;
            SelectComboByTag(TranslateLangCombo, s.TranslateLang);
            SelectComboByTag(TranslateBackendCombo, s.TranslateBackend);
            ClaudeApiKeyBox.Text    = s.ClaudeApiKey;
            TranslatePromptBox.Text = s.TranslatePrompt;
            SelectComboByTag(ChannelCombo, s.Channel);
            NotifyCheck.IsChecked   = s.NotifyOnComplete;
            if (!string.IsNullOrWhiteSpace(s.SubtitleEditPath))
                _subtitleEditExe = s.SubtitleEditPath;

            if (!string.IsNullOrWhiteSpace(s.OutputDir))
            {
                OutputDirBox.Text       = s.OutputDir;
                OutputDirBox.Foreground = Brushes.Black;
            }
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
                NotifyOnComplete  = NotifyCheck.IsChecked == true,
                SubtitleEditPath  = _subtitleEditExe ?? "",
                OutputDir         = OutputDirBox.Text == OutputDirPlaceholder ? "" : OutputDirBox.Text,
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
        var pendingItems = QueueItems.Where(q => q.Status == QueueStatus.Pending).ToList();
        if (pendingItems.Count == 0)
        {
            MessageBox.Show("佇列中沒有待處理的檔案。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            return;
        }

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

        int processedCount = 0;
        bool anyError = false;

        try
        {
            bool usePipeline = translate && pendingItems.Count >= 2;

            if (!usePipeline)
            {
                foreach (var item in pendingItems)
                {
                    if (_cts!.Token.IsCancellationRequested) { item.Status = QueueStatus.Pending; break; }
                    item.Status = QueueStatus.Transcribing;

                    string audioFile = item.FilePath;
                    string outputFile = DeriveOutputFile(audioFile);

                    try
                    {
                        await RunTranscription(audioFile, outputFile, model, language, device,
                            computeType, prompt, merge, vad, beamSize,
                            translate, translateLang, translateBackend, claudeApiKey,
                            translatePrompt, channel, _cts.Token);
                        item.Status = QueueStatus.Done;
                        processedCount++;
                        _lastAudioFile = audioFile;
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = QueueStatus.Pending;
                        StatusText.Text       = "已取消";
                        StatusText.Foreground = Brushes.Gray;
                        AppendLog("[已取消]");
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.Status = QueueStatus.Error;
                        anyError = true;
                        AppendLog($"[錯誤] {item.FileName}: {ex.Message}");
                    }
                }
            }
            else
            {
                // Pipeline: translation of file N overlaps with transcription of file N+1
                Task? translateTask = null;

                async Task FlushTranslation()
                {
                    if (translateTask != null)
                    {
                        try { await translateTask; } catch { }
                        translateTask = null;
                    }
                }

                foreach (var item in pendingItems)
                {
                    if (_cts!.Token.IsCancellationRequested)
                    {
                        item.Status = QueueStatus.Pending;
                        await FlushTranslation();
                        break;
                    }

                    string audioFile  = item.FilePath;
                    string outputFile = DeriveOutputFile(audioFile);
                    string segJson    = Path.Combine(Path.GetTempPath(), $"whisper_{Guid.NewGuid():N}.json");

                    item.Status = QueueStatus.Transcribing;
                    try
                    {
                        await RunTranscriptionOnly(audioFile, outputFile, segJson,
                            model, language, device, computeType, prompt, merge, vad, beamSize, channel,
                            _cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = QueueStatus.Pending;
                        if (File.Exists(segJson)) File.Delete(segJson);
                        await FlushTranslation();
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.Status = QueueStatus.Error;
                        anyError = true;
                        AppendLog($"[錯誤] {item.FileName}: {ex.Message}");
                        if (File.Exists(segJson)) File.Delete(segJson);
                        await FlushTranslation();
                        continue;
                    }

                    // Wait for the previous translation to finish before starting a new one
                    await FlushTranslation();

                    // Start translation of this item (runs while next item is being transcribed)
                    item.Status = QueueStatus.Translating;
                    var capturedItem   = item;
                    var capturedJson   = segJson;
                    var capturedOutput = outputFile;
                    var capturedAudio  = audioFile;
                    translateTask = RunTranslationOnly(
                        capturedAudio, capturedOutput, capturedJson,
                        translateLang, translateBackend, claudeApiKey, translatePrompt,
                        _cts.Token)
                        .ContinueWith(t =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                capturedItem.Status = (t.IsFaulted || t.IsCanceled)
                                    ? QueueStatus.Error
                                    : QueueStatus.Done;
                                if (t.IsFaulted)
                                    AppendLog($"[翻譯錯誤] {capturedItem.FileName}: {t.Exception?.InnerException?.Message}");
                                else
                                {
                                    processedCount++;
                                    _lastAudioFile = capturedAudio;
                                }
                            });
                            if (File.Exists(capturedJson)) File.Delete(capturedJson);
                        }, TaskScheduler.Default);
                }

                // Wait for the last translation to complete
                await FlushTranslation();
            }

            if (processedCount > 0)
            {
                if (!anyError)
                {
                    StatusText.Text       = $"完成！共處理 {processedCount} 個檔案";
                    StatusText.Foreground = Brushes.DarkGreen;
                }
                else
                {
                    StatusText.Text       = $"完成（部分錯誤）：{processedCount} 個成功";
                    StatusText.Foreground = Brushes.DarkOrange;
                }

                if (NotifyCheck.IsChecked == true && _notifyIcon is not null)
                {
                    _notifyIcon.Visible = true;
                    _notifyIcon.ShowBalloonTip(4000, "Whisper 轉錄完成",
                        $"已完成 {processedCount} 個檔案", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
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

        string? se = _subtitleEditExe is not null && File.Exists(_subtitleEditExe)
            ? _subtitleEditExe
            : Array.Find(_subtitleEditPaths, File.Exists);

        if (se is null)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "找不到 Subtitle Edit，請手動選擇執行檔",
                Filter = "Subtitle Edit|SubtitleEdit.exe|執行檔|*.exe",
            };
            if (dlg.ShowDialog() != true) return;
            se = dlg.FileName;
            _subtitleEditExe = se;
            SaveSettings();
        }

        Process.Start(new ProcessStartInfo(se) { Arguments = $"\"{target}\"", UseShellExecute = false });
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastAudioFile) && File.Exists(_lastAudioFile))
            Process.Start(new ProcessStartInfo(_lastAudioFile) { UseShellExecute = true });
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
            throw new Exception($"Python 退出碼 {exitCode}");
        }
    }

    private string DeriveOutputFile(string audioFile)
    {
        string outputDir = OutputDirBox.Text == OutputDirPlaceholder
            ? Path.GetDirectoryName(audioFile)!
            : OutputDirBox.Text;
        return Path.Combine(outputDir, Path.GetFileNameWithoutExtension(audioFile) + ".srt");
    }

    private async Task RunTranscriptionOnly(string audioFile, string outputFile, string segmentsJsonPath,
        string model, string language, string device, string computeType,
        string prompt, bool merge, bool vad, int beamSize, string channel, CancellationToken ct)
    {
        string pythonExe  = Path.Combine(RepoRoot, "venv", "Scripts", "python.exe");
        string scriptPath = Path.Combine(RepoRoot, "WhisperTranscriber.py");

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
        if (merge) sb.Append(" --merge");
        if (vad)   sb.Append(" --vad-filter");
        sb.Append($" --segments-out \"{segmentsJsonPath}\"");

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

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process = proc;
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => exitTcs.TrySetResult(proc.ExitCode);
        proc.Start();

        AppendLog($"[轉錄] {Path.GetFileName(audioFile)}  模型:{model}");

        var stdoutTask = ConsumeStreamAsync(proc.StandardOutput, isStdout: true);
        var stderrTask = ConsumeStreamAsync(proc.StandardError, isStdout: false);

        using var reg = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            exitTcs.TrySetCanceled();
        });

        int exitCode = await exitTcs.Task;
        await Task.WhenAll(stdoutTask, stderrTask);
        proc.Dispose();
        _process = null;

        if (exitCode != 0)
            throw new Exception($"Python 退出碼 {exitCode}");
    }

    private async Task RunTranslationOnly(string audioFile, string outputFile, string segmentsJsonPath,
        string translateLang, string translateBackend, string claudeApiKey,
        string translatePrompt, CancellationToken ct)
    {
        string pythonExe  = Path.Combine(RepoRoot, "venv", "Scripts", "python.exe");
        string scriptPath = Path.Combine(RepoRoot, "WhisperTranscriber.py");

        var sb = new StringBuilder($"\"{scriptPath}\"");
        sb.Append($" --file \"{audioFile}\"");
        sb.Append($" --output \"{outputFile}\"");
        sb.Append($" --segments-in \"{segmentsJsonPath}\"");
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

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => exitTcs.TrySetResult(proc.ExitCode);
        proc.Start();

        AppendLog($"[翻譯] {Path.GetFileName(audioFile)}  後端:{translateBackend}");

        var stdoutTask = ConsumeStreamAsync(proc.StandardOutput, isStdout: true);
        var stderrTask = ConsumeStreamAsync(proc.StandardError, isStdout: false);

        using var reg = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            exitTcs.TrySetCanceled();
        });

        int exitCode = await exitTcs.Task;
        await Task.WhenAll(stdoutTask, stderrTask);
        proc.Dispose();

        if (exitCode != 0)
            throw new Exception($"Python 翻譯退出碼 {exitCode}");
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
                        StatusText.Text       = $"載入 {modelName} 模型中… {elapsed} 秒{hint}";
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
                    StatusText.Text       = "模型載入完成，開始辨識...";
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
        StartButton.IsEnabled      = !running;
        StartButton.Content        = running ? "轉錄中..." : "開始轉錄";
        StopButton.IsEnabled       = running;
        AddFilesButton.IsEnabled   = !running;
        RemoveFileButton.IsEnabled = !running;
        ClearDoneButton.IsEnabled  = !running;
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
    public bool   NotifyOnComplete  { get; set; } = false;
    public string SubtitleEditPath  { get; set; } = "";
    public string OutputDir         { get; set; } = "";
}

public enum QueueStatus { Pending, Transcribing, Translating, Done, Error }

public class QueueItem : System.ComponentModel.INotifyPropertyChanged
{
    public string FilePath { get; init; } = "";
    public string FileName => Path.GetFileName(FilePath);

    private QueueStatus _status = QueueStatus.Pending;
    public QueueStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusBrush)); }
    }

    public string StatusText => Status switch
    {
        QueueStatus.Pending      => "等待中",
        QueueStatus.Transcribing => "轉錄中",
        QueueStatus.Translating  => "翻譯中",
        QueueStatus.Done         => "完成",
        QueueStatus.Error        => "錯誤",
        _                        => ""
    };

    public System.Windows.Media.Brush StatusBrush => Status switch
    {
        QueueStatus.Done         => System.Windows.Media.Brushes.DarkGreen,
        QueueStatus.Error        => System.Windows.Media.Brushes.Red,
        QueueStatus.Transcribing => System.Windows.Media.Brushes.DodgerBlue,
        QueueStatus.Translating  => System.Windows.Media.Brushes.DodgerBlue,
        _                        => System.Windows.Media.Brushes.Gray
    };

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
