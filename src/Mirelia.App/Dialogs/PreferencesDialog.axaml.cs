using Avalonia.Controls;
using Avalonia.Interactivity;
using Mirelia.App.Services;
using Mirelia.AudioBridge;

namespace Mirelia.App.Dialogs;

/// <summary>
/// Playback settings (output device, exclusive mode, ReplayGain) and
/// appearance settings (theme), grouped into tabs. Device enumeration runs
/// on a throwaway engine instance so the dialog never touches the playback
/// engine.
/// </summary>
public partial class PreferencesDialog : Window
{
    private sealed record DeviceChoice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly SettingsService? _settings;

    /// <summary>XAML-loader constructor.</summary>
    public PreferencesDialog()
    {
        InitializeComponent();
    }

    /// <summary>Creates the dialog over the settings service.</summary>
    public PreferencesDialog(SettingsService settings)
        : this()
    {
        _settings = settings;

        var choices = new List<DeviceChoice> { new(null, "(System default)") };
        try
        {
            using var engine = AudioEngine.Create();
            foreach (AudioDeviceInfo device in engine.EnumerateDevices())
            {
                choices.Add(new DeviceChoice(
                    device.Id,
                    $"{device.Name} — {device.NativeRate} Hz, {device.NativeChannels} ch"));
            }
        }
        catch (Exception ex) when (ex is AudioEngineException or DllNotFoundException)
        {
            // No backend: only the default entry is offered.
        }

        DeviceBox.ItemsSource = choices;
        DeviceBox.SelectedItem =
            choices.FirstOrDefault(c => c.Id == settings.Current.DeviceId) ?? choices[0];
        ExclusiveCheck.IsChecked = settings.Current.Exclusive;

        RgBox.ItemsSource = new[] { ReplayGainMode.Off, ReplayGainMode.Track };
        RgBox.SelectedItem = settings.Current.ReplayGain;

        ThemeBox.ItemsSource = new[] { AppTheme.Auto, AppTheme.Light, AppTheme.Dark };
        ThemeBox.SelectedItem = settings.Current.Theme;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            Close(false);
            return;
        }

        // Copy from Current (not `new AppSettings`) so fields this dialog
        // does not manage, such as the persisted window placement, survive.
        _settings.Save(_settings.Current with
        {
            DeviceId = (DeviceBox.SelectedItem as DeviceChoice)?.Id,
            Exclusive = ExclusiveCheck.IsChecked == true,
            ReplayGain = RgBox.SelectedItem as ReplayGainMode? ?? ReplayGainMode.Off,
            Theme = ThemeBox.SelectedItem as AppTheme? ?? AppTheme.Auto,
        });
        Close(true);
    }
}
