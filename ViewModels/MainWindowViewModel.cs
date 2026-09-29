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
    private readonly RadiosNetService _catalog;
    private readonly IRadioPlayerService _player;
    private readonly SettingsService _settings;
    private readonly LogoService _logos;
    private readonly IMediaKeyService _mediaKeys;
    private readonly ILogger<MainWindowViewModel> _logger;
    private CancellationTokenSource? _volumeSave;
    private int _logoGeneration;
    private string? _lastLogoSource;
    private readonly DispatcherTimer _scheduleTimer;
    private CancellationTokenSource? _scheduleCancellation;
    private RadiosNetStationDetails? _scheduleDetails;
    private int? _requestedCatalogId;
    private DateOnly _requestedDate;
    private TimeSpan _requestedOffset;
    private int _scheduleGeneration;

    public MainWindowViewModel(IStationService stationService, RadiosNetService catalog, IRadioPlayerService player,
        SettingsService settings, LogoService logos, IMediaKeyService mediaKeys,
        ILogger<MainWindowViewModel> logger)
    {
        _stationService = stationService;
        _catalog = catalog;
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
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _scheduleTimer.Tick += (_, _) => RefreshScheduleTime();
    }

    public Action? ShowStations { get; set; }
    public Action? ShowSearch { get; set; }
    public Func<Task>? ShowSaveCurrentAsync { get; set; }
    public Func<Task>? ShowSettingsAsync { get; set; }
    public Func<Task>? ShowDetailsAsync { get; set; }

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
    [ObservableProperty] private bool _isProgramVisible;
    [ObservableProperty] private string _currentProgram = string.Empty;
    [ObservableProperty] private string _additionalPrograms = string.Empty;
    [ObservableProperty] private bool _hasAdditionalPrograms;

    public void Initialize()
    {
        _player.Volume = _settings.Current.Volume;
        Volume = _settings.Current.Volume;
        RefreshStations();
        RefreshPlayback(_player.State, null);
        _scheduleTimer.Start();
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
        QueueScheduleLoad(current?.CatalogId);
        _mediaKeys.Update(current, _player.State);
        if (_lastLogoSource != current?.LogoSource)
        {
            _lastLogoSource = current?.LogoSource;
            _ = LoadLogoAsync(_lastLogoSource, ++_logoGeneration);
        }
    }

    private void QueueScheduleLoad(int? catalogId)
    {
        IsProgramVisible = catalogId is > 0;
        if (catalogId is not > 0)
        {
            _scheduleCancellation?.Cancel();
            _scheduleDetails = null;
            _requestedCatalogId = null;
            CurrentProgram = string.Empty;
            HasAdditionalPrograms = false;
            return;
        }
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.DateTime);
        if (_requestedCatalogId == catalogId && _requestedDate == today && _requestedOffset == now.Offset) return;
        _scheduleCancellation?.Cancel();
        _scheduleCancellation?.Dispose();
        _scheduleCancellation = new CancellationTokenSource();
        _scheduleDetails = null;
        _requestedCatalogId = catalogId;
        _requestedDate = today;
        _requestedOffset = now.Offset;
        CurrentProgram = "Consultando programação...";
        HasAdditionalPrograms = false;
        _ = LoadScheduleAsync(catalogId.Value, ++_scheduleGeneration, _scheduleCancellation.Token);
    }

    private async Task LoadScheduleAsync(int catalogId, int generation, CancellationToken token)
    {
        try
        {
            var details = await _catalog.GetDetailsAsync(catalogId, token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _scheduleGeneration || token.IsCancellationRequested) return;
                _scheduleDetails = details;
                RefreshScheduleTime();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException
                                   or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Não foi possível consultar a programação da rádio {CatalogId}.", catalogId);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _scheduleGeneration) return;
                _requestedCatalogId = null;
                CurrentProgram = "Programação indisponível.";
                HasAdditionalPrograms = false;
            });
        }
    }

    private void RefreshScheduleTime()
    {
        QueueScheduleLoad(_stationService.CurrentStation?.CatalogId);
        if (_scheduleDetails is null) return;
        var current = ProgramSchedule.GetCurrent(_scheduleDetails.Schedule, DateTime.Now);
        CurrentProgram = current.Count == 0
            ? "Sem programa previsto neste horário."
            : current[0].Title;
        HasAdditionalPrograms = current.Count > 1;
        AdditionalPrograms = current.Count > 1 ? $"+{current.Count - 1}" : string.Empty;
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
    [RelayCommand] private Task DetailsAsync() => ShowDetailsAsync?.Invoke() ?? Task.CompletedTask;

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
        _scheduleTimer.Stop();
        _scheduleCancellation?.Cancel();
        _scheduleCancellation?.Dispose();
        CurrentLogo?.Dispose();
    }
}
