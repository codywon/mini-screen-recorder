using System.Diagnostics;
using MiniRec.Services;
using Wpf = System.Windows;
using Input = System.Windows.Input;
using Media = System.Windows.Media;

namespace MiniRec.Views;

public partial class AboutWindow : Wpf.Window
{
    private const string RepoUrl = "https://github.com/codywon/mini-screen-recorder";

    private readonly Func<Task<UpdateCheckResult>>? _checkUpdateAsync;
    private readonly Action<UpdateReleaseInfo>? _onUpdateAvailable;
    private bool _isChecking;

    internal AboutWindow(
        Func<Task<UpdateCheckResult>>? checkUpdateAsync = null,
        Action<UpdateReleaseInfo>? onUpdateAvailable = null)
    {
        InitializeComponent();

        _checkUpdateAsync = checkUpdateAsync;
        _onUpdateAvailable = onUpdateAvailable;
        TitleVersionText.Text = $"MiniRec v{UpdateService.GetCurrentVersionString()}";
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
        if (e.Key == Input.Key.Escape || e.Key == Input.Key.Enter)
        {
            e.Handled = true;
            Close();
        }
    }

    private void RepoLink_MouseLeftButtonUp(object sender, Input.MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RepoUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser launch failures.
        }
    }

    private async void CheckUpdateButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_isChecking) return;
        _isChecking = true;
        CheckUpdateBtn.IsEnabled = false;
        CheckUpdateBtn.Content = "检查中...";
        ShowStatus("正在连接 GitHub 检查最新版本...", Media.Color.FromRgb(148, 163, 184));

        try
        {
            UpdateCheckResult result = _checkUpdateAsync is not null
                ? await _checkUpdateAsync()
                : await UpdateService.CheckForUpdateAsync();

            if (!IsLoaded) return;

            switch (result.Status)
            {
                case UpdateCheckStatus.UpToDate:
                    ShowStatus($"✓ 当前 v{UpdateService.FormatVersion(result.CurrentVersion)} 已是最新版本", Media.Color.FromRgb(52, 211, 153));
                    break;

                case UpdateCheckStatus.UpdateAvailable when result.Release is not null:
                    UpdateReleaseInfo release = result.Release;
                    Close();
                    _onUpdateAvailable?.Invoke(release);
                    break;

                default:
                    ShowStatus(result.ErrorMessage ?? "检查更新失败，请稍后重试", Media.Color.FromRgb(252, 165, 165));
                    break;
            }
        }
        catch (Exception ex)
        {
            if (IsLoaded)
            {
                ShowStatus($"检查更新失败：{ex.Message}", Media.Color.FromRgb(252, 165, 165));
            }
        }
        finally
        {
            _isChecking = false;
            if (IsLoaded)
            {
                CheckUpdateBtn.IsEnabled = true;
                CheckUpdateBtn.Content = "检查更新";
            }
        }
    }

    private void ShowStatus(string text, Media.Color color)
    {
        UpdateStatusText.Text = text;
        UpdateStatusText.Foreground = new Media.SolidColorBrush(color);
        UpdateStatusText.Visibility = Wpf.Visibility.Visible;
    }

    private void CloseButton_Click(object sender, Wpf.RoutedEventArgs e) => Close();
}
