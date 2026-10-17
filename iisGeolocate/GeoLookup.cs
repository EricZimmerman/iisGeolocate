using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using MaxMind.GeoIP2;

namespace iisGeolocate;

public class GeoResult
{
    /// <summary>
    ///     What goes in the log CSVs: names with spaces replaced by '_' (as in earlier versions), "NA" when the address
    ///     is not a public IP address or is not in the database, and an empty city when the database has none
    /// </summary>
    public string City { get; set; }

    public string Country { get; set; }

    public static readonly GeoResult NotAvailable = new() { City = "NA", Country = "NA" };
}

public class UniqueIp
{
    public string IpAddress { get; set; }
    public string City { get; set; }
    public string Country { get; set; }
}

/// <summary>
///     Looks up addresses in a MaxMind City, Enterprise or Country database (GeoLite2-City.mmdb, GeoIP2-City.mmdb, ...),
///     caching results. Addresses that were found are collected in <see cref="UniqueIps" /> in the order first seen.
/// </summary>
public sealed class GeoLookup : IDisposable
{
    private enum Kind
    {
        City,
        Enterprise,
        Country
    }

    private readonly Dictionary<string, GeoResult> _cache = new(StringComparer.Ordinal);
    private readonly Kind _kind;
    private readonly DatabaseReader _reader;

    /// <exception cref="InvalidDataException">The file is not a MaxMind database, or not one with countries</exception>
    public GeoLookup(string databasePath)
    {
        try
        {
            _reader = new DatabaseReader(databasePath);
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Not a valid MaxMind database: {ex.Message}", ex);
        }

        DatabaseType = _reader.Metadata.DatabaseType ?? string.Empty;

        if (DatabaseType.IndexOf("Enterprise", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _kind = Kind.Enterprise;
        }
        else if (DatabaseType.IndexOf("City", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _kind = Kind.City;
        }
        else if (DatabaseType.IndexOf("Country", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _kind = Kind.Country;
        }
        else
        {
            _reader.Dispose();
            throw new InvalidDataException(
                $"Database type '{DatabaseType}' has no locations. Use a City, Enterprise or Country database");
        }
    }

    public string DatabaseType { get; }

    public DateTime BuildDate => _reader.Metadata.BuildDate;

    public List<UniqueIp> UniqueIps { get; } = new();

    /// <summary>Distinct values that are not IP addresses (such as "-")</summary>
    public int InvalidAddresses { get; private set; }

    /// <summary>Distinct private, loopback, link local, multicast or reserved addresses</summary>
    public int NonPublicAddresses { get; private set; }

    /// <summary>Distinct public addresses that are not in the database</summary>
    public int NotFoundAddresses { get; private set; }

    /// <summary>Distinct addresses whose lookup failed (a damaged database)</summary>
    public List<string> LookupErrors { get; } = new();

    public void Dispose()
    {
        _reader.Dispose();
    }

    public GeoResult Lookup(string value)
    {
        value ??= string.Empty;

        if (_cache.TryGetValue(value, out var cached))
        {
            return cached;
        }

        var result = Resolve(value);
        _cache.Add(value, result);
        return result;
    }

    private GeoResult Resolve(string value)
    {
        if (IpAddresses.TryParse(value, out var address) == false)
        {
            InvalidAddresses += 1;
            return GeoResult.NotAvailable;
        }

        if (IpAddresses.IsPublic(address) == false)
        {
            NonPublicAddresses += 1;
            return GeoResult.NotAvailable;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            address = new IPAddress(address.GetAddressBytes());
        }

        string city = null;
        string country;

        try
        {
            switch (_kind)
            {
                case Kind.Enterprise:
                    if (_reader.TryEnterprise(address, out var enterprise) == false)
                    {
                        NotFoundAddresses += 1;
                        return GeoResult.NotAvailable;
                    }

                    city = enterprise.City.Name;
                    country = enterprise.Country.Name;
                    break;
                case Kind.City:
                    if (_reader.TryCity(address, out var cityResponse) == false)
                    {
                        NotFoundAddresses += 1;
                        return GeoResult.NotAvailable;
                    }

                    city = cityResponse.City.Name;
                    country = cityResponse.Country.Name;
                    break;
                default:
                    if (_reader.TryCountry(address, out var countryResponse) == false)
                    {
                        NotFoundAddresses += 1;
                        return GeoResult.NotAvailable;
                    }

                    country = countryResponse.Country.Name;
                    break;
            }
        }
        catch (Exception ex)
        {
            LookupErrors.Add($"{value}: {ex.Message}");
            return GeoResult.NotAvailable;
        }

        UniqueIps.Add(new UniqueIp { IpAddress = value, City = city, Country = country });

        return new GeoResult { City = city?.Replace(' ', '_'), Country = country?.Replace(' ', '_') };
    }
}
