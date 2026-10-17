using System.IO;
using FluentAssertions;
using NUnit.Framework;

namespace iisGeolocate.Test;

/// <summary>Values are from MaxMind's test databases, checked with Python's maxminddb</summary>
[TestFixture]
public class GeoLookupTests
{
    [TestCase("81.2.69.142", "London", "United_Kingdom")]
    [TestCase("89.160.20.112", "Linköping", "Sweden")]
    [TestCase("216.160.83.56", "Milton", "United_States")]
    [TestCase("214.0.1.5", "Melbourne", "Australia")]
    [TestCase("::ffff:81.2.69.142", "London", "United_Kingdom")]
    [TestCase("2001:218::1", null, "Japan")]
    [TestCase("[2001:218::1]", null, "Japan")]
    [TestCase("67.43.156.1", null, "Bhutan")]
    [TestCase("1.1.1.1", "NA", "NA")]
    [TestCase("10.0.0.1", "NA", "NA")]
    [TestCase("::1", "NA", "NA")]
    [TestCase("-", "NA", "NA")]
    public void City(string ip, string city, string country)
    {
        using var geo = new GeoLookup(TestData.CityDb);

        var result = geo.Lookup(ip);

        result.City.Should().Be(city);
        result.Country.Should().Be(country);
    }

    [Test]
    public void UniqueIpsKeepNamesAndOrder()
    {
        using var geo = new GeoLookup(TestData.CityDb);

        foreach (var ip in new[] { "89.160.20.112", "81.2.69.142", "89.160.20.112", "10.0.0.1", "-", "1.1.1.1", "x" })
        {
            geo.Lookup(ip);
        }

        geo.UniqueIps.Should().HaveCount(2);
        geo.UniqueIps[0].IpAddress.Should().Be("89.160.20.112");
        geo.UniqueIps[1].City.Should().Be("London");
        geo.UniqueIps[1].Country.Should().Be("United Kingdom");
        geo.NonPublicAddresses.Should().Be(1);
        geo.InvalidAddresses.Should().Be(2);
        geo.NotFoundAddresses.Should().Be(1);
    }

    [Test]
    public void CountryDatabase()
    {
        using var geo = new GeoLookup(TestData.File("MaxMind", "GeoLite2-Country-Test.mmdb"));

        var result = geo.Lookup("81.2.69.142");
        result.City.Should().BeNull();
        result.Country.Should().Be("United_Kingdom");
    }

    [Test]
    public void EnterpriseDatabase()
    {
        using var geo = new GeoLookup(TestData.File("MaxMind", "GeoIP2-Enterprise-Test.mmdb"));

        var result = geo.Lookup("2.125.160.216");
        result.City.Should().Be("Boxford");
        result.Country.Should().Be("United_Kingdom");
    }

    [Test]
    public void DatabaseWithoutLocationsIsRefused()
    {
        var act = () => new GeoLookup(TestData.File("MaxMind", "GeoLite2-ASN-Test.mmdb"));

        act.Should().Throw<InvalidDataException>().WithMessage("*GeoLite2-ASN*");
    }

    [Test]
    public void NotADatabaseIsRefused()
    {
        var dir = TestData.NewTempDir();
        try
        {
            var path = Path.Combine(dir, "bad.mmdb");
            File.WriteAllBytes(path, new byte[1000]);

            var act = () => new GeoLookup(path);

            act.Should().Throw<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
