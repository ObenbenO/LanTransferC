using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XTransferTool.Discovery;

public static class MdnsTxtBuilder
{
    public static IReadOnlyDictionary<string, string> Build(DeviceIdentity identity, int controlPort)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = Trim(identity.Id, 64),
            // Makaretu.Dns TXTRecord currently only allows ASCII strings.
            // Use UTF-8 base64 encoding for non-ASCII display fields.
            ["nickname"] = EncodeAsciiSafe(Trim(identity.Nickname, 32)),
            ["tags"] = EncodeAsciiSafe(BuildTags(identity.Tags)),
            ["os"] = identity.Os,
            ["app"] = identity.App,
            ["ver"] = identity.Ver,
            ["cap"] = string.Join(',', identity.Capabilities ?? Array.Empty<string>()),
            ["controlPort"] = controlPort.ToString()
        };

        return EnforceBudget(dict, DiscoveryConstants.TxtBudgetBytes);
    }

    private static string BuildTags(string[] tags)
    {
        if (tags is null || tags.Length == 0)
            return string.Empty;

        // Use ';' separator per step-doc.
        var normalized = tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => Trim(t.Trim(), 64))
            .Take(8)
            .ToArray();

        return string.Join(';', normalized);
    }

    private static IReadOnlyDictionary<string, string> EnforceBudget(Dictionary<string, string> txt, int budgetBytes)
    {
        // Conservative sizing: sum of "k=v" + delimiter overhead in UTF-8.
        int SizeBytes(Dictionary<string, string> d)
            => d.Sum(kv => Encoding.UTF8.GetByteCount(kv.Key) + 1 + Encoding.UTF8.GetByteCount(kv.Value) + 1);

        if (SizeBytes(txt) <= budgetBytes)
            return txt;

        // First, shrink tags: keep first two tags (会场/片区) as priority.
        if (txt.TryGetValue("tags", out var tags) && !string.IsNullOrEmpty(tags))
        {
            var parts = tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 2)
            {
                txt["tags"] = string.Join(';', parts.Take(2));
            }
        }

        if (SizeBytes(txt) <= budgetBytes)
            return txt;

        // Drop non-essential fields in order.
        foreach (var key in new[] { "cap" })
        {
            txt.Remove(key);
            if (SizeBytes(txt) <= budgetBytes)
                return txt;
        }

        // As last resort, truncate nickname further.
        if (txt.TryGetValue("nickname", out var nn))
        {
            for (var max = 24; max >= 8; max -= 4)
            {
                txt["nickname"] = Trim(nn, max);
                if (SizeBytes(txt) <= budgetBytes)
                    return txt;
            }
        }

        return txt;
    }

    private static string EncodeAsciiSafe(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] > 0x7F)
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
                return "u8b64:" + b64;
            }
        }

        return s;
    }

    private static string Trim(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        return s.Length <= maxChars ? s : s[..maxChars];
    }
}

