using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using RadioZapper.Models;

namespace RadioZapper.Services;

public sealed record RadiosNetSearchItem(int Id, string Name, string? Location, string? LogoSource);

public sealed class RadiosNetService : IDisposable
{
    private const string BaseUrl = "https://app.venganet.com/radiosnet/2.2/";
    private const string LogoBaseUrl = "https://img.radios.com.br/radio/md/";
    private readonly HttpClient _http;
    private readonly Dictionary<int, (DateOnly Day, TimeSpan Offset, RadiosNetStationDetails Details)> _detailsCache = [];
    private readonly object _cacheGate = new();

    public RadiosNetService(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RadioZapper/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<RadiosNetSearchItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0) throw new ArgumentException("Informe o nome da rádio para pesquisar.");
        using var document = await GetAsync($"auto/busca?q={Uri.EscapeDataString(query)}", cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("A API retornou uma busca em formato inesperado.");
        var items = new List<RadiosNetSearchItem>();
        foreach (var entry in root.EnumerateArray())
        {
            var id = GetInt(entry, "id");
            var name = GetString(entry, "title");
            if (id <= 0 || string.IsNullOrWhiteSpace(name)) continue;
            items.Add(new RadiosNetSearchItem(id, name, GetString(entry, "subtitle"),
                MakeLogoUrl(GetString(entry, "url_logo"))));
        }
        return items;
    }

    public async Task<RadioStation> GetStationAsync(int id, CancellationToken cancellationToken = default)
        => (await GetDetailsAsync(id, cancellationToken)).Station;

    public async Task<RadiosNetStationDetails> GetDetailsAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        var now = DateTimeOffset.Now;
        var cacheDay = DateOnly.FromDateTime(now.DateTime);
        lock (_cacheGate)
            if (_detailsCache.TryGetValue(id, out var cached) && cached.Day == cacheDay && cached.Offset == now.Offset)
                return cached.Details;
        using var document = await GetAsync($"radio/{id}", cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A API retornou detalhes em formato inesperado.");
        var name = GetString(root, "title");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A API não informou o nome da rádio.");
        string? stream = null;
        string? userAgent = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in streams.EnumerateArray())
            {
                var candidate = GetString(item, "url");
                if (!StationService.IsStreamUrlValid(candidate)) continue;
                stream = candidate;
                if (item.TryGetProperty("headers", out var headers))
                    userAgent = GetString(headers, "User-Agent");
                break;
            }
        }
        if (stream is null) throw new InvalidDataException("Esta rádio não oferece um stream HTTP ou HTTPS reproduzível.");
        var station = new RadioStation
        {
            CatalogId = id,
            Name = name,
            StreamUrl = stream,
            StreamUserAgent = IsUserAgentSafe(userAgent) ? userAgent : null,
            Location = GetString(root, "localizacao"),
            LogoSource = MakeLogoUrl(GetString(root, "url_logo"))
        };
        var contacts = new List<RadioContact>();
        if (root.TryGetProperty("contatos", out var contactItems) && contactItems.ValueKind == JsonValueKind.Array)
            foreach (var contact in contactItems.EnumerateArray())
                contacts.Add(new RadioContact(GetString(contact, "type") ?? string.Empty,
                    GetString(contact, "title") ?? string.Empty, GetString(contact, "detail"), GetString(contact, "value")));
        var schedule = new List<RadioProgram>();
        if (root.TryGetProperty("schedule", out var scheduleRoot) && scheduleRoot.ValueKind == JsonValueKind.Object
            && scheduleRoot.TryGetProperty("items", out var days) && days.ValueKind == JsonValueKind.Object)
        {
            foreach (var day in days.EnumerateObject())
            {
                if (!TryDay(day.Name, out var weekday) || day.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in day.Value.EnumerateArray())
                {
                    var title = GetString(entry, "title");
                    if (string.IsNullOrWhiteSpace(title)
                        || !TimeOnly.TryParseExact(GetString(entry, "start_time"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                        || !TimeOnly.TryParseExact(GetString(entry, "end_time"), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) continue;
                    schedule.Add(new RadioProgram(GetInt(entry, "id"), title, GetString(entry, "detail"), weekday, start, end));
                }
            }
        }
        var details = new RadiosNetStationDetails(station, GetString(root, "description"), GetString(root, "segmento"), contacts, schedule);
        lock (_cacheGate) _detailsCache[id] = (cacheDay, now.Offset, details);
        return details;
    }

    private static bool IsUserAgentSafe(string? value) => value is { Length: > 0 and <= 512 }
        && !value.Any(char.IsControl);

    private static bool TryDay(string value, out DayOfWeek day)
    {
        day = value switch
        {
            "Sun" => DayOfWeek.Sunday,
            "Mon" => DayOfWeek.Monday,
            "Tue" => DayOfWeek.Tuesday,
            "Wed" => DayOfWeek.Wednesday,
            "Thu" => DayOfWeek.Thursday,
            "Fri" => DayOfWeek.Friday,
            "Sat" => DayOfWeek.Saturday,
            _ => (DayOfWeek)(-1)
        };
        return day != (DayOfWeek)(-1);
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        var offset = DateTimeOffset.Now.ToString("zzz", CultureInfo.InvariantCulture);
        var separator = path.Contains('?') ? '&' : '?';
        using var response = await _http.GetAsync($"{path}{separator}app=android&lang=pt&tz={Uri.EscapeDataString(offset)}&v=28207", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() : null;

    private static int GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result : 0;

    private static string? MakeLogoUrl(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : LogoBaseUrl + Uri.EscapeDataString(Path.GetFileName(fileName));

    public void Dispose() => _http.Dispose();
}
