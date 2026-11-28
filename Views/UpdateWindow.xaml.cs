using MiniRec.Services;
using Wpf = System.Windows;
using Input = System.Windows.Input;

namespace MiniRec.Views;

public partial class UpdateWindow : Wpf.Window
{
    private readonly UpdateReleaseInfo _release;
    private readonly Func<string?> _checkBusyBeforeUpdate;
    private readonly Action<string> _onSkipVersion;
    private readonly Action _onExitForUpdate;

    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading;
    private string? _downloadedFilePath;

    internal UpdateWindow(
        Version currentVersion,
        UpdateReleaseInfo release,
        Func<string?> checkBusyBeforeUpdate,
        Action<string> onSkipVersion,
        Action onExitForUpdate)
    {
        InitializeComponent();

        _release = release;
        _checkBusyBeforeUpdate = checkBusyBeforeUpdate;
        _onSkipVersion = onSkipVersion;
        _onExitForUpdate = onExitForUpdate;

        CurrentVersionText.Text = $"v{UpdateService.FormatVersion(currentVersion)}";
        RemoteVersionText.Text = $"v{UpdateService.FormatVersion(release.Version)}";
        if (release.PublishedAt is { } dt)
        {
            PublishedDateText.Text = dt.ToLocalTime().ToString("yyyy-MM-dd");
        }

        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "常规功能优化与体验改进，建议更新以获得最佳体验。"
            : release.Notes.Trim();
    }

    private void Header_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        if (e.Key == Input.Key.Escape)
        {
            e.Handled = true;
            CancelDownloadAndClose();
        }
    }

    private void SkipVersionButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_isDownloading) return;
        _onSkipVersion(UpdateService.FormatVersion(_release.Version));
        Close();
    }

    private void OpenReleasePageButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        UpdateService.OpenUrl(_release.ReleaseUrl);
    }

    private void OpenDownloadedFolderBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_downloadedFilePath))
        {
            UpdateService.RevealFileInExplorer(_downloadedFilePath);
        }
    }

    private void LaterButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        CancelDownloadAndClose();
    }

    private void CancelDownloadAndClose()
    {
        _downloadCts?.Cancel();
        Close();
    }

    private async void UpdateNowButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            _downloadCts?.Cancel();
            return;
        }

        string? busyMessage = _checkBusyBeforeUpdate();
        if (!string.IsNullOrWhiteSpace(busyMessage))
        {
            ShowAlert(busyMessage, showDownloadedFolder: false);
            return;
        }

        HideAlert();
        _isDownloading = true;
        _downloadCts = new CancellationTokenSource();

        SkipVersionBtn.IsEnabled = false;
        LaterBtn.IsEnabled = false;
        UpdateNowBtn.Content = "取消下载";
        ProgressPanel.Visibility = Wpf.Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = true;
        ProgressStageText.Text = "正在连接下载服务器...";
        ProgressDetailText.Text = string.Empty;

        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                ProgressStageText.Text = p.StageText;
                if (p.Percentage is { } pct && p.TotalBytes is { } total)
                {
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = pct;
                    ProgressDetailText.Text = $"{pct:0}% · {FormatSize(p.BytesReceived)} / {FormatSize(total)}";
                }
                else
                {
                    DownloadProgressBar.IsIndeterminate = true;
                    ProgressDetailText.Text = p.BytesReceived > 0 ? FormatSize(p.BytesReceived) : string.Empty;
                }
            });

            string downloadedPath = await UpdateService.DownloadUpdateAsync(_release, progress, _downloadCts.Token);
            _downloadedFilePath = downloadedPath;

            if (!UpdateService.CanSelfReplaceCurrentExecutable(out string? cannotReason))
            {
                ResetIdleButtons("重新下载");
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressBar.Value = 100;
                ProgressStageText.Text = "新版安装包已下载并校验通过";
                ShowAlert(cannotReason ?? "当前环境不支持自动替换，请手动替换。", showDownloadedFolder: true);
                return;
            }

            ProgressStageText.Text = "校验通过，正在启动更新并重启 MiniRec...";
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 100;

            string targetVerText = UpdateService.FormatVersion(_release.Version);
            if (!UpdateService.TryLaunchSelfUpdater(downloadedPath, targetVerText, out string? launchError))
            {
                ResetIdleButtons("重试更新");
                ShowAlert(launchError ?? "启动更新程序失败，请手动替换。", showDownloadedFolder: true);
                return;
            }

            Close();
            _onExitForUpdate();
        }
        catch (OperationCanceledException)
        {
            ResetIdleButtons("立即更新");
            ProgressPanel.Visibility = Wpf.Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ResetIdleButtons("重试更新");
            ProgressPanel.Visibility = Wpf.Visibility.Collapsed;
            ShowAlert($"更新失败：{ex.Message}", showDownloadedFolder: false);
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            _isDownloading = false;
        }
    }

    private void ResetIdleButtons(string primaryLabel)
    {
        _isDownloading = false;
        SkipVersionBtn.IsEnabled = true;
        LaterBtn.IsEnabled = true;
        UpdateNowBtn.Content = primaryLabel;
    }

    private void ShowAlert(string message, bool showDownloadedFolder)
    {
        AlertText.Text = message;
        OpenDownloadedFolderBtn.Visibility = showDownloadedFolder ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        AlertBanner.Visibility = Wpf.Visibility.Visible;
    }

    private void HideAlert()
    {
        AlertBanner.Visibility = Wpf.Visibility.Collapsed;
        OpenDownloadedFolderBtn.Visibility = Wpf.Visibility.Collapsed;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024.0):0.00} MB";
        }
        return $"{Math.Max(1, bytes / 1024.0):0} KB";
    }
}
