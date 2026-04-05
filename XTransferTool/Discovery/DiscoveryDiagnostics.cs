using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;

namespace XTransferTool.Discovery;

/// <summary>
/// Console diagnostics for mDNS / LAN multi-NIC issues (Windows often advertises unexpected addresses).
/// </summary>
public static class DiscoveryDiagnostics
{
    /// <summary>
    /// Lists non-loopback unicast IPv4 per adapter for log lines; link-local 169.254.x.x tagged.
    /// </summary>
    public static string FormatLocalUnicastIPv4ForLog()
    {
        try
        {
            var parts = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(ua.Address))
                        continue;

                    var ip = ua.Address.ToString();
                    var tag = ip.StartsWith("169.254.", StringComparison.Ordinal) ? "link-local" : nic.Name;
                    parts.Add($"{ip}<{tag}>");
                }
            }

            return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
        }
        catch
        {
            return "(failed-to-enumerate)";
        }
    }
}
