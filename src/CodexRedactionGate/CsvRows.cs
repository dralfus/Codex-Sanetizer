using System;
using System.Collections.Generic;
using System.Text;

namespace CodexRedactionGate;

internal static class CsvRows
{
    public static IReadOnlyList<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];

            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (character == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        if (inQuotes)
        {
            throw new FormatException("CSV row contains an unterminated quoted field.");
        }

        fields.Add(current.ToString());
        return fields;
    }

    public static string Escape(string value)
    {
        return value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}
