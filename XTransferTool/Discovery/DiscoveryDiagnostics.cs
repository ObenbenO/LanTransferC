using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace XTransferTool.Discovery;

/// <summary>
/// Console diagnostics for mDNS / LAN multi-NIC issues (Windows often advertises unexpected addresses).
/// </summary>
public static class DiscoveryDiagnostics
{
    public static IEnumerable<NetworkInterface> FilterMdnsInterfaces(IEnumerable<NetworkInterface> interfaces)
    {
        var nics = interfaces
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(n => n.SupportsMulticast)
            .ToList();

        bool LooksVirtual(NetworkInterface n)
        {
            var name = n.Name ?? "";
            var desc = n.Description ?? "";
            if (name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase))
                return true;
            if (desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
                return true;
            if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                return true;
            if (desc.Contains("VMware", StringComparison.OrdinalIgnoreCase))
                return true;
            if (desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase))
                return true;
            if (desc.Contains("TAP", StringComparison.OrdinalIgnoreCase))
                return true;
            if (desc.Contains("VPN", StringComparison.OrdinalIgnoreCase))
                return true;
            if (desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        if (OperatingSystem.IsWindows())
            nics = nics.Where(n => !LooksVirtual(n)).ToList();

        nics = nics
            .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            .ToList();

        static bool HasDefaultGateway(NetworkInterface n)
        {
            try
            {
                return n.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address is not null &&
                    g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(g.Address) &&
                    !g.Address.Equals(IPAddress.Any) &&
                    !g.Address.ToString().Equals("0.0.0.0", StringComparison.Ordinal));
            }
            catch
            {
                return false;
            }
        }

        var withGw = nics.Where(HasDefaultGateway).ToList();
        if (withGw.Count > 0)
            return withGw;

        return nics.Count > 0 ? nics : interfaces;
    }

    public static string FormatMdnsBindInterfacesForLog()
    {
        try
        {
            var parts = new List<string>();
            var nics = FilterMdnsInterfaces(NetworkInterface.GetAllNetworkInterfaces());
            foreach (var nic in nics)
            {
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(ua.Address))
                        continue;

                    var ip = ua.Address.ToString();
                    parts.Add($"{ip}<{nic.Name}>");
                }
            }

            return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
        }
        catch
        {
            return "(failed-to-enumerate)";
        }
    }

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
