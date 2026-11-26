using System.IO;
using System.Text.Json;

namespace Pothole;

public sealed class BlockListSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; }
    public int Count { get; set; }
    public DateTime? Updated { get; set; }
}

public sealed class Settings
{
    public List<string> Upstreams { get; set; } = ["1.1.1.1", "1.0.0.1"];
    public List<BlockListSource> BlockLists { get; set; } =
    [
        new() { Name = "StevenBlack ads & malware", Url = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts", Enabled = true },
        new() { Name = "HaGeZi Light", Url = "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/wildcard/light-onlydomains.txt" },
        new() { Name = "HaGeZi Multi (stronger)", Url = "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/wildcard/multi-onlydomains.txt" },
        new() { Name = "OISD Small", Url = "https://small.oisd.nl/domainswild" },
    ];
    public List<string> Allowed { get; set; } = [];
    public List<string> Blocked { get; set; } = [];
    public bool BlockingEnabled { get; set; } = true;
    public bool StartServerOnLaunch { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public ChartSpan ChartSpan { get; set; } = ChartSpan.Hour;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static string FilePath => Path.Combine(App.DataDir, "settings.json");

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) { App.Log("Settings unreadable, using defaults: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        lock (Json)
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
    }
}
