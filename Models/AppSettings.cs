namespace RadioZapper.Models;

public sealed class AppSettings
{
    public Guid? LastStationId { get; set; }
    public int Volume { get; set; } = 70;
    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    public string Theme { get; set; } = "System";
}
