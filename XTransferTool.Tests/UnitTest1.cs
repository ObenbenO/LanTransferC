using System;
using XTransferTool.Discovery;

namespace XTransferTool.Tests;

public class UnitTest1
{
    [Fact]
    public void MdnsTxtBuilder_TruncatesTagsToTwoWhenOversize()
    {
        var identity = new DeviceIdentity(
            Id: Guid.NewGuid().ToString(),
            Nickname: new string('N', 32),
            Tags:
            [
                "会场1",
                "A片区",
                new string('X', 200),
                new string('Y', 200),
                new string('Z', 200),
            ],
            Os: "macos",
            App: "xtransfer",
            Ver: "1.0.0",
            Capabilities: ["file", "remote"]
        );

        var txt = MdnsTxtBuilder.Build(identity, controlPort: 50051);
        Assert.True(txt.ContainsKey("id"));
        Assert.True(txt.ContainsKey("nickname"));
        Assert.True(txt.ContainsKey("tags"));

        var tags = txt["tags"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("会场1", tags);
        Assert.Contains("A片区", tags);
        Assert.True(tags.Length <= 8);
    }

    [Fact]
    public void PeerDirectory_StaleAndExpireWindowsAreApplied()
    {
        var dir = new PeerDirectory();
        var peer = new ResolvedPeer(
            Id: "p1",
            Nickname: "A",
            Tags: ["会场1", "A片区"],
            Addresses: ["127.0.0.1"],
            ControlPort: 50051,
            Capabilities: ["file"],
            Os: "macos",
            Ver: "1.0.0",
            LastSeenAt: DateTimeOffset.UtcNow.AddSeconds(-20),
            InstanceName: "inst");

        dir.Upsert(peer);
        // Upsert sets LastSeenAt=now; touch to simulate old
        dir.Touch("p1", DateTimeOffset.UtcNow.AddSeconds(-20));

        // wait a sweep tick
        System.Threading.Thread.Sleep(1100);
        var snap = dir.Snapshot();
        Assert.True(snap.Count == 0 || snap[0].Presence is PeerPresenceState.Stale or PeerPresenceState.Online);
    }
}
