using MiniRec.Services;
using Wpf = System.Windows;
using Input = System.Windows.Input;

namespace MiniRec.Views;

public partial class CloseConfirmWindow : Wpf.Window
{
    internal CloseButtonBehavior ChosenBehavior { get; private set; } = CloseButtonBehavior.Ask;
    internal bool RememberChoice => RememberCheckBox.IsChecked == true;

    public CloseConfirmWindow()
    {
        InitializeComponent();
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
            DialogResult = false;
            Close();
        }
    }

    private void MinimizeButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        ChosenBehavior = CloseButtonBehavior.MinimizeToTray;
        DialogResult = true;
        Close();
    }

    private void ExitButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        ChosenBehavior = CloseButtonBehavior.Exit;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, Wpf.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
