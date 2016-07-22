# iisGeolocate
geolocate ip addresses in IIS logs

When the tool is started (it is a command line tool, so open a cmd window then run iisgeolocate.exe from there vs double clicking), it will look for all *.log files and process them. all output files will be written to a new subdirectory called 'out'

Additionally, every unique, geolocated IP will be written out to a file in the 'out' directory called !UniqueIPs.tsv. This is a tab separated file you can load into Excel and go nuts on.

Extract it to the same directory as all your logs, then open a command prompt and:

1. navigate to where your logs are
2. run iisgeolocate.exe
3. wait
4. look in the out directory for processed logs and a file containing all unique IPs

The Geolocation data will be added to the end of each log entry

lemme know if you have any issues or if you want other features added
