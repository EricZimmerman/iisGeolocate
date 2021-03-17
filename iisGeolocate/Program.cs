using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using CsvHelper;
using CsvHelper.Configuration;
using Exceptionless;
using Fclp;
using Fclp.Internals.Extensions;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace iisGeolocate
{
    internal class Program
    {
        private static Logger _logger;
        private static FluentCommandLineParser<ApplicationArguments> _fluentCommandLineParser;
        private static Dictionary<string, UniqueIp> _uniqueIps;

        private static void SetupNLog()
        {
            var config = new LoggingConfiguration();
            var loglevel = LogLevel.Info;

            var layout = @"${message}";

            var consoleTarget = new ColoredConsoleTarget();

            config.AddTarget("console", consoleTarget);

            consoleTarget.Layout = layout;

            var rule1 = new LoggingRule("*", loglevel, consoleTarget);
            config.LoggingRules.Add(rule1);

            LogManager.Configuration = config;
        }

        private static void Main(string[] args)
        {
            ExceptionlessClient.Default.Startup("ujUuuNlhz7ZQKoDxBohBMKmPxErDgbFmNdYvPRHM");

            SetupNLog();

            _fluentCommandLineParser = new FluentCommandLineParser<ApplicationArguments>
            {
                IsCaseSensitive = false
            };
            _fluentCommandLineParser.Setup(arg => arg.LogDirectory)
                .As('d')
                .WithDescription(
                    "The directory that contains IIS logs. This will be recursively searched for *.log files");

            _fluentCommandLineParser.Setup(arg => arg.CsvDirectory)
                .As("csv")
                .WithDescription(
                    "The directory to write results to. Required");

            _fluentCommandLineParser.Setup(arg => arg.SuppressBadLines)
                .As("sbl")
                .WithDescription(
                    "When true, do NOT show bad lines to console (they are still logged to a file). Default is FALSE").SetDefault(false);


            

            // _fluentCommandLineParser.Setup(arg => arg.FieldName)
            //     .As('f')
            //     .WithDescription(
            //         "The field name to find to do the geolocation on. Default is 'c-ip'")
            //     .SetDefault("c-ip");

            _logger = LogManager.GetCurrentClassLogger();

            var header =
                $"iisgeolocate version {Assembly.GetExecutingAssembly().GetName().Version}" +
                "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
                "\r\nhttps://github.com/EricZimmerman/iisGeolocate";

            _fluentCommandLineParser.SetupHelp("?", "help", "h")
                .WithHeader(header)
                .Callback(text => _logger.Info(text + "\r\n" + ""));

            var result = _fluentCommandLineParser.Parse(args);

            if (result.HelpCalled)
            {
                return;
            }

            if (result.HasErrors)
            {
                _logger.Error("");
                _logger.Error(result.ErrorText);

                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                return;
            }

            if (_fluentCommandLineParser.Object.LogDirectory.IsNullOrEmpty() ||
                _fluentCommandLineParser.Object.CsvDirectory.IsNullOrEmpty())
            {
                _fluentCommandLineParser.HelpOption.ShowHelp(_fluentCommandLineParser.Options);

                _logger.Warn("Both -d and --csv are required. Exiting");
                return;
            }

            _uniqueIps = new Dictionary<string, UniqueIp>();

            
            _logger.Info(header);
            _logger.Info("");


            var logFiles = Directory.GetFiles(_fluentCommandLineParser.Object.LogDirectory, "*.log",SearchOption.AllDirectories);

            if (logFiles.Length > 0)
            {
                _logger.Info($"Found {logFiles.Length} log files");
            }
            else
            {
                _logger.Fatal("No files ending in .log found. Exiting...");
                return;
            }
            
            if (File.Exists("GeoLite2-City.mmdb") == false && File.Exists("GeoIP2-City.mmdb") == false)
            {
                _logger.Fatal("'GeoLite2-City.mmdb' or 'GeoIP2-City.mmdb' missing! Cannot continue. Exiting");
                return;
            }

            var dbName = "GeoLite2-City.mmdb";

            if (File.Exists("GeoIP2-City.mmdb"))
            {
                _logger.Info("Found 'GeoIP2-City.mmdb', so using that vs lite...");
                dbName = "GeoIP2-City.mmdb";
            }


            if (Directory.Exists(_fluentCommandLineParser.Object.CsvDirectory) == false)
            {
                Directory.CreateDirectory(_fluentCommandLineParser.Object.CsvDirectory);
            }


            _logger.Warn(
                "NOTE: multicast, private, or reserved addresses will be SKIPPED (including IPv6 that starts with 'fe80'");

            var badDataFile = Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, "BadDatarows_REVIEW_ME.txt");
            var badStream = new StreamWriter(badDataFile);

            _logger.Info($"\r\nAll malformed data rows will be IGNORED but written to '{badDataFile}'. REVIEW THIS!\r\n");

            using (var reader = new DatabaseReader(dbName))
            {
                foreach (var file in logFiles)
                {
                    _logger.Warn($"Opening '{file}'");

                    var fileChunks = new Dictionary<string, List<string>>();

                    using (var instream = File.OpenText(file))
                    {
                        var line = instream.ReadLine();

                        string lastHeaderRow = null;

                        while (line!=null)
                        {
                            if (line.StartsWith("#"))
                            {
                                if (line.StartsWith("#Fields:"))
                                {
                                    var headerRow = line.Substring(9);

                                    //need to change to underscore so the dynamic object knows how to get data out vs trying to subtract c - ip. stupid microsoft and these names
                                    headerRow = headerRow.Replace("-", "_");

                                    if (headerRow == lastHeaderRow)
                                    {
                                        //the second header is the same, so keep appending
                                        line = instream.ReadLine();
                                        continue;
                                    }
                                    
                                    {
                                        //new data based on header

                                        lastHeaderRow = headerRow;
                                        
                                        fileChunks.Add(headerRow,new List<string>());

                                        headerRow = $"{headerRow} GeoCity GeoCountry";
                                    }
                                    
                                    fileChunks[lastHeaderRow].Add(headerRow);
                                    
                                    line = instream.ReadLine();
                                    continue;
                                }
                                
                                line = instream.ReadLine();
                                continue;
                            }
                            
                            //this is where data needs to be persisted for later
                            fileChunks[lastHeaderRow].Add(line);
                            
                            line = instream.ReadLine();
                        }

                    }
                    
                    //at this point, iterate all fileChunks and make it a csv, do lookup, update extra fields, write it out

                    var ts = DateTimeOffset.UtcNow;
                    var counter = 0;

                    _logger.Info($"\tLog chunks found in '{file}': {fileChunks.Count}. Processing chunks...");
                    foreach (var fileChunk in fileChunks)
                    {
                        counter += 1;

                        _logger.Info($"\tFound {fileChunk.Value.Count:N0} rows in chunk {counter}");
                      
                        //outcsv stuff

                        var logBaseName = Path.GetFileNameWithoutExtension(file);

                        var fout = Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, $"{ts:yyyyMMddHHmmss}_{logBaseName}_Chunk{counter}.csv");

                        var csvOut = new CsvWriter(new StreamWriter(fout), CultureInfo.CurrentCulture);


                        //outcsv stuff



                        var conf = new CsvConfiguration(CultureInfo.CurrentCulture);
                        conf.Delimiter = " ";
                        conf.BadDataFound = rawData =>
                        {
                            if (_fluentCommandLineParser.Object.SuppressBadLines)
                            {
                                return;
                                
                            }
                            _logger.Warn($"Bad data found! Ignoring!!! Row: '{rawData.RawRecord.Trim()}'");
                            badStream.Write(rawData.RawRecord);
                        };

                        var aa = new StringReader(string.Join("\r\n", fileChunk.Value));
                        var csv = new CsvReader(aa,conf);
                        
                        csv.Read();
                        csv.ReadHeader();

                      
                        
                        var rows = csv.GetRecords<dynamic>().ToList();

                        foreach (var row in rows)
                        {
                           string ip = row.c_ip;

                           if (ip == "127.0.0.1" || ip == "::1" || ip.StartsWith("10.") || ip.StartsWith("192.168"))
                           {
                               row.GeoCity = "NA";
                               row.GeoCountry = "NA";
                               continue;
                           }

                           var gr = GetIpInfo(ip,reader);
                            
                           row.GeoCity = gr.City;
                           row.GeoCountry = gr.Country;
                        }

                        csvOut.WriteRecords(rows);

                        csvOut.Flush();
                        csvOut.Dispose();

                    }
                    
                    badStream.Flush();
                    
                    
                    // //OLD
                    //
                    // var baseFilename = Path.GetFileName(file);
                    // var outFilename = Path.Combine(outDir, baseFilename);
                    //
                    // using (var outstream = new StreamWriter(File.Open(outFilename, FileMode.OpenOrCreate,
                    //     FileAccess.Write, FileShare.Read)))
                    // {
                    //     if (uniqueIps.Count > 0)
                    //     {
                    //         _logger.Info($"Unique IPs found so far: {uniqueIps.Count:N0}");
                    //         return;
                    //     }
                    //
                    //     using (var instream = File.OpenText(file))
                    //     {
                    //         var conf = new CsvConfiguration(CultureInfo.CurrentCulture);
                    //         conf.Delimiter = " ";
                    //
                    //         var csv = new CsvReader(instream,conf);
                    //         //csv.Configuration.Delimiter = " ";
                    //         //csv.Configuration.HasHeaderRecord = false;
                    //
                    //         csv.Read();
                    //
                    //         string[] fields = null;
                    //         dynamic currentRecord;
                    //
                    //         var rawLine = csv.Context.Parser.RawRecord.Trim();
                    //
                    //         while (rawLine.StartsWith("#"))
                    //         {
                    //             if (rawLine.StartsWith("#Fields"))
                    //             {
                    //                 fields = rawLine.Split(' ').Skip(1).ToArray();
                    //
                    //                 rawLine += " GeoCity GeoCountry";
                    //             }
                    //
                    //             outstream.WriteLine(rawLine);
                    //
                    //             csv.Read();
                    //
                    //             rawLine = csv.Context.Parser.RawRecord.Trim();
                    //         }
                    //
                    //         if (fields == null)
                    //         {
                    //             _logger.Warn("Unable to find 'Fields' info in file. Skipping...");
                    //             continue;
                    //         }
                    //
                    //         var pos = 0;
                    //         _logger.Info(
                    //             $"Looking for/verifying '{_fluentCommandLineParser.Object.FieldName}' field position...");
                    //         foreach (var field in fields)
                    //         {
                    //             if (field.Equals(_fluentCommandLineParser.Object.FieldName,
                    //                 StringComparison.OrdinalIgnoreCase))
                    //             {
                    //                 dataSlot = pos;
                    //
                    //                 _logger.Info(
                    //                     $"Found '{_fluentCommandLineParser.Object.FieldName}' field position in column '{dataSlot}'!");
                    //                 break;
                    //             }
                    //
                    //             pos += 1;
                    //         }
                    //
                    //         
                    //
                    //
                    //         //we are at the actual data now
                    //
                    //         while (csv.Read())
                    //         {
                    //             rawLine = csv.Context.Parser.RawRecord.Trim();
                    //
                    //             currentRecord = csv.GetRecord<dynamic>();
                    //
                    //             var rec = (IDictionary<string, object>) currentRecord;
                    //
                    //             var key = $"Field{dataSlot + 1}"; //fields start at 1
                    //
                    //             var ipAddress = ((string) rec[key]).Replace("\"", "");
                    //
                    //             if (ipAddress.StartsWith("fe80"))
                    //             {
                    //                 continue;
                    //             }
                    //
                    //             //do ip work
                    //
                    //             var geoCity = "NA";
                    //             var geoCountry = "NA";
                    //
                    //             try
                    //             {
                    //                 var segs2 = ipAddress.Split('.');
                    //                 if (segs2.Length > 1)
                    //                 {
                    //                     var first = int.Parse(segs2[0]);
                    //                     var second = int.Parse(segs2[1]);
                    //
                    //                     if (first >= 224 || first == 10 || first == 192 && second == 168 ||
                    //                         first == 172 && second >= 16 && second <= 31)
                    //                     {
                    //                         continue;
                    //                     }
                    //                 }
                    //
                    //                 var city = reader.City(ipAddress);
                    //                 geoCity = city.City?.Name?.Replace(' ', '_');
                    //
                    //                 geoCountry = city.Country.Name.Replace(' ', '_');
                    //
                    //                 if (uniqueIps.ContainsKey(ipAddress) == false)
                    //                 {
                    //                     var ui = new UniqueIp {City = city.City?.Name};
                    //                     ui.Country = city.Country.Name;
                    //                     ui.IpAddress = ipAddress;
                    //
                    //                     uniqueIps.Add(ipAddress, ui);
                    //                 }
                    //             }
                    //             catch (AddressNotFoundException an)
                    //             {
                    //             }
                    //             catch (Exception ex)
                    //             {
                    //                 _logger.Info($"Error: {ex.Message} for line: {rawLine}");
                    //                 geoCity = $"City error: {ex.Message}";
                    //                 geoCountry = "Country error: (See city error)";
                    //             }
                    //
                    //             rawLine += $" {geoCity} {geoCountry}";
                    //
                    //             outstream.WriteLine(rawLine);
                    //         }
                    //     }
                    //
                    //     outstream.Flush();
                    // }
                    //
                    // //END OLD
                }

                badStream.Flush();
                badStream.Close();
            }

            _logger.Info("");

            if (_uniqueIps.Count <= 0)
            {
                _logger.Info("No unique, geolocated IPs found!\r\n");
                return;
            }

            _logger.Info("Saving unique IPs to '!UniqueIPs.csv'");

            using (var uniqOut = new StreamWriter(File.OpenWrite(Path.Combine(_fluentCommandLineParser.Object.CsvDirectory, "!UniqueIPs.csv"))))
            {
                var csw = new CsvWriter(uniqOut,CultureInfo.CurrentCulture);
                csw.WriteHeader<UniqueIp>();
                csw.NextRecord();
                csw.WriteRecords(_uniqueIps.Values);
                uniqOut.Flush();
            }

            _logger.Info("");
        }

        public static GeoResults GetIpInfo(string ip, DatabaseReader reader)
        {
            var gr = new GeoResults();
            gr.City = "NA";
            gr.Country = "NA";

            try
            {
                var city = reader.City(ip);
                gr.City = city.City?.Name?.Replace(' ', '_');
                gr.Country = city.Country?.Name?.Replace(' ', '_');
                

                if (_uniqueIps.ContainsKey(ip) == false)
                {
                    var ui = new UniqueIp {City = city.City?.Name};
                    ui.Country = city.Country.Name;
                    ui.IpAddress = ip;
                
                    _uniqueIps.Add(ip, ui);
                }
            }

            catch (AddressNotFoundException a)
            {
                //eat it
   
            }
            catch (Exception ex)
            {
                _logger.Info($"Error: {ex.Message} for ip: {ip}");
            }

            return gr;
        }

        internal class GeoResults
        {
            public string City { get; set; }
            public string Country { get; set; }
        }

        internal class UniqueIp
        {
            public string IpAddress { get; set; }
            public string City { get; set; }
            public string Country { get; set; }
        }

        internal class ApplicationArguments
        {
            public string LogDirectory { get; set; }
            public bool SuppressBadLines { get; set; }
            public string CsvDirectory { get; set; }
        }
    }
}