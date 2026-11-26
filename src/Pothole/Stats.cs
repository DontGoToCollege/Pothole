using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Pothole;

/// <summary>Traffic counters for the dashboard, saved to stats.json so they survive restarts.</summary>
public sealed class Stats
{
    public sealed class Data
    {
        public long Total { get; set; }
        public long Blocked { get; set; }
        public long Failed { get; set; }
        public DateTime Since { get; set; } = DateTime.Now;
        /// <summary>Key: hour as "yyyy-MM-dd HH"; value: [queries, blocked].</summary>
        public Dictionary<string, long[]> Hours { get; set; } = [];
        public ConcurrentDictionary<string, long> Sites { get; set; } = new();
        public ConcurrentDictionary<string, long> BlockedSites { get; set; } = new();
        public ConcurrentDictionary<string, long> Devices { get; set; } = new();
    }

    Data _d = new();
    readonly ConcurrentQueue<QueryEntry> _recent = new();
    const int RecentMax = 2000, TopKeep = 2000;
    static string FilePath => Path.Combine(App.DataDir, "stats.json");

    public Data Snapshot => _d;

    public void Record(QueryEntry e)
    {
        var d = _d;
        lock (d)
        {
            d.Total++;
            if (e.Result == QueryResult.Blocked) d.Blocked++;
            if (e.Result == QueryResult.Failed) d.Failed++;
            var key = e.Time.ToString("yyyy-MM-dd HH");
            if (!d.Hours.TryGetValue(key, out var h)) d.Hours[key] = h = new long[2];
            h[0]++;
            if (e.Result == QueryResult.Blocked) h[1]++;
        }
        (e.Result == QueryResult.Blocked ? d.BlockedSites : d.Sites).AddOrUpdate(e.Name, 1, (_, n) => n + 1);
        d.Devices.AddOrUpdate(e.Client, 1, (_, n) => n + 1);
        _recent.Enqueue(e);
        while (_recent.Count > RecentMax) _recent.TryDequeue(out _);
    }

    public QueryEntry[] Recent() => _recent.Reverse().ToArray();

    /// <summary>[queries, blocked] for each of the last 24 hours, oldest first.</summary>
    public (DateTime Hour, long Queries, long Blocked)[] Last24Hours()
    {
        var now = DateTime.Now;
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(-23);
        lock (_d)
            return Enumerable.Range(0, 24).Select(i =>
            {
                var t = start.AddHours(i);
                return _d.Hours.TryGetValue(t.ToString("yyyy-MM-dd HH"), out var h) ? (t, h[0], h[1]) : (t, 0L, 0L);
            }).ToArray();
    }

    public static List<KeyValuePair<string, long>> Top(ConcurrentDictionary<string, long> d, int n) =>
        d.OrderByDescending(kv => kv.Value).Take(n).ToList();

    public void Reset()
    {
        _d = new Data();
        _recent.Clear();
        Save();
    }

    public void Load()
    {
        try { if (File.Exists(FilePath)) _d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) { App.Log("Stats unreadable, starting fresh: " + ex.Message); }
    }

    public void Save()
    {
        try
        {
            var d = _d;
            string json;
            lock (d)
            {
                var cutoff = DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd HH");
                foreach (var k in d.Hours.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) d.Hours.Remove(k);
                Trim(d.Sites); Trim(d.BlockedSites); Trim(d.Devices);
                json = JsonSerializer.Serialize(d);
            }
            File.WriteAllText(FilePath + ".tmp", json);
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception ex) { App.Log("Saving stats failed: " + ex.Message); }
    }

    // shortcut: rare sites beyond the top 2,000 are dropped at each save so the file stays small; their counts are lost.
    static void Trim(ConcurrentDictionary<string, long> d)
    {
        if (d.Count <= TopKeep) return;
        var floor = d.Values.OrderByDescending(v => v).ElementAt(TopKeep);
        foreach (var kv in d) if (kv.Value <= floor) d.TryRemove(kv.Key, out _);
    }
}
