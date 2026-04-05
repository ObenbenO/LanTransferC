using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XTransferTool.Config;

public sealed class SettingsStore
{
    private readonly object _gate = new();
    private AppSettings _current = new();

    public string Profile { get; }
    public string SettingsPath { get; }

    public SettingsStore(string profile = "default")
    {
        Profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile.Trim();
        SettingsPath = ResolveSettingsPath(Profile);
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
        private set { lock (_gate) _current = value; }
    }

    public event EventHandler<AppSettings>? Changed;

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

        if (!File.Exists(SettingsPath))
        {
            var defaults = CreateDefault(Profile);
            await SaveAsync(defaults, ct);
            Current = defaults;
            return defaults;
        }

        await using var fs = File.OpenRead(SettingsPath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(fs, JsonOptions(), ct) ?? CreateDefault(Profile);

        settings.Identity.DeviceId = string.IsNullOrWhiteSpace(settings.Identity.DeviceId)
            ? Guid.NewGuid().ToString()
            : settings.Identity.DeviceId;

        if (string.IsNullOrWhiteSpace(settings.Identity.Nickname))
            settings.Identity.Nickname = "赵小明";

        if (string.IsNullOrWhiteSpace(settings.Receive.DefaultFolder))
            settings.Receive.DefaultFolder = DefaultReceiveFolder(Profile);

        Current = settings;
        Changed?.Invoke(this, settings);
        return settings;
    }

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(settings, JsonOptions());
        return File.WriteAllTextAsync(SettingsPath, json, ct);
    }

    public async Task UpdateAsync(Action<AppSettings> mutator, CancellationToken ct = default)
    {
        var copy = Clone(Current);
        mutator(copy);
        await SaveAsync(copy, ct);
        Current = copy;
        Changed?.Invoke(this, copy);
    }

    public static AppSettings Clone(AppSettings src)
    {
        return new AppSettings
        {
            Identity = new IdentitySettings
            {
                DeviceId = src.Identity.DeviceId,
                Nickname = src.Identity.Nickname,
                Tags = (string[])src.Identity.Tags.Clone()
            },
            Receive = new ReceiveSettings { DefaultFolder = src.Receive.DefaultFolder },
            Network = new NetworkSettings { EnableDiscovery = src.Network.EnableDiscovery },
            Appearance = new AppearanceSettings { Theme = src.Appearance.Theme },
            Remote = new RemoteSettings { AllowRemoteControl = src.Remote.AllowRemoteControl },
            Control = new ControlSettings
            {
                PreferredPort = src.Control.PreferredPort,
                FallbackToEphemeralPort = src.Control.FallbackToEphemeralPort
            }
        };
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static AppSettings CreateDefault(string profile)
    {
        return new AppSettings
        {
            Identity = new IdentitySettings
            {
                DeviceId = Guid.NewGuid().ToString(),
                Nickname = profile == "default" ? "赵小明" : $"赵小明-{profile}",
                Tags = ["会场1", "A片区"]
            },
            Receive = new ReceiveSettings { DefaultFolder = DefaultReceiveFolder(profile) },
            Network = new NetworkSettings { EnableDiscovery = true },
            Appearance = new AppearanceSettings { Theme = "Dark" },
            Remote = new RemoteSettings { AllowRemoteControl = true },
            Control = new ControlSettings { PreferredPort = 50051, FallbackToEphemeralPort = true }
        };
    }

    private static string ResolveSettingsPath(string profile)
    {
        // Prefer "portable" mode: save next to executable if writable.
        // This matches the user's expectation: install dir (or subdir) holds personal data when possible.
        var portable = Path.Combine(AppContext.BaseDirectory, "data", profile, "appsettings.json");
        if (CanWriteTo(Path.GetDirectoryName(portable)!))
            return portable;

        // Fallback to OS conventional per-user data directory.
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "XTransferTool", profile, "appsettings.json");
    }

    private static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DefaultReceiveFolder(string profile)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return AppContext.BaseDirectory;
        return Path.Combine(home, "Downloads", "XTransferTool", profile);
    }
}

