using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace Pothole;

/// <summary>Points this PC's own DNS at Pothole and back. Changing it needs admin, so each switch shows a UAC prompt.</summary>
public static class PcDns
{
    static string BackupFile => Path.Combine(App.DataDir, "pc-dns-backup.json");

    // Unplugged adapters are skipped; only a failure on a connected one counts as an error.
    const string Prelude = """
        $ErrorActionPreference = 'Stop'
        $adapters = Get-NetAdapter -Physical | Where-Object Status -ne 'Not Present'
        function Try-Adapter($a, $do) { try { & $do } catch { if ($a.Status -eq 'Up') { throw } } }

        """;

    /// <summary>True when any active adapter already asks 127.0.0.1 for DNS.</summary>
    public static bool IsOn() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Any(n => n.GetIPProperties().DnsAddresses.Contains(IPAddress.Loopback));

    /// <summary>Returns null on success, or a plain-language reason it didn't happen.</summary>
    public static Task<string?> Set(bool on) => Task.Run(() =>
    {
        var backup = BackupFile.Replace("'", "''");
        // Before switching on, save any DNS servers the person typed in themselves (DHCP ones show as empty),
        // so switching off puts back exactly what was there. Physical adapters only: not VPNs or Hyper-V switches.
        var script = on
            ? $$"""
              $b = @{}
              foreach ($a in $adapters) {
                $g = $a.InterfaceGuid
                $v4 = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$g" -ErrorAction SilentlyContinue).NameServer
                $v6 = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\$g" -ErrorAction SilentlyContinue).NameServer
                $b[$g] = "$v4 $v6".Trim()
              }
              if (-not (Test-Path '{{backup}}')) { $b | ConvertTo-Json | Set-Content '{{backup}}' }
              foreach ($a in $adapters) { Try-Adapter $a { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses '127.0.0.1','::1' } }
              Clear-DnsClientCache
              """
            : $$"""
              $b = if (Test-Path '{{backup}}') { Get-Content '{{backup}}' -Raw | ConvertFrom-Json } else { $null }
              foreach ($a in $adapters) {
                $old = if ($b) { @("$($b.($a.InterfaceGuid))" -split '[ ,]+' | Where-Object { $_ -and $_ -ne '127.0.0.1' -and $_ -ne '::1' }) } else { @() }
                Try-Adapter $a {
                  Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses
                  if ($old.Count) { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses $old }
                }
              }
              Remove-Item '{{backup}}' -ErrorAction SilentlyContinue
              Clear-DnsClientCache
              """;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Prelude + script)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            if (!p.WaitForExit(60_000)) return "Windows took too long to change the setting.";
            App.Log($"This PC's DNS {(on ? "pointed at Pothole" : "put back")}, exit code {p.ExitCode}");
            return p.ExitCode == 0 ? null : "Windows couldn't change the DNS setting. See the log in the data folder.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "The change was cancelled, so nothing was changed.";
        }
    });
}
