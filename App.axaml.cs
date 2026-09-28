using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RadioZapper.Models;
using RadioZapper.Services;
using RadioZapper.Services.MediaKeys;
using RadioZapper.ViewModels;
using RadioZapper.Views;

namespace RadioZapper;

public partial class App : Application
{
    private ILoggerFactory? _logging;
    private SettingsService? _settings;
    private IRadioPlayerService? _player;
    private IStationService? _stations;
    private IMediaKeyService? _mediaKeys;
    private LogoService? _logos;
    private StationImportService? _importer;
    private MainWindowViewModel? _viewModel;
    private MainWindow? _window;
    private StationManagementWindow? _managementWindow;
    private TrayIcon? _tray;
    private NativeMenuItem? _stationItem;
    private NativeMenuItem? _playItem;
    private bool _exiting;
    private long _lastTrayClick;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _logging = LoggerFactory.Create(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Information));
        var paths = new AppDataPaths();
        var files = new JsonFileStore(_logging.CreateLogger<JsonFileStore>());
        _settings = new SettingsService(paths, files);
        _player = new RadioPlayerService(_logging.CreateLogger<RadioPlayerService>());
        _stations = new StationService(new StationRepository(paths, files), _settings, _player,
            _logging.CreateLogger<StationService>());
        _mediaKeys = new WindowsMediaKeyService(_logging.CreateLogger<WindowsMediaKeyService>());
        _logos = new LogoService(paths, _logging.CreateLogger<LogoService>());
        _importer = new StationImportService(_logging.CreateLogger<StationImportService>());
        _viewModel = new MainWindowViewModel(_stations, _player, _settings, _logos, _mediaKeys,
            _logging.CreateLogger<MainWindowViewModel>());
        _window = new MainWindow { DataContext = _viewModel };
        _viewModel.ShowStations = ShowStationManagementWindow;
        _viewModel.ShowSettingsAsync = ShowSettingsAsync;
        _viewModel.PropertyChanged += (_, _) => UpdateTrayText();
        _window.Opened += (_, _) =>
        {
            var handle = _window.TryGetPlatformHandle()?.Handle ?? 0;
            _mediaKeys.Initialize(handle);
            _mediaKeys.Update(_stations.CurrentStation, _player.State);
        };
        _window.Icon = LoadIcon();
        desktop.MainWindow = _window;
        CreateTray();
        desktop.Exit += (_, _) => DisposeServices();
        base.OnFrameworkInitializationCompleted();
        _ = InitializeDataAsync();
    }

    private async Task InitializeDataAsync()
    {
        try
        {
            await _settings!.LoadAsync();
            await _stations!.InitializeAsync();
            _viewModel!.Initialize();
            ApplyTheme();
            if (_settings.Current.StartMinimized) HideToTray();
        }
        catch (Exception ex)
        {
            _logging?.CreateLogger<App>().LogError(ex, "Falha ao inicializar os dados do aplicativo.");
        }
    }

    private static WindowIcon LoadIcon() => new(AssetLoader.Open(new Uri("avares://RadioZapper/Assets/radio.ico")));

    private void CreateTray()
    {
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem { Header = "Abrir Radio Zapper", Command = new RelayCommand(ShowWindow) });
        _stationItem = new NativeMenuItem { Header = "Nenhuma rádio", IsEnabled = false };
        menu.Items.Add(_stationItem);
        _playItem = new NativeMenuItem { Header = "Play", Command = _viewModel!.ToggleCommand };
        menu.Items.Add(_playItem);
        menu.Items.Add(new NativeMenuItem { Header = "Estação anterior", Command = _viewModel.PreviousCommand });
        menu.Items.Add(new NativeMenuItem { Header = "Próxima estação", Command = _viewModel.NextCommand });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem { Header = "Sair", Command = new AsyncRelayCommand(ExitAsync) });
        var tray = new TrayIcon { Icon = LoadIcon(), ToolTipText = "Radio Zapper", Menu = menu };
        _tray = tray;
        tray.Clicked += (_, _) =>
        {
            var now = Environment.TickCount64;
            if (now - _lastTrayClick <= 500) { _lastTrayClick = 0; ShowWindow(); }
            else _lastTrayClick = now;
        };
        TrayIcon.SetIcons(this, new TrayIcons { tray });
        UpdateTrayText();
    }

    private void UpdateTrayText()
    {
        if (_stationItem is not null) _stationItem.Header = _stations?.CurrentStation?.Name ?? "Nenhuma rádio";
        if (_playItem is not null) _playItem.Header = _player?.State is PlaybackState.Playing or PlaybackState.Connecting ? "Pause" : "Play";
        if (_tray is not null) _tray.ToolTipText = _stations?.CurrentStation?.Name is { } name ? $"Radio Zapper — {name}" : "Radio Zapper";
    }

    private void ShowStationManagementWindow()
    {
        if (_window is null || _stations is null || _logging is null) return;
        if (_managementWindow is { } existing)
        {
            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var viewModel = new StationManagementViewModel(_stations,
            _logging.CreateLogger<StationManagementViewModel>());
        var window = new StationManagementWindow { DataContext = viewModel, Icon = LoadIcon() };
        _managementWindow = window;
        viewModel.ShowEditorAsync = station => ShowEditorAsync(station, window);
        viewModel.StationPlayed += (_, _) =>
        {
            window.Close();
            ShowWindow();
        };
        window.Closed += (_, _) =>
        {
            viewModel.Dispose();
            if (ReferenceEquals(_managementWindow, window)) _managementWindow = null;
        };
        window.Show(_window);
    }

    private async Task<RadioStation?> ShowEditorAsync(RadioStation? station, Window owner)
    {
        if (_logos is null || _importer is null) return null;
        var dialog = new RadioEditorWindow { DataContext = new RadioEditorViewModel(station, _logos, _importer!) };
        return await dialog.ShowDialog<RadioStation?>(owner);
    }

    private async Task ShowSettingsAsync()
    {
        if (_window is null || _settings is null) return;
        var vm = new SettingsViewModel(_settings.Current);
        var dialog = new SettingsWindow { DataContext = vm };
        if (!await dialog.ShowDialog<bool>(_window)) return;
        vm.ApplyTo(_settings.Current);
        ApplyTheme();
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { _logging?.CreateLogger<App>().LogError(ex, "Falha ao salvar configurações."); }
    }

    private void ApplyTheme() => RequestedThemeVariant = _settings?.Current.Theme switch
    {
        "Dark" => ThemeVariant.Dark,
        "Light" => ThemeVariant.Light,
        _ => ThemeVariant.Default
    };

    public void HideToTray()
    {
        if (_window is null || _exiting) return;
        _managementWindow?.Close();
        _window.ShowInTaskbar = false;
        _window.Hide();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.ShowInTaskbar = true;
        _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    public void HandleWindowClosing(MainWindow window, CancelEventArgs args)
    {
        if (_exiting) return;
        if (_settings?.Current.CloseToTray ?? true)
        {
            args.Cancel = true;
            HideToTray();
        }
        else
        {
            args.Cancel = true;
            _ = ExitAsync();
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            if (_settings is not null) await _settings.SaveAsync();
            if (_player is not null) await _player.StopAsync();
        }
        catch (Exception ex) { _logging?.CreateLogger<App>().LogError(ex, "Falha durante o encerramento."); }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    private void DisposeServices()
    {
        _viewModel?.Dispose();
        _mediaKeys?.Dispose();
        _player?.Dispose();
        _logos?.Dispose();
        _importer?.Dispose();
        _tray?.Dispose();
        _logging?.Dispose();
    }
}
