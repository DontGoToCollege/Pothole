using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Pothole;

public enum QueryResult { Allowed, Blocked, Failed }

public sealed record QueryEntry(DateTime Time, string Client, string Name, string Type, QueryResult Result, string Reason, int Ms);

/// <summary>A forwarding DNS server: blocked names get 0.0.0.0, everything else is passed to the upstream servers.</summary>
public sealed class DnsServer(Filter filter, Stats stats)
{
    const int Port = 53;
    readonly List<Socket> _udp = [];
    readonly List<TcpListener> _tcp = [];
    CancellationTokenSource? _cts;
    volatile IPEndPoint[] _upstreams = [];
    HashSet<IPAddress> _self = []; // this PC's addresses, so its own requests show as "This PC"

    public bool Running => _cts != null;
    /// <summary>The addresses actually listened on, for the status line.</summary>
    public List<IPAddress> Listening { get; } = [];

    public void SetUpstreams(IEnumerable<string> servers) =>
        _upstreams = servers.Select(s => IPEndPoint.TryParse(s.Trim(), out var ep) ? new IPEndPoint(ep.Address, ep.Port == 0 ? 53 : ep.Port) : null)
                            .OfType<IPEndPoint>().ToArray();

    /// <summary>Binds port 53 on every address; returns null on success or a plain-language reason.</summary>
    public string? Start()
    {
        if (Running) return null;
        _cts = new();
        _self = LocalAddresses().Append(IPAddress.IPv6Loopback).ToHashSet();
        // Windows' Internet Connection Sharing often holds 0.0.0.0:53, but each address can still be bound on its own.
        List<IPAddress> addrs = TryBind(IPAddress.Any) ? [IPAddress.Any] : LocalAddresses().Where(TryBind).ToList();
        if (_udp.Count == 0)
        {
            Stop();
            return "Port 53 is in use by another program, so Pothole can't listen for DNS requests. " +
                   "Close other DNS servers (another Pothole, Technitium, Pi-hole for Windows) and try again.";
        }
        // ::1 too, so Windows' IPv6 DNS setting can point at Pothole (the "Use on this PC" switch sets both)
        if (TryBind(IPAddress.IPv6Loopback)) addrs.Add(IPAddress.IPv6Loopback);
        Listening.Clear();
        Listening.AddRange(addrs);
        foreach (var s in _udp) _ = UdpLoop(s, _cts.Token);
        foreach (var l in _tcp) _ = TcpLoop(l, _cts.Token);
        App.Log("Listening on " + string.Join(", ", addrs));
        return null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        foreach (var s in _udp) s.Dispose();
        foreach (var l in _tcp) l.Stop();
        _udp.Clear();
        _tcp.Clear();
        Listening.Clear();
    }

    public static IEnumerable<IPAddress> LocalAddresses() =>
        new[] { IPAddress.Loopback }.Concat(NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.")))
        .Distinct();

    bool TryBind(IPAddress addr)
    {
        var s = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            s.ExclusiveAddressUse = true;
            s.Bind(new IPEndPoint(addr, Port));
            s.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0], null); // else one unreachable client breaks ReceiveFrom
            _udp.Add(s);
        }
        catch (SocketException) { s.Dispose(); return false; }
        try { var l = new TcpListener(addr, Port); l.Start(); _tcp.Add(l); }
        catch (SocketException ex) { App.Log($"TCP {addr}:53 unavailable ({ex.SocketErrorCode}); UDP only there"); }
        return true;
    }

    async Task UdpLoop(Socket s, CancellationToken ct)
    {
        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(s.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { App.Log("UDP receive: " + ex.Message); continue; }
            var query = buf.AsSpan(0, r.ReceivedBytes).ToArray();
            var client = (IPEndPoint)r.RemoteEndPoint;
            _ = Task.Run(async () =>
            {
                var reply = await Handle(query, client.Address, tcp: false);
                if (reply != null)
                    try { await s.SendToAsync(reply, SocketFlags.None, client); } catch { /* client gone */ }
            });
        }
    }

    async Task TcpLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { App.Log("TCP accept: " + ex.Message); continue; }
            _ = Task.Run(async () =>
            {
                using (c)
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var st = c.GetStream();
                    var addr = ((IPEndPoint)c.Client.RemoteEndPoint!).Address;
                    try
                    {
                        while (true)
                        {
                            idle.CancelAfter(10_000);
                            var query = await ReadFramed(st, idle.Token);
                            if (query == null) return;
                            var reply = await Handle(query, addr, tcp: true);
                            if (reply == null) return;
                            await WriteFramed(st, reply, idle.Token);
                        }
                    }
                    catch { /* client closed or timed out */ }
                }
            });
        }
    }

    static async Task<byte[]?> ReadFramed(Stream st, CancellationToken ct)
    {
        var len = new byte[2];
        try { await st.ReadExactlyAsync(len, ct); } catch (EndOfStreamException) { return null; }
        var msg = new byte[BinaryPrimitives.ReadUInt16BigEndian(len)];
        await st.ReadExactlyAsync(msg, ct);
        return msg;
    }

    static async Task WriteFramed(Stream st, byte[] msg, CancellationToken ct)
    {
        var framed = new byte[msg.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)msg.Length);
        msg.CopyTo(framed, 2);
        await st.WriteAsync(framed, ct);
    }

    async Task<byte[]?> Handle(byte[] q, IPAddress client, bool tcp)
    {
        if (!TryParseQuestion(q, out var name, out var qtype, out var qEnd)) return null; // not a DNS query: ignore
        var sw = Stopwatch.StartNew();
        byte[] reply;
        QueryResult result;
        var reason = filter.Check(name);
        if (reason != null) { reply = BlockedReply(q, qEnd, qtype); result = QueryResult.Blocked; }
        else if (await Forward(q, tcp) is { } fwd) { reply = fwd; result = QueryResult.Allowed; reason = ""; }
        else { reply = ErrorReply(q, qEnd, rcode: 2); result = QueryResult.Failed; reason = "No answer from upstream servers"; }
        var who = client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client;
        stats.Record(new QueryEntry(DateTime.Now, _self.Contains(who) ? "This PC" : who.ToString(), name, TypeName(qtype), result, reason, (int)sw.ElapsedMilliseconds));
        return reply;
    }

    async Task<byte[]?> Forward(byte[] q, bool tcp)
    {
        foreach (var up in _upstreams)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
                if (tcp)
                {
                    using var c = new TcpClient(up.AddressFamily);
                    await c.ConnectAsync(up, cts.Token);
                    var st = c.GetStream();
                    await WriteFramed(st, q, cts.Token);
                    var r = await ReadFramed(st, cts.Token);
                    if (r != null && r.Length >= 12 && r[0] == q[0] && r[1] == q[1]) return r;
                }
                else
                {
                    using var s = new Socket(up.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    await s.ConnectAsync(up, cts.Token);
                    await s.SendAsync(q, SocketFlags.None, cts.Token);
                    var buf = new byte[4096];
                    while (true)
                    {
                        int n = await s.ReceiveAsync(buf, SocketFlags.None, cts.Token);
                        if (n >= 12 && buf[0] == q[0] && buf[1] == q[1]) return buf[..n]; // same ID; ignore strays
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException) { /* try the next server */ }
        }
        return null;
    }

    public static bool TryParseQuestion(byte[] q, out string name, out ushort qtype, out int qEnd)
    {
        name = ""; qtype = 0; qEnd = 0;
        if (q.Length < 17 || (q[2] & 0x80) != 0 || BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(4)) == 0) return false;
        var sb = new StringBuilder();
        int i = 12;
        while (i < q.Length && q[i] != 0)
        {
            int len = q[i];
            if (len > 63 || i + 1 + len > q.Length) return false; // compression pointers never appear in a question
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(q, i + 1, len));
            i += 1 + len;
        }
        if (i + 5 > q.Length) return false;
        name = sb.ToString().ToLowerInvariant();
        qtype = BinaryPrimitives.ReadUInt16BigEndian(q.AsSpan(i + 1));
        qEnd = i + 5;
        return true;
    }

    /// <summary>Answers A with 0.0.0.0 and AAAA with ::, so the site simply fails to load; other types get an empty answer.</summary>
    static byte[] BlockedReply(byte[] q, int qEnd, ushort qtype)
    {
        int rdLen = qtype == 1 ? 4 : qtype == 28 ? 16 : 0;
        var r = ErrorReply(q, qEnd, 0, rdLen == 0 ? 0 : 12 + rdLen);
        if (rdLen == 0) return r;
        r[7] = 1; // ANCOUNT
        var a = r.AsSpan(qEnd);
        a[0] = 0xC0; a[1] = 0x0C; // name: pointer to the question
        BinaryPrimitives.WriteUInt16BigEndian(a[2..], qtype);
        BinaryPrimitives.WriteUInt16BigEndian(a[4..], 1); // IN
        BinaryPrimitives.WriteUInt32BigEndian(a[6..], 60); // TTL
        BinaryPrimitives.WriteUInt16BigEndian(a[10..], (ushort)rdLen); // address bytes stay zero
        return r;
    }

    /// <summary>Header + first question with the given response code, room left for <paramref name="extra"/> answer bytes.</summary>
    static byte[] ErrorReply(byte[] q, int qEnd, int rcode, int extra = 0)
    {
        var r = new byte[qEnd + extra];
        Array.Copy(q, r, qEnd);
        r[2] = (byte)(0x80 | (q[2] & 0x79)); // QR, keep opcode and RD
        r[3] = (byte)(0x80 | rcode);         // RA
        r[4] = 0; r[5] = 1;
        r.AsSpan(6, 6).Clear();
        return r;
    }

    static string TypeName(ushort t) => t switch
    {
        1 => "A", 2 => "NS", 5 => "CNAME", 6 => "SOA", 12 => "PTR", 15 => "MX", 16 => "TXT",
        28 => "AAAA", 33 => "SRV", 64 => "SVCB", 65 => "HTTPS", 255 => "ANY", _ => t.ToString(),
    };
}
