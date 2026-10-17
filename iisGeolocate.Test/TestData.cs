using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace iisGeolocate.Test;

public static class TestData
{
    public const string IisFields =
        "#Fields: date time s-ip cs-method cs-uri-stem cs-uri-query s-port cs-username c-ip cs(User-Agent) sc-status time-taken";

    public static string File(params string[] parts)
    {
        return Path.Combine(new[] { TestContext.CurrentContext.TestDirectory, "TestFiles" }.Concat(parts).ToArray());
    }

    public static string CityDb => File("MaxMind", "GeoIP2-City-Test.mmdb");

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "iisgeolocate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>An IIS 10 style row for the <see cref="IisFields" /> fields</summary>
    public static string Row(string clientIp, string uri = "/index.htm")
    {
        return $"2026-01-02 03:04:05 192.168.1.10 GET {uri} - 443 - {clientIp} Mozilla/5.0+(Windows+NT+10.0) 200 15";
    }

    public static string WriteLog(string dir, string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        return path;
    }

    public static IEnumerable<string> IisLog(params string[] clientIps)
    {
        yield return "#Software: Microsoft Internet Information Services 10.0";
        yield return "#Version: 1.0";
        yield return "#Date: 2026-01-02 03:04:05";
        yield return IisFields;
        foreach (var ip in clientIps)
        {
            yield return Row(ip);
        }
    }
}
