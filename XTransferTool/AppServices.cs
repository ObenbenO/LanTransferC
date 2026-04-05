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
                _profile = SanitizeProfile(a.Substring("--profile=".Length));
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

    public static IPeerDirectory? PeerDirectory => _discovery?.Directory;
    public static IInboxRepository? InboxRepository => _inbox;
    public static SettingsStore? Settings => _settings;

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
    }

    public static async Task StopAsync()
    {
        if (_discovery is null)
            return;

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

