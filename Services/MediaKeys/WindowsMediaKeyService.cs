using Microsoft.Extensions.Logging;
using RadioZapper.Models;
using Windows.Media;

namespace RadioZapper.Services.MediaKeys;

public sealed class WindowsMediaKeyService(ILogger<WindowsMediaKeyService> logger) : IMediaKeyService
{
    private SystemMediaTransportControls? _controls;

    public event EventHandler? PlayPausePressed;
    public event EventHandler? NextPressed;
    public event EventHandler? PreviousPressed;

    public void Initialize(nint windowHandle)
    {
        if (_controls is not null || windowHandle == 0) return;
        try
        {
            _controls = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
            _controls.IsEnabled = true;
            _controls.IsPlayEnabled = true;
            _controls.IsPauseEnabled = true;
            _controls.IsNextEnabled = true;
            _controls.IsPreviousEnabled = true;
            _controls.ButtonPressed += OnButtonPressed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao iniciar os controles de mídia do Windows.");
        }
    }

    public void Update(RadioStation? station, PlaybackState state)
    {
        if (_controls is null) return;
        try
        {
            _controls.IsEnabled = station is not null;
            _controls.PlaybackStatus = state switch
            {
                PlaybackState.Playing => MediaPlaybackStatus.Playing,
                PlaybackState.Connecting => MediaPlaybackStatus.Changing,
                PlaybackState.Paused => MediaPlaybackStatus.Paused,
                _ => MediaPlaybackStatus.Stopped
            };
            _controls.DisplayUpdater.Type = MediaPlaybackType.Music;
            _controls.DisplayUpdater.MusicProperties.Title = station?.Name ?? "Radio Zapper";
            _controls.DisplayUpdater.MusicProperties.Artist = station?.Location ?? string.Empty;
            _controls.DisplayUpdater.Update();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível atualizar a sessão de mídia.");
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
            case SystemMediaTransportControlsButton.Pause:
                PlayPausePressed?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Next:
                NextPressed?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Previous:
                PreviousPressed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        if (_controls is null) return;
        _controls.ButtonPressed -= OnButtonPressed;
        _controls.IsEnabled = false;
        _controls = null;
    }
}
