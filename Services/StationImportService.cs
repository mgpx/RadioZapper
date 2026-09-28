using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RadioZapper.Services;

public sealed record StationImportResult(string? Name, string? StreamUrl, string? Location, string? LogoSource);

public sealed class StationImportService : IDisposable
{
    private const int MaxHtmlBytes = 1_000_000;
    private const int MaxPlaylistBytes = 64_000;
    private readonly HttpClient _http;
    private readonly ILogger<StationImportService> _logger;

    public StationImportService(ILogger<StationImportService> logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(12);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9");
    }

    public async Task<StationImportResult> ImportAsync(string input, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Informe um link HTTP ou HTTPS válido.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var requestToken = timeout.Token;

        if (uri.AbsolutePath.EndsWith(".pls", StringComparison.OrdinalIgnoreCase))
        {
            var playlist = await ReadPlaylistAsync(uri, requestToken);
            var stationId = GetPlaylistStationId(uri);
            if (stationId is null) return new(playlist.Title, playlist.StreamUrl, null, null);
            var details = await TryReadMetadataAsync(stationId.Value, null, requestToken, cancellationToken);
            return details is null ? new(playlist.Title, playlist.StreamUrl, null, null)
                : details with { StreamUrl = playlist.StreamUrl, Name = details.Name ?? playlist.Title };
        }

        var pageId = GetPageStationId(uri);
        if (pageId is null)
            throw new ArgumentException("Use uma página de rádio do Radios.com.br ou um link .pls.");

        (string StreamUrl, string? Title)? playlistResult = null;
        try
        {
            playlistResult = await ReadPlaylistAsync(
                new Uri($"https://www.radios.com.br/play/playlist/{pageId}/listen-radio.pls"), requestToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            _logger.LogWarning(ex, "Não foi possível obter a playlist da rádio {StationId}", pageId);
        }

        var metadata = await TryReadMetadataAsync(pageId.Value, uri, requestToken, cancellationToken);
        if (metadata is null && playlistResult is null)
            throw new InvalidDataException("Não foi possível obter o stream nem os dados da rádio. Tente o link PLS ou preencha manualmente.");
        return metadata is null ? new(playlistResult?.Title, playlistResult?.StreamUrl, null, null)
            : metadata with { StreamUrl = playlistResult?.StreamUrl, Name = metadata.Name ?? playlistResult?.Title };
    }

    private async Task<StationImportResult?> TryReadMetadataAsync(
        int stationId, Uri? pageUri, CancellationToken requestToken, CancellationToken callerToken)
    {
        try
        {
            return await ReadPageAsync(pageUri ?? new Uri($"https://www.radios.com.br/aovivo/radio/{stationId}"), requestToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (callerToken.IsCancellationRequested) throw;
            _logger.LogWarning(ex, "Não foi possível obter os dados no Radios.com.br para {StationId}", stationId);
        }

        try
        {
            return await ReadLegacyPageAsync(
                new Uri($"https://www.radiosnet.com/aovivo/radio/{stationId}"), requestToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            if (callerToken.IsCancellationRequested) throw;
            _logger.LogWarning(ex, "Não foi possível obter os dados alternativos para {StationId}", stationId);
            return null;
        }
    }

    private async Task<StationImportResult> ReadLegacyPageAsync(Uri uri, CancellationToken cancellationToken)
    {
        var html = await ReadTextAsync(uri, MaxHtmlBytes, cancellationToken);
        var title = WebUtility.HtmlDecode(Regex.Match(html, @"<title\b[^>]*>(?<value>.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["value"].Value).Trim();
        var match = Regex.Match(title,
            @"^(?<name>.+?)\s+-\s+(?<location>.+?\s*/\s*[A-Z]{2})\s+-\s+Brasil\s*\|\s*Radiosnet$",
            RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidDataException("A página alternativa não contém dados reconhecíveis da rádio.");
        return new(match.Groups["name"].Value.Trim(), null,
            match.Groups["location"].Value.Trim(), GetMeta(html, "og:image"));
    }

    private async Task<StationImportResult> ReadPageAsync(Uri uri, CancellationToken cancellationToken)
    {
        var html = await ReadTextAsync(uri, MaxHtmlBytes, cancellationToken);
        var name = GetMeta(html, "og:title");
        var logo = GetMeta(html, "og:image");
        var location = Regex.Matches(html, @"<h2\b[^>]*>(?<value>.*?)</h2>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(match => WebUtility.HtmlDecode(Regex.Replace(match.Groups["value"].Value, "<[^>]+>", "")).Trim())
            .FirstOrDefault(value => Regex.IsMatch(value, @"^.+?\s*/\s*[A-Z]{2}\s*-\s*Brasil$", RegexOptions.IgnoreCase));
        if (location is not null) location = Regex.Replace(location, @"\s*-\s*Brasil$", "", RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(location))
            throw new InvalidDataException("Não foi possível identificar os dados da página da rádio.");
        return new(name, null, location, logo);
    }

    private async Task<(string StreamUrl, string? Title)> ReadPlaylistAsync(Uri uri, CancellationToken cancellationToken)
    {
        var contents = await ReadTextAsync(uri, MaxPlaylistBytes, cancellationToken);
        if (!Regex.IsMatch(contents, @"^\s*\[playlist\]", RegexOptions.IgnoreCase))
            throw new InvalidDataException("O link não retornou uma playlist PLS válida.");

        foreach (Match entry in Regex.Matches(contents, @"^\s*File(?<index>\d+)\s*=\s*(?<url>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            var stream = entry.Groups["url"].Value.Trim();
            if (!StationService.IsStreamUrlValid(stream)) continue;
            var index = entry.Groups["index"].Value;
            var title = Regex.Match(contents, $@"^\s*Title{index}\s*=\s*(?<title>[^\r\n]+)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline).Groups["title"].Value.Trim();
            return (stream, string.IsNullOrWhiteSpace(title) ? null : title);
        }
        throw new InvalidDataException("A playlist não contém uma URL HTTP ou HTTPS de áudio.");
    }

    private async Task<string> ReadTextAsync(Uri uri, int limit, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length > limit)
            throw new InvalidDataException("O conteúdo do link é maior que o permitido.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("O conteúdo do link é maior que o permitido.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string? GetMeta(string html, string property)
    {
        foreach (Match tag in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attributes = Regex.Matches(tag.Value,
                "(?<key>[\\w:-]+)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)')",
                RegexOptions.IgnoreCase).Cast<Match>().ToDictionary(
                    match => match.Groups["key"].Value, match => match.Groups["value"].Value,
                    StringComparer.OrdinalIgnoreCase);
            if (attributes.TryGetValue("property", out var key) && key.Equals(property, StringComparison.OrdinalIgnoreCase)
                && attributes.TryGetValue("content", out var content))
                return WebUtility.HtmlDecode(content.Trim());
        }
        return null;
    }

    private static int? GetPlaylistStationId(Uri uri)
    {
        if (!IsRadiosHost(uri)) return null;
        var match = Regex.Match(uri.AbsolutePath, @"^/play/playlist/(?<id>\d+)/listen-radio\.pls$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["id"].Value, out var id) ? id : null;
    }

    private static int? GetPageStationId(Uri uri)
    {
        if (!IsRadiosHost(uri)) return null;
        var match = Regex.Match(uri.AbsolutePath, @"^/aovivo/(?:[^/]+/)?(?<id>\d+)/?$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["id"].Value, out var id) ? id : null;
    }

    private static bool IsRadiosHost(Uri uri) => uri.Host.Equals("radios.com.br", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("www.radios.com.br", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _http.Dispose();
}
