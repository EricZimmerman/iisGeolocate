# iisGeolocate

Converts IIS logs (and other W3C extended logs, such as HTTPERR logs) to CSV and adds the city and country of each
client IP address (`c-ip`) from a MaxMind database. Every geolocated address is also listed in its own file, and rows
that can't be read are written to a file to review.

| | |
|---|---|
| **Version** | 2026.10.0 |
| **Download** | [.NET 10](https://download.ericzimmermanstools.com/net/iisGeolocate.zip) (recommended) &middot; [.NET Framework 4.7.2](https://download.ericzimmermanstools.com/iisGeolocate.zip) |
| **All tools** | [ericzimmerman.github.io](https://ericzimmerman.github.io) or [Get-ZimmermanTools](https://download.ericzimmermanstools.com/Get-ZimmermanTools.zip), which downloads them all and keeps them current |
| **Changes** | [ChangeLog](https://ericzimmerman.github.io/changelog/) |
| **Issues** | [EricZimmerman/iisGeolocate](https://github.com/EricZimmerman/iisGeolocate/issues) or [EricZimmerman/Issues](https://github.com/EricZimmerman/Issues/issues) |

## Requirements

- **.NET 10 build**: the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Run `iisGeolocate.exe` on
  Windows, or `dotnet iisGeolocate.dll` on any system with the runtime.
- **.NET Framework build**: .NET Framework 4.7.2 or later (Windows only).
- A MaxMind database (see [The database](#the-database)).

## Command line

```text
Usage:
  iisGeolocate [options]

Options:
  -d <d>          Directory to recursively process for IIS logs. This will be searched for *.log files
  --csv <csv>     Directory to save CSV formatted results to. Be sure to include the full path in double quotes
  --db <db>       MaxMind database to use (City, Enterprise or Country). Default is GeoIP2-City.mmdb, else GeoLite2-City.mmdb, next to iisGeolocate
  --sbl           When true, do NOT show bad lines to console (they are still logged to a file). Default is false
  --nul           When true, do NOT create updated CSV files in --csv directory. Default is false
  -?, -h, --help  Show help and usage information
  --version       Show version information
```

Short options (a single letter) start with one dash, long options with two.

## Examples

```text
iisGeolocate.exe -d "C:\inetpub\logs\LogFiles" --csv "C:\temp\out"
iisGeolocate.exe -d "C:\temp\logs" --csv "C:\temp\out" --db "C:\GeoIP\GeoLite2-City.mmdb"
```

## Output

In the `--csv` directory:

- **Log CSVs** (`<timestamp>_<log name>_Chunk<n>.csv`): the rows of each log, with a header row of the log's field names
  as written (`c-ip`, `cs(User-Agent)`, ...) and `GeoCity` and `GeoCountry` added at the end. A log gets one CSV per
  distinct `#Fields` line (IIS writes them again after a restart, and the fields can change). Logs with the same name in
  different folders (W3SVC1, W3SVC2, ...) get `_2`, `_3`, ... at the end. `--nul` skips these files.
- **`!UniqueIPs.csv`**: every address that was geolocated (`IpAddress`, `City`, `Country`), in the order first seen.
  Load it into Timeline Explorer. It is only written when something was geolocated.
- **`BadDataRows_REVIEW_ME.txt`** (tab separated: `SourceFile`, `LineNumber`, `Reason`, `Row`): rows that were not
  written, with the reason (the wrong number of values, or no `#Fields` line before them). REVIEW IT.

`GeoCity` and `GeoCountry` are `NA` for private, shared (CGNAT), loopback, link local, multicast, documentation and
reserved addresses, for addresses that are not in the database, when the value is not an IP address, and when the log
has no `c-ip` field. Spaces in their names are written as `_` (not in `!UniqueIPs.csv`). The city is empty when the
database has a country but no city for the address. CSVs always use commas, whatever the system's list separator.

Values are separated by one or more spaces or tabs, and quoted values (IIS Advanced Logging) are read as one value.
Logs whose first line doesn't start with `#`, and Exchange logs, are skipped. The summary at the end shows the number
of rows and bad rows and how many distinct addresses were geolocated, not found, not public or not an IP address.
Nothing is written outside the `--csv` directory.

## The database

Without `--db`, iisGeolocate uses `GeoIP2-City.mmdb` next to it, or else `GeoLite2-City.mmdb`. The download zips include
a `GeoLite2-City.mmdb`, carried over from release to release, so it may not be current; the repository has none. For a
current one, get GeoLite2 City (free, with a MaxMind account) from
[MaxMind](https://dev.maxmind.com/geoip/geolite2-free-geolocation-data/), or GeoIP2 City from
[maxmind.com](https://www.maxmind.com), and put the `.mmdb` file next to iisGeolocate or point `--db` at it. City,
Enterprise and Country databases work (a Country database gives no cities); other types, such as ASN, are refused.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. Damaged logs (bad rows, errors while processing; listed under "Failed files") and finding no logs are not a failure |
| `1` | The tool failed: invalid or missing arguments, a missing `-d` directory, no usable database, a log or folder it can't read, or an output file that can't be written. Folders under `-d` that can't be read are reported (`Unable to read folder <path>: <reason>`) and the rest is still processed |

## What's new in 2026.10

- About 9x faster with a fifth of the memory (197 MB log: 11.8 s -> 1.3 s, 619 MB -> 112 MB); nothing is written next
  to the program any more (`tmp.txt`).
- The log CSVs have a header row (the field names from the log plus `GeoCity` and `GeoCountry`).
- Logs that crashed it are read; values separated by more than one space and quoted values stay in their columns; rows
  with the wrong number of values go to `BadDataRows_REVIEW_ME.txt` with their file, line and reason instead of into the
  CSV with values in the wrong columns.
- All private, shared (CGNAT), link local, multicast, documentation and reserved IPv4 and IPv6 ranges are skipped (only
  127.0.0.1, ::1, 10.x and 192.168.x were); IPv4 mapped IPv6 addresses are looked up as IPv4.
- Logs with the same name in different folders (W3SVC1, W3SVC2, ...) each get their own CSV; the second overwrote the
  first.
- New `--db` option to use a MaxMind database from anywhere; Country and Enterprise databases work too.
- CSVs always use commas (they used the system's list separator, such as `;` on German systems).
- Exit code 1 when the tool fails (it was always 0, even after a crash). Error reports include source file and line
  numbers.

The full list is in the [ChangeLog](https://ericzimmerman.github.io/changelog/).

## Building

```text
dotnet build iisGeolocate.slnx -c Release
```

Builds `net10.0` and `net472`. Lookups use MaxMind's [MaxMind.GeoIP2](https://www.nuget.org/packages/MaxMind.GeoIP2)
library.

## Special thanks

Open source development funding and support provided by the [SANS Institute](https://www.sans.org/) and
[SANS DFIR](https://www.sans.org/digital-forensics-incident-response/).

## License

[MIT](LICENSE), Copyright (c) Eric R. Zimmerman 2027.
