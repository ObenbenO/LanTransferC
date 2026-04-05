using System.Text.Json.Serialization;

namespace XTransferTool.Discovery;

public sealed class UdpAnnounce
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "announce";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = "";

    [JsonPropertyName("tags")]
    public string[] Tags { get; set; } = [];

    [JsonPropertyName("os")]
    public string Os { get; set; } = "";

    [JsonPropertyName("ver")]
    public string Ver { get; set; } = "";

    [JsonPropertyName("controlPort")]
    public int ControlPort { get; set; }

    [JsonPropertyName("cap")]
    public string[] Cap { get; set; } = [];

    [JsonPropertyName("ts")]
    public long Ts { get; set; }
}

