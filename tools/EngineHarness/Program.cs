using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using VendorMatching.Code;

namespace VendorMatching.Harness
{
    /// <summary>
    /// Command-line front end for the matching engine.
    ///
    ///   dotnet run -- test  [--data &lt;folder&gt;]
    ///   dotnet run -- match --invoices &lt;csv&gt; --sap &lt;csv&gt; [--output &lt;folder&gt;] [--config &lt;json&gt;]
    ///                       [--auto-match &lt;score&gt;] [--review &lt;score&gt;]
    ///
    /// Exit codes: 0 success, 1 failed tests or a bad invocation, 2 an unhandled error.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return args.Length == 0 ? 1 : 0;
            }

            try
            {
                string command = args[0].ToLowerInvariant();
                Dictionary<string, string> options = ParseOptions(args, 1);

                switch (command)
                {
                    case "test":
                        return RunTests(options);
                    case "match":
                        return RunMatch(options);
                    default:
                        Console.Error.WriteLine("Unknown command '" + args[0] + "'.");
                        PrintUsage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 2;
            }
        }

        private static int RunTests(Dictionary<string, string> options)
        {
            string dataFolder = Get(options, "data", DefaultSampleDataFolder());
            Console.WriteLine("Running self-tests (sample data: " + dataFolder + ")");

            List<string> failures = SelfTests.RunAll(dataFolder);
            if (failures.Count == 0)
            {
                Console.WriteLine("All self-tests passed.");
                return 0;
            }

            Console.Error.WriteLine(failures.Count + " self-test(s) failed:");
            foreach (string failure in failures)
            {
                Console.Error.WriteLine("  - " + failure);
            }
            return 1;
        }

        private static int RunMatch(Dictionary<string, string> options)
        {
            string sampleFolder = DefaultSampleDataFolder();
            string invoices = Get(options, "invoices", Path.Combine(sampleFolder, "invoices.sample.csv"));
            string sapVendors = Get(options, "sap", Path.Combine(sampleFolder, "sap_vendors.sample.csv"));
            string output = Get(options, "output", Path.Combine(Directory.GetCurrentDirectory(), "output"));
            string config = Get(options, "config", null);

            if (!string.IsNullOrEmpty(config) && !File.Exists(config))
            {
                Console.WriteLine("Config file " + config + " not found; using the built-in defaults.");
                config = null;
            }

            RunSummary summary = MatchRunner.Run(new RunOptions
            {
                InvoicesPath = invoices,
                SapVendorsPath = sapVendors,
                OutputFolder = output,
                ConfigPath = config,
                AutoMatchThreshold = GetDouble(options, "auto-match"),
                ReviewThreshold = GetDouble(options, "review"),
                Log = Console.WriteLine
            });

            Console.WriteLine();
            Console.WriteLine("Invoices        : " + summary.InvoiceRows);
            Console.WriteLine("SAP vendors     : " + summary.SapVendorRows);
            Console.WriteLine("Auto-matched    : " + summary.AutoMatched);
            Console.WriteLine("Needs review    : " + summary.NeedsReview + " (ambiguous: " + summary.Ambiguous + ")");
            Console.WriteLine("No match        : " + summary.NoMatch);
            Console.WriteLine("By tax id       : " + summary.TaxIdMatches);
            Console.WriteLine("Exact / acronym : " + summary.ExactMatches + " / " + summary.AcronymMatches);
            Console.WriteLine("Fuzzy           : " + summary.FuzzyMatches);
            Console.WriteLine("Duration        : " + summary.DurationSeconds + "s");
            Console.WriteLine();
            Console.WriteLine("Results    : " + summary.ResultsCsvPath);
            Console.WriteLine("Exceptions : " + summary.ExceptionsCsvPath);
            Console.WriteLine("Summary    : " + summary.SummaryJsonPath);

            return 0;
        }

        /// <summary>Locates VendorMatching/Data by walking up from the working directory.</summary>
        private static string DefaultSampleDataFolder()
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "VendorMatching", "Data");
                if (Directory.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
            return Path.Combine(Directory.GetCurrentDirectory(), "Data");
        }

        private static Dictionary<string, string> ParseOptions(string[] args, int startIndex)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = startIndex; i < args.Length; i++)
            {
                string argument = args[i];
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Expected an option starting with '--' but found '" + argument + "'.");
                }

                string name = argument.Substring(2);
                string value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : "true";
                options[name] = value;
            }
            return options;
        }

        private static string Get(Dictionary<string, string> options, string name, string fallback)
        {
            string value;
            return options.TryGetValue(name, out value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
        }

        private static double GetDouble(Dictionary<string, string> options, string name)
        {
            string raw = Get(options, name, null);
            if (string.IsNullOrWhiteSpace(raw)) return 0.0;

            double value;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new ArgumentException("--" + name + " expects a number but got '" + raw + "'.");
            }
            return value;
        }

        private static bool IsHelp(string argument)
        {
            return argument == "-h" || argument == "--help" || argument == "help" || argument == "-?";
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Vendor matching engine harness");
            Console.WriteLine();
            Console.WriteLine("  test  [--data <folder>]");
            Console.WriteLine("        Runs the engine self-tests, including the end-to-end run over the sample data.");
            Console.WriteLine();
            Console.WriteLine("  match --invoices <csv> --sap <csv> [--output <folder>] [--config <json>]");
            Console.WriteLine("        [--auto-match <score>] [--review <score>]");
            Console.WriteLine("        Matches invoice vendor names against the SAP vendor master.");
        }
    }
}
