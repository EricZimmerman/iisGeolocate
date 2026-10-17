using FluentAssertions;
using NUnit.Framework;

namespace iisGeolocate.Test;

[TestFixture]
public class W3CLogTests
{
    [TestCase("a b c", new[] { "a", "b", "c" })]
    [TestCase("  a \t b   c  ", new[] { "a", "b", "c" })]
    [TestCase("", new string[0])]
    [TestCase("   ", new string[0])]
    [TestCase("- \"Mozilla/5.0 (X11; Linux)\" 200", new[] { "-", "Mozilla/5.0 (X11; Linux)", "200" })]
    [TestCase("\"\" x", new[] { "", "x" })]
    [TestCase("\"say \"\"hi\"\"\" x", new[] { "say \"hi\"", "x" })]
    [TestCase("OutlookSession=\"{AAAA}\" next", new[] { "OutlookSession=\"{AAAA}\"", "next" })]
    [TestCase("\"ab\"cd e", new[] { "abcd", "e" })]
    public void Splits(string line, string[] expected)
    {
        W3CLog.TrySplit(line, out var values).Should().BeTrue();
        values.Should().Equal(expected);
    }

    [Test]
    public void UnclosedQuoteIsReported()
    {
        W3CLog.TrySplit("a \"b c", out var values).Should().BeFalse();
        values.Should().Equal("a", "b c");
    }
}

[TestFixture]
public class IpAddressTests
{
    [TestCase("81.2.69.142", true)]
    [TestCase("8.8.8.8", true)]
    [TestCase("2001:218::1", true)]
    [TestCase("[2001:218::1]", true)]
    [TestCase("::ffff:81.2.69.142", true)]
    [TestCase("214.0.1.5", true)]
    [TestCase("172.32.0.1", true)]
    [TestCase("100.128.0.1", true)]
    [TestCase("127.0.0.1", false)]
    [TestCase("10.1.2.3", false)]
    [TestCase("172.16.0.1", false)]
    [TestCase("172.31.255.255", false)]
    [TestCase("192.168.1.1", false)]
    [TestCase("169.254.1.1", false)]
    [TestCase("100.64.0.1", false)]
    [TestCase("0.0.0.0", false)]
    [TestCase("192.0.2.1", false)]
    [TestCase("198.18.0.1", false)]
    [TestCase("198.51.100.1", false)]
    [TestCase("203.0.113.1", false)]
    [TestCase("224.0.0.1", false)]
    [TestCase("255.255.255.255", false)]
    [TestCase("::1", false)]
    [TestCase("::", false)]
    [TestCase("fe80::1%3", false)]
    [TestCase("fe80::1ff:fe23:4567:890a%3", false)]
    [TestCase("fd00::1", false)]
    [TestCase("ff02::1", false)]
    [TestCase("2001:db8::1", false)]
    [TestCase("::ffff:10.0.0.1", false)]
    public void Public(string value, bool expected)
    {
        IpAddresses.TryParse(value, out var address).Should().BeTrue();
        IpAddresses.IsPublic(address).Should().Be(expected);
    }

    [TestCase("-")]
    [TestCase("")]
    [TestCase(null)]
    [TestCase("1")]
    [TestCase("1.2.3")]
    [TestCase("1.2.3.4.5")]
    [TestCase("256.1.1.1")]
    [TestCase("1.2.3.04444")]
    [TestCase("0x7f.0.0.1")]
    [TestCase("random\\ranuser1")]
    [TestCase("example.com")]
    [TestCase("1.2.3.4:80")]
    [TestCase(":::")]
    public void NotAnAddress(string value)
    {
        IpAddresses.TryParse(value, out _).Should().BeFalse();
    }
}
