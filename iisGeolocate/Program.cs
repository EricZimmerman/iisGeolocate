using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using Exceptionless;
using Serilog;

namespace iisGeolocate;

public class Program
{
    public const string BadRowsFileName = "BadDataRows_REVIEW_ME.txt";
    public const string UniqueIpsFileName = "!UniqueIPs.csv";

    private static readonly string[] DatabaseNames = { "GeoIP2-City.mmdb", "GeoLite2-City.mmdb" };

    private static readonly string Header =
        $"iisgeolocate version {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)}" +
        "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
        "\r\nhttps://github.com/EricZimmerman/iisGeolocate";

    private static readonly string Footer =
        @"Examples: iisGeolocate.exe -d ""C:\inetpub\logs\LogFiles"" --csv ""C:\temp\out""" + "\r\n\t " +
        @"   iisGeolocate.exe -d ""C:\temp\logs"" --csv ""C:\temp\out"" --db ""C:\GeoIP\GeoLite2-City.mmdb""" + "\r\n\t" +
        "\r\n\t" +
        "    Without --db, GeoIP2-City.mmdb or GeoLite2-City.mmdb must be next to iisGeolocate\r\n";

    private static RootCommand _rootCommand;

    private static async Task<int> Main(string[] args)
    {
        ExceptionlessClient.Default.Startup("ujUuuNlhz7ZQKoDxBohBMKmPxErDgbFmNdYvPRHM");

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: "{Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var dOpt = new Option<string>("-d")
        {
            Description = "Directory to recursively process for IIS logs. This will be searched for *.log files"
        };

        var csvOpt = new Option<string>("--csv")
        {
            Description = "Directory to save CSV formatted results to. Be sure to include the full path in double quotes"
        };

        var dbOpt = new Option<string>("--db")
        {
            Description =
                "MaxMind database to use (City, Enterprise or Country). Default is GeoIP2-City.mmdb, else GeoLite2-City.mmdb, next to iisGeolocate"
        };

        var sblOpt = new Option<bool>("--sbl")
        {
            Description = "When true, do NOT show bad lines to console (they are still logged to a file). Default is false",
            DefaultValueFactory = _ => false
        };

        var nulOpt = new Option<bool>("--nul")
        {
            Description = "When true, do NOT create updated CSV files in --csv directory. Default is false",
            DefaultValueFactory = _ => false
        };

        _rootCommand = new RootCommand
        {
            dOpt,
            csvOpt,
            dbOpt,
            sblOpt,
            nulOpt
        };

        _rootCommand.Description = Header + "\r\n\r\n" + Footer;

        _rootCommand.SetAction(result => DoWork(result.GetValue(dOpt), result.GetValue(csvOpt), result.GetValue(dbOpt),
            result.GetValue(sblOpt), result.GetValue(nulOpt)));

        var exitCode = await _rootCommand.Parse(args).InvokeAsync();

        Log.CloseAndFlush();

        return exitCode;
    }

    /// <returns>
    ///     0 on success. 1 when arguments are invalid, the database can't be used, a log can't be read or an output
    ///     file can't be written
    /// </returns>
    private static int DoWork(string d, string csv, string db, bool sbl, bool nul)
    {
        if (string.IsNullOrEmpty(d) || string.IsNullOrEmpty(csv))
        {
            var helpAction = new CustomHelpAction(new HelpAction());
            helpAction.Invoke(_rootCommand.Parse(""));

            Log.Warning("Both -d and --csv are required. Exiting");
            Console.WriteLine();
            return 1;
        }

        Log.Information("{Header}", Header);
        Console.WriteLine();
        Log.Information("Command line: {Args}", string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
        Console.WriteLine();

        d = Path.GetFullPath(d);
        csv = Path.GetFullPath(csv);

        if (Directory.Exists(d) == false)
        {
            Log.Warning("{D} does not exist. Exiting", d);
            Console.WriteLine();
            return 1;
        }

        if (string.IsNullOrEmpty(db))
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            db = DatabaseNames.Select(t => Path.Combine(baseDir, t)).FirstOrDefault(File.Exists);

            if (db == null)
            {
                Log.Fatal("{CityIp} or {CityLite} missing from {Dir}! Put one there or use --db. Exiting",
                    DatabaseNames[0], DatabaseNames[1], baseDir);
                Console.WriteLine();
                return 1;
            }
        }
        else if (File.Exists(db) == false)
        {
            Log.Fatal("Database {Db} not found. Exiting", db);
            Console.WriteLine();
            return 1;
        }

        GeoLookup geo;
        try
        {
            geo = new GeoLookup(db);
        }
        catch (Exception ex)
        {
            Log.Fatal("Could not use database {Db}: {Message}. Exiting", db, ex.Message);
            Console.WriteLine();
            return 1;
        }

        using (geo)
        {
            Log.Information("Using {Db} ({Type}, built {Built:yyyy-MM-dd})", db, geo.DatabaseType, geo.BuildDate);
            Console.WriteLine();

            return ProcessLogs(d, csv, sbl, nul, geo);
        }
    }

    private static int ProcessLogs(string d, string csv, bool sbl, bool nul, GeoLookup geo)
    {
        var logFiles = FindLogs(d);

        if (logFiles.Count == 0)
        {
            Log.Warning("No files ending in {Log} found in {D}. Exiting", ".log", d);
            Console.WriteLine();
            return 0;
        }

        Log.Information("Found {Count:N0} log files", logFiles.Count);

        var badDataFile = Path.Combine(csv, BadRowsFileName);
        CsvWriter badCsv;

        try
        {
            Directory.CreateDirectory(csv);

            badCsv = new CsvWriter(new StreamWriter(badDataFile, false, new UTF8Encoding(false)),
                new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "\t" });
            badCsv.WriteHeader<BadRow>();
            badCsv.NextRecord();
        }
        catch (Exception ex)
        {
            Log.Fatal("Could not create {BadDataFile}: {Message}. Exiting", badDataFile, ex.Message);
            return 1;
        }

        Log.Information(
            "NOTE: private, loopback, link local, multicast and reserved addresses are not looked up (GeoCity and GeoCountry are NA)");
        Console.WriteLine();
        Log.Information("All malformed data rows will be IGNORED but written to {BadDataFile}. REVIEW THIS!", badDataFile);
        Console.WriteLine();

        var failed = new List<string>();
        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var sw = Stopwatch.StartNew();
        long totalRows = 0;
        long totalBad = 0;

        using (badCsv)
        {
            foreach (var file in logFiles)
            {
                Log.Information("Opening {File}", file);

                try
                {
                    var result = LogProcessor.Process(file, nul ? null : csv, ts, geo, bad =>
                    {
                        badCsv.WriteRecord(bad);
                        badCsv.NextRecord();

                        if (sbl == false)
                        {
                            Log.Warning("\tBad data found ({Reason}, line {Line:N0})! Ignoring!!! Row: '{Bad}'",
                                bad.Reason, bad.LineNumber, bad.Row.Trim());
                        }
                    });

                    badCsv.Flush();

                    if (result.Skipped != null)
                    {
                        Log.Information("\t{File} {Reason}. Skipping...", file, result.Skipped);
                        continue;
                    }

                    foreach (var chunk in result.Chunks)
                    {
                        Log.Information("\tChunk {Counter:N0}: {Rows:N0} rows{Out}", chunk.Number, chunk.Rows,
                            chunk.CsvFile == null ? string.Empty : $", saved to {Path.GetFileName(chunk.CsvFile)}");

                        if (chunk.HasClientIp == false)
                        {
                            Log.Warning("\tChunk {Counter:N0} has no {Field} field, so there is nothing to geolocate",
                                chunk.Number, LogProcessor.ClientIpField);
                        }
                    }

                    if (result.BadRows > 0)
                    {
                        Log.Warning("\tBad rows: {Bad:N0}", result.BadRows);
                    }

                    totalRows += result.Rows;
                    totalBad += result.BadRows;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(file);
                    Log.Error("\tError processing {File}: {Message}", file, ex.Message);
                }
                catch (Exception ex)
                {
                    failed.Add(file);
                    Log.Error(ex, "\tError processing {File}: {Message}", file, ex.Message);
                }
            }
        }

        sw.Stop();
        Console.WriteLine();

        Log.Information("Processed {Count:N0} files in {Elapsed:N4} seconds. Rows: {Rows:N0}, bad rows: {Bad:N0}",
            logFiles.Count - failed.Count, sw.Elapsed.TotalSeconds, totalRows, totalBad);

        Log.Information(
            "Distinct addresses: geolocated {Found:N0}, not in the database {NotFound:N0}, not public {NonPublic:N0}, not an IP address {Invalid:N0}",
            geo.UniqueIps.Count, geo.NotFoundAddresses, geo.NonPublicAddresses, geo.InvalidAddresses);

        foreach (var error in geo.LookupErrors.Take(10))
        {
            Log.Error("Lookup error for {Error}", error);
        }

        if (failed.Count > 0)
        {
            Console.WriteLine();
            Log.Error("Failed files: {Count:N0}", failed.Count);
            foreach (var file in failed)
            {
                Log.Error("\t{File}", file);
            }
        }

        Console.WriteLine();

        var exitCode = failed.Count > 0 ? 1 : 0;

        if (geo.UniqueIps.Count == 0)
        {
            Log.Information("No unique, geolocated IPs found!");
            Console.WriteLine();
            return exitCode;
        }

        var uniqueFile = Path.Combine(csv, UniqueIpsFileName);
        Log.Information("Saving unique IPs to {File}", uniqueFile);

        try
        {
            using var uniqOut = new StreamWriter(uniqueFile, false, new UTF8Encoding(false));
            using var csw = new CsvWriter(uniqOut, CultureInfo.InvariantCulture);
            csw.WriteRecords(geo.UniqueIps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not write {File}: {Message}", uniqueFile, ex.Message);
            exitCode = 1;
        }

        Console.WriteLine();

        return exitCode;
    }

    /// <summary>*.log files under the directory (any case), sorted, skipping folders that can't be read</summary>
    private static List<string> FindLogs(string dir)
    {
#if NET
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = 0
        };
        var files = Directory.EnumerateFiles(dir, "*.log", options).ToList();
#else
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(dir);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                //on Windows "*.log" also matches longer extensions through 8.3 names, so check it
                files.AddRange(Directory.GetFiles(current, "*.log")
                    .Where(t => string.Equals(Path.GetExtension(t), ".log", StringComparison.OrdinalIgnoreCase)));

                foreach (var sub in Directory.GetDirectories(current))
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(sub);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning("Could not read {Dir}: {Message}", current, ex.Message);
            }
        }
#endif
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private class CustomHelpAction : SynchronousCommandLineAction
    {
        private readonly HelpAction _defaultHelp;

        public CustomHelpAction(HelpAction action)
        {
            _defaultHelp = action;
        }

        public override int Invoke(ParseResult parseResult)
        {
            return _defaultHelp.Invoke(parseResult);
        }
    }
}
