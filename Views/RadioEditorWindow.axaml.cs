using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RadioZapper.Models;
using RadioZapper.ViewModels;

namespace RadioZapper.Views;

public partial class RadioEditorWindow : Window
{
    public RadioEditorWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RadioEditorViewModel vm) vm.Saved += OnSaved;
        };
    }

    private void OnSaved(object? sender, RadioStation station) => Close(station);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private async void BrowseLogo_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Escolher logotipo",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Imagens") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }]
        });
        if (files.Count > 0 && DataContext is RadioEditorViewModel vm)
            vm.LogoSource = files[0].Path.LocalPath;
    }
}
