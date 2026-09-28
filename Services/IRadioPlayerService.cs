using RadioZapper.Models;

namespace RadioZapper.Services;

public interface IRadioPlayerService : IDisposable
{
    event EventHandler<PlaybackChangedEventArgs>? PlaybackChanged;
    Task PlayAsync(RadioStation station);
    Task StopAsync();
    Task PauseAsync();
    double Volume { get; set; }
    bool IsPlaying { get; }
    PlaybackState State { get; }
}
