# iisGeolocate
geolocate ip addresses in IIS logs

When the tool is started (it is a command line tool, so open a PowerShell window then run iisgeolocate.exe from there vs double clicking)

Additionally, every unique, geolocated IP will be written out to a file in the '--csv' directory called !UniqueIPs.tsv. This is a comma separated file you can load into Timeline Explorer and go nuts on.

Extract the program, then:

1. run iisgeolocate.exe and see usage
2. run iisgeolocate.exe -d <yourlogdir> --csv <wheretosavedir>
3. wait
4. look in the out directory for processed logs, a file containing all unique IPs, and a file with any bad data in it (i.e. not valid csv data). REVIEW IT

The Geolocation data will be added to the end of each log entry

let me know if you have any issues or if you want other features added
