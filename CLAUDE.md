# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

iisGeolocate is a command line tool (`net472;net10.0`) that converts IIS logs (any W3C extended log) to CSV and adds the location of the client IP (`c-ip`) from a MaxMind database (MaxMind.GeoIP2). It is not packed; the user builds, signs and releases it. KAPE runs it as `-d %sourceDirectory% --csv %destinationDirectory%` with GeoLite2-City.mmdb next to the exe (KapeFiles `Modules/EZTools/iisGeoLocate.mkape`), so keep those options and that default. Input may be corrupt or hostile.

- `W3CLog.TrySplit`: values are separated by runs of spaces/tabs (IIS writes spaces in values as `+`; some writers pad with several spaces; plaso's edge case sample has a double space). A value starting with `"` is a quoted string with `""` escapes (IIS Advanced Logging); a quote inside a value (`OutlookSession="{...}"`) is kept. 2026.5 used CsvHelper with a space delimiter, so a double space was an empty value that shifted the columns.
- `LogProcessor.Process`: streams one log. The first non-blank line must start with `#`, and `#Software: Microsoft Exchange` logs are skipped. Each distinct `#Fields` list is a chunk with its own CSV (`<ts>_<name>_Chunk<n>.csv`, `_2`, `_3`... when the name exists, as for W3SVC1/W3SVC2 logs of the same day); going back to an earlier list continues that chunk (it crashed 2026.5). Rows with the wrong number of values, or before any `#Fields`, go to `onBadRow` and not to the CSV. No `c-ip` field: GeoCity/GeoCountry are `NA`.
- `IpAddresses`: strict parsing (`IPAddress.TryParse` accepts `1` or `1.2` as IPv4), brackets and zone ids allowed; `IsPublic` excludes private, CGNAT, loopback, link local, multicast, documentation, benchmarking and reserved ranges (IPv4 mapped IPv6 checked as IPv4).
- `GeoLookup`: City, Enterprise or Country databases (by `Metadata.DatabaseType`; anything else, such as ASN, is refused with `InvalidDataException`). Results are cached per distinct value; `UniqueIps` holds geolocated addresses in first-seen order.
- `Program.cs`: the CLI (`-d`, `--csv`, `--db`, `--sbl`, `--nul`). `*.log` files any case, sorted, inaccessible folders skipped. Writes `BadDataRows_REVIEW_ME.txt` (tab separated: SourceFile, LineNumber, Reason, Row) and `!UniqueIPs.csv` (IpAddress, City, Country, only when something was geolocated). Exit codes: 0, or 1 for missing arguments/directory, no usable database, a log that can't be read (others continue), or an output file that can't be written. Bad rows and "no logs found" don't change the exit code.

## Behavior to keep

- Chunk CSV columns are the log's field names as written (`c-ip`, `cs(User-Agent)`), then `GeoCity`, `GeoCountry`. CSVs use `InvariantCulture` (2026.5 used the current culture, so `;` on some systems). 2026.5 wrote no header row (`CsvWriter.WriteRecord` of dynamic records doesn't write one).
- `NA`/`NA` for addresses that are not public, not in the database or not an IP address; spaces in names become `_` in the chunk CSVs (not in `!UniqueIPs.csv`), as in earlier versions; a null city (country-only data) is an empty cell.
- Nothing is written outside `--csv` (2026.5 wrote `tmp.txt` next to the exe and buffered whole logs in memory).

## Commands

The SDK is pinned in `global.json` (.NET 10). `LangVersion 10`. Tests target `net10.0` and use NUnit 5 + FluentAssertions 7 (pinned to `[7.2.2,8.0.0)`, since 8.x requires a commercial license).

```bash
dotnet build -c Release                                              # iisGeolocate.slnx
dotnet test -c Release                                               # CI (test.yml); CorpusTests is [Explicit]
dotnet test -c Release --filter "FullyQualifiedName~CorpusTests"     # ../Corpus/IISLogs (or IISGEOLOCATE_CORPUS)
IISGEOLOCATE_FUZZ_ROUNDS=20000 dotnet test -c Release --filter Name~RandomDamage
dotnet run --project iisGeolocate -f net10.0 -- -d iisGeolocate.Test/TestFiles --csv /tmp/out --db iisGeolocate.Test/TestFiles/MaxMind/GeoIP2-City-Test.mmdb
```

## Tests

- `TestFiles`: plaso's IIS samples (Apache-2.0), lnav's W3C samples (BSD-2-Clause) and MaxMind's test databases (Apache-2.0/MIT); see the READMEs. `.gitignore` ignores `*.log` except under `TestFiles`. MaxMind test data: 81.2.69.142 London, 89.160.20.112 Linköping, 216.160.83.56 Milton, 2001:218::1 Japan (no city); most other public addresses are not in it.
- `ParsingTests` (splitting, address ranges), `GeoLookupTests` (values checked with Python's `maxminddb`), `ProcessorTests` (chunks, bad rows, every sample read completely: rows + bad rows = data lines, fuzzing), `CliTests` (iisGeolocate.dll as a process: outputs, exit codes, nothing in the working folder).
- `CorpusTests`: every `*.log` in `Corpus/IISLogs` next to the repository (or `IISGEOLOCATE_CORPUS`), with a real database from that folder if there is one. There is no such corpus yet; never copy corpus files into the repo.
- Validation of a change: run the old and new builds over the same logs and diff the chunk CSVs (2026.5 has no header row; it also had an extra empty column for IIS 6 logs, whose `#Fields` line ends with a space). For lookups, generate a log with an address from every network in MaxMind's `source-data/GeoIP2-City-Test.json` and compare with Python's `maxminddb` (760 addresses agreed for 2026.10.0).

## Versioning and release

- `<Version>` in the csproj, bumped by hand. The user builds, signs and releases it.
- `.github/workflows/test.yml` builds and tests on pushes to `master` and PRs. Dependabot (`.github/dependabot.yml`) opens monthly PRs for GitHub Actions and NuGet updates.
- Release builds are staged, signed and zipped by `Build-ZimmermanTools.ps1` in the get-zimmermantools repository (cloned next to this one). Its program table says what goes in this tool's download zip (the files taken from the build, folders copied from the repositories, files kept from the current release zip); a new output file, a content folder or a change in how the tool is built (single file, another framework) needs that entry updated.
