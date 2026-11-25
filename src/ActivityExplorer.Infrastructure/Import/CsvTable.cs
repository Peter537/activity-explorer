using System.Text;

namespace ActivityExplorer.Infrastructure.Import;

internal static class CsvTable
{
    public static (IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, IReadOnlyList<string> AmbiguousColumns) Read(
        string path, IReadOnlySet<string> selectedColumns, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var rows = Parse(reader).GetEnumerator();
        if (!rows.MoveNext()) return ([], []);

        var positions = rows.Current.Select((name, index) => (Name: name, Index: index))
            .Where(column => selectedColumns.Contains(column.Name))
            .ToLookup(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var columns = positions.Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        var ambiguousColumns = positions.Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        var result = new List<IReadOnlyDictionary<string, string>>();
        while (rows.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows.Current;
            var values = columns.ToDictionary(column => column.Name,
                column => column.Index < row.Count ? row[column.Index] : string.Empty, StringComparer.OrdinalIgnoreCase);
            if (values.Values.Any(value => !string.IsNullOrWhiteSpace(value))) result.Add(values);
        }

        return (result, ambiguousColumns);
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
