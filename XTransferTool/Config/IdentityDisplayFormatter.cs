namespace XTransferTool.Config;

/// <summary>
/// Same rules as the main window identity line: 昵称（标签1 / 标签2）.
/// </summary>
public static class IdentityDisplayFormatter
{
    public static string FormatSummary(AppSettings? s)
    {
        if (s is null)
            return "（未加载配置）";

        return FormatSummary(s.Identity);
    }

    public static string FormatSummary(IdentitySettings identity)
    {
        var nickname = string.IsNullOrWhiteSpace(identity.Nickname) ? "未命名" : identity.Nickname.Trim();
        var tags = identity.Tags ?? [];
        var tagSummary = tags.Length switch
        {
            0 => "",
            1 => tags[0],
            _ => $"{tags[0]} / {tags[1]}"
        };

        return string.IsNullOrWhiteSpace(tagSummary) ? nickname : $"{nickname}（{tagSummary}）";
    }

    public static string SenderFromId(AppSettings? s)
    {
        var id = s?.Identity.DeviceId?.Trim();
        return string.IsNullOrWhiteSpace(id) ? "unknown" : id;
    }
}
