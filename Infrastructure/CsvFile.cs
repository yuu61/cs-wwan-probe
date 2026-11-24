using System.Globalization;
using System.Text;

namespace WwanProbe.Infrastructure
{
    // CSV file writing. UTF-8 with BOM so Excel does not misread it as the ANSI code page.
    // Values are formatted with the invariant culture; lines end with CRLF.
    public static class CsvFile
    {
        private static readonly Encoding Utf8Bom = new UTF8Encoding(true);
        private static readonly char[] QuoteTriggers = { '"', ',', '\r', '\n' };

        // RFC 4180 field: quoted only when it contains a comma, quote or line break; null -> empty.
        public static string Field(object? value)
        {
            if (value is null) return "";
            string s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (s.IndexOfAny(QuoteTriggers) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        public static void Initialize(string path, IEnumerable<string> columns)
        {
            File.WriteAllText(path, string.Join(',', columns.Select(Field)) + "\r\n", Utf8Bom);
        }

        // The BOM is only written when the file is empty (StreamWriter skips the preamble when appending).
        public static void AppendRow(string path, IEnumerable<object?> fields)
        {
            using StreamWriter writer = new StreamWriter(path, append: true, Utf8Bom);
            writer.Write(string.Join(',', fields.Select(Field)) + "\r\n");
        }
    }
}
