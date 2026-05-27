using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace WhisperGUI;

public partial class SetupWindow : Window
{
    private readonly string _repoRoot;

    public SetupWindow(string repoRoot)
    {
        InitializeComponent();
        _repoRoot = repoRoot;
        Loaded += async (_, _) => await RunSetupAsync();
    }

    private async Task RunSetupAsync()
    {
        string setupBat = Path.Combine(_repoRoot, "setup.bat");
        if (!File.Exists(setupBat))
        {
            AppendLog($"[錯誤] 找不到 setup.bat：{setupBat}");
            Finish(success: false);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            Arguments              = $"/c \"{setupBat}\" /nopause",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            WorkingDirectory       = _repoRoot,
        };

        var process = new Process { StartInfo = psi };
        process.Start();

        await Task.WhenAll(
            ReadStreamAsync(process.StandardOutput),
            ReadStreamAsync(process.StandardError));
        await process.WaitForExitAsync();

        Finish(success: process.ExitCode == 0);
    }

    private async Task ReadStreamAsync(StreamReader reader)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync();
            if (line is null) break;
            Dispatcher.Invoke(() => AppendLog(line));
        }
    }

    private void AppendLog(string text)
    {
        SetupLog.AppendText(text + "\n");
        SetupLog.ScrollToEnd();
    }

    private void Finish(bool success)
    {
        Dispatcher.Invoke(() =>
        {
            SetupProgress.IsIndeterminate = false;
            SetupProgress.Value           = 100;
            CloseButton.IsEnabled         = true;
            if (success)
            {
                AppendLog("\n安裝完成！");
                CloseButton.Content = "完成，開始使用";
            }
            else
            {
                AppendLog("\n安裝失敗，請查看上方錯誤訊息。");
                CloseButton.Content = "關閉";
            }
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
