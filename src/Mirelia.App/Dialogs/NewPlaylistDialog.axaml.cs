using Avalonia.Controls;
using Avalonia.Interactivity;
using Mirelia.App.ViewModels;

namespace Mirelia.App.Dialogs;

/// <summary>
/// Creates a static or smart playlist. Validation (DSL parse/compile,
/// cycle check, name uniqueness) runs in the store; errors show inline
/// and keep the dialog open.
/// </summary>
public partial class NewPlaylistDialog : Window
{
    private readonly MainViewModel? _viewModel;

    /// <summary>XAML-loader constructor.</summary>
    public NewPlaylistDialog()
    {
        InitializeComponent();
        SmartCheck.IsCheckedChanged += (_, _) =>
            SmartPanel.IsVisible = SmartCheck.IsChecked == true;
    }

    /// <summary>Creates the dialog bound to the main view-model.</summary>
    public NewPlaylistDialog(MainViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Close(false);
            return;
        }

        string name = NameBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            ShowError("Enter a playlist name.");
            return;
        }

        string? query = SmartCheck.IsChecked == true ? QueryBox.Text : null;
        if (SmartCheck.IsChecked == true && string.IsNullOrWhiteSpace(query))
        {
            ShowError("Enter a query, or uncheck “Smart playlist”.");
            return;
        }

        string? error = await _viewModel.CreatePlaylistAsync(name, query);
        if (error is null)
        {
            Close(true);
        }
        else
        {
            ShowError(error);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
