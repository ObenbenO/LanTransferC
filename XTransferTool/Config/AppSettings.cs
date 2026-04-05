namespace XTransferTool.Config;

public sealed class AppSettings
{
    public IdentitySettings Identity { get; set; } = new();
    public ReceiveSettings Receive { get; set; } = new();
    public NetworkSettings Network { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public RemoteSettings Remote { get; set; } = new();
    public ControlSettings Control { get; set; } = new();
}

public sealed class IdentitySettings
{
    public string DeviceId { get; set; } = "";
    public string Nickname { get; set; } = "赵小明";
    public string[] Tags { get; set; } = ["会场1", "A片区"];
}

public sealed class ReceiveSettings
{
    public string DefaultFolder { get; set; } = "";
}

public sealed class NetworkSettings
{
    public bool EnableDiscovery { get; set; } = true;
}

public sealed class AppearanceSettings
{
    public string Theme { get; set; } = "Dark"; // Dark/Light/Default
}

public sealed class RemoteSettings
{
    public bool AllowRemoteControl { get; set; } = true;
}

public sealed class ControlSettings
{
    public int PreferredPort { get; set; } = 50051;
    public bool FallbackToEphemeralPort { get; set; } = true;
}

