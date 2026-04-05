using System;
using System.Threading.Tasks;
using XTransferTool.Config;
using XTransferTool.Control;
using XTransferTool.Discovery;
using XTransferTool.Inbox;

namespace XTransferTool;

public static class AppServices
{
    private static string _profile = "default";

    public static string Profile => _profile;

    public static void SetStartupArgs(string[] args)
    {
        // Env var wins; args are convenience for multi-instance testing.
        var env = Environment.GetEnvironmentVariable("XTRANSFER_PROFILE");
        if (!string.IsNullOrWhiteSpace(env))
        {
            _profile = SanitizeProfile(env);
            return;
        }

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--profile" && i + 1 < args.Length)
            {
                _profile = SanitizeProfile(args[i + 1]);
                return;
            }

            if (a.StartsWith("--profile=", StringComparison.Ordinal))
            {
                _profile = SanitizeProfile(a["--profile=".Length..]);
                return;
            }
        }
    }

    private static string SanitizeProfile(string raw)
    {
        var s = raw.Trim();
        if (string.IsNullOrWhiteSpace(s))
            return "default";
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Length > 32 ? s[..32] : s;
    }

    private static DiscoveryOrchestrator? _discovery;
    private static ControlServerHost? _controlHost;
    private static InMemoryInboxRepository? _inbox;
    private static SettingsStore? _settings;
    private static UdpDiscoveryAnnouncer? _udpAnnouncer;
    private static UdpDiscoveryListener? _udpListener;

    public static IPeerDirectory? PeerDirectory => _discovery?.Directory;
    public static IInboxRepository? InboxRepository => _inbox;
    public static SettingsStore? Settings => _settings;
    public static int? ControlPort => _controlHost?.Port;

    public static void RefreshDiscovery()
    {
        _discovery?.Refresh();
    }

    public static async Task StartAsync()
    {
        if (_discovery is not null)
            return;

        _inbox = new InMemoryInboxRepository();
        _settings = new SettingsStore(_profile);
        var settings = await _settings.LoadAsync();

        _controlHost = new ControlServerHost(
            preferredPort: settings.Control.PreferredPort,
            fallbackToEphemeralPort: settings.Control.FallbackToEphemeralPort,
            inbox: _inbox,
            settings: _settings);
        await _controlHost.StartAsync();

        var os = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "windows"
            : "macos";
        var identity = new DeviceIdentity(
            Id: settings.Identity.DeviceId,
            Nickname: settings.Identity.Nickname,
            Tags: settings.Identity.Tags,
            Os: os,
            App: "xtransfer",
            Ver: "0.1.0",
            Capabilities: ["file", "remote"]);
        var announcer = new MdnsDiscoveryAnnouncer();
        var browser = new MdnsDiscoveryBrowser();
        var resolver = new MdnsPeerResolver(settings.Identity.DeviceId);
        var directory = new PeerDirectory();

        _discovery = new DiscoveryOrchestrator(announcer, browser, resolver, directory);

        if (settings.Network.EnableDiscovery)
            await _discovery.StartAsync(identity, controlPort: _controlHost.Port);

        if (settings.Network.EnableDiscovery)
            await StartUdpDiscoveryAsync(identity, _controlHost.Port);
    }

    private static async Task StartUdpDiscoveryAsync(DeviceIdentity identity, int controlPort)
    {
        _udpAnnouncer = new UdpDiscoveryAnnouncer();
        _udpListener = new UdpDiscoveryListener();

        _udpListener.AnnounceReceived += (_, ev) =>
        {
            try
            {
                if (_settings is null || _discovery is null)
                    return;

                var msg = ev.Announce;
                if (string.IsNullOrWhiteSpace(msg.Id) || string.Equals(msg.Id, _settings.Current.Identity.DeviceId, StringComparison.OrdinalIgnoreCase))
                    return;

                var ip = ev.RemoteEndPoint.Address.ToString();
                var peer = new ResolvedPeer(
                    Id: msg.Id,
                    Nickname: string.IsNullOrWhiteSpace(msg.Nickname) ? msg.Id[..Math.Min(6, msg.Id.Length)] : msg.Nickname,
                    Tags: msg.Tags ?? Array.Empty<string>(),
                    Addresses: [ip],
                    ControlPort: msg.ControlPort,
                    Capabilities: msg.Cap ?? Array.Empty<string>(),
                    Os: msg.Os ?? "",
                    Ver: msg.Ver ?? "",
                    LastSeenAt: DateTimeOffset.UtcNow,
                    InstanceName: "udp:" + msg.Id
                );

                _discovery.Directory.Upsert(peer);
            }
            catch
            {
            }
        };

        await _udpListener.StartAsync();
        await _udpAnnouncer.StartAsync(identity, controlPort);
    }

    public static async Task StopAsync()
    {
        if (_discovery is null)
            return;

        if (_udpListener is not null)
        {
            try { await _udpListener.StopAsync(); } catch { }
            _udpListener = null;
        }
        if (_udpAnnouncer is not null)
        {
            try { await _udpAnnouncer.StopAsync(); } catch { }
            _udpAnnouncer = null;
        }

        await _discovery.DisposeAsync();
        _discovery = null;

        if (_controlHost is not null)
        {
            await _controlHost.DisposeAsync();
            _controlHost = null;
        }

        _inbox = null;
        _settings = null;
    }
}

