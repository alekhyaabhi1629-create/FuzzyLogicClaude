using System;
using System.IO;
using UiPath.CodedWorkflows;
using VendorMatching.Code;

namespace VendorMatching
{
    /// <summary>
    /// Process entry point: matches the vendor names on incoming invoices against the SAP vendor
    /// master and writes three artefacts to the output folder.
    ///
    ///   vendor-match-results.csv     one row per invoice, with the score breakdown behind it
    ///   vendor-match-exceptions.csv  the review and no-match rows, ready for Action Center
    ///   vendor-match-summary.json    counts and thresholds for the run
    ///
    /// Every argument is optional: leaving one empty falls back to the sample data shipped with
    /// the project, the project's Config/matching-config.json, and the configured thresholds.
    /// </summary>
    public class Main : CodedWorkflow
    {
        [Workflow]
        public string Execute(
            string invoicesCsvPath,
            string sapVendorsCsvPath,
            string outputFolder,
            string configPath,
            double autoMatchThreshold,
            double reviewThreshold)
        {
            string projectFolder = Directory.GetCurrentDirectory();

            string invoices = Resolve(projectFolder, invoicesCsvPath, Path.Combine("Data", "invoices.sample.csv"));
            string vendors = Resolve(projectFolder, sapVendorsCsvPath, Path.Combine("Data", "sap_vendors.sample.csv"));
            string config = Resolve(projectFolder, configPath, Path.Combine("Config", "matching-config.json"));
            string output = Resolve(projectFolder, outputFolder, "Output");

            if (!File.Exists(config))
            {
                // A missing config file is not an error - the engine has usable defaults.
                Log("No matching config found at " + config + "; using the built-in defaults.");
                config = null;
            }

            Log("Matching invoices in " + invoices + " against the SAP vendor master in " + vendors + ".");

            // Wrapped in a lambda rather than passed as a method group: CodedWorkflow.Log carries
            // an optional log-level parameter, and C# will not convert such a method to Action<string>.
            Action<string> log = delegate(string message) { Log(message); };

            RunSummary summary = MatchRunner.Run(new RunOptions
            {
                InvoicesPath = invoices,
                SapVendorsPath = vendors,
                OutputFolder = output,
                ConfigPath = config,
                AutoMatchThreshold = autoMatchThreshold,
                ReviewThreshold = reviewThreshold,
                Log = log
            });

            Log("Results written to " + summary.ResultsCsvPath);
            Log("Exceptions for review written to " + summary.ExceptionsCsvPath);

            if (summary.NeedsReview > 0 || summary.NoMatch > 0)
            {
                Log(summary.NeedsReview + " invoice(s) need review and " + summary.NoMatch
                    + " could not be matched. Route " + summary.ExceptionsCsvPath + " to Action Center.");
            }

            return File.ReadAllText(summary.SummaryJsonPath);
        }

        /// <summary>
        /// Turns an argument into an absolute path. Empty arguments fall back to the shipped
        /// default, and relative paths are resolved against the project folder so the process
        /// behaves the same in Studio and on a robot.
        /// </summary>
        private static string Resolve(string projectFolder, string value, string fallbackRelativePath)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? fallbackRelativePath : value.Trim();
            if (Path.IsPathRooted(candidate)) return candidate;
            return Path.GetFullPath(Path.Combine(projectFolder, candidate));
        }
    }
}
