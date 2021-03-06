﻿using System;
using System.Collections.Generic;
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
                    "The directory that contains IIS logs. This will be recursively searched for *.log files. Required");

            _fluentCommandLineParser.Setup(arg => arg.CsvDirectory)
                .As("csv")
                .WithDescription(
                    "The directory to write results to. Required");

            _fluentCommandLineParser.Setup(arg => arg.SuppressBadLines)
                .As("sbl")
                .WithDescription(
                    "When true, do NOT show bad lines to console (they are still logged to a file). Default is FALSE").SetDefault(false);

            _fluentCommandLineParser.Setup(arg => arg.NoUpdatedLogs)
                .As("nul")
                .WithDescription(
                    "When true, do NOT create updated CSV files in --csv directory. Default is FALSE").SetDefault(false);

            
            
            _logger = LogManager.GetCurrentClassLogger();

            var header =
                $"iisgeolocate version {Assembly.GetExecutingAssembly().GetName().Version}" +
                "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
                "\r\nhttps://github.com/EricZimmerman/iisGeolocate";

            _fluentCommandLineParser.SetupHelp("?", "help", "h")
                .WithHeader(header)
                .Callback(text => _logger.Info(text + "\r\n" + ""));

            var result = _fluentCommandLineParser.Parse(args);

            var baseDir =Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);

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

            _fluentCommandLineParser.Object.LogDirectory = Path.GetFullPath(_fluentCommandLineParser.Object.LogDirectory);
            _fluentCommandLineParser.Object.CsvDirectory = Path.GetFullPath(_fluentCommandLineParser.Object.CsvDirectory);

            if (Directory.Exists(_fluentCommandLineParser.Object.LogDirectory) == false)
            {
                _logger.Warn($"'{_fluentCommandLineParser.Object.LogDirectory}' does not exist. Exiting");
                return;
            }

            var litePath = Path.Combine(baseDir, "GeoLite2-City.mmdb");
            var cityPath = Path.Combine(baseDir, "GeoIP2-City.mmdb");

            if (File.Exists(litePath) == false && File.Exists(cityPath) == false)
            {
                _logger.Fatal("'GeoLite2-City.mmdb' or 'GeoIP2-City.mmdb' missing! Cannot continue. Exiting");
                return;
            }

            var dbName = litePath;

            if (File.Exists(cityPath))
            {
                _logger.Info("Found 'GeoIP2-City.mmdb', so using that vs lite...");
                dbName = cityPath;
            }

            var logFiles = Directory.GetFiles(_fluentCommandLineParser.Object.LogDirectory, "*.log", SearchOption.AllDirectories);

            if (logFiles.Length > 0)
            {
                _logger.Info($"Found {logFiles.Length} log files");
            }
            else
            {
                _logger.Fatal("No files ending in .log found. Exiting...");
                return;
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

            var ipinfo = new Dictionary<string, GeoResults>();

            using (var reader = new DatabaseReader(dbName))
            {
                foreach (var file in logFiles)
                {
                    _logger.Warn($"Opening '{file}'");

                    var fileChunks = new Dictionary<string, List<string>>();

                    using (var instream = File.OpenText(file))
                    {
                        if (instream.BaseStream.Length == 0)
                        {
                            _logger.Fatal($"\t'{file}' is empty. Skipping...");
                            instream.Close();
                            continue;
                        }
                        var line = instream.ReadLine();

                        if (line.StartsWith("#") == false)
                        {
                            _logger.Fatal($"\tThe first line in '{file}' does not start with a #! Is this an IIS log? Skipping...");
                            instream.Close();
                            continue;
                        }


                        if (line.StartsWith("#Software: Microsoft Exchange"))
                        {
                            _logger.Fatal($"\tSkipping '{file}'! Does not appear to be an IIS related file. Skipping...");
                            instream.Close();
                            continue;
                        }

                        string lastHeaderRow = null;

                        while (line != null)
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

                                    //new data based on header
                                   
                                    lastHeaderRow = headerRow;

                                    fileChunks.Add(headerRow, new List<string>());

                                    headerRow = $"{headerRow} GeoCity GeoCountry";

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

                        CsvWriter csvOut = null;

                        if (_fluentCommandLineParser.Object.NoUpdatedLogs == false)
                        {
                            csvOut = new CsvWriter(new StreamWriter(fout), CultureInfo.CurrentCulture);
                        }
                        
                        //outcsv stuff end

                        var conf = new CsvConfiguration(CultureInfo.CurrentCulture);
                        conf.Delimiter = " ";
                        conf.BadDataFound = rawData =>
                        {
                            badStream.Write(rawData.RawRecord);
                            if (_fluentCommandLineParser.Object.SuppressBadLines)
                            {
                                return;
                            }

                            _logger.Warn($"Bad data found! Ignoring!!! Row: '{rawData.RawRecord.Trim()}'");
                        };

                        //write out lines to temp file to avoid out of memory error
                        var tmp = Path.Combine(baseDir, "tmp.txt");
                        File.WriteAllLines(tmp,fileChunk.Value);

                        using (var sw = new StreamReader(tmp))
                        {
                            var csv = new CsvReader(sw, conf);    

                            csv.Read();
                            csv.ReadHeader();

                            while (csv.Read())
                            {
                                var record = csv.GetRecord<dynamic>();

                                string ip = record.c_ip;

                                if (ip == "127.0.0.1" || ip == "::1" || ip.StartsWith("10.") || ip.StartsWith("192.168"))
                                {
                                    record.GeoCity = "NA";
                                    record.GeoCountry = "NA";
                                }
                                else
                                {
                                    if (ipinfo.ContainsKey(ip) == false)
                                    {
                                        var gr = GetIpInfo(ip, reader);
                                        ipinfo.Add(ip,gr);
                                    }
                                    record.GeoCity = ipinfo[ip].City;
                                    record.GeoCountry = ipinfo[ip].Country;    
                                }

                                csvOut?.WriteRecord(record);
                                csvOut?.NextRecord();

                                if (csvOut?.Row % 10_000 == 0) 
                                {
                                    csvOut?.Flush();
                                }
                            }
                       
                            csvOut?.Flush();
                            csvOut?.Dispose();
                            
                            sw.Close();
                        }
                        File.Delete(tmp);
                     
                    }

                    badStream.Flush();
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
                var csw = new CsvWriter(uniqOut, CultureInfo.CurrentCulture);
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
            public bool NoUpdatedLogs { get; set; }
            public string CsvDirectory { get; set; }
        }
    }
}