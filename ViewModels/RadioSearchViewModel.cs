using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RadioZapper.Models;
using RadioZapper.Services;

namespace RadioZapper.ViewModels;

public partial class RadioSearchViewModel : ObservableObject, IDisposable
{
    private readonly RadiosNetService _catalog;
    private readonly IStationService _stations;
    private CancellationTokenSource? _searchCancellation;

    public RadioSearchViewModel(RadiosNetService catalog, IStationService stations)
    {
        _catalog = catalog;
        _stations = stations;
    }

    public Func<RadioStation, Task<RadioStation?>>? ShowEditorAsync { get; set; }
    public ObservableCollection<RadioSearchItemViewModel> Results { get; } = [];
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasSearched;
    public bool IsEmpty => HasSearched && Results.Count == 0 && !IsLoading && ErrorMessage is null;

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchText.Trim();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        Results.Clear();
        HasSearched = false;
        ErrorMessage = query.Length == 0 ? "Informe o nome da rádio para pesquisar." : null;
        OnPropertyChanged(nameof(IsEmpty));
        if (query.Length > 0) await LoadResultsAsync(query, _searchCancellation.Token);
    }

    private async Task LoadResultsAsync(string query, CancellationToken token)
    {
        IsLoading = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsEmpty));
        try
        {
            var items = await _catalog.SearchAsync(query, token);
            if (token.IsCancellationRequested) return;
            foreach (var item in items) Results.Add(new RadioSearchItemViewModel(item, this));
            HasSearched = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException
                                   or System.Text.Json.JsonException or ArgumentException)
        {
            ErrorMessage = ex is OperationCanceledException
                ? "A busca demorou demais. Tente novamente."
                : $"Não foi possível pesquisar: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    internal async Task PlayAsync(RadioSearchItemViewModel item)
    {
        try
        {
            ErrorMessage = null;
            var station = await item.GetStationAsync(_catalog);
            await _stations.PlayTemporaryAsync(station);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException
                                   or System.Text.Json.JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Não foi possível tocar a rádio: {ex.Message}";
        }
    }

    internal async Task SaveAsync(RadioSearchItemViewModel item)
    {
        try
        {
            ErrorMessage = null;
            var station = await item.GetStationAsync(_catalog);
            if (ShowEditorAsync is null) return;
            var edited = await ShowEditorAsync(station);
            if (edited is not null) await _stations.AddAsync(edited);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException
                                   or System.Text.Json.JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Não foi possível salvar a rádio: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
    }
}

public partial class RadioSearchItemViewModel : ObservableObject
{
    private readonly RadioSearchViewModel _owner;
    private RadioStation? _station;

    public RadioSearchItemViewModel(RadiosNetSearchItem item, RadioSearchViewModel owner)
    {
        Item = item;
        _owner = owner;
    }

    public RadiosNetSearchItem Item { get; }
    public string Name => Item.Name;
    public string Location => Item.Location ?? string.Empty;
    [ObservableProperty] private bool _isBusy;

    public async Task<RadioStation> GetStationAsync(RadiosNetService service)
    {
        if (_station is not null) return _station;
        var station = await service.GetStationAsync(Item.Id);
        station.Location ??= Item.Location;
        station.LogoSource ??= Item.LogoSource;
        return _station = station;
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await _owner.PlayAsync(this); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await _owner.SaveAsync(this); }
        finally { IsBusy = false; }
    }
}
