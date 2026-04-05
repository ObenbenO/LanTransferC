using System;
using System.Runtime.InteropServices;

namespace XTransferTool.Discovery;

public sealed record DeviceIdentity(
    string Id,
    string Nickname,
    string[] Tags,
    string Os,
    string App,
    string Ver,
    string[] Capabilities
)
{
    public static DeviceIdentity DemoDefault()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "macos";
        return new DeviceIdentity(
            Id: Guid.NewGuid().ToString(),
            Nickname: "赵小明",
            Tags: ["会场1", "A片区"],
            Os: os,
            App: "xtransfer",
            Ver: "0.1.0",
            Capabilities: ["file", "remote"]
        );
    }

    public string InstanceName(string? suffix = null)
    {
        var shortId = Id.Length >= 6 ? Id[..6] : Id;
        var baseName = $"{Nickname}-{shortId}";
        return suffix is null ? baseName : $"{baseName}-{suffix}";
    }
}

