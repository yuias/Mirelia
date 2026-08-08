using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Mirelia.App.Dialogs;

/// <summary>
/// One-line text input with async validation. The validator returns error
/// text (dialog stays open) or null (dialog closes returning the value).
/// </summary>
public partial class InputDialog : Window
{
    private readonly Func<string, Task<string?>>? _validate;

    /// <summary>XAML-loader constructor.</summary>
    public InputDialog()
    {
        InitializeComponent();
    }

    /// <summary>Creates the dialog.</summary>
    /// <param name="title">Window title.</param>
    /// <param name="prompt">Label above the text box.</param>
    /// <param name="initialValue">Pre-filled value.</param>
    /// <param name="validate">Returns error text, or null to accept.</param>
    public InputDialog(
        string title,
        string prompt,
        string initialValue,
        Func<string, Task<string?>> validate)
        : this()
    {
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        _validate = validate;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        string value = ValueBox.Text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            ErrorText.Text = "Enter a value.";
            ErrorText.IsVisible = true;
            return;
        }

        string? error = _validate is null ? null : await _validate(value);
        if (error is null)
        {
            Close(value);
        }
        else
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
        }
    }
}
