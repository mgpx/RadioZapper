using Avalonia;
using Avalonia.Controls;

namespace RadioZapper.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, e) => (Application.Current as App)?.HandleWindowClosing(this, e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.NewValue is WindowState.Minimized)
            (Application.Current as App)?.HideToTray();
    }
}
