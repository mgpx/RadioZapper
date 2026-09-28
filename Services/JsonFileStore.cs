using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RadioZapper.Services;

public sealed class JsonFileStore(ILogger<JsonFileStore> logger)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task<T> ReadAsync<T>(string path, T fallback) where T : class
    {
        if (!File.Exists(path)) return fallback;

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options) ?? fallback;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Falha ao ler {Path}", path);
            try
            {
                var backup = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                File.Copy(path, backup);
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(backupError, "Não foi possível preservar {Path}", path);
            }
            return fallback;
        }
    }

    public async Task WriteAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options);
                await stream.FlushAsync();
            }
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Falha ao salvar {Path}", path);
            throw;
        }
    }
}
