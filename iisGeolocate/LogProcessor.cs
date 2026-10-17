using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CsvHelper;

namespace iisGeolocate;

public class BadRow
{
    public string SourceFile { get; set; }
    public long LineNumber { get; set; }
    public string Reason { get; set; }
    public string Row { get; set; }
}

public class LogFileResult
{
    /// <summary>Why the file was not processed (not a W3C log, an Exchange log, empty), or null</summary>
    public string Skipped { get; set; }

    /// <summary>One per distinct #Fields list, in the order first seen</summary>
    public List<ChunkResult> Chunks { get; } = new();

    public long Rows { get; set; }
    public long BadRows { get; set; }
}

public class ChunkResult
{
    public int Number { get; set; }
    public List<string> Fields { get; set; }

    /// <summary>The CSV written, or null with --nul</summary>
    public string CsvFile { get; set; }

    public long Rows { get; set; }

    /// <summary>False when the fields have no c-ip, so GeoCity and GeoCountry are NA</summary>
    public bool HasClientIp { get; set; }
}

/// <summary>
///     Reads one W3C log and writes a CSV per distinct #Fields list (chunk), with GeoCity and GeoCountry added at the end.
///     Rows are streamed, so the size of a log doesn't matter.
/// </summary>
public static class LogProcessor
{
    public const string ClientIpField = "c-ip";

    /// <param name="file">The log</param>
    /// <param name="csvDirectory">Where the chunk CSVs go, or null to write none (--nul)</param>
    /// <param name="timestamp">Prefix of the CSV names</param>
    /// <param name="geo">The lookup</param>
    /// <param name="onBadRow">Called for every row that is not written: no #Fields before it, or a wrong number of values</param>
    /// <exception cref="IOException">The log could not be read or a CSV could not be written</exception>
    public static LogFileResult Process(string file, string csvDirectory, string timestamp, GeoLookup geo,
        Action<BadRow> onBadRow)
    {
        var result = new LogFileResult();
        var chunks = new Dictionary<string, Chunk>(StringComparer.Ordinal);
        Chunk current = null;

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length == 0)
            {
                result.Skipped = "is empty";
                return result;
            }

            using var reader = new StreamReader(stream, new UTF8Encoding(false), true);

            long lineNumber = 0;
            var first = true;
            string line;

            while ((line = reader.ReadLine()) != null)
            {
                lineNumber += 1;

                if (line.Trim().Length == 0)
                {
                    continue;
                }

                if (first)
                {
                    first = false;

                    if (line.StartsWith("#") == false)
                    {
                        result.Skipped = "does not start with a # line. Is this an IIS log?";
                        return result;
                    }

                    if (line.StartsWith("#Software: Microsoft Exchange", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Skipped = "is an Exchange log, not an IIS log";
                        return result;
                    }
                }

                if (line.StartsWith("#"))
                {
                    if (line.StartsWith(W3CLog.FieldsDirective, StringComparison.OrdinalIgnoreCase))
                    {
                        W3CLog.TrySplit(line.Substring(W3CLog.FieldsDirective.Length), out var fields);
                        var key = string.Join(" ", fields);

                        if (chunks.TryGetValue(key, out current) == false)
                        {
                            current = new Chunk(chunks.Count + 1, fields, file, csvDirectory, timestamp);
                            chunks.Add(key, current);
                            result.Chunks.Add(current.Result);
                        }
                    }

                    continue;
                }

                if (current == null)
                {
                    result.BadRows += 1;
                    onBadRow(new BadRow
                        { SourceFile = file, LineNumber = lineNumber, Reason = "No #Fields line before it", Row = line });
                    continue;
                }

                if (W3CLog.TrySplit(line, out var values) == false || values.Count != current.Result.Fields.Count)
                {
                    result.BadRows += 1;
                    onBadRow(new BadRow
                    {
                        SourceFile = file, LineNumber = lineNumber,
                        Reason = $"{values.Count:N0} values for {current.Result.Fields.Count:N0} fields", Row = line
                    });
                    continue;
                }

                var geoResult = current.IpIndex >= 0 ? geo.Lookup(values[current.IpIndex]) : GeoResult.NotAvailable;

                current.Write(values, geoResult);
                result.Rows += 1;
            }
        }
        finally
        {
            foreach (var chunk in chunks.Values)
            {
                chunk.Dispose();
            }
        }

        return result;
    }

    private sealed class Chunk : IDisposable
    {
        private readonly CsvWriter _csv;

        public Chunk(int number, List<string> fields, string file, string csvDirectory, string timestamp)
        {
            IpIndex = fields.FindIndex(t => string.Equals(t, ClientIpField, StringComparison.OrdinalIgnoreCase));

            Result = new ChunkResult { Number = number, Fields = fields, HasClientIp = IpIndex >= 0 };

            if (csvDirectory == null)
            {
                return;
            }

            var baseName = $"{timestamp}_{Path.GetFileNameWithoutExtension(file)}_Chunk{number}";
            var outFile = Path.Combine(csvDirectory, baseName + ".csv");
            var n = 2;
            while (File.Exists(outFile))
            {
                //logs with the same name in different folders (W3SVC1\u_ex...log, W3SVC2\u_ex...log)
                outFile = Path.Combine(csvDirectory, $"{baseName}_{n}.csv");
                n += 1;
            }

            Result.CsvFile = outFile;

            _csv = new CsvWriter(new StreamWriter(outFile, false, new UTF8Encoding(false)), CultureInfo.InvariantCulture);

            foreach (var field in fields)
            {
                _csv.WriteField(field);
            }

            _csv.WriteField("GeoCity");
            _csv.WriteField("GeoCountry");
            _csv.NextRecord();
        }

        public ChunkResult Result { get; }

        public int IpIndex { get; }

        public void Dispose()
        {
            _csv?.Dispose();
        }

        public void Write(List<string> values, GeoResult geo)
        {
            Result.Rows += 1;

            if (_csv == null)
            {
                return;
            }

            foreach (var value in values)
            {
                _csv.WriteField(value);
            }

            _csv.WriteField(geo.City);
            _csv.WriteField(geo.Country);
            _csv.NextRecord();
        }
    }
}
