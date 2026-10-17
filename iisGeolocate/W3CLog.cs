using System.Collections.Generic;
using System.Text;

namespace iisGeolocate;

/// <summary>
///     W3C extended log format (IIS, HTTPERR, IIS Advanced Logging and other W3C writers)
/// </summary>
public static class W3CLog
{
    public const string FieldsDirective = "#Fields:";

    /// <summary>
    ///     Splits a data line or the field list of a #Fields directive. Runs of spaces and tabs separate the values
    ///     (IIS writes spaces in values as '+', and some writers pad with several spaces). A value that starts with a
    ///     double quote is a string up to the closing quote, and "" in it is a quote (W3C, IIS Advanced Logging).
    /// </summary>
    /// <returns>false when a quoted string is not closed; the rest of the line is then the last value</returns>
    public static bool TrySplit(string line, out List<string> values)
    {
        values = new List<string>();
        var i = 0;

        while (true)
        {
            while (i < line.Length && IsSeparator(line[i]))
            {
                i += 1;
            }

            if (i >= line.Length)
            {
                return true;
            }

            if (line[i] != '"')
            {
                var start = i;
                while (i < line.Length && IsSeparator(line[i]) == false)
                {
                    i += 1;
                }

                values.Add(line.Substring(start, i - start));
                continue;
            }

            var sb = new StringBuilder();
            i += 1;
            var closed = false;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }

                    closed = true;
                    i += 1;
                    break;
                }

                sb.Append(line[i]);
                i += 1;
            }

            if (closed == false)
            {
                values.Add(sb.ToString());
                return false;
            }

            if (i < line.Length && IsSeparator(line[i]) == false)
            {
                //text right after the closing quote belongs to the same value, as in "abc"def
                var start = i;
                while (i < line.Length && IsSeparator(line[i]) == false)
                {
                    i += 1;
                }

                sb.Append(line, start, i - start);
            }

            values.Add(sb.ToString());
        }
    }

    private static bool IsSeparator(char c)
    {
        return c == ' ' || c == '\t';
    }
}
