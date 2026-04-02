using System.Text.RegularExpressions;

namespace SLH.Services;

public static class LocalChatParser
{
    private static readonly Regex Joined =
        new(@"^\s*\[\s*[\d\.\s:]+\s*\]\s*(?:EVE System|.*?)\s*>\s*(?:Channel join/leave:\s*)?(.+?)\s+has joined the channel", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Left =
        new(@"^\s*\[\s*[\d\.\s:]+\s*\]\s*(?:EVE System|.*?)\s*>\s*(?:Channel join/leave:\s*)?(.+?)\s+has left the channel", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses a line from an EVE chat log. Returns (name, joined?) where joined true = joined, false = left, null = ignore.</summary>
    public static (string Name, bool? Joined)? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var j = Joined.Match(line);
        if (j.Success)
            return (NormalizeName(j.Groups[1].Value), true);

        var l = Left.Match(line);
        if (l.Success)
            return (NormalizeName(l.Groups[1].Value), false);

        return null;
    }

    /// <summary>Plain list: one pilot name per non-empty line.</summary>
    public static IEnumerable<string> ParseNameList(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0)
                continue;
            var parsed = ParseLine(t);
            if (parsed != null)
            {
                if (parsed.Value.Joined == true)
                    yield return parsed.Value.Name;
                continue;
            }

            if (t.Contains('>') && t.Contains('['))
                continue;
            yield return NormalizeName(t);
        }
    }

    private static string NormalizeName(string s) => s.Trim().Trim('"').Trim();
}
