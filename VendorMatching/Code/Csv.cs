using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VendorMatching.Code
{
    /// <summary>A parsed delimited file: a header row plus the data rows.</summary>
    public sealed class CsvTable
    {
        public CsvTable()
        {
            Header = new string[0];
            Rows = new List<string[]>();
        }

        public string[] Header { get; set; }
        public List<string[]> Rows { get; set; }
        public char Delimiter { get; set; }

        /// <summary>
        /// Finds the first header that matches one of the accepted names for a logical field.
        /// Returns -1 when the field is absent, which callers treat as "value not supplied".
        /// </summary>
        public int IndexOfAny(string[] candidateHeaders)
        {
            if (candidateHeaders == null) return -1;
            foreach (string candidate in candidateHeaders)
            {
                for (int i = 0; i < Header.Length; i++)
                {
                    if (string.Equals(Header[i], candidate, StringComparison.OrdinalIgnoreCase)) return i;
                }
            }
            return -1;
        }

        public static string Value(string[] row, int index)
        {
            if (index < 0 || row == null || index >= row.Length) return string.Empty;
            return row[index] ?? string.Empty;
        }
    }

    /// <summary>
    /// A small RFC 4180 reader/writer. Deliberately dependency-free so the engine can run
    /// inside UiPath, from the CLI harness, or in a unit test without a package restore.
    /// </summary>
    public static class Csv
    {
        private static readonly char[] CandidateDelimiters = new[] { ',', ';', '\t', '|' };

        public static CsvTable Read(string path)
        {
            return Read(path, '\0');
        }

        /// <summary>Reads a delimited file. Pass '\0' as the delimiter to auto-detect it.</summary>
        public static CsvTable Read(string path, char delimiter)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("File path is required.", "path");
            if (!File.Exists(path)) throw new FileNotFoundException("Input file was not found.", path);

            string text = File.ReadAllText(path, Encoding.UTF8);
            return Parse(text, delimiter);
        }

        public static CsvTable Parse(string text, char delimiter)
        {
            var table = new CsvTable();
            if (string.IsNullOrEmpty(text)) return table;

            // Strip a UTF-8 byte order mark so the first header name stays comparable.
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);

            if (delimiter == '\0') delimiter = DetectDelimiter(text);
            table.Delimiter = delimiter;

            List<string[]> records = ParseRecords(text, delimiter);
            if (records.Count == 0) return table;

            string[] header = records[0];
            for (int i = 0; i < header.Length; i++)
            {
                header[i] = (header[i] ?? string.Empty).Trim();
            }
            table.Header = header;

            for (int i = 1; i < records.Count; i++)
            {
                if (IsBlank(records[i])) continue;
                table.Rows.Add(records[i]);
            }

            return table;
        }

        /// <summary>Picks the delimiter that splits the first line into the most fields.</summary>
        public static char DetectDelimiter(string text)
        {
            int lineEnd = text.IndexOf('\n');
            string firstLine = lineEnd < 0 ? text : text.Substring(0, lineEnd);

            char best = ',';
            int bestCount = 0;
            foreach (char candidate in CandidateDelimiters)
            {
                int count = CountOutsideQuotes(firstLine, candidate);
                if (count > bestCount)
                {
                    bestCount = count;
                    best = candidate;
                }
            }
            return best;
        }

        private static int CountOutsideQuotes(string line, char delimiter)
        {
            int count = 0;
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == delimiter && !inQuotes)
                {
                    count++;
                }
            }
            return count;
        }

        private static List<string[]> ParseRecords(string text, char delimiter)
        {
            var records = new List<string[]>();
            var fields = new List<string>();
            var field = new StringBuilder();
            bool inQuotes = false;
            bool sawAnyChar = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // A doubled quote inside a quoted field is a literal quote.
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                    sawAnyChar = true;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = true;
                    sawAnyChar = true;
                }
                else if (c == delimiter)
                {
                    fields.Add(field.ToString());
                    field.Length = 0;
                    sawAnyChar = true;
                }
                else if (c == '\r')
                {
                    // Swallow; the following '\n' ends the record.
                }
                else if (c == '\n')
                {
                    fields.Add(field.ToString());
                    field.Length = 0;
                    records.Add(fields.ToArray());
                    fields.Clear();
                    sawAnyChar = false;
                }
                else
                {
                    field.Append(c);
                    sawAnyChar = true;
                }
            }

            if (sawAnyChar || field.Length > 0 || fields.Count > 0)
            {
                fields.Add(field.ToString());
                records.Add(fields.ToArray());
            }

            return records;
        }

        private static bool IsBlank(string[] row)
        {
            if (row == null || row.Length == 0) return true;
            foreach (string value in row)
            {
                if (!string.IsNullOrWhiteSpace(value)) return false;
            }
            return true;
        }

        public static void Write(string path, IEnumerable<string[]> rows)
        {
            Write(path, rows, ',');
        }

        public static void Write(string path, IEnumerable<string[]> rows, char delimiter)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("File path is required.", "path");

            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var sb = new StringBuilder();
            foreach (string[] row in rows)
            {
                for (int i = 0; i < row.Length; i++)
                {
                    if (i > 0) sb.Append(delimiter);
                    sb.Append(Escape(row[i], delimiter));
                }
                sb.Append("\r\n");
            }

            // UTF-8 with BOM keeps Excel happy with non-ASCII vendor names.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static string Escape(string value, char delimiter)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            bool needsQuotes = value.IndexOf('"') >= 0
                || value.IndexOf('\n') >= 0
                || value.IndexOf('\r') >= 0
                || value.IndexOf(delimiter) >= 0
                || value[0] == ' '
                || value[value.Length - 1] == ' ';

            if (!needsQuotes) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
