using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using RadioZapper.Models;

namespace RadioZapper.Services;

public sealed record RadiosNetSearchItem(int Id, string Name, string? Location, string? LogoSource, string? Genres);
public sealed record RadiosNetSearchPage(IReadOnlyList<RadiosNetSearchItem> Items, int Page, int Pages);

public sealed class RadiosNetService : IDisposable
{
    private const string BaseUrl = "https://app.venganet.com/radiosnet/2.2/";
    private const string LogoBaseUrl = "https://img.radios.com.br/radio/md/";
    private readonly HttpClient _http;

    public RadiosNetService(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RadioZapper/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<RadiosNetSearchPage> SearchAsync(string query, int page = 1, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0) throw new ArgumentException("Informe o nome da rádio para pesquisar.");
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        using var document = await GetAsync($"busca/todos?q={Uri.EscapeDataString(query)}&pg={page}&limit=20", cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A API retornou uma busca em formato inesperado.");
        ThrowApiError(results);
        var items = new List<RadiosNetSearchItem>();
        if (results.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("items", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var id = GetInt(entry, "id");
                var name = GetString(entry, "title");
                if (id <= 0 || string.IsNullOrWhiteSpace(name)) continue;
                items.Add(new RadiosNetSearchItem(id, name, GetString(entry, "detail"),
                    MakeLogoUrl(GetString(entry, "url_logo")), GetString(entry, "extra")));
            }
        }
        return new RadiosNetSearchPage(items, GetInt(root, "page"), GetInt(root, "pages"));
    }

    public async Task<RadioStation> GetStationAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        using var document = await GetAsync($"radio/{id}", cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A API retornou detalhes em formato inesperado.");
        var name = GetString(root, "title");
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A API não informou o nome da rádio.");
        string? stream = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in streams.EnumerateArray())
            {
                var candidate = GetString(item, "url");
                if (!StationService.IsStreamUrlValid(candidate)) continue;
                stream = candidate;
                break;
            }
        }
        if (stream is null) throw new InvalidDataException("Esta rádio não oferece um stream HTTP ou HTTPS reproduzível.");
        return new RadioStation
        {
            Name = name,
            StreamUrl = stream,
            Location = GetString(root, "localizacao"),
            LogoSource = MakeLogoUrl(GetString(root, "url_logo"))
        };
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

    private static void ThrowApiError(JsonElement results)
    {
        if (!results.TryGetProperty("error", out var error) || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
        throw new InvalidDataException(GetString(error, "user_message") ?? "O RadiosNet não conseguiu concluir a busca.");
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
