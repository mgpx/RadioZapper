using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RadioZapper.Models;

namespace RadioZapper.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings settings)
    {
        StartMinimized = settings.StartMinimized;
        CloseToTray = settings.CloseToTray;
        ThemeIndex = settings.Theme switch { "Dark" => 1, "Light" => 2, _ => 0 };
    }

    public event EventHandler? Saved;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private int _themeIndex;

    [RelayCommand] private void Save() => Saved?.Invoke(this, EventArgs.Empty);

    public void ApplyTo(AppSettings settings)
    {
        settings.StartMinimized = StartMinimized;
        settings.CloseToTray = CloseToTray;
        settings.Theme = ThemeIndex switch { 1 => "Dark", 2 => "Light", _ => "System" };
    }
}
