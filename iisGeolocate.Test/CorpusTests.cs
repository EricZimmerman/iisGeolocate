using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace iisGeolocate.Test;

/// <summary>
///     Every *.log in a local collection of real IIS logs that is not in the repository: Corpus/IISLogs next to the
///     repository (or IISGEOLOCATE_CORPUS). Uses GeoLite2-City.mmdb or GeoIP2-City.mmdb from that folder when there is
///     one, else MaxMind's test database. Never copy corpus files into the repository.
/// </summary>
[TestFixture]
[Explicit("Needs the local IIS log corpus")]
public class CorpusTests
{
    private static string CorpusDir()
    {
        var env = Environment.GetEnvironmentVariable("IISGEOLOCATE_CORPUS");
        if (string.IsNullOrEmpty(env) == false)
        {
            return env;
        }

        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Corpus", "IISLogs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Test]
    public void EveryLogIsReadCompletely()
    {
        var corpus = CorpusDir();
        if (corpus == null)
        {
            Assert.Ignore("No Corpus/IISLogs found");
        }

        var db = new[] { "GeoIP2-City.mmdb", "GeoLite2-City.mmdb" }.Select(t => Path.Combine(corpus!, t))
            .FirstOrDefault(File.Exists) ?? TestData.CityDb;

        var output = TestData.NewTempDir();
        try
        {
            using var geo = new GeoLookup(db);
            var bad = new List<BadRow>();
            long rows = 0;

            foreach (var file in Directory.GetFiles(corpus!, "*.log", SearchOption.AllDirectories))
            {
                var result = LogProcessor.Process(file, output, "corpus", geo, bad.Add);

                if (result.Skipped != null)
                {
                    TestContext.Out.WriteLine($"{file}: {result.Skipped}");
                    continue;
                }

                var dataLines = File.ReadLines(file).Count(t => t.Trim().Length > 0 && t.StartsWith("#") == false);
                (result.Rows + result.BadRows).Should().Be(dataLines, file);
                rows += result.Rows;
            }

            TestContext.Out.WriteLine($"Rows {rows:N0}, bad rows {bad.Count:N0}, geolocated {geo.UniqueIps.Count:N0}");
            foreach (var row in bad.Take(50))
            {
                TestContext.Out.WriteLine($"{row.SourceFile}({row.LineNumber}): {row.Reason}");
            }
        }
        finally
        {
            Directory.Delete(output, true);
        }
    }
}
