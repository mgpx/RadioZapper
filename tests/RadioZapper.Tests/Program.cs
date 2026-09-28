using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RadioZapper.Models;
using RadioZapper.Services;

await TestSearchAndDetailsAsync();
await TestApiErrorsAsync();
await TestTemporaryPlaybackAsync();
Console.WriteLine("3 testes passaram.");

static async Task TestSearchAndDetailsAsync()
{
    var requests = new List<Uri>();
    using var service = new RadiosNetService(new StubHandler(request =>
    {
        requests.Add(request.RequestUri!);
        Assert(request.Headers.UserAgent.Count > 0, "User-Agent ausente");
        Assert(request.Headers.Accept.Any(value => value.MediaType == "application/json"), "Accept ausente");
        var body = request.RequestUri!.AbsolutePath.EndsWith("/auto/busca")
            ? """[{"id":-151},{"id":98,"title":"Rádio 93 FM","subtitle":"Boa Vista / RR - Brasil","url_logo":"radio98.jpg"}]"""
            : """{"id":98,"title":"Rádio 93 FM","localizacao":"Boa Vista / RR - Brasil","url_logo":"radio98.jpg","streams":[{"url":"ftp://inválido"},{"url":"https://stream.example/radio"}]}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }));
    var items = await service.SearchAsync("  jovem  ");
    Assert(items.Count == 1 && items[0].Id == 98 && items[0].Location == "Boa Vista / RR - Brasil", "Busca incorreta");
    Assert(requests[0].AbsolutePath.EndsWith("/auto/busca") && requests[0].Query.Contains("q=jovem")
        && !requests[0].Query.Contains("pg=") && !requests[0].Query.Contains("limit=")
        && requests[0].Query.Contains("app=android"), "Parâmetros incorretos");
    var station = await service.GetStationAsync(98);
    Assert(station.Name == "Rádio 93 FM" && station.StreamUrl == "https://stream.example/radio", "Detalhes incorretos");
    Assert(station.LogoSource == "https://img.radios.com.br/radio/md/radio98.jpg", "Logo incorreto");
}

static async Task TestTemporaryPlaybackAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "RadioZapper.Tests", Guid.NewGuid().ToString());
    Environment.SetEnvironmentVariable("RADIOZAPPER_DATA_DIR", root);
    try
    {
        var paths = new AppDataPaths();
        var files = new JsonFileStore(NullLogger<JsonFileStore>.Instance);
        var settings = new SettingsService(paths, files);
        var player = new StubPlayer();
        var service = new StationService(new StationRepository(paths, files), settings, player,
            NullLogger<StationService>.Instance);
        await service.InitializeAsync();
        var saved = new RadioStation { Name = "Salva", StreamUrl = "https://example.com/salva" };
        await service.AddAsync(saved);
        var draft = new RadioStation { Name = "Busca", StreamUrl = "https://example.com/busca" };
        await service.PlayTemporaryAsync(draft);
        Assert(service.CurrentStation?.Name == "Busca" && service.IsCurrentStationTemporary, "Rádio temporária não selecionada");
        Assert(service.Stations.Count == 1 && settings.Current.LastStationId is null, "Rádio temporária foi persistida");
        await service.PreviousAsync();
        Assert(service.CurrentStation?.Id == saved.Id, "Anterior não foi para a rádio salva");
        await service.PlayTemporaryAsync(draft);
        await service.NextAsync();
        Assert(service.CurrentStation?.Id == saved.Id, "Próxima não foi para a rádio salva");
        await service.PlayTemporaryAsync(draft);
        await service.AddAsync(draft);
        Assert(!service.IsCurrentStationTemporary && service.CurrentStation?.Id == draft.Id, "Cadastro não incorporou rádio atual");
        Assert(settings.Current.LastStationId == draft.Id && service.Stations.Count == 2, "Cadastro não foi persistido");
    }
    finally
    {
        Environment.SetEnvironmentVariable("RADIOZAPPER_DATA_DIR", null);
        Directory.Delete(root, true);
    }
}

static async Task TestApiErrorsAsync()
{
    using var noStream = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"id":98,"title":"Sem áudio","streams":[{"url":"ftp://invalid"}]}""")
    }));
    await ExpectAsync<InvalidDataException>(() => noStream.GetStationAsync(98), "Stream inválido foi aceito");

    using var apiError = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"results":{"error":{"user_message":"Busca indisponível"}}}""")
    }));
    await ExpectAsync<InvalidDataException>(() => apiError.SearchAsync("rádio"), "Formato inesperado foi aceito");
}

static async Task ExpectAsync<TException>(Func<Task> action, string message) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new Exception(message);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(response(request));
}

sealed class StubPlayer : IRadioPlayerService
{
    public event EventHandler<PlaybackChangedEventArgs>? PlaybackChanged { add { } remove { } }
    public double Volume { get; set; }
    public bool IsPlaying => State == PlaybackState.Playing;
    public PlaybackState State { get; private set; }
    public Task PlayAsync(RadioStation station) { State = PlaybackState.Playing; return Task.CompletedTask; }
    public Task StopAsync() { State = PlaybackState.Stopped; return Task.CompletedTask; }
    public Task PauseAsync() { State = PlaybackState.Paused; return Task.CompletedTask; }
    public void Dispose() { }
}
