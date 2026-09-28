using RadioZapper.Models;

namespace RadioZapper.Services;

public sealed class StationRepository(AppDataPaths paths, JsonFileStore files)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public Task<List<RadioStation>> LoadAsync() => files.ReadAsync(paths.Stations, new List<RadioStation>());

    public async Task SaveAsync(IEnumerable<RadioStation> stations)
    {
        var snapshot = stations.Select(s => s.Copy()).ToList();
        await _writeGate.WaitAsync();
        try { await files.WriteAsync(paths.Stations, snapshot); }
        finally { _writeGate.Release(); }
    }
}
