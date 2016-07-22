using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;

namespace iisGeolocate
{
    class Program
    {
        static void Main(string[] args)
        {
            var outDirName = "out";
            var outDir = Path.Combine(Environment.CurrentDirectory, outDirName);

            if (Directory.Exists(outDir) == false)
            {
                Directory.CreateDirectory(outDir);
            }



            //#Software: Microsoft Internet Information Services 6.0
            //#Version: 1.0
            //#Date: 2016-01-01 00:00:11
            //#Fields: date time s-sitename s-ip cs-method cs-uri-stem cs-uri-query s-port cs-username c-ip cs(User-Agent) sc-status sc-substatus sc-win32-status 
            //2016-01-01 00:00:11 W3SVC1 192.168.22.6 GET /iisstart.htm - 443 - 205.210.42.86 easyDNS+Monitoring+(+http://easyurl.net/monitoring+) 200 0 0
            //2016-01-01 00:02:03 W3SVC1 192.168.22.6 POST /Autodiscover/Autodiscover.xml - 443 WRIGHTUSA\dweber 192.168.22.210 Microsoft+Office/12.0+(Windows+NT+6.1;+Microsoft+Office+Outlook+12.0.6739;+Pro) 200 0 0

            var uniqueIps = new Dictionary<string, string>();

            var logFiles = Directory.GetFiles(Environment.CurrentDirectory, "*.log");

            if (logFiles.Length > 0)
            {
                Console.WriteLine($"Found {logFiles.Length} log files");
            }
            else
            {
                Console.WriteLine("No files ending in .log found. Exiting...");
                return;
            }

            var cIpSlot = -1;


            using (var reader = new DatabaseReader("GeoLite2-City.mmdb"))
            {

                foreach (var file in logFiles)
                {
                    Console.WriteLine($"Opening '{file}'");

                    var baseFilename = Path.GetFileName(file);
                    var outFilename = Path.Combine(outDir, baseFilename);

                    using (var outstream = new StreamWriter(File.OpenWrite(outFilename)))
                    {
                        using (var instream = File.OpenText(file))
                        {
                            var line = instream.ReadLine();

                            while (line != null)
                            {
                                if (line.StartsWith("#Fields"))
                                {
                                    line = line.Trim() + " GeoCountry GeoCity";
                                    var fields = line.Split(' ');
                                    var pos = 0;
                                    Console.WriteLine("Looking for/verifying 'c-ip' field position...");
                                    foreach (var field in fields)
                                    {
                                        if (field.Equals("c-ip"))
                                        {
                                            cIpSlot = pos - 1; //account for #Fields: 

                                            Console.WriteLine($"Found 'c-ip' field position in column '{cIpSlot}'!");
                                            break;
                                        }
                                        pos += 1;
                                    }
                                }

                                var geoCity = "NA";
                                var geoCountry = "NA";

                                if (line.StartsWith("#") == false)
                                {
                                    var segs = line.Split(' ');
                                    var ip = segs[cIpSlot];

                                    try
                                    {
                                        var city = reader.City(ip);
                                        geoCity = city.City?.Name?.Replace(' ', '_');
                                        
                                        geoCountry = city.Country.Name.Replace(' ', '_');

                                        if (uniqueIps.ContainsKey(ip) == false)
                                        {
                                            uniqueIps.Add(ip, $"{geoCity}, {geoCountry}");
                                        }
                                    }
                                    catch (AddressNotFoundException)
                                    { }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Error: {ex.Message} for line: {line}");
                                        geoCity = $"City error: {ex.Message}";
                                        geoCountry = "Country error: (See city error)";
                                    }

                                    line = line + $" {geoCity} {geoCountry}";
                                }

                              

                                outstream.WriteLine(line);

                                line = instream.ReadLine();
                            }

                        }

                        outstream.Flush();
                    }
                }


            }

            Console.WriteLine("\r\n\r\n");

            if (uniqueIps.Count <= 0)
            {
                Console.WriteLine("No unique, geolocated IPs found!");
                return;
            }
            
                Console.WriteLine($"Saving unique IPs to '!UniqueIPs.tsv'");
                using (var uniqOut = new StreamWriter(File.OpenWrite(Path.Combine(outDir, "!UniqueIPs.tsv"))))
                {
                    foreach (var uniqueIp in uniqueIps)
                    {
                        var line = $"{uniqueIp.Key}\t{uniqueIp.Value}";

                        uniqOut.WriteLine(line);
                    }

                    uniqOut.Flush();
                }
            
        }
    }
}
