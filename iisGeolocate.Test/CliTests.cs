using System.Diagnostics;
using System.IO;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace iisGeolocate.Test;

/// <summary>
///     Runs the built iisGeolocate.dll (copied next to the tests by the project reference) as a separate process
/// </summary>
[TestFixture]
public class CliTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = TestData.NewTempDir();
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_dir, true);
    }

    private string Out => Path.Combine(_dir, "out");

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir
        };
        start.ArgumentList.Add(Path.Combine(TestContext.CurrentContext.TestDirectory, "iisGeolocate.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();

        process.WaitForExit(60_000).Should().BeTrue("iisGeolocate should finish");

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private string Logs()
    {
        var logs = Path.Combine(_dir, "logs");
        TestData.WriteLog(logs, Path.Combine("W3SVC1", "u_ex260102.log"),
            TestData.IisLog("81.2.69.142", "10.0.0.1", "89.160.20.112", "81.2.69.142"));
        TestData.WriteLog(logs, Path.Combine("W3SVC2", "u_ex260102.LOG"),
            TestData.IisLog("2001:218::1").Concat(new[] { "bad row" }));
        File.WriteAllText(Path.Combine(logs, "notes.txt"), "not a log");
        return logs;
    }

    [Test]
    public void ProcessesLogs()
    {
        var (exitCode, output) = Run("-d", Logs(), "--csv", Out, "--db", TestData.CityDb, "--sbl");

        exitCode.Should().Be(0, output);
        output.Should().Contain("Found 2 log files");
        output.Should().Contain("Rows: 5, bad rows: 1");
        output.Should().Contain("geolocated 3, not in the database 0, not public 1, not an IP address 0");
        output.Should().NotContain("Ignoring!!!");

        var csvs = Directory.GetFiles(Out, "*.csv").Select(Path.GetFileName).OrderBy(t => t).ToList();
        csvs.Should().HaveCount(3);
        csvs.Should().Contain(Program.UniqueIpsFileName);
        csvs.Should().Contain(t => t.EndsWith("_u_ex260102_Chunk1.csv"));
        csvs.Should().Contain(t => t.EndsWith("_u_ex260102_Chunk1_2.csv"));

        var first = File.ReadAllLines(Directory.GetFiles(Out, "*_Chunk1.csv").Single());
        first[0].Should().Be(
            "date,time,s-ip,cs-method,cs-uri-stem,cs-uri-query,s-port,cs-username,c-ip,cs(User-Agent),sc-status,time-taken,GeoCity,GeoCountry");
        first.Skip(1).Select(t => string.Join(",", t.Split(',').Skip(12))).Should()
            .Equal("London,United_Kingdom", "NA,NA", "Linköping,Sweden", "London,United_Kingdom");

        File.ReadAllLines(Path.Combine(Out, Program.UniqueIpsFileName)).Should().Equal(
            "IpAddress,City,Country",
            "81.2.69.142,London,United Kingdom",
            "89.160.20.112,Linköping,Sweden",
            "2001:218::1,,Japan");

        var bad = File.ReadAllLines(Path.Combine(Out, Program.BadRowsFileName));
        bad[0].Should().Be("SourceFile\tLineNumber\tReason\tRow");
        bad.Should().HaveCount(2);
        bad[1].Should().EndWith("u_ex260102.LOG\t6\t2 values for 12 fields\tbad row");

        //nothing in the working folder (2026.5 wrote tmp.txt next to the program)
        Directory.GetFiles(_dir).Should().BeEmpty();
        File.Exists(Path.Combine(TestContext.CurrentContext.TestDirectory, "tmp.txt")).Should().BeFalse();
    }

    [Test]
    public void BadRowsAreShownUnlessSuppressed()
    {
        var (exitCode, output) = Run("-d", Logs(), "--csv", Out, "--db", TestData.CityDb);

        exitCode.Should().Be(0, output);
        output.Should().Contain("Ignoring!!! Row: 'bad row'");
    }

    [Test]
    public void UniqueIpsFileIsReplaced()
    {
        //2026.5 opened it without truncating, so an older, longer file left its end behind
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, Program.UniqueIpsFileName), new string('x', 5000));

        Run("-d", Logs(), "--csv", Out, "--db", TestData.CityDb).ExitCode.Should().Be(0);

        File.ReadAllLines(Path.Combine(Out, Program.UniqueIpsFileName)).Should().HaveCount(4);
    }

    [Test]
    public void NulWritesOnlyUniqueIpsAndBadRows()
    {
        var (exitCode, output) = Run("-d", Logs(), "--csv", Out, "--db", TestData.CityDb, "--nul");

        exitCode.Should().Be(0, output);
        Directory.GetFiles(Out).Select(Path.GetFileName).Should()
            .BeEquivalentTo(Program.UniqueIpsFileName, Program.BadRowsFileName);
    }

    [Test]
    public void SamplesExitZero()
    {
        var (exitCode, output) = Run("-d", TestData.File(), "--csv", Out, "--db", TestData.CityDb, "--sbl");

        exitCode.Should().Be(0, output);
        output.Should().Contain("Found 13 log files");
        Directory.GetFiles(Out, "*_Chunk*.csv").Should().HaveCount(13);
    }

    [Test]
    public void MissingArgumentsExitOne()
    {
        var (exitCode, output) = Run();

        exitCode.Should().Be(1);
        output.Should().Contain("Both -d and --csv are required");

        Run("-d", Logs()).ExitCode.Should().Be(1);
    }

    [Test]
    public void MissingDirectoryExitsOne()
    {
        Run("-d", Path.Combine(_dir, "nope"), "--csv", Out, "--db", TestData.CityDb).ExitCode.Should().Be(1);
    }

    [Test]
    public void MissingDatabaseExitsOne()
    {
        var (exitCode, output) = Run("-d", Logs(), "--csv", Out, "--db", Path.Combine(_dir, "nope.mmdb"));
        exitCode.Should().Be(1);
        output.Should().Contain("not found");

        //no --db and none next to the program
        (exitCode, output) = Run("-d", Logs(), "--csv", Out);
        exitCode.Should().Be(1);
        output.Should().Contain("GeoLite2-City.mmdb missing");
    }

    [Test]
    public void UnusableDatabaseExitsOne()
    {
        var (exitCode, output) = Run("-d", Logs(), "--csv", Out, "--db",
            TestData.File("MaxMind", "GeoLite2-ASN-Test.mmdb"));

        exitCode.Should().Be(1);
        output.Should().Contain("has no locations");
    }

    [Test]
    public void NoLogsExitsZero()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));

        var (exitCode, output) = Run("-d", Path.Combine(_dir, "empty"), "--csv", Out, "--db", TestData.CityDb);

        exitCode.Should().Be(0);
        output.Should().Contain("No files ending in .log found");
    }

    [Test]
    public void DamagedLogExitsZero()
    {
        // random bytes: every line is a bad row; a damaged log is not a failure of the tool
        var logs = Logs();
        var bytes = new byte[8192];
        new System.Random(1).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(logs, "random.log"), bytes);

        var (exitCode, output) = Run("-d", logs, "--csv", Out, "--db", TestData.CityDb, "--sbl");

        exitCode.Should().Be(0, output);
    }

    [Test]
    public void LockedLogExitsOne()
    {
        if (System.OperatingSystem.IsWindows() == false)
        {
            Assert.Ignore("Windows only");
        }

        var logs = Logs();
        var locked = Directory.GetFiles(logs, "*.log", SearchOption.AllDirectories).First();

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var (exitCode, output) = Run("-d", logs, "--csv", Out, "--db", TestData.CityDb);

            exitCode.Should().Be(1, output);
            output.Should().Contain("Failed files: 1");
        }
    }

    [Test]
    public void UnwritableOutputExitsOne()
    {
        var file = Path.Combine(_dir, "file");
        File.WriteAllText(file, "x");

        //--csv is a file, so the folder can't be created
        Run("-d", Logs(), "--csv", file, "--db", TestData.CityDb).ExitCode.Should().Be(1);
    }
}
