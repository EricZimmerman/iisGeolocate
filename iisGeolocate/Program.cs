using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Exceptionless;
using Fclp;
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

        internal class ApplicationArguments
        {
            public string LogDirectory { get; set; }
          
        }


        private static void SetupNLog()
        {
            var config = new LoggingConfiguration();
            var loglevel = LogLevel.Info;

            var layout = @"${longdate} | ${message}";

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
                    "The directory that contains IIS logs. If not specified, defaults to same directory as executable")
                .SetDefault(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));

            _logger = LogManager.GetCurrentClassLogger();

            var header =
                $"iisgeolocate version {Assembly.GetExecutingAssembly().GetName().Version}" +
                "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
                "\r\nhttps://github.com/EricZimmerman/iisGeolocate";

            _fluentCommandLineParser.SetupHelp("?", "help")
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

            var outDirName = "out";
            var outDir = Path.Combine(_fluentCommandLineParser.Object.LogDirectory, outDirName);

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

            var logFiles = Directory.GetFiles(_fluentCommandLineParser.Object.LogDirectory, "*.log");

            if (logFiles.Length > 0)
            {
                _logger.Info($"Found {logFiles.Length} log files");
            }
            else
            {
                _logger.Fatal("No files ending in .log found. Exiting...");
                return;
            }

            var cIpSlot = -1;

            if (File.Exists("GeoLite2-City.mmdb") == false)
            {
                _logger.Fatal("'GeoLite2-City.mmdb' not found! Cannot continue. Exiting");
                return;
            }


            using (var reader = new DatabaseReader("GeoLite2-City.mmdb"))
            {
                foreach (var file in logFiles)
                {
                    _logger.Warn($"Opening '{file}'");

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
                                    _logger.Info("Looking for/verifying 'c-ip' field position...");
                                    foreach (var field in fields)
                                    {
                                        if (field.Equals("c-ip"))
                                        {
                                            cIpSlot = pos - 1; //account for #Fields: 

                                            _logger.Info($"Found 'c-ip' field position in column '{cIpSlot}'!");
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
                                    {
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.Info($"Error: {ex.Message} for line: {line}");
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

            _logger.Info("\r\n\r\n");

            if (uniqueIps.Count <= 0)
            {
                _logger.Info("No unique, geolocated IPs found!");
                return;
            }

            _logger.Info("Saving unique IPs to \'!UniqueIPs.tsv\'");
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