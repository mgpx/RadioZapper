using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RadioZapper.Models;
using RadioZapper.Services;
using RadioZapper.ViewModels;

await TestSearchAndDetailsAsync();
await TestCatalogMetadataAsync();
await TestDetailsPresentationAsync();
await TestImportFromCatalogAsync();
await TestImportFallbackAsync();
await TestPlaylistCatalogPriorityAsync();
await TestPlaylistFallbackKeepsIdAsync();
await TestEditorPreservesCatalogAsync();
await TestLinkedStationPlaybackAsync();
await TestSavedUserAgentAvoidsCatalogAsync();
await TestPlayerSendsUserAgentAsync();
TestCurrentSchedule();
await TestApiErrorsAsync();
await TestTemporaryPlaybackAsync();
Console.WriteLine("14 testes passaram.");

static async Task TestDetailsPresentationAsync()
{
    using var catalog = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"id":12796,"title":"Difusora","description":"Descrição da rádio","segmento":"Notícias","streams":[{"url":"https://radio.example/stream"}],"contatos":[{"type":"email","title":"radio@example.com","detail":"Contato"}],"schedule":{"items":{"Mon":[{"id":1,"title":"Jornal","detail":"Equipe","start_time":"08:00","end_time":"09:00"}]}}}""")
    }));
    using var vm = new RadioDetailsViewModel(catalog, new RadioStation { Name = "Difusora", CatalogId = 12796 });
    await vm.LoadAsync();
    Assert(vm.Description == "Descrição da rádio" && vm.Segments == "Notícias", "Metadados não chegaram à janela");
    Assert(vm.Contacts.Single() == "Contato: radio@example.com", "Contato não foi apresentado");
    Assert(vm.Days.Single().Day == "Segunda-feira" && vm.Days.Single().Programs.Single().Title == "Jornal",
        "Grade semanal não foi apresentada");
}

static async Task TestSavedUserAgentAvoidsCatalogAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "RadioZapper.Tests", Guid.NewGuid().ToString());
    Environment.SetEnvironmentVariable("RADIOZAPPER_DATA_DIR", root);
    try
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var catalog = new RadiosNetService(new AsyncStubHandler(_ => response.Task));
        var paths = new AppDataPaths();
        var files = new JsonFileStore(NullLogger<JsonFileStore>.Instance);
        var player = new StubPlayer { FailingUserAgent = "Agente antigo" };
        var service = new StationService(new StationRepository(paths, files), new SettingsService(paths, files), player,
            NullLogger<StationService>.Instance, catalog);
        await service.InitializeAsync();
        var station = new RadioStation { Name = "Difusora", StreamUrl = "https://radio.example/stream",
            CatalogId = 12796, StreamUserAgent = "Agente antigo" };
        await service.AddAsync(station);
        await service.PlayAsync(station).WaitAsync(TimeSpan.FromSeconds(1));
        Assert(player.LastStation?.StreamUserAgent == "Agente antigo" && player.State == PlaybackState.Error,
            "Reprodução com User-Agent salvo esperou o catálogo");
        response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":12796,"title":"Difusora","streams":[{"url":"https://radio.example/stream","headers":{"User-Agent":"Agente novo"}}]}""")
        });
        for (var attempt = 0; attempt < 100 && player.State != PlaybackState.Playing; attempt++)
            await Task.Delay(20);
        Assert(player.State == PlaybackState.Playing && player.LastStation?.StreamUserAgent == "Agente novo"
            && player.PlayCount == 2, "User-Agent novo não recuperou a reprodução após falha");
        Assert((await new StationRepository(paths, files).LoadAsync()).Single().StreamUserAgent == "Agente novo",
            "User-Agent novo não foi salvo");
    }
    finally
    {
        Environment.SetEnvironmentVariable("RADIOZAPPER_DATA_DIR", null);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static async Task TestPlaylistCatalogPriorityAsync()
{
    using var catalog = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"id":12796,"title":"Difusora","streams":[{"url":"https://radio.example/stream","headers":{"User-Agent":"Agente"}}]}""")
    }));
    using var importer = new StationImportService(NullLogger<StationImportService>.Instance, catalog,
        new StubHandler(_ => throw new HttpRequestException("PLS indisponível")));
    var result = await importer.ImportAsync("https://www.radios.com.br/play/playlist/12796/listen-radio.pls");
    Assert(result.CatalogId == 12796 && result.StreamUserAgent == "Agente", "PLS falho impediu importação pela API");
}

static async Task TestPlaylistFallbackKeepsIdAsync()
{
    using var catalog = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
    using var importer = new StationImportService(NullLogger<StationImportService>.Instance, catalog,
        new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".pls")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[playlist]\nFile1=https://fallback.example/stream\n") }
            : throw new HttpRequestException("Página indisponível")));
    var result = await importer.ImportAsync("https://www.radios.com.br/play/playlist/12796/listen-radio.pls");
    Assert(result.CatalogId == 12796 && result.StreamUrl == "https://fallback.example/stream",
        "ID da rádio foi perdido quando apenas o PLS funcionou");
}

static async Task TestPlayerSendsUserAgentAsync()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var received = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var lines = new List<string>();
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) lines.Add(line);
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/aacp\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response);
        return lines;
    });
    using var player = new RadioPlayerService(NullLogger<RadioPlayerService>.Instance);
    await player.PlayAsync(new RadioStation { Name = "Teste", StreamUrl = $"http://127.0.0.1:{port}/stream",
        StreamUserAgent = "RadiosNet/2.8.2 (Java; Android)" });
    var headers = await received.WaitAsync(TimeSpan.FromSeconds(10));
    Assert(headers.Any(line => line.Equals("User-Agent: RadiosNet/2.8.2 (Java; Android)", StringComparison.OrdinalIgnoreCase)),
        "LibVLC não enviou o User-Agent da rádio");
    await player.StopAsync();
}

static async Task TestLinkedStationPlaybackAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "RadioZapper.Tests", Guid.NewGuid().ToString());
    Environment.SetEnvironmentVariable("RADIOZAPPER_DATA_DIR", root);
    try
    {
        using var catalog = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":12796,"title":"Difusora","streams":[{"url":"https://radio.example/stream","headers":{"User-Agent":"RadiosNet/2.8.2 (Java; Android)"}}]}""")
        }));
        var paths = new AppDataPaths();
        var files = new JsonFileStore(NullLogger<JsonFileStore>.Instance);
        var settings = new SettingsService(paths, files);
        var player = new StubPlayer();
        var service = new StationService(new StationRepository(paths, files), settings, player,
            NullLogger<StationService>.Instance, catalog);
        await service.InitializeAsync();
        var station = new RadioStation { Name = "Minha rádio", StreamUrl = "https://radio.example/stream", CatalogId = 12796 };
        await service.AddAsync(station);
        await service.PlayAsync(station);
        var played = player.LastStation ?? throw new Exception("Rádio não foi enviada ao reprodutor");
        Assert(played.StreamUserAgent == "RadiosNet/2.8.2 (Java; Android)", "Reprodução não recuperou User-Agent pelo ID");
        var reloaded = await new StationRepository(paths, files).LoadAsync();
        Assert(reloaded.Single().StreamUserAgent == played.StreamUserAgent, "User-Agent não persistido para uso posterior");
    }
    finally
    {
        Environment.SetEnvironmentVariable("RADIOZAPPER_DATA_DIR", null);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static async Task TestEditorPreservesCatalogAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "RadioZapper.Tests", Guid.NewGuid().ToString());
    Environment.SetEnvironmentVariable("RADIOZAPPER_DATA_DIR", root);
    try
    {
        using var logos = new LogoService(new AppDataPaths(), NullLogger<LogoService>.Instance);
        using var importer = new StationImportService(NullLogger<StationImportService>.Instance);
        using var catalog = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":12796,"title":"Nome da API","localizacao":"Caxias / RS","streams":[{"url":"https://radio.example/stream","headers":{"User-Agent":"Agente da API"}}]}""")
        }));
        var original = new RadioStation { Name = "Personalizada", StreamUrl = "https://radio.example/stream",
            CatalogId = 12796, StreamUserAgent = "RadiosNet/2.8.2 (Java; Android)" };
        var editor = new RadioEditorViewModel(original, logos, importer);
        RadioStation? saved = null;
        editor.Saved += (_, station) => saved = station;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert(saved?.CatalogId == 12796 && saved?.StreamUserAgent == original.StreamUserAgent, "Editor perdeu vínculo do catálogo");
        editor.StreamUrl = "https://outro.example/stream";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert(saved?.StreamUserAgent is null && saved?.CatalogId == 12796, "User-Agent antigo enviado para outra URL");
        var manual = new RadioEditorViewModel(new RadioStation { Name = "Nome pessoal", StreamUrl = "https://radio.example/stream" },
            logos, importer, catalog: catalog) { CatalogIdText = "12796" };
        RadioStation? linked = null;
        manual.Saved += (_, station) => linked = station;
        await manual.LookupCatalogCommand.ExecuteAsync(null);
        await manual.SaveCommand.ExecuteAsync(null);
        Assert(linked?.Name == "Nome pessoal" && linked.StreamUrl == "https://radio.example/stream"
            && linked.CatalogId == 12796 && linked.StreamUserAgent == "Agente da API", "Vínculo manual sobrescreveu campos ou perdeu o User-Agent");
        var direct = new RadioEditorViewModel(new RadioStation { Name = "Outra rádio", StreamUrl = "https://radio.example/stream" },
            logos, importer, catalog: catalog) { CatalogIdText = "12796" };
        RadioStation? savedDirect = null;
        direct.Saved += (_, station) => savedDirect = station;
        await direct.SaveCommand.ExecuteAsync(null);
        Assert(savedDirect?.StreamUserAgent == "Agente da API", "Salvar ID manual não buscou o User-Agent da API");
        using var offlineCatalog = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var offline = new RadioEditorViewModel(new RadioStation { Name = "Rádio manual", StreamUrl = "https://manual.example/stream" },
            logos, importer, catalog: offlineCatalog) { CatalogIdText = "12796" };
        RadioStation? savedOffline = null;
        offline.Saved += (_, station) => savedOffline = station;
        await offline.SaveCommand.ExecuteAsync(null);
        Assert(savedOffline?.CatalogId == 12796 && savedOffline.StreamUrl == "https://manual.example/stream",
            "Falha da API impediu salvar a rádio manual");
    }
    finally
    {
        Environment.SetEnvironmentVariable("RADIOZAPPER_DATA_DIR", null);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static async Task TestImportFallbackAsync()
{
    using var catalog = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
    using var importer = new StationImportService(NullLogger<StationImportService>.Instance, catalog,
        new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith(".pls")
                ? "[playlist]\nFile1=https://fallback.example/stream\n"
                : "<meta property=\"og:title\" content=\"Difusora\">")
        }));
    var result = await importer.ImportAsync("https://www.radios.com.br/aovivo/radio-difusora-caxiense-1250-am/12796");
    Assert(result.CatalogId == 12796 && result.StreamUrl == "https://fallback.example/stream"
        && result.StreamUserAgent is null, "Importação não preservou o fallback sem API");
}

static async Task TestImportFromCatalogAsync()
{
    using var catalog = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"id":12796,"title":"Difusora","streams":[{"url":"https://11.stmip.net:2020/stream","headers":{"User-Agent":"RadiosNet/2.8.2 (Java; Android)"}}]}""")
    }));
    using var importer = new StationImportService(NullLogger<StationImportService>.Instance, catalog,
        new StubHandler(_ => throw new Exception("A importação da página deve consultar a API primeiro.")));
    var result = await importer.ImportAsync("https://www.radios.com.br/aovivo/radio-difusora-caxiense-1250-am/12796");
    Assert(result.CatalogId == 12796 && result.StreamUserAgent == "RadiosNet/2.8.2 (Java; Android)", "Importação perdeu ID ou User-Agent");
    Assert(result.StreamUrl == "https://11.stmip.net:2020/stream", "Importação não usou o fluxo da API");
}

static async Task TestCatalogMetadataAsync()
{
    using var service = new RadiosNetService(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"id":12796,"title":"Difusora","localizacao":"Caxias / RS","url_logo":"logo.jpg","description":"Descrição","segmento":"Gospel","streams":[{"url":"https://11.stmip.net:2020/stream","headers":{"User-Agent":"RadiosNet/2.8.2 (Java; Android)"}}],"contatos":[{"type":"email","title":"Contato","detail":"Comercial","value":"contato@example.com"}],"schedule":{"items":{"Mon":[{"id":1,"title":"Programa A","detail":"Apresentador","start_time":"13:00","end_time":"17:00"}]}}}""")
    }));
    var details = await service.GetDetailsAsync(12796);
    Assert(details.Station.CatalogId == 12796 && details.Station.StreamUserAgent == "RadiosNet/2.8.2 (Java; Android)", "ID ou User-Agent não preservado");
    Assert(details.Description == "Descrição" && details.Segments == "Gospel", "Metadados não lidos");
    Assert(details.Contacts.Count == 1 && details.Contacts[0].Title == "Contato", "Contatos não lidos");
    Assert(details.Schedule.Count == 1 && details.Schedule[0].Day == DayOfWeek.Monday, "Grade não lida");
}

static void TestCurrentSchedule()
{
    var schedule = new[]
    {
        new RadioProgram(1, "Programa A", null, DayOfWeek.Monday, new TimeOnly(13, 0), new TimeOnly(17, 0)),
        new RadioProgram(2, "Programa B", null, DayOfWeek.Monday, new TimeOnly(15, 0), new TimeOnly(15, 15)),
        new RadioProgram(3, "Programa B", null, DayOfWeek.Monday, new TimeOnly(15, 0), new TimeOnly(15, 15)),
        new RadioProgram(4, "Madrugada", null, DayOfWeek.Monday, new TimeOnly(22, 0), TimeOnly.MinValue)
    };
    var active = ProgramSchedule.GetCurrent(schedule, new DateTime(2026, 9, 28, 15, 5, 0));
    Assert(active.Count == 2 && active[0].Title == "Programa A" && active[1].Title == "Programa B", "Sobreposição ou duplicata incorreta");
    Assert(ProgramSchedule.GetCurrent(schedule, new DateTime(2026, 9, 29, 0, 0, 0)).Count == 0, "Fim à meia-noite incorreto");
    Assert(ProgramSchedule.GetCurrent(schedule, new DateTime(2026, 9, 28, 23, 0, 0)).Single().Title == "Madrugada", "Programa noturno incorreto");
}

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

sealed class AsyncStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        response(request);
}

sealed class StubPlayer : IRadioPlayerService
{
    public event EventHandler<PlaybackChangedEventArgs>? PlaybackChanged { add { } remove { } }
    public double Volume { get; set; }
    public bool IsPlaying => State == PlaybackState.Playing;
    public PlaybackState State { get; private set; }
    public RadioStation? LastStation { get; private set; }
    public string? FailingUserAgent { get; set; }
    public int PlayCount { get; private set; }
    public Task PlayAsync(RadioStation station)
    {
        LastStation = station.Copy();
        PlayCount++;
        State = station.StreamUserAgent == FailingUserAgent && FailingUserAgent is not null
            ? PlaybackState.Error : PlaybackState.Playing;
        return Task.CompletedTask;
    }
    public Task StopAsync() { State = PlaybackState.Stopped; return Task.CompletedTask; }
    public Task PauseAsync() { State = PlaybackState.Paused; return Task.CompletedTask; }
    public void Dispose() { }
}
