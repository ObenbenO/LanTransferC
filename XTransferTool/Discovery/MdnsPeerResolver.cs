using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;

namespace XTransferTool.Discovery;

public sealed class MdnsPeerResolver : IPeerResolver
{
    private readonly string? _localId;

    public MdnsPeerResolver(string? localId = null)
    {
        _localId = string.IsNullOrWhiteSpace(localId) ? null : localId.Trim();
    }

    public Task<ResolvedPeer?> TryResolveAsync(DiscoveredPeerEvent discovered, CancellationToken ct = default)
    {
        // For Makaretu, the discovery event includes a DNS Message with additional records.
        if (discovered.Raw is not Message msg)
            return Task.FromResult<ResolvedPeer?>(null);

        var instanceFqdn = discovered.InstanceName.TrimEnd('.');

        // SRV: instance -> target host + port
        var srv = msg.Answers
            .Concat(msg.AdditionalRecords)
            .OfType<SRVRecord>()
            .FirstOrDefault(r => SameName(r.Name, instanceFqdn));

        // TXT: properties
        var txt = msg.Answers
            .Concat(msg.AdditionalRecords)
            .OfType<TXTRecord>()
            .FirstOrDefault(r => SameName(r.Name, instanceFqdn));

        if (srv is null || txt is null)
        {
            // Debug aid: makes it obvious whether we are receiving only PTR,
            // or receiving SRV/TXT but names don't match.
            Console.WriteLine($"[discovery] resolve-miss instance={instanceFqdn} srv={(srv is null ? 0 : 1)} txt={(txt is null ? 0 : 1)} ans={msg.Answers.Count} add={msg.AdditionalRecords.Count}");
            return Task.FromResult<ResolvedPeer?>(null);
        }

        var controlPort = srv.Port;

        // Addresses: A/AAAA of target host
        var host = srv.Target?.ToString().TrimEnd('.') ?? string.Empty;
        var ips = msg.Answers
            .Concat(msg.AdditionalRecords)
            .Where(r => SameName(r.Name, host))
            .SelectMany(r => r switch
            {
                ARecord a => new[] { a.Address },
                AAAARecord aaaa => new[] { aaaa.Address },
                _ => Array.Empty<IPAddress>()
            })
            .Distinct()
            .ToArray();

        var props = ParseTxt(txt);

        if (!props.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
            return Task.FromResult<ResolvedPeer?>(null);

        // Do not list ourselves.
        if (_localId is not null && string.Equals(_localId, id, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<ResolvedPeer?>(null);

        props.TryGetValue("nickname", out var nickname);
        props.TryGetValue("tags", out var tagsRaw);
        props.TryGetValue("os", out var os);
        props.TryGetValue("ver", out var ver);
        props.TryGetValue("cap", out var capRaw);

        nickname = DecodeIfNeeded(nickname);
        tagsRaw = DecodeIfNeeded(tagsRaw);

        var tags = (tagsRaw ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToArray();

        var caps = (capRaw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(16)
            .ToArray();

        var addresses = SortAddresses(ips).Select(a => a.ToString()).ToArray();

        var idShort = id.Length >= 8 ? id[..8] : id;
        Console.WriteLine(
            $"[discovery] resolved peer id={idShort}... nick={nickname ?? "?"} os={os} controlPort={controlPort} " +
            $"srvTarget={host} addresses=[{string.Join(", ", addresses)}] " +
            $"(gRPC uses first; order: same-subnet-as-this-machine > RFC1918 > other IPv4 > 169.254 > IPv6)");

        return Task.FromResult<ResolvedPeer?>(new ResolvedPeer(
            Id: id,
            Nickname: nickname ?? id[..Math.Min(6, id.Length)],
            Tags: tags,
            Addresses: addresses,
            ControlPort: controlPort,
            Capabilities: caps,
            Os: os ?? string.Empty,
            Ver: ver ?? string.Empty,
            LastSeenAt: discovered.SeenAt,
            InstanceName: discovered.InstanceName
        ));
    }

    private static bool SameName(DomainName? dn, DomainName? other)
        => dn is not null && other is not null &&
           string.Equals(dn.ToString().TrimEnd('.'), other.ToString().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static bool SameName(DomainName? dn, string? fqdnOrHost)
        => dn is not null && !string.IsNullOrWhiteSpace(fqdnOrHost) &&
           string.Equals(dn.ToString().TrimEnd('.'), fqdnOrHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseTxt(TXTRecord txt)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in txt.Strings ?? [])
        {
            var idx = s.IndexOf('=');
            if (idx <= 0)
                continue;
            var key = s[..idx];
            var value = s[(idx + 1)..];
            dict[key] = value;
        }
        return dict;
    }

    private static string? DecodeIfNeeded(string? value)
    {
        const string prefix = "u8b64:";
        if (string.IsNullOrWhiteSpace(value))
            return value;
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            return value;

        try
        {
            var bytes = Convert.FromBase64String(value.Substring(prefix.Length));
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return value;
        }
    }

    private static IEnumerable<IPAddress> SortAddresses(IEnumerable<IPAddress> ips)
    {
        var localSubnets = BuildLocalIpv4Subnets();
        return ips
            .Distinct()
            .OrderByDescending(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .ThenByDescending(ip => ip.AddressFamily == AddressFamily.InterNetwork
                ? Ipv4PreferenceRank(ip, localSubnets)
                : 0)
            .ThenBy(ip => ip.ToString(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Higher = try first. Fixes multi-NIC Windows often publishing VPN/virtual + LAN; we pick co-subnet with this host.
    /// </summary>
    private static int Ipv4PreferenceRank(IPAddress ip, List<(IPAddress Addr, IPAddress Mask)> localSubnets)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4)
            return 0;

        if (b[0] == 169 && b[1] == 254)
            return 1;

        if (localSubnets.Count > 0 && localSubnets.Any(s => SameSubnetIpv4(ip, s.Addr, s.Mask)))
            return 1000;

        if (IsPrivateRfc1918Ipv4(ip))
            return 500;

        return 100;
    }

    private static List<(IPAddress Addr, IPAddress Mask)> BuildLocalIpv4Subnets()
    {
        var list = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(ua.Address))
                        continue;
                    if (ua.IPv4Mask is null)
                        continue;

                    list.Add((ua.Address, ua.IPv4Mask));
                }
            }
        }
        catch
        {
            // ignore; fall back to string sort only
        }

        return list;
    }

    private static bool SameSubnetIpv4(IPAddress ip, IPAddress iface, IPAddress mask)
    {
        var ipB = ip.GetAddressBytes();
        var ifB = iface.GetAddressBytes();
        var mB = mask.GetAddressBytes();
        if (ipB.Length != 4 || ifB.Length != 4 || mB.Length != 4)
            return false;

        for (var i = 0; i < 4; i++)
        {
            if ((ipB[i] & mB[i]) != (ifB[i] & mB[i]))
                return false;
        }

        return true;
    }

    private static bool IsPrivateRfc1918Ipv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4)
            return false;
        if (b[0] == 10)
            return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            return true;
        if (b[0] == 192 && b[1] == 168)
            return true;
        return false;
    }
}

