namespace SLH.Services;

public sealed class DscanEntry
{
    public string RawLine { get; init; } = "";
    public string Name { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string Distance { get; init; } = "";
}

public static class DscanParser
{
    public static IReadOnlyList<DscanEntry> Parse(string text)
    {
        var list = new List<DscanEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var parts = line.Split('\t');
            if (parts.Length >= 3)
            {
                list.Add(new DscanEntry
                {
                    RawLine = line,
                    Name = parts[0].Trim(),
                    TypeName = parts[1].Trim(),
                    Distance = parts[2].Trim()
                });
                continue;
            }

            var squish = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (squish.Length >= 3)
            {
                list.Add(new DscanEntry
                {
                    RawLine = line,
                    Name = squish[0],
                    TypeName = squish[1],
                    Distance = string.Join(' ', squish.Skip(2))
                });
            }
            else
            {
                list.Add(new DscanEntry { RawLine = line, Name = line });
            }
        }

        return list;
    }
}
