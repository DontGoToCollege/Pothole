using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Pothole;

/// <summary>Decides whether a name is blocked. Lists are swapped in whole, so lookups never lock.</summary>
public sealed class Filter
{
    volatile HashSet<string> _lists = new(StringComparer.OrdinalIgnoreCase);
    volatile HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    volatile HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
    public volatile bool Enabled = true;
    public int ListCount => _lists.Count;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static string ListsDir => Directory.CreateDirectory(Path.Combine(App.DataDir, "lists")).FullName;

    /// <summary>null = allowed; otherwise why it was blocked. A rule on a domain covers all its subdomains.</summary>
    public string? Check(string name)
    {
        if (!Enabled) return null;
        HashSet<string> allowed = _allowed, blocked = _blocked, lists = _lists;
        for (var n = name; n.Length > 0; n = Parent(n))
            if (allowed.Contains(n)) return null;
        for (var n = name; n.Length > 0; n = Parent(n))
        {
            if (blocked.Contains(n)) return "Your block list";
            if (lists.Contains(n)) return "Block list";
        }
        return null;
    }

    static string Parent(string n) { int i = n.IndexOf('.'); return i < 0 ? "" : n[(i + 1)..]; }

    public void SetCustom(IEnumerable<string> allowed, IEnumerable<string> blocked)
    {
        _allowed = new(allowed.Select(Clean).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        _blocked = new(blocked.Select(Clean).OfType<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Loads enabled lists from disk, downloading any that are missing, or all of them when <paramref name="download"/>.</summary>
    public async Task ReloadLists(Settings s, bool download)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in s.BlockLists.Where(b => b.Enabled).ToList())
        {
            var file = Path.Combine(ListsDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(src.Url)))[..16] + ".txt");
            if (download || !File.Exists(file))
            {
                try
                {
                    var text = await Http.GetStringAsync(src.Url);
                    await File.WriteAllTextAsync(file + ".tmp", text);
                    File.Move(file + ".tmp", file, true);
                    src.Updated = DateTime.Now;
                }
                catch (Exception ex) { App.Log($"Download failed for {src.Url}: {ex.Message}"); }
            }
            if (!File.Exists(file)) continue;
            int before = set.Count, count = 0;
            foreach (var line in File.ReadLines(file))
                if (ParseLine(line) is { } d) { set.Add(d); count++; }
            src.Count = count;
            App.Log($"Loaded {src.Name}: {count:N0} domains ({set.Count - before:N0} new)");
        }
        _lists = set;
    }

    /// <summary>Reads hosts files ("0.0.0.0 ads.com"), plain domain lists and Adblock rules ("||ads.com^").</summary>
    public static string? ParseLine(string line)
    {
        var t = line.AsSpan().Trim();
        if (t.IsEmpty || t[0] is '#' or '!' or '[') return null;
        int hash = t.IndexOf('#');
        if (hash > 0) t = t[..hash].TrimEnd();
        string token;
        if (t.StartsWith("||"))
        {
            var rest = t[2..];
            int end = rest.IndexOfAny("^$/");
            // rules for a path inside a site can't be enforced by DNS, so skip them rather than block the whole site
            if (end >= 0 && (rest[end] == '/' || rest[end] == '^' && end + 1 < rest.Length && rest[end + 1] != '$')) return null;
            token = (end < 0 ? rest : rest[..end]).ToString();
        }
        else
        {
            var parts = t.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            token = parts.Length >= 2 && IPAddress.TryParse(parts[0], out _) ? parts[1] : parts[0];
        }
        return Clean(token);
    }

    public static string? Clean(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return null;
        d = d.Trim().TrimEnd('.').ToLowerInvariant();
        if (d.StartsWith("*.")) d = d[2..];
        if (d.Contains("://")) d = Uri.TryCreate(d, UriKind.Absolute, out var u) ? u.Host : ""; // someone pasted a web address
        if (!d.Contains('.') || d is "localhost.localdomain" || d.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')))
            return null;
        return d;
    }
}
