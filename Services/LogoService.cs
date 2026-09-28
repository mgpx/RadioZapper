using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace RadioZapper.Services;

public sealed class LogoService(AppDataPaths paths, ILogger<LogoService> logger) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public async Task<string?> PrepareAsync(Guid stationId, string? source)
    {
        source = source?.Trim();
        if (string.IsNullOrEmpty(source)) return null;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return source;
        if (uri?.IsFile == true) source = uri.LocalPath;
        if (source.StartsWith("logos/", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(source) == source[6..]) return source;
        if (!File.Exists(source)) throw new ArgumentException("A imagem local não foi encontrada.");
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
            throw new ArgumentException("Escolha uma imagem PNG, JPG, WebP ou BMP.");
        Directory.CreateDirectory(paths.Logos);
        var destination = Path.Combine(paths.Logos, stationId + extension);
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return "logos/" + stationId + extension;
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(destination))
            await input.CopyToAsync(output);
        return "logos/" + stationId + extension;
    }

    public async Task<Bitmap?> LoadAsync(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        try
        {
            byte[] bytes;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 5_000_000) return null;
                await using var remote = await response.Content.ReadAsStreamAsync();
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await remote.ReadAsync(buffer)) > 0)
                {
                    if (output.Length + read > 5_000_000) return null;
                    output.Write(buffer, 0, read);
                }
                bytes = output.ToArray();
            }
            else
            {
                if (uri?.IsFile == true) source = uri.LocalPath;
                var path = source.StartsWith("logos/", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(paths.Root, source.Replace('/', Path.DirectorySeparatorChar)) : source;
                bytes = await File.ReadAllBytesAsync(path);
            }
            return await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            });
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Não foi possível carregar o logotipo {Source}", source);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
