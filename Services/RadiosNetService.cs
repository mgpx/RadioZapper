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
