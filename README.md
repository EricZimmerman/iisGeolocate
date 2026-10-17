# iisGeolocate

Geolocates the client IP addresses in IIS logs (and other W3C extended logs, such as HTTPERR logs) with a MaxMind database, and converts the logs to CSV.

## Command Line Interface

    -d        Directory to recursively process for IIS logs. This will be searched for *.log files
    --csv     Directory to save CSV formatted results to. Be sure to include the full path in double quotes
    --db      MaxMind database to use (City, Enterprise or Country). Default is GeoIP2-City.mmdb, else GeoLite2-City.mmdb, next to iisGeolocate
    --sbl     When true, do NOT show bad lines to console (they are still logged to a file). Default is false
    --nul     When true, do NOT create updated CSV files in --csv directory. Default is false

    Examples: iisGeolocate.exe -d "C:\inetpub\logs\LogFiles" --csv "C:\temp\out"
              iisGeolocate.exe -d "C:\temp\logs" --csv "C:\temp\out" --db "C:\GeoIP\GeoLite2-City.mmdb"

It is a command line tool, so open a PowerShell window and run it from there rather than double clicking it.

## The database

iisGeolocate does not include a database. Get GeoLite2 City (free, with a MaxMind account) or GeoIP2 City from https://www.maxmind.com and put `GeoLite2-City.mmdb` (or `GeoIP2-City.mmdb`, which is used first) next to iisGeolocate, or point `--db` at it. Country and Enterprise databases work too (a Country database gives no cities).

## Output

In the `--csv` directory:

- `<timestamp>_<log name>_Chunk<n>.csv`: the rows of each log, with `GeoCity` and `GeoCountry` added at the end. A log gets one CSV per distinct `#Fields` line (IIS writes them again after a restart, and the fields can change). Logs with the same name in different folders (W3SVC1, W3SVC2, ...) get `_2`, `_3`, ... at the end.
- `!UniqueIPs.csv`: every address that was geolocated, with its city and country. Load it into Timeline Explorer.
- `BadDataRows_REVIEW_ME.txt` (tab separated): rows that were not written, with their file, line and the reason (the wrong number of values, or no `#Fields` line before them). REVIEW IT.

`GeoCity` and `GeoCountry` are `NA` for private, loopback, link local, multicast and reserved addresses, for addresses that are not in the database, and when the value is not an IP address. Spaces in their names are written as `_`. The city is empty when the database has a country but no city for the address.

Logs whose first line doesn't start with `#`, and Exchange logs, are skipped. The summary at the end shows the number of rows and bad rows and how many distinct addresses were geolocated, not found, not public or not an IP address.

Exit code 0 means success. Exit code 1 means invalid arguments, a database that can't be used, a log that could not be read, or an output file that could not be written.

The .NET 10 build runs on Linux and macOS.

Let me know if you have any issues or if you want other features added.
