using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RadioZapper.Models;
using RadioZapper.Services;

namespace RadioZapper.ViewModels;

public partial class StationManagementViewModel : ObservableObject, IDisposable
{
    private readonly IStationService _stations;
    private readonly ILogger<StationManagementViewModel> _logger;

    public StationManagementViewModel(IStationService stations, ILogger<StationManagementViewModel> logger)
    {
        _stations = stations;
        _logger = logger;
        _stations.Changed += OnStationsChanged;
        Refresh();
    }

    public Func<RadioStation?, Task<RadioStation?>>? ShowEditorAsync { get; set; }
    public event EventHandler? StationPlayed;
    public ObservableCollection<StationItemViewModel> Stations { get; } = [];
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isEmpty;

    private void OnStationsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        Stations.Clear();
        foreach (var station in _stations.Stations)
            Stations.Add(new StationItemViewModel(station, this, station.Id == _stations.CurrentStation?.Id));
        IsEmpty = Stations.Count == 0;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (ShowEditorAsync is null) return;
        var station = await ShowEditorAsync(null);
        if (station is not null) await RunAsync(() => _stations.AddAsync(station));
    }

    public Task SelectAsync(RadioStation station) => RunAsync(() => _stations.SelectAsync(station.Id));

    public async Task PlayAsync(RadioStation station)
    {
        if (await RunAsync(() => _stations.PlayAsync(station))) StationPlayed?.Invoke(this, EventArgs.Empty);
    }

    public Task MoveAsync(RadioStation station, int direction) => RunAsync(() => _stations.MoveAsync(station.Id, direction));
    public Task FavoriteAsync(RadioStation station) => RunAsync(() => _stations.SetFavoriteAsync(station.Id, !station.IsFavorite));
    public Task DeleteAsync(RadioStation station) => RunAsync(() => _stations.DeleteAsync(station.Id));

    public async Task EditAsync(RadioStation station)
    {
        if (ShowEditorAsync is null) return;
        var edited = await ShowEditorAsync(station);
        if (edited is not null) await RunAsync(() => _stations.UpdateAsync(edited));
    }

    private async Task<bool> RunAsync(Func<Task> action)
    {
        try { ErrorMessage = null; await action(); return true; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível concluir a operação da rádio.");
            ErrorMessage = "Não foi possível concluir a operação.";
            return false;
        }
    }

    public void Dispose() => _stations.Changed -= OnStationsChanged;
}
