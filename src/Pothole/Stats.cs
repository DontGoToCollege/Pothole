using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Pothole;

public enum ChartSpan { Minute, Hour, Day, Month, Year }

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
        /// <summary>Key: "yyyy-MM-dd HH:mm", kept for two hours.</summary>
        public Dictionary<string, long[]> Minutes { get; set; } = [];
        /// <summary>Key: "yyyy-MM-dd", kept for good (months and years are added up from these).</summary>
        public Dictionary<string, long[]> Days { get; set; } = [];
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
            Bump(d.Minutes, e.Time.ToString("yyyy-MM-dd HH:mm"), e.Result);
            Bump(d.Hours, e.Time.ToString("yyyy-MM-dd HH"), e.Result);
            Bump(d.Days, e.Time.ToString("yyyy-MM-dd"), e.Result);
        }
        (e.Result == QueryResult.Blocked ? d.BlockedSites : d.Sites).AddOrUpdate(e.Name, 1, (_, n) => n + 1);
        d.Devices.AddOrUpdate(e.Client, 1, (_, n) => n + 1);
        _recent.Enqueue(e);
        while (_recent.Count > RecentMax) _recent.TryDequeue(out _);
    }

    static void Bump(Dictionary<string, long[]> buckets, string key, QueryResult r)
    {
        if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = new long[2];
        b[0]++;
        if (r == QueryResult.Blocked) b[1]++;
    }

    public QueryEntry[] Recent() => _recent.Reverse().ToArray();

    /// <summary>Requests and blocked per minute, hour, day, month or year, oldest first, ending with the current one.</summary>
    public (DateTime Start, long Queries, long Blocked)[] Series(ChartSpan span)
    {
        var now = DateTime.Now;
        lock (_d)
        {
            (DateTime, long, long) From(Dictionary<string, long[]> b, DateTime t, string fmt) =>
                b.TryGetValue(t.ToString(fmt), out var v) ? (t, v[0], v[1]) : (t, 0L, 0L);
            // Months and years add up the days that start with "yyyy-MM" or "yyyy".
            (DateTime, long, long) Sum(DateTime t, string prefix)
            {
                long q = 0, bl = 0;
                foreach (var (k, v) in _d.Days) if (k.StartsWith(prefix)) { q += v[0]; bl += v[1]; }
                return (t, q, bl);
            }
            var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
            var month = new DateTime(now.Year, now.Month, 1);
            return span switch
            {
                ChartSpan.Minute => Enumerable.Range(0, 60).Select(i => From(_d.Minutes, minute.AddMinutes(i - 59), "yyyy-MM-dd HH:mm")).ToArray(),
                ChartSpan.Hour => Enumerable.Range(0, 24).Select(i => From(_d.Hours, minute.AddMinutes(-now.Minute).AddHours(i - 23), "yyyy-MM-dd HH")).ToArray(),
                ChartSpan.Day => Enumerable.Range(0, 30).Select(i => From(_d.Days, now.Date.AddDays(i - 29), "yyyy-MM-dd")).ToArray(),
                ChartSpan.Month => Enumerable.Range(0, 12).Select(i => month.AddMonths(i - 11)).Select(t => Sum(t, t.ToString("yyyy-MM"))).ToArray(),
                _ => Enumerable.Range(0, 5).Select(i => new DateTime(now.Year + i - 4, 1, 1)).Select(t => Sum(t, t.ToString("yyyy"))).ToArray(),
            };
        }
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
        if (_d.Days.Count == 0) // stats saved before 0.3.0 only had hours: rebuild the days from them
            foreach (var g in _d.Hours.GroupBy(h => h.Key[..10]))
                _d.Days[g.Key] = [g.Sum(h => h.Value[0]), g.Sum(h => h.Value[1])];
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
                var minuteCutoff = DateTime.Now.AddHours(-2).ToString("yyyy-MM-dd HH:mm");
                foreach (var k in d.Minutes.Keys.Where(k => string.CompareOrdinal(k, minuteCutoff) < 0).ToList()) d.Minutes.Remove(k);
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
