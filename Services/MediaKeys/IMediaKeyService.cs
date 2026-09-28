using RadioZapper.Models;

namespace RadioZapper.Services.MediaKeys;

public interface IMediaKeyService : IDisposable
{
    event EventHandler? PlayPausePressed;
    event EventHandler? NextPressed;
    event EventHandler? PreviousPressed;
    void Initialize(nint windowHandle);
    void Update(RadioStation? station, PlaybackState state);
}
