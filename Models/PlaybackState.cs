namespace RadioZapper.Models;

public enum PlaybackState
{
    Stopped,
    Connecting,
    Playing,
    Paused,
    Offline,
    Error
}

public sealed record PlaybackChangedEventArgs(PlaybackState State, string? Message = null);
