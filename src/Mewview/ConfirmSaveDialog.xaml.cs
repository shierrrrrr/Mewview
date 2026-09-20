using System.Windows;

namespace Mewview;

public enum SaveConfirmChoice
{
    Overwrite,
    SaveAs,
}

/// <summary>
/// Shown only for the toolbar Save button: asks whether to overwrite the
/// original file. The Ctrl+S shortcut bypasses this dialog entirely.
/// Cancel = DialogResult false (Esc or 取消).
/// </summary>
public partial class ConfirmSaveDialog : Window
{
    public SaveConfirmChoice Choice { get; private set; } = SaveConfirmChoice.Overwrite;

    public ConfirmSaveDialog()
    {
        InitializeComponent();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Choice = SaveConfirmChoice.Overwrite;
        DialogResult = true;
    }

    private void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        Choice = SaveConfirmChoice.SaveAs;
        DialogResult = true;
    }
}
