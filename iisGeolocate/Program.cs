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
                    "The directory that contains IIS logs. If not specified, defaults to same directory as executable")
                .SetDefault(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));

            _fluentCommandLineParser.Setup(arg => arg.FieldName)
                .As('f')
                .WithDescription(
                    "The field name to find to do the geolocation on. Default is 'c-ip'")
                .SetDefault("c-ip");

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

            var outDirName = "out";
            var outDir = Path.Combine(_fluentCommandLineParser.Object.LogDirectory, outDirName);

            if (Directory.Exists(outDir) == false)
            {
                Directory.CreateDirectory(outDir);
            }

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

            var dataSlot = -1;

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

            _logger.Warn(
                "NOTE: multicast, private, or reserved addresses will be SKIPPED (including IPv6 that starts with 'fe80'");


            using (var reader = new DatabaseReader(dbName))
            {
                foreach (var file in logFiles)
                {
                    _logger.Warn($"Opening '{file}'");

                    var baseFilename = Path.GetFileName(file);
                    var outFilename = Path.Combine(outDir, baseFilename);

                    using (var outstream = new StreamWriter(File.Open(outFilename,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read)))
                    {

                        using (var instream = File.OpenText(file))
                        {
                            var line = instream.ReadLine();

                            if (uniqueIps.Count > 0)
                            {
                                _logger.Info($"Unique IPs found: {uniqueIps.Count:N0}");
                                return;
                            }

                            while (line != null)
                            {
                                //                                if (line.StartsWith("#Fields"))
//                                {
//                                    line = line.Trim() + " GeoCity GeoCountry";
//                                    var fields = line.Split(' ');
//                                    var pos = 0;
//                                    _logger.Info(
//                                        $"Looking for/verifying '{_fluentCommandLineParser.Object.FieldName}' field position...");
//                                    foreach (var field in fields)
//                                    {
//                                        if (field.Equals(_fluentCommandLineParser.Object.FieldName,
//                                            StringComparison.OrdinalIgnoreCase))
//                                        {
//                                            dataSlot = pos - 1; //account for #Fields: 
//
//                                            _logger.Info(
//                                                $"Found '{_fluentCommandLineParser.Object.FieldName}' field position in column '{dataSlot}'!");
//                                            break;
//                                        }
//
//                                        pos += 1;
//                                    }
//                                }

                                var geoCity = "NA";
                                var geoCountry = "NA";

                                if (line.StartsWith("#") == false)
                                {
                                    var segs = line.Split(' ');

                                    dataSlot = segs.Length - 1;

                                    var ip = segs[dataSlot].Replace("\"","");

                                    if (ip.StartsWith("fe80"))
                                    {
                                        continue;
                                    }

                                    {
                                        try
                                        {
                                            var segs2 = ip.Split('.');
                                            if (segs2.Length > 1)
                                            {
                                                var first = int.Parse(segs2[0]);
                                                var second = int.Parse(segs2[1]);

                                                if (first >= 224 || first == 10 || first == 192 && second == 168 ||
                                                    first == 172 && second >= 16 && second <= 31)
                                                {
                                                    continue;
                                                }
                                            }

                                            var city = reader.City(ip);
                                            geoCity = city.City?.Name?.Replace(' ', '_');

                                            geoCountry = city.Country.Name.Replace(' ', '_');

                                            if (uniqueIps.ContainsKey(ip) == false)
                                            {
                                                uniqueIps.Add(ip, $"{geoCity}, {geoCountry}");
                                            }
                                        }
                                        catch (AddressNotFoundException an)
                                        {
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.Info($"Error: {ex.Message} for line: {line}");
                                            geoCity = $"City error: {ex.Message}";
                                            geoCountry = "Country error: (See city error)";
                                        }
                                    }

                                    line += $" {geoCity} {geoCountry}";
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
                _logger.Info("No unique, geolocated IPs found!\r\n");
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

        internal class ApplicationArguments
        {
            public string LogDirectory { get; set; }
            public string FieldName { get; set; }
        }
    }
}