using System.Windows;

namespace AorinEQ.UI;

public enum SkinInstallChoice { InstallAndUse, InstallOnly, Cancel }

/// <summary>The aorineq:// trust boundary: one modal confirmation per link, shown BEFORE any
/// download starts. Owned by no window (a link can arrive with nothing else open), centered,
/// topmost so it can't get lost behind the browser that fired the link. Worst case from a
/// hostile site is this dialog appearing — nothing installs without a click.</summary>
public partial class SkinInstallDialog : Wpf.Ui.Controls.FluentWindow
{
    private SkinInstallChoice _choice = SkinInstallChoice.Cancel; // X / Esc are a Cancel

    private SkinInstallDialog(string skinName, string host, bool overwrites)
    {
        InitializeComponent();
        HeadingText.Text = $"Install skin '{skinName}' from {host}?";
        OverwriteText.Visibility = overwrites ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the confirmation modally and returns the user's choice.</summary>
    public static SkinInstallChoice Confirm(string skinName, string host, bool overwrites)
    {
        var dialog = new SkinInstallDialog(skinName, host, overwrites);
        dialog.ShowDialog();
        return dialog._choice;
    }

    private void OnInstallUse(object sender, RoutedEventArgs e)
    {
        _choice = SkinInstallChoice.InstallAndUse;
        Close();
    }

    private void OnInstallOnly(object sender, RoutedEventArgs e)
    {
        _choice = SkinInstallChoice.InstallOnly;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
