using Avalonia.Controls;
using Avalonia.Interactivity;
using RadioZapper.ViewModels;

namespace RadioZapper.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) vm.Saved += OnSaved;
        };
    }

    private void OnSaved(object? sender, EventArgs e) => Close(true);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
