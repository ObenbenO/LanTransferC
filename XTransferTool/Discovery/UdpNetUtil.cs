using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace XTransferTool.Discovery;

public static class UdpNetUtil
{
    public static IReadOnlyList<IPEndPoint> GetBroadcastEndpoints(int port)
    {
        var eps = new List<IPEndPoint>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var ipProps = nic.GetIPProperties();
            foreach (var ua in ipProps.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    continue;
                if (ua.IPv4Mask is null)
                    continue;

                var bcast = BroadcastAddress(ua.Address, ua.IPv4Mask);
                eps.Add(new IPEndPoint(bcast, port));
            }
        }

        // fallback: limited broadcast
        if (eps.Count == 0)
            eps.Add(new IPEndPoint(IPAddress.Broadcast, port));

        return eps.Distinct(new IPEndPointComparer()).ToList();
    }

    private static IPAddress BroadcastAddress(IPAddress address, IPAddress mask)
    {
        var ipBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var result = new byte[4];
        for (int i = 0; i < 4; i++)
            result[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));
        return new IPAddress(result);
    }

    private sealed class IPEndPointComparer : IEqualityComparer<IPEndPoint>
    {
        public bool Equals(IPEndPoint? x, IPEndPoint? y)
            => x is not null && y is not null && x.Port == y.Port && x.Address.Equals(y.Address);

        public int GetHashCode(IPEndPoint obj) => obj.Address.GetHashCode() ^ obj.Port;
    }
}

