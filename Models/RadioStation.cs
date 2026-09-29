namespace RadioZapper.Models;

public sealed class RadioStation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public int? CatalogId { get; set; }
    public string? StreamUserAgent { get; set; }
    public string? LogoSource { get; set; }
    public string? Location { get; set; }
    public bool IsFavorite { get; set; }
    public int Order { get; set; }

    public RadioStation Copy() => (RadioStation)MemberwiseClone();
}
