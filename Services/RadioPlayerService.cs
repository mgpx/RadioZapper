using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using RadioZapper.Models;

namespace RadioZapper.Services;

public sealed class RadioPlayerService : IRadioPlayerService
{
    private readonly LibVLC _libVlc;
    private readonly ILogger<RadioPlayerService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile MediaPlayer? _player;
    private EventHandler<EventArgs>? _playingHandler;
    private EventHandler<EventArgs>? _errorHandler;
    private EventHandler<EventArgs>? _endHandler;
    private EventHandler<EventArgs>? _stoppedHandler;
    private CancellationTokenSource? _connectionTimeout;
    private int _volume = 70;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private bool _disposed;

    public RadioPlayerService(ILogger<RadioPlayerService> logger)
    {
        _logger = logger;
        Core.Initialize();
        _libVlc = new LibVLC("--no-video");
    }

    public event EventHandler<PlaybackChangedEventArgs>? PlaybackChanged;
    public PlaybackState State => _state;
    public bool IsPlaying => _state == PlaybackState.Playing;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp((int)Math.Round(value), 0, 100);
            if (_player is { } player)
            {
                try { player.Volume = _volume; }
                catch (ObjectDisposedException) { }
            }
        }
    }

    public async Task PlayAsync(RadioStation station)
    {
        await _gate.WaitAsync();
        try
        {
            ThrowIfDisposed();
            await Task.Run(() => ReleasePlayer());
            SetState(PlaybackState.Connecting);
            await Task.Run(() =>
            {
                var media = new Media(_libVlc, new Uri(station.StreamUrl));
                try
                {
                    media.AddOption(":no-video");
                    var player = new MediaPlayer(media) { Volume = _volume };
                    Attach(player);
                    _player = player;
                    if (!player.Play()) throw new InvalidOperationException("O LibVLC recusou iniciar o stream.");
                    if (_state == PlaybackState.Connecting) StartTimeout(player);
                }
                finally { media.Dispose(); }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível reproduzir {Station}", station.Name);
            SetState(PlaybackState.Error, $"Não foi possível reproduzir \"{station.Name}\".");
        }
        finally { _gate.Release(); }
    }

    public Task PauseAsync() => StopCoreAsync(PlaybackState.Paused);
    public Task StopAsync() => StopCoreAsync(PlaybackState.Stopped);

    private async Task StopCoreAsync(PlaybackState result)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            await Task.Run(ReleasePlayer);
            SetState(result);
        }
        finally { _gate.Release(); }
    }

    private void Attach(MediaPlayer player)
    {
        _playingHandler = (_, _) => OnPlaying(player);
        _errorHandler = (_, _) => OnError(player);
        _endHandler = (_, _) => OnEndReached(player);
        _stoppedHandler = (_, _) => OnStopped(player);
        player.Playing += _playingHandler;
        player.EncounteredError += _errorHandler;
        player.EndReached += _endHandler;
        player.Stopped += _stoppedHandler;
    }

    private void Detach(MediaPlayer player)
    {
        if (_playingHandler is not null) player.Playing -= _playingHandler;
        if (_errorHandler is not null) player.EncounteredError -= _errorHandler;
        if (_endHandler is not null) player.EndReached -= _endHandler;
        if (_stoppedHandler is not null) player.Stopped -= _stoppedHandler;
        _playingHandler = null;
        _errorHandler = null;
        _endHandler = null;
        _stoppedHandler = null;
    }

    private void OnPlaying(MediaPlayer player)
    {
        if (!ReferenceEquals(player, _player)) return;
        CancelTimeout();
        SetState(PlaybackState.Playing);
    }

    private void OnError(MediaPlayer player)
    {
        if (!ReferenceEquals(player, _player)) return;
        CancelTimeout();
        _logger.LogError("O LibVLC relatou erro de reprodução do stream.");
        SetState(PlaybackState.Error, "Não foi possível conectar à rádio selecionada.");
    }

    private void OnEndReached(MediaPlayer player)
    {
        if (!ReferenceEquals(player, _player) || _state == PlaybackState.Error) return;
        CancelTimeout();
        SetState(PlaybackState.Offline, "O stream da rádio foi encerrado.");
    }

    private void OnStopped(MediaPlayer player)
    {
        if (!ReferenceEquals(player, _player) || _state is PlaybackState.Paused or PlaybackState.Stopped or PlaybackState.Error) return;
        CancelTimeout();
        SetState(PlaybackState.Offline, "A rádio ficou offline.");
    }

    private void StartTimeout(MediaPlayer player)
    {
        CancelTimeout();
        var cts = new CancellationTokenSource();
        _connectionTimeout = cts;
        _ = ObserveTimeoutAsync(player, cts.Token);
    }

    private async Task ObserveTimeoutAsync(MediaPlayer player, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), token);
            if (!ReferenceEquals(player, _player) || _state != PlaybackState.Connecting) return;
            _logger.LogWarning("Tempo limite ao conectar ao stream.");
            SetState(PlaybackState.Error, "A conexão com a rádio demorou demais.");
            await StopPlayerIfCurrentAsync(player);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Falha ao encerrar uma conexão sem resposta."); }
    }

    private async Task StopPlayerIfCurrentAsync(MediaPlayer player)
    {
        await _gate.WaitAsync();
        try { if (ReferenceEquals(player, _player)) await Task.Run(ReleasePlayer); }
        finally { _gate.Release(); }
    }

    private void CancelTimeout()
    {
        var current = Interlocked.Exchange(ref _connectionTimeout, null);
        current?.Cancel();
        current?.Dispose();
    }

    private void ReleasePlayer()
    {
        CancelTimeout();
        var player = _player;
        _player = null;
        if (player is null) return;
        Detach(player);
        player.Stop();
        player.Dispose();
    }

    private void SetState(PlaybackState state, string? message = null)
    {
        _state = state;
        PlaybackChanged?.Invoke(this, new PlaybackChangedEventArgs(state, message));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RadioPlayerService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleasePlayer();
        _libVlc.Dispose();
        _gate.Dispose();
    }
}
