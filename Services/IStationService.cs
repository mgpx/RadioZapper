using RadioZapper.Models;

namespace RadioZapper.Services;

public interface IStationService
{
    event EventHandler? Changed;
    IReadOnlyList<RadioStation> Stations { get; }
    RadioStation? CurrentStation { get; }
    Task InitializeAsync();
    Task SelectAsync(Guid id);
    Task PlayAsync(RadioStation station);
    Task ToggleAsync();
    Task NextAsync();
    Task PreviousAsync();
    Task AddAsync(RadioStation station);
    Task UpdateAsync(RadioStation station);
    Task DeleteAsync(Guid id);
    Task MoveAsync(Guid id, int direction);
    Task SetFavoriteAsync(Guid id, bool favorite);
}
