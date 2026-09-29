using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RadioZapper.Models;
using RadioZapper.Services;

namespace RadioZapper.ViewModels;

public sealed record ProgramRow(string Time, string Title, string? Detail);
public sealed record ProgramDayRow(string Day, IReadOnlyList<ProgramRow> Programs);

public partial class RadioDetailsViewModel(RadiosNetService catalog, RadioStation station) : ObservableObject, IDisposable
{
    private static readonly DayOfWeek[] Week =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];
    private static readonly string[] DayNames =
    [
        "Segunda-feira", "Terça-feira", "Quarta-feira", "Quinta-feira",
        "Sexta-feira", "Sábado", "Domingo"
    ];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private RadiosNetStationDetails? _details;
    private DateOnly _loadedDate;
    private TimeSpan _loadedOffset;
    private bool _fetching;
    private bool _disposed;

    public string StationName { get; } = station.Name;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private string? _segments;
    [ObservableProperty] private string _currentPrograms = "Consultando programação...";
    [ObservableProperty] private IReadOnlyList<string> _contacts = [];
    [ObservableProperty] private IReadOnlyList<ProgramDayRow> _days = [];

    public async Task LoadAsync()
    {
        if (_fetching || _disposed || station.CatalogId is not { } id) return;
        _fetching = true;
        IsLoading = true;
        try
        {
            var details = await catalog.GetDetailsAsync(id);
            if (_disposed) return;
            _details = details;
            var now = DateTimeOffset.Now;
            _loadedDate = DateOnly.FromDateTime(now.DateTime);
            _loadedOffset = now.Offset;
            Description = details.Description;
            Segments = details.Segments;
            Contacts = details.Contacts.Select(contact => string.IsNullOrWhiteSpace(contact.Detail)
                ? contact.Title : $"{contact.Detail}: {contact.Title}")
                .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            Days = Week.Select((day, index) => new ProgramDayRow(DayNames[index],
                details.Schedule.Where(program => program.Day == day)
                    .DistinctBy(program => (program.Title, program.Start, program.End))
                    .OrderBy(program => program.Start)
                    .Select(program => new ProgramRow($"{program.Start:HH:mm}–{program.End:HH:mm}",
                        program.Title, program.Detail)).ToArray()))
                .Where(day => day.Programs.Count > 0).ToArray();
            UpdateCurrentPrograms();
            ErrorMessage = null;
            if (!_timer.IsEnabled)
            {
                _timer.Tick += OnTimerTick;
                _timer.Start();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException
                                   or System.Text.Json.JsonException)
        {
            _details = null;
            Days = [];
            ErrorMessage = $"Não foi possível consultar o catálogo: {ex.Message}";
            CurrentPrograms = "Programação indisponível.";
        }
        finally { _fetching = false; IsLoading = false; }
    }

    private void OnTimerTick(object? sender, EventArgs args)
    {
        var now = DateTimeOffset.Now;
        if (_loadedDate != DateOnly.FromDateTime(now.DateTime) || _loadedOffset != now.Offset)
            _ = LoadAsync();
        else UpdateCurrentPrograms();
    }

    private void UpdateCurrentPrograms()
    {
        if (_details is null) return;
        var current = ProgramSchedule.GetCurrent(_details.Schedule, DateTime.Now);
        CurrentPrograms = current.Count == 0 ? "Sem programa previsto neste horário."
            : string.Join(" • ", current.Select(program => program.Title));
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
