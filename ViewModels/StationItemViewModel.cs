using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RadioZapper.Models;

namespace RadioZapper.ViewModels;

public sealed class StationItemViewModel : ObservableObject
{
    private readonly StationManagementViewModel _owner;
    public StationItemViewModel(RadioStation station, StationManagementViewModel owner, bool isCurrent)
    {
        Station = station;
        _owner = owner;
        IsCurrent = isCurrent;
        SelectCommand = new AsyncRelayCommand(() => _owner.SelectAsync(Station));
        PlayCommand = new AsyncRelayCommand(() => _owner.PlayAsync(Station));
        EditCommand = new AsyncRelayCommand(() => _owner.EditAsync(Station));
        DeleteCommand = new AsyncRelayCommand(() => _owner.DeleteAsync(Station));
        MoveUpCommand = new AsyncRelayCommand(() => _owner.MoveAsync(Station, -1));
        MoveDownCommand = new AsyncRelayCommand(() => _owner.MoveAsync(Station, 1));
        FavoriteCommand = new AsyncRelayCommand(() => _owner.FavoriteAsync(Station));
    }

    public RadioStation Station { get; }
    public string Name => Station.Name;
    public string Location => Station.Location ?? string.Empty;
    public string FavoriteGlyph => Station.IsFavorite ? "♥" : "♡";
    public bool IsCurrent { get; }
    public IAsyncRelayCommand SelectCommand { get; }
    public IAsyncRelayCommand PlayCommand { get; }
    public IAsyncRelayCommand EditCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand MoveUpCommand { get; }
    public IAsyncRelayCommand MoveDownCommand { get; }
    public IAsyncRelayCommand FavoriteCommand { get; }
}
