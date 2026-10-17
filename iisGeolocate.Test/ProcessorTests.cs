using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using NUnit.Framework;

namespace iisGeolocate.Test;

[TestFixture]
public class ProcessorTests
{
    private string _dir;
    private GeoLookup _geo;
    private List<BadRow> _bad;

    [SetUp]
    public void SetUp()
    {
        _dir = TestData.NewTempDir();
        _geo = new GeoLookup(TestData.CityDb);
        _bad = new List<BadRow>();
    }

    [TearDown]
    public void TearDown()
    {
        _geo.Dispose();
        Directory.Delete(_dir, true);
    }

    private string Out => Path.Combine(_dir, "out");

    private LogFileResult Process(string log, bool csv = true)
    {
        Directory.CreateDirectory(Out);
        return LogProcessor.Process(log, csv ? Out : null, "20260101000000", _geo, _bad.Add);
    }

    private static List<string[]> ReadCsv(string path)
    {
        using var reader = new CsvHelper.CsvReader(new StreamReader(path),
            System.Globalization.CultureInfo.InvariantCulture);
        var rows = new List<string[]>();
        while (reader.Read())
        {
            rows.Add(reader.Parser.Record!);
        }

        return rows;
    }

    /// <summary>Every line that isn't blank or a directive is either written or reported</summary>
    private static long DataLines(string path)
    {
        return File.ReadLines(path).Count(t => t.Trim().Length > 0 && t.StartsWith("#") == false);
    }

    [Test]
    public void HeaderAndGeoColumns()
    {
        var log = TestData.WriteLog(_dir, "u_ex260102.log",
            TestData.IisLog("81.2.69.142", "10.0.0.5", "2001:218::1", "-", "89.160.20.112"));

        var result = Process(log);

        result.Skipped.Should().BeNull();
        result.Rows.Should().Be(5);
        result.Chunks.Should().ContainSingle();
        var chunk = result.Chunks[0];
        Path.GetFileName(chunk.CsvFile).Should().Be("20260101000000_u_ex260102_Chunk1.csv");

        var rows = ReadCsv(chunk.CsvFile);
        rows[0].Should().Equal("date", "time", "s-ip", "cs-method", "cs-uri-stem", "cs-uri-query", "s-port",
            "cs-username", "c-ip", "cs(User-Agent)", "sc-status", "time-taken", "GeoCity", "GeoCountry");
        rows.Should().HaveCount(6);
        rows[1].Should().Equal("2026-01-02", "03:04:05", "192.168.1.10", "GET", "/index.htm", "-", "443", "-",
            "81.2.69.142", "Mozilla/5.0+(Windows+NT+10.0)", "200", "15", "London", "United_Kingdom");
        rows[2].Skip(12).Should().Equal("NA", "NA");
        rows[3].Skip(12).Should().Equal("", "Japan");
        rows[4].Skip(12).Should().Equal("NA", "NA");
        rows[5].Skip(12).Should().Equal("Linköping", "Sweden");
    }

    [Test]
    public void NoCsvWithNul()
    {
        var log = TestData.WriteLog(_dir, "a.log", TestData.IisLog("81.2.69.142"));

        var result = Process(log, false);

        result.Rows.Should().Be(1);
        result.Chunks[0].CsvFile.Should().BeNull();
        Directory.GetFiles(Out).Should().BeEmpty();
        _geo.UniqueIps.Should().ContainSingle();
    }

    [Test]
    public void ChunkPerFieldList()
    {
        //IIS writes the directives again after a restart; a changed field list starts a new chunk, and going back
        //to an earlier one (which crashed 2026.5) continues that chunk
        var lines = TestData.IisLog("81.2.69.142").ToList();
        lines.Add("#Software: Microsoft Internet Information Services 10.0");
        lines.Add(TestData.IisFields);
        lines.Add(TestData.Row("89.160.20.112"));
        lines.Add("#Fields: date time c-ip cs-method");
        lines.Add("2026-01-02 04:00:00 216.160.83.56 GET");
        lines.Add(TestData.IisFields);
        lines.Add(TestData.Row("10.1.1.1"));
        var log = TestData.WriteLog(_dir, "a.log", lines);

        var result = Process(log);

        result.Chunks.Should().HaveCount(2);
        result.Chunks[0].Rows.Should().Be(3);
        result.Chunks[1].Rows.Should().Be(1);
        ReadCsv(result.Chunks[0].CsvFile).Select(t => t[8]).Should()
            .Equal("c-ip", "81.2.69.142", "89.160.20.112", "10.1.1.1");
        ReadCsv(result.Chunks[1].CsvFile).Should().HaveCount(2).And.Contain(t =>
            t.SequenceEqual(new[] { "2026-01-02", "04:00:00", "216.160.83.56", "GET", "Milton", "United_States" }));
        _bad.Should().BeEmpty();
    }

    [Test]
    public void BadRowsAreReportedNotWritten()
    {
        var lines = new List<string>
        {
            "#Software: Microsoft Internet Information Services 10.0",
            "2026-01-02 03:04:05 data before any fields",
            TestData.IisFields,
            TestData.Row("81.2.69.142"),
            "2026-01-02 too few",
            TestData.Row("81.2.69.142") + " extra",
            "2026-01-02 03:04:05 \"unclosed quote",
            TestData.Row("89.160.20.112")
        };
        var log = TestData.WriteLog(_dir, "a.log", lines);

        var result = Process(log);

        result.Rows.Should().Be(2);
        result.BadRows.Should().Be(4);
        _bad.Select(t => t.LineNumber).Should().Equal(2, 5, 6, 7);
        _bad[0].Reason.Should().Be("No #Fields line before it");
        _bad[1].Reason.Should().Be("3 values for 12 fields");
        _bad[1].Row.Should().Be("2026-01-02 too few");
        _bad.Should().OnlyContain(t => t.SourceFile == log);
    }

    [Test]
    public void NoClientIpField()
    {
        var log = TestData.WriteLog(_dir, "a.log",
            new[] { "#Fields: date time s-ip cs-method", "2026-01-02 03:04:05 81.2.69.142 GET" });

        var result = Process(log);

        result.Chunks[0].HasClientIp.Should().BeFalse();
        ReadCsv(result.Chunks[0].CsvFile)[1].Should().Equal("2026-01-02", "03:04:05", "81.2.69.142", "GET", "NA", "NA");
    }

    [Test]
    public void SameNameInTwoFoldersGivesTwoCsvs()
    {
        var a = TestData.WriteLog(_dir, Path.Combine("W3SVC1", "u_ex260102.log"), TestData.IisLog("81.2.69.142"));
        var b = TestData.WriteLog(_dir, Path.Combine("W3SVC2", "u_ex260102.log"), TestData.IisLog("89.160.20.112"));

        var first = Process(a);
        var second = Process(b);

        Path.GetFileName(first.Chunks[0].CsvFile).Should().Be("20260101000000_u_ex260102_Chunk1.csv");
        Path.GetFileName(second.Chunks[0].CsvFile).Should().Be("20260101000000_u_ex260102_Chunk1_2.csv");
        ReadCsv(first.Chunks[0].CsvFile)[1][8].Should().Be("81.2.69.142");
        ReadCsv(second.Chunks[0].CsvFile)[1][8].Should().Be("89.160.20.112");
    }

    [TestCase("", "is empty")]
    [TestCase("2026-01-02 03:04:05 GET /\r\n", "does not start with a # line. Is this an IIS log?")]
    [TestCase("#Software: Microsoft Exchange Server\r\n#Fields: date-time,client-ip\r\n", "is an Exchange log, not an IIS log")]
    public void Skipped(string content, string reason)
    {
        var log = Path.Combine(_dir, "a.log");
        File.WriteAllText(log, content);

        var result = Process(log);

        result.Skipped.Should().Be(reason);
        Directory.GetFiles(Out).Should().BeEmpty();
    }

    [Test]
    public void ByteOrderMarkAndBlankLines()
    {
        var log = Path.Combine(_dir, "a.log");
        File.WriteAllText(log, "\r\n" + string.Join("\r\n", TestData.IisLog("81.2.69.142")) + "\r\n\r\n",
            new UTF8Encoding(true));

        var result = Process(log);

        result.Skipped.Should().BeNull();
        result.Rows.Should().Be(1);
    }

    [Test]
    public void LogInUseCanBeRead()
    {
        var log = TestData.WriteLog(_dir, "a.log", TestData.IisLog("81.2.69.142"));

        using (new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Process(log).Rows.Should().Be(1);
        }
    }

    [Test]
    public void EverySampleIsReadCompletely()
    {
        var files = Directory.GetFiles(TestData.File(), "*.log", SearchOption.AllDirectories);
        files.Should().HaveCount(13);

        foreach (var file in files)
        {
            var result = Process(file);

            result.Skipped.Should().BeNull(file);
            (result.Rows + result.BadRows).Should().Be(DataLines(file), file);
            result.Chunks.Sum(t => t.Rows).Should().Be(result.Rows);
        }

        _bad.Should().BeEmpty();
    }

    [Test]
    public void Iis6TrailingSpaceAddsNoColumn()
    {
        var result = Process(TestData.File("plaso", "iis6.log"));

        var rows = ReadCsv(result.Chunks[0].CsvFile);
        rows[0].Should().HaveCount(16).And.EndWith(new[] { "sc-win32-status", "GeoCity", "GeoCountry" });
        rows.Skip(1).Should().OnlyContain(t => t.Length == 16);
    }

    [Test]
    public void DoubleSpaceKeepsColumnsAligned()
    {
        //2026.5 read the double space as an empty value, shifting the user name into c-ip
        var result = Process(TestData.File("plaso", "iis10_edge_cases.log"));

        var rows = ReadCsv(result.Chunks[0].CsvFile);
        rows[1][7].Should().Be(@"random\ranuser1");
        rows[1][8].Should().Be("::1");
        _geo.InvalidAddresses.Should().Be(0);
    }

    [Test]
    public void QuotedValues()
    {
        //IIS Advanced Logging: quoted strings with spaces, "" escapes
        var result = Process(TestData.File("lnav", "logfile_w3c.3.log"));

        var rows = ReadCsv(result.Chunks[0].CsvFile);
        rows.Should().HaveCount(4);
        rows[1][8].Should().Be("70.95.0.0");
        rows[1][9].Should().StartWith("Mozilla/5.0 (Linux; Android 4.4.4;");
        rows[3][13].Should().Be(" \"garbage\" w/ spaces ");
    }

    [Test]
    public void RandomDamageNeverThrows()
    {
        var sample = File.ReadAllBytes(TestData.File("plaso", "iis10_edge_cases.log"));
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("IISGEOLOCATE_FUZZ_ROUNDS"), out var n) ? n : 300;
        var random = new Random(1234);
        var log = Path.Combine(_dir, "fuzz.log");

        for (var i = 0; i < rounds; i++)
        {
            var bytes = (byte[])sample.Clone();
            var changes = random.Next(1, 40);
            for (var j = 0; j < changes; j++)
            {
                bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            }

            if (random.Next(4) == 0)
            {
                Array.Resize(ref bytes, random.Next(bytes.Length));
            }

            File.WriteAllBytes(log, bytes);
            if (Directory.Exists(Out))
            {
                Directory.Delete(Out, true);
            }

            _bad.Clear();
            var result = Process(log);

            if (result.Skipped == null)
            {
                (result.Rows + result.BadRows).Should().Be(DataLines(log));
            }
        }
    }
}
