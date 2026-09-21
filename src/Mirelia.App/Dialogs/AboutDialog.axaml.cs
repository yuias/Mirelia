using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Mirelia.Library;

namespace Mirelia.App.Dialogs;

/// <summary>
/// Shows the product name, version, the commit this build came from, the
/// third-party notices the distribution has to carry, and where the log is.
/// </summary>
public partial class AboutDialog : Window
{
    private static readonly Uri NoticesUri =
        new("avares://Mirelia.App/Assets/THIRD-PARTY-NOTICES.md");

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

        NoticesText.Text = ReadNotices();
        LogPathText.Text = AppLog.Path ?? "not available";
    }

    private static string ReadNotices()
    {
        try
        {
            using Stream stream = AssetLoader.Open(NoticesUri);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            // The dialog is not worth failing over a missing resource.
            return "The third-party notices could not be loaded from this build.";
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
