using Microsoft.Extensions.Logging;
using RadioZapper.Models;

namespace RadioZapper.Services;

public sealed class StationService(
    StationRepository repository,
    SettingsService settings,
    IRadioPlayerService player,
    ILogger<StationService> logger) : IStationService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<RadioStation> _stations = [];

    public event EventHandler? Changed;
    public IReadOnlyList<RadioStation> Stations => _stations;
    public RadioStation? CurrentStation { get; private set; }
    public bool IsCurrentStationTemporary => CurrentStation is not null && Find(CurrentStation.Id) is null;

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _stations.Clear();
            _stations.AddRange((await repository.LoadAsync())
                .Where(s => s.Id != Guid.Empty && !string.IsNullOrWhiteSpace(s.Name) && IsStreamUrlValid(s.StreamUrl))
                .GroupBy(s => s.Id).Select(g => g.First())
                .OrderBy(s => s.Order).ThenBy(s => s.Name));
            NormalizeOrder();
            CurrentStation = _stations.FirstOrDefault(s => s.Id == settings.Current.LastStationId)
                ?? _stations.FirstOrDefault();
            if (settings.Current.LastStationId != CurrentStation?.Id)
            {
                settings.Current.LastStationId = CurrentStation?.Id;
                await settings.SaveAsync();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task SelectAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            var station = Find(id);
            if (station is null || CurrentStation?.Id == id) return;
            await player.StopAsync();
            SetCurrent(station);
            await settings.SaveAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task PlayAsync(RadioStation station)
    {
        await _gate.WaitAsync();
        try { await PlayCoreAsync(Find(station.Id) ?? throw new InvalidOperationException("Rádio inexistente.")); }
        finally { _gate.Release(); }
    }

    public async Task PlayTemporaryAsync(RadioStation station)
    {
        Validate(station);
        await _gate.WaitAsync();
        try
        {
            var copy = station.Copy();
            if (copy.Id == Guid.Empty || Find(copy.Id) is not null) copy.Id = Guid.NewGuid();
            SetCurrent(copy);
            await settings.SaveAsync();
            await player.PlayAsync(copy);
        }
        finally { _gate.Release(); }
    }

    public async Task ToggleAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (player.State is PlaybackState.Playing or PlaybackState.Connecting)
            {
                await player.PauseAsync();
                return;
            }
            var station = CurrentStation ?? _stations.FirstOrDefault();
            if (station is not null) await PlayCoreAsync(station);
        }
        finally { _gate.Release(); }
    }

    public Task NextAsync() => NavigateAsync(1);
    public Task PreviousAsync() => NavigateAsync(-1);

    private async Task NavigateAsync(int direction)
    {
        await _gate.WaitAsync();
        try
        {
            if (_stations.Count == 0) return;
            var index = CurrentStation is null ? -1 : _stations.FindIndex(s => s.Id == CurrentStation.Id);
            var next = index < 0 ? (direction > 0 ? 0 : _stations.Count - 1)
                : (index + direction + _stations.Count) % _stations.Count;
            await PlayCoreAsync(_stations[next]);
        }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(RadioStation station)
    {
        Validate(station);
        await _gate.WaitAsync();
        try
        {
            if (_stations.Any(s => s.Id == station.Id)) station.Id = Guid.NewGuid();
            var wasTemporary = IsCurrentStationTemporary && CurrentStation?.Id == station.Id;
            var previousUrl = wasTemporary ? CurrentStation?.StreamUrl : null;
            station.Order = _stations.Count;
            var copy = station.Copy();
            _stations.Add(copy);
            if (CurrentStation is null || wasTemporary)
            {
                SetCurrent(copy);
                await settings.SaveAsync();
                if (wasTemporary && previousUrl != copy.StreamUrl
                    && player.State is (PlaybackState.Playing or PlaybackState.Connecting))
                    await player.PlayAsync(copy);
            }
            await PersistAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateAsync(RadioStation station)
    {
        Validate(station);
        await _gate.WaitAsync();
        try
        {
            var index = _stations.FindIndex(s => s.Id == station.Id);
            if (index < 0) throw new InvalidOperationException("Rádio inexistente.");
            var previousUrl = _stations[index].StreamUrl;
            var copy = station.Copy();
            copy.Order = index;
            _stations[index] = copy;
            if (CurrentStation?.Id == copy.Id)
            {
                SetCurrent(copy);
                if (player.State is (PlaybackState.Playing or PlaybackState.Connecting) && previousUrl != copy.StreamUrl)
                    await player.PlayAsync(copy);
            }
            await PersistAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            var index = _stations.FindIndex(s => s.Id == id);
            if (index < 0) return;
            var wasCurrent = CurrentStation?.Id == id;
            if (wasCurrent) await player.StopAsync();
            _stations.RemoveAt(index);
            NormalizeOrder();
            if (wasCurrent)
            {
                SetCurrent(_stations.Count == 0 ? null : _stations[Math.Min(index, _stations.Count - 1)]);
                await settings.SaveAsync();
            }
            await PersistAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task MoveAsync(Guid id, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        await _gate.WaitAsync();
        try
        {
            var index = _stations.FindIndex(s => s.Id == id);
            var destination = index + direction;
            if (index < 0 || destination < 0 || destination >= _stations.Count) return;
            (_stations[index], _stations[destination]) = (_stations[destination], _stations[index]);
            NormalizeOrder();
            await PersistAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task SetFavoriteAsync(Guid id, bool favorite)
    {
        await _gate.WaitAsync();
        try
        {
            var station = Find(id);
            if (station is null || station.IsFavorite == favorite) return;
            station.IsFavorite = favorite;
            await PersistAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    private async Task PlayCoreAsync(RadioStation station)
    {
        SetCurrent(station);
        await settings.SaveAsync();
        await player.PlayAsync(station);
    }

    private void SetCurrent(RadioStation? station)
    {
        CurrentStation = station;
        settings.Current.LastStationId = station is not null && Find(station.Id) is not null ? station.Id : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private RadioStation? Find(Guid id) => _stations.FirstOrDefault(s => s.Id == id);
    private void NormalizeOrder()
    {
        for (var index = 0; index < _stations.Count; index++) _stations[index].Order = index;
    }

    private Task PersistAsync()
    {
        logger.LogInformation("Salvando {Count} rádios.", _stations.Count);
        return repository.SaveAsync(_stations);
    }

    public static bool IsStreamUrlValid(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

    private static void Validate(RadioStation station)
    {
        station.Name = station.Name.Trim();
        station.StreamUrl = station.StreamUrl.Trim();
        if (station.Name.Length == 0) throw new ArgumentException("Informe o nome da rádio.");
        if (!IsStreamUrlValid(station.StreamUrl)) throw new ArgumentException("Informe uma URL HTTP ou HTTPS válida para o stream.");
    }
}
