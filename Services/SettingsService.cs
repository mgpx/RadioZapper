using RadioZapper.Models;

namespace RadioZapper.Services;

public sealed class SettingsService(AppDataPaths paths, JsonFileStore files)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync()
    {
        Current = await files.ReadAsync(paths.Settings, new AppSettings());
        Current.Volume = Math.Clamp(Current.Volume, 0, 100);
        if (Current.Theme is not ("System" or "Dark" or "Light")) Current.Theme = "System";
    }

    public async Task SaveAsync()
    {
        await _writeGate.WaitAsync();
        try { await files.WriteAsync(paths.Settings, Current); }
        finally { _writeGate.Release(); }
    }
}
