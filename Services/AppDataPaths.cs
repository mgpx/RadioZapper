namespace RadioZapper.Services;

public sealed class AppDataPaths
{
    public AppDataPaths()
    {
        var developmentRoot = Environment.GetEnvironmentVariable("RADIOZAPPER_DATA_DIR");
        Root = string.IsNullOrWhiteSpace(developmentRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RadioZapper")
            : Path.GetFullPath(developmentRoot);
        Logos = Path.Combine(Root, "logos");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string Logos { get; }
    public string Stations => Path.Combine(Root, "stations.json");
    public string Settings => Path.Combine(Root, "settings.json");
}
