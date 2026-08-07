using System.Text;

namespace ActivityExplorer.Infrastructure.Import;

internal static class CsvTable
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var rows = Parse(reader).ToArray();
        if (rows.Length == 0)
        {
            return [];
        }

        var headers = rows[0];
        return rows.Skip(1)
            .Where(x => x.Any(value => !string.IsNullOrWhiteSpace(value)))
            .Select(row => (IReadOnlyDictionary<string, string>)headers
                .Select((header, index) => (header, value: index < row.Count ? row[index] : string.Empty))
                .ToDictionary(x => x.header, x => x.value, StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IEnumerable<IReadOnlyList<string>> Parse(TextReader reader)
    {
        var row = new List<string>();
        var value = new StringBuilder();
        var quoted = false;

        while (reader.Read() is var read && read >= 0)
        {
            var character = (char)read;
            if (quoted)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        value.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    value.Append(character);
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                row.Add(value.ToString());
                value.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                row.Add(value.ToString());
                value.Clear();
                yield return row.ToArray();
                row.Clear();
            }
            else
            {
                value.Append(character);
            }
        }

        if (value.Length > 0 || row.Count > 0)
        {
            row.Add(value.ToString());
            yield return row.ToArray();
        }
    }
}
