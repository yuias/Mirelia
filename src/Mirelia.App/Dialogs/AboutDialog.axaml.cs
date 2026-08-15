using Avalonia.Controls;
using Avalonia.Interactivity;
using Mirelia.Library;

namespace Mirelia.App.Dialogs;

/// <summary>Shows the product name, version, and the commit this build came from.</summary>
public partial class AboutDialog : Window
{
    /// <summary>Creates the dialog.</summary>
    public AboutDialog()
    {
        InitializeComponent();

        NameText.Text = AppInfo.Name;
        VersionText.Text = $"Version {AppInfo.Version}";
        if (AppInfo.GitHash is { } hash)
        {
            GitHashText.Text = hash;
            GitHashText.IsVisible = true;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
