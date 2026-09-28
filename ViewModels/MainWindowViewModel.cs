using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RadioZapper.Models;
using RadioZapper.Services;
using RadioZapper.Services.MediaKeys;

namespace RadioZapper.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IStationService _stationService;
    private readonly IRadioPlayerService _player;
    private readonly SettingsService _settings;
    private readonly LogoService _logos;
    private readonly IMediaKeyService _mediaKeys;
    private readonly ILogger<MainWindowViewModel> _logger;
    private CancellationTokenSource? _volumeSave;
    private int _logoGeneration;
    private string? _lastLogoSource;

    public MainWindowViewModel(IStationService stationService, IRadioPlayerService player,
        SettingsService settings, LogoService logos, IMediaKeyService mediaKeys,
        ILogger<MainWindowViewModel> logger)
    {
        _stationService = stationService;
        _player = player;
        _settings = settings;
        _logos = logos;
        _mediaKeys = mediaKeys;
        _logger = logger;
        _stationService.Changed += OnStationsChanged;
        _player.PlaybackChanged += OnPlaybackChanged;
        _mediaKeys.PlayPausePressed += OnMediaPlayPause;
        _mediaKeys.NextPressed += OnMediaNext;
        _mediaKeys.PreviousPressed += OnMediaPrevious;
    }

    public Action? ShowStations { get; set; }
    public Action? ShowSearch { get; set; }
    public Func<Task>? ShowSaveCurrentAsync { get; set; }
    public Func<Task>? ShowSettingsAsync { get; set; }

    [ObservableProperty] private string _currentName = "Nenhuma rádio selecionada";
    [ObservableProperty] private string _currentLocation = "Cadastre uma rádio para começar";
    [ObservableProperty] private string _status = "Parado";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _showPauseIcon;
    [ObservableProperty] private bool _showPlayIcon = true;
    [ObservableProperty] private Bitmap? _currentLogo;
    [ObservableProperty] private bool _isLogoMissing = true;
    [ObservableProperty] private double _volume = 70;
    [ObservableProperty] private bool _canSaveCurrent;

    public void Initialize()
    {
        _player.Volume = _settings.Current.Volume;
        Volume = _settings.Current.Volume;
        RefreshStations();
        RefreshPlayback(_player.State, null);
    }

    partial void OnVolumeChanged(double value)
    {
        _player.Volume = value;
        _settings.Current.Volume = (int)Math.Round(value);
        _volumeSave?.Cancel();
        _volumeSave?.Dispose();
        var cts = new CancellationTokenSource();
        _volumeSave = cts;
        _ = SaveVolumeLaterAsync(cts.Token);
    }

    private async Task SaveVolumeLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            await _settings.SaveAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex, "Não foi possível salvar o volume."); }
    }

    private void OnStationsChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(RefreshStations);
    private void OnPlaybackChanged(object? sender, PlaybackChangedEventArgs args) =>
        Dispatcher.UIThread.Post(() => RefreshPlayback(args.State, args.Message));

    private void OnMediaPlayPause(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() => ToggleCommand.Execute(null));
    private void OnMediaNext(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() => NextCommand.Execute(null));
    private void OnMediaPrevious(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() => PreviousCommand.Execute(null));

    private void RefreshStations()
    {
        var current = _stationService.CurrentStation;
        CurrentName = current?.Name ?? "Nenhuma rádio selecionada";
        CurrentLocation = current?.Location ?? (current is null ? "Cadastre uma rádio para começar" : string.Empty);
        CanSaveCurrent = _stationService.IsCurrentStationTemporary;
        _mediaKeys.Update(current, _player.State);
        if (_lastLogoSource != current?.LogoSource)
        {
            _lastLogoSource = current?.LogoSource;
            _ = LoadLogoAsync(_lastLogoSource, ++_logoGeneration);
        }
    }

    private async Task LoadLogoAsync(string? source, int generation)
    {
        try
        {
            var bitmap = await _logos.LoadAsync(source);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _logoGeneration) { bitmap?.Dispose(); return; }
                var previous = CurrentLogo;
                CurrentLogo = bitmap;
                IsLogoMissing = bitmap is null;
                previous?.Dispose();
            });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Falha ao exibir o logotipo."); }
    }

    private void RefreshPlayback(PlaybackState state, string? message)
    {
        Status = state switch
        {
            PlaybackState.Connecting => "Conectando...",
            PlaybackState.Playing => "Tocando",
            PlaybackState.Paused => "Pausado",
            PlaybackState.Offline => "Offline",
            PlaybackState.Error => "Erro ao conectar",
            _ => "Parado"
        };
        ShowPauseIcon = state is PlaybackState.Playing or PlaybackState.Connecting;
        ShowPlayIcon = !ShowPauseIcon;
        ErrorMessage = state == PlaybackState.Error && _stationService.CurrentStation is { } station
            ? $"Não foi possível reproduzir \"{station.Name}\"." : message;
        _mediaKeys.Update(_stationService.CurrentStation, state);
    }

    [RelayCommand] private Task ToggleAsync() => RunAsync(_stationService.ToggleAsync);
    [RelayCommand] private Task NextAsync() => RunAsync(_stationService.NextAsync);
    [RelayCommand] private Task PreviousAsync() => RunAsync(_stationService.PreviousAsync);

    [RelayCommand] private void Stations() => ShowStations?.Invoke();
    [RelayCommand] private void Search() => ShowSearch?.Invoke();
    [RelayCommand] private Task SaveCurrentAsync() => RunAsync(() => ShowSaveCurrentAsync?.Invoke() ?? Task.CompletedTask);

    [RelayCommand]
    private Task SettingsAsync() => ShowSettingsAsync?.Invoke() ?? Task.CompletedTask;

    private async Task RunAsync(Func<Task> action)
    {
        try { ErrorMessage = null; await action(); }
        catch (Exception ex) { ReportError(ex, "Não foi possível concluir a operação."); }
    }

    private void ReportError(Exception ex, string message)
    {
        _logger.LogError(ex, "{Message}", message);
        Dispatcher.UIThread.Post(() => ErrorMessage = message);
    }

    public void Dispose()
    {
        _stationService.Changed -= OnStationsChanged;
        _player.PlaybackChanged -= OnPlaybackChanged;
        _mediaKeys.PlayPausePressed -= OnMediaPlayPause;
        _mediaKeys.NextPressed -= OnMediaNext;
        _mediaKeys.PreviousPressed -= OnMediaPrevious;
        _volumeSave?.Cancel();
        _volumeSave?.Dispose();
        CurrentLogo?.Dispose();
    }
}
