using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.NamingConventionBinder;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using Exceptionless;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Serilog;


namespace iisGeolocate;

internal class Program
{
    private static Dictionary<string, UniqueIp> _uniqueIps;

    private static readonly string Header =
        $"iisgeolocate version {Assembly.GetExecutingAssembly().GetName().Version}" +
        "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
        "\r\nhttps://github.com/EricZimmerman/iisGeolocate";

    private static RootCommand _rootCommand;

    private static async Task Main(string[] args)
    {
        ExceptionlessClient.Default.Startup("ujUuuNlhz7ZQKoDxBohBMKmPxErDgbFmNdYvPRHM");

        _rootCommand = new RootCommand
        {
            new Option<string>(
                "-d",
                "The directory that contains IIS logs. This will be recursively searched for *.log files"),

            new Option<string>(
                "--csv",
                "The directory to write results to"),

            new Option<bool>(
                "--sbl",
                () => false,
                "When true, do NOT show bad lines to console (they are still logged to a file)"),

            new Option<bool>(
                "--nul",
                () => false,
                "When true, do NOT create updated CSV files in --csv directory")
        };

        _rootCommand.Options.Single(t=>t.Name == "d").IsRequired = true;
        _rootCommand.Options.Single(t=>t.Name == "csv").IsRequired = true;
        
        _rootCommand.Description = Header;

        _rootCommand.Handler = CommandHandler.Create(DoWork);

        await _rootCommand.InvokeAsync(args);
        
        Log.CloseAndFlush();
    }

    private static void DoWork(string d, string csv, bool sbl, bool nul)
    {

        var template = "{Message:lj}{NewLine}{Exception}";

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: template)
            .CreateLogger();


        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        if (string.IsNullOrEmpty(d) || string.IsNullOrEmpty(csv))
        {
            var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
            var hc = new HelpContext(helpBld, _rootCommand, Console.Out);

            helpBld.Write(hc);

            Log.Warning("Both -d and --csv are required. Exiting");
            Console.WriteLine();
            return;
        }

        _uniqueIps = new Dictionary<string, UniqueIp>();

        Log.Information("{Header}",Header);
        Console.WriteLine();

        d = Path.GetFullPath(d);
        csv = Path.GetFullPath(csv);

        if (Directory.Exists(d) == false)
        {
            Log.Warning("{D} does not exist. Exiting",d);
            Console.WriteLine();
            return;
        }

        var litePath = Path.Combine(baseDir, "GeoLite2-City.mmdb");
        var cityPath = Path.Combine(baseDir, "GeoIP2-City.mmdb");

        if (File.Exists(litePath) == false && File.Exists(cityPath) == false)
        {
            Log.Fatal("{CityLite} or {CityIp} missing! Cannot continue. Exiting","GeoLite2-City.mmdb","GeoIP2-City.mmdb");
            Console.WriteLine();
            return;
        }

        var dbName = litePath;

        if (File.Exists(cityPath))
        {
            Log.Information("Found {Db}, so using that vs lite...","GeoIP2-City.mmdb");
            dbName = cityPath;
        }

        var logFiles = Directory.GetFiles(d, "*.log", SearchOption.AllDirectories);

        if (logFiles.Length > 0)
        {
            Log.Information("Found {Count:N0} log files",logFiles.Length);
        }
        else
        {
            Log.Fatal("No files ending in {Log} found. Exiting...",".log");
            Console.WriteLine();
            return;
        }

        if (Directory.Exists(csv) == false)
        {
            Directory.CreateDirectory(csv);
        }

        Log.Information("NOTE: multicast, private, or reserved addresses will be SKIPPED (including IPv6 that starts with {Mask}","fe80");

        var badDataFile = Path.Combine(csv, "BadDataRows_REVIEW_ME.txt");
        var badStream = new StreamWriter(badDataFile);

        Console.WriteLine();
        Log.Information("All malformed data rows will be IGNORED but written to {BadDataFile}. REVIEW THIS!",badDataFile);
        Console.WriteLine();
        
        var ipinfo = new Dictionary<string, GeoResults>();

        using (var reader = new DatabaseReader(dbName))
        {
            foreach (var file in logFiles)
            {
                Log.Information("Opening {File}",file);

                var fileChunks = new Dictionary<string, List<string>>();

                using var inStream = File.OpenText(file);
                if (inStream.BaseStream.Length == 0)
                {
                    Log.Information("\t{File} is empty. Skipping...",file);
                    inStream.Close();
                    continue;
                }

                var line = inStream.ReadLine();

                if (line.StartsWith("#") == false)
                {
                    Log.Information("\tThe first line in {File} does not start with a #! Is this an IIS log? Skipping...",file);
                    inStream.Close();
                    continue;
                }

                if (line.StartsWith("#Software: Microsoft Exchange"))
                {
                    Log.Information("\tSkipping {File}! Does not appear to be an IIS related file. Skipping...",file);
                    inStream.Close();
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
                                line = inStream.ReadLine();
                                continue;
                            }

                            //new data based on header

                            lastHeaderRow = headerRow;

                            fileChunks.Add(headerRow, new List<string>());

                            headerRow = $"{headerRow} GeoCity GeoCountry";

                            fileChunks[lastHeaderRow].Add(headerRow);

                            line = inStream.ReadLine();
                            continue;
                        }

                        line = inStream.ReadLine();
                        continue;
                    }

                    //this is where data needs to be persisted for later
                    fileChunks[lastHeaderRow].Add(line);

                    line = inStream.ReadLine();
                }

                //at this point, iterate all fileChunks and make it a csv, do lookup, update extra fields, write it out

                var ts = DateTimeOffset.UtcNow;
                var counter = 0;

                Log.Information("\tLog chunks found in {File}: {Count:N0}. Processing chunks...",file,fileChunks.Count);
                
                foreach (var fileChunk in fileChunks)
                {
                    counter += 1;

                    Log.Information("\tFound {Count:N0} rows in chunk {Counter:N0}",fileChunk.Value.Count,counter);

                    //outcsv stuff

                    var logBaseName = Path.GetFileNameWithoutExtension(file);

                    var fout = Path.Combine(csv, $"{ts:yyyyMMddHHmmss}_{logBaseName}_Chunk{counter}.csv");

                    CsvWriter csvOut = null;

                    if (nul == false)
                    {
                        csvOut = new CsvWriter(new StreamWriter(fout), CultureInfo.CurrentCulture);
                    }

                    //outcsv stuff end

                    var conf = new CsvConfiguration(CultureInfo.CurrentCulture);
                    //hack so the idiotic iis logs can be processed
                    conf.WhiteSpaceChars[0] = '|';
                    conf.Delimiter = " ";
                    
                    conf.BadDataFound = rawData =>
                    {
                        badStream.Write(rawData.RawRecord);
                        if (sbl)
                        {
                            return;
                        }

                        Log.Warning("Bad data found! Ignoring!!! Row: '{Bad}'",rawData.RawRecord.Trim());
                    };

                    //write out lines to temp file to avoid out of memory error
                    var tmp = Path.Combine(baseDir, "tmp.txt");
                    File.WriteAllLines(tmp, fileChunk.Value);

                    using (var sw = new StreamReader(tmp))
                    {
                        var csvReader = new CsvReader(sw, conf);

                        csvReader.Read();
                        csvReader.ReadHeader();

                        while (csvReader.Read())
                        {
                            var record = csvReader.GetRecord<dynamic>();

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
                                    ipinfo.Add(ip, gr);
                                }

                                record.GeoCity = ipinfo[ip].City;
                                record.GeoCountry = ipinfo[ip].Country;
                            }

                            csvOut?.WriteRecord(record);
                            csvOut?.NextRecord();

                            if (csvOut?.Row % 10_000 == 0)
                            {
                                csvOut.Flush();
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

        Console.WriteLine();

        if (_uniqueIps.Count <= 0)
        {
            Log.Information("No unique, geolocated IPs found!");
            Console.WriteLine();
            return;
        }

        Log.Information("Saving unique IPs to {File}","!UniqueIPs.csv");

        using (var uniqOut = new StreamWriter(File.OpenWrite(Path.Combine(csv, "!UniqueIPs.csv"))))
        {
            var csw = new CsvWriter(uniqOut, CultureInfo.CurrentCulture);
            csw.WriteHeader<UniqueIp>();
            csw.NextRecord();
            csw.WriteRecords(_uniqueIps.Values);
            uniqOut.Flush();
        }

        Console.WriteLine();
    }

    private static GeoResults GetIpInfo(string ip, DatabaseReader reader)
    {
        var gr = new GeoResults();
        gr.City = "NA";
        gr.Country = "NA";

        try
        {
            var city = reader.City(ip);
            gr.City = city.City.Name?.Replace(' ', '_');
            gr.Country = city.Country.Name?.Replace(' ', '_');


            if (_uniqueIps.ContainsKey(ip) == false)
            {
                var ui = new UniqueIp { City = city.City.Name };
                ui.Country = city.Country.Name;
                ui.IpAddress = ip;

                _uniqueIps.Add(ip, ui);
            }
        }

        catch (AddressNotFoundException)
        {
            //eat it
        }
        catch (Exception ex)
        {
            Log.Error(ex,"Error {Message} for ip: {Ip}",ex.Message,ip);
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