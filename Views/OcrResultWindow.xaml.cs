using System.Windows.Controls.Primitives;
using MiniRec.Services;
using Wpf = System.Windows;
using Input = System.Windows.Input;

namespace MiniRec.Views;

public partial class OcrResultWindow : Wpf.Window
{
    private string _rawText = string.Empty;
    private bool _linesJoined;

    internal OcrResultWindow(OcrDocumentResult result, Wpf.Rect? anchorDip = null)
    {
        InitializeComponent();

        if (anchorDip is { Width: > 0 } anchor)
        {
            Wpf.Rect work = Wpf.SystemParameters.WorkArea;
            double left = anchor.Right + 8;
            if (left + Width > work.Right)
            {
                left = Math.Max(work.Left, anchor.Left - Width - 8);
            }
            double top = Math.Clamp(anchor.Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = Wpf.WindowStartupLocation.CenterScreen;
        }

        if (result.Success)
        {
            _rawText = string.IsNullOrWhiteSpace(result.FullText) ? string.Empty : result.FullText;
            TitleInfoText.Text = result.Lines.Count > 0
                ? $"OCR 提取文字（{result.Lines.Count} 行）"
                : "OCR 提取文字（未检测到文字）";
            ResultTextBox.Text = string.IsNullOrEmpty(_rawText) ? "未识别到清晰文字内容" : _rawText;
        }
        else
        {
            TitleInfoText.Text = "OCR 识别失败";
            ResultTextBox.Text = result.ErrorMessage ?? "未知错误";
            JoinLinesBtn.IsEnabled = false;
        }
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
            Close();
        }
    }

    private void JoinLinesBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_rawText)) return;
        _linesJoined = !_linesJoined;
        if (_linesJoined)
        {
            ResultTextBox.Text = string.Join(" ", _rawText
                .Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()));
            JoinLinesBtn.Content = "恢复换行";
        }
        else
        {
            ResultTextBox.Text = _rawText;
            JoinLinesBtn.Content = "去除换行";
        }
    }

    private void CopyAllBtn_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ResultTextBox.Text))
        {
            Wpf.Clipboard.SetText(ResultTextBox.Text);
            CopiedHintText.Text = "✓ 已复制";
        }
    }

    private void CloseButton_Click(object sender, Wpf.RoutedEventArgs e) => Close();

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, 1200);
        Height = Math.Clamp(Height + e.VerticalChange, MinHeight, 900);
    }
}
