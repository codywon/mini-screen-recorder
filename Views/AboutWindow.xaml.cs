using System.Diagnostics;
using System.Reflection;
using Wpf = System.Windows;
using Input = System.Windows.Input;

namespace MiniRec.Views;

public partial class AboutWindow : Wpf.Window
{
    private const string RepoUrl = "https://github.com/codywon/mini-screen-recorder";

    public AboutWindow()
    {
        InitializeComponent();

        Version? ver = Assembly.GetExecutingAssembly().GetName().Version;
        string verText = ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
        TitleVersionText.Text = $"MiniRec v{verText}";
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

    private void CloseButton_Click(object sender, Wpf.RoutedEventArgs e) => Close();
}
