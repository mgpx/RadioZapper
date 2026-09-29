using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RadioZapper.Models;
using RadioZapper.Services;

namespace RadioZapper.ViewModels;

public partial class RadioEditorViewModel : ObservableObject
{
    private readonly LogoService _logos;
    private readonly StationImportService _importer;
    private readonly RadiosNetService? _catalog;
    private readonly Guid _id;
    private readonly int _order;
    private string? _agentStreamUrl;
    private string? _streamUserAgent;
    private int? _agentCatalogId;

    public RadioEditorViewModel(RadioStation? station, LogoService logos, StationImportService importer,
        bool isNew = false, RadiosNetService? catalog = null)
    {
        _logos = logos;
        _importer = importer;
        _catalog = catalog;
        _id = station?.Id ?? Guid.NewGuid();
        _order = station?.Order ?? 0;
        CatalogIdText = station?.CatalogId?.ToString() ?? string.Empty;
        Name = station?.Name ?? string.Empty;
        StreamUrl = station?.StreamUrl ?? string.Empty;
        _agentStreamUrl = station?.StreamUrl;
        _streamUserAgent = station?.StreamUserAgent;
        _agentCatalogId = station?.CatalogId;
        Location = station?.Location ?? string.Empty;
        LogoSource = station?.LogoSource ?? string.Empty;
        IsFavorite = station?.IsFavorite ?? false;
        Title = station is null || isNew ? "Adicionar rádio" : "Editar rádio";
        IsImportVisible = station is null || isNew;
    }

    public event EventHandler<RadioStation>? Saved;
    public string Title { get; }
    public bool IsImportVisible { get; }

    [ObservableProperty] private string _importLink = string.Empty;
    [ObservableProperty] private string? _importStatus;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _catalogIdText = string.Empty;
    [ObservableProperty] private string _streamUrl = string.Empty;
    [ObservableProperty] private string _location = string.Empty;
    [ObservableProperty] private string _logoSource = string.Empty;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string? _errorMessage;

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (!IsImportVisible || IsImporting) return;
        ErrorMessage = null;
        ImportStatus = "Buscando dados da rádio...";
        IsImporting = true;
        try
        {
            var result = await _importer.ImportAsync(ImportLink);
            if (!string.IsNullOrWhiteSpace(result.Name)) Name = result.Name;
            if (!string.IsNullOrWhiteSpace(result.StreamUrl)) StreamUrl = result.StreamUrl;
            if (!string.IsNullOrWhiteSpace(result.Location)) Location = result.Location;
            if (!string.IsNullOrWhiteSpace(result.LogoSource)) LogoSource = result.LogoSource;
            if (result.CatalogId is { } id) CatalogIdText = id.ToString();
            _agentStreamUrl = result.StreamUrl;
            _streamUserAgent = result.StreamUserAgent;
            _agentCatalogId = result.CatalogId;
            ImportStatus = result.StreamUrl is null
                ? "Dados encontrados. Informe a URL do stream para salvar."
                : result.Name is null
                    ? "Stream encontrado. Confira o nome e os demais campos antes de salvar."
                    : "Dados encontrados. Confira os campos antes de salvar.";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or ArgumentException)
        {
            ImportStatus = null;
            ErrorMessage = ex is OperationCanceledException
                ? "A busca demorou demais. Tente novamente ou preencha manualmente."
                : $"Não foi possível importar: {ex.Message}";
        }
        finally { IsImporting = false; }
    }

    [RelayCommand]
    private async Task LookupCatalogAsync()
    {
        if (_catalog is null || IsImporting) return;
        if (!int.TryParse(CatalogIdText, out var id) || id <= 0)
        {
            ErrorMessage = "Informe um ID numérico válido do catálogo.";
            return;
        }
        ErrorMessage = null;
        ImportStatus = "Buscando dados no catálogo...";
        IsImporting = true;
        try
        {
            var station = await _catalog.GetStationAsync(id);
            if (string.IsNullOrWhiteSpace(Name)) Name = station.Name;
            if (string.IsNullOrWhiteSpace(StreamUrl)) StreamUrl = station.StreamUrl;
            if (string.IsNullOrWhiteSpace(Location)) Location = station.Location ?? string.Empty;
            if (string.IsNullOrWhiteSpace(LogoSource)) LogoSource = station.LogoSource ?? string.Empty;
            _agentStreamUrl = station.StreamUrl;
            _streamUserAgent = station.StreamUserAgent;
            _agentCatalogId = id;
            ImportStatus = "Dados encontrados. Seus campos já preenchidos foram preservados.";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException
                                   or System.Text.Json.JsonException)
        {
            ImportStatus = null;
            ErrorMessage = $"Não foi possível consultar o catálogo: {ex.Message}";
        }
        finally { IsImporting = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        if (IsImporting) { ErrorMessage = "Aguarde o fim da busca antes de salvar."; return; }
        int? catalogId = null;
        if (!string.IsNullOrWhiteSpace(CatalogIdText))
        {
            if (!int.TryParse(CatalogIdText.Trim(), out var parsedId) || parsedId <= 0)
            {
                ErrorMessage = "Informe um ID numérico válido do catálogo.";
                return;
            }
            catalogId = parsedId;
        }
        if (catalogId is not null && (_agentCatalogId != catalogId || _streamUserAgent is null))
            await LookupCatalogAsync();
        if (string.IsNullOrWhiteSpace(Name)) { ErrorMessage = "Informe o nome da rádio."; return; }
        if (!StationService.IsStreamUrlValid(StreamUrl))
        {
            ErrorMessage = "Informe uma URL HTTP ou HTTPS válida para o stream.";
            return;
        }
        try
        {
            var logo = await _logos.PrepareAsync(_id, LogoSource);
            Saved?.Invoke(this, new RadioStation
            {
                Id = _id,
                Name = Name.Trim(),
                StreamUrl = StreamUrl.Trim(),
                CatalogId = catalogId,
                StreamUserAgent = catalogId == _agentCatalogId && StreamUrl.Trim() == _agentStreamUrl
                    ? _streamUserAgent : null,
                Location = string.IsNullOrWhiteSpace(Location) ? null : Location.Trim(),
                LogoSource = logo,
                IsFavorite = IsFavorite,
                Order = _order
            });
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
    }
}
