using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace VendorMatching.Code
{
    /// <summary>Inputs for one end-to-end matching run.</summary>
    public sealed class RunOptions
    {
        public string InvoicesPath { get; set; }
        public string SapVendorsPath { get; set; }
        public string OutputFolder { get; set; }
        public string ConfigPath { get; set; }

        /// <summary>Overrides the configured auto-match threshold when greater than zero.</summary>
        public double AutoMatchThreshold { get; set; }

        /// <summary>Overrides the configured review threshold when greater than zero.</summary>
        public double ReviewThreshold { get; set; }

        /// <summary>Optional sink for progress messages, wired to the UiPath log in the workflow.</summary>
        public Action<string> Log { get; set; }
    }

    /// <summary>
    /// End-to-end orchestration: read both files, match, write the three output artefacts.
    /// Contains no UiPath types so the same code runs from Studio, the CLI harness and tests.
    /// </summary>
    public static class MatchRunner
    {
        public const string ResultsFileName = "vendor-match-results.csv";
        public const string ExceptionsFileName = "vendor-match-exceptions.csv";
        public const string SummaryFileName = "vendor-match-summary.json";

        private static readonly string[] ResultHeader =
        {
            "InvoiceId", "InvoiceRow", "InvoiceVendorName", "NormalizedInvoiceName",
            "Decision", "MatchType", "Score", "Ambiguous",
            "SapVendorCode", "SapVendorName", "SapCountry", "SapCity",
            "ScoreLevenshtein", "ScoreJaroWinkler", "ScoreTokenSort", "ScoreTokenSet", "ScorePartial",
            "Bonus", "Penalty", "CandidatesEvaluated",
            "RunnerUp1Code", "RunnerUp1Name", "RunnerUp1Score",
            "RunnerUp2Code", "RunnerUp2Name", "RunnerUp2Score",
            "Reason"
        };

        public static RunSummary Run(RunOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (string.IsNullOrWhiteSpace(options.InvoicesPath)) throw new ArgumentException("InvoicesPath is required.", "options");
            if (string.IsNullOrWhiteSpace(options.SapVendorsPath)) throw new ArgumentException("SapVendorsPath is required.", "options");

            Action<string> log = options.Log ?? delegate { };
            var stopwatch = Stopwatch.StartNew();
            DateTime startedUtc = DateTime.UtcNow;

            MatchConfig config = MatchConfig.Load(options.ConfigPath);
            if (options.AutoMatchThreshold > 0.0) config.Thresholds.AutoMatch = options.AutoMatchThreshold;
            if (options.ReviewThreshold > 0.0) config.Thresholds.Review = options.ReviewThreshold;
            config.Validate();

            string outputFolder = string.IsNullOrWhiteSpace(options.OutputFolder)
                ? Path.GetDirectoryName(Path.GetFullPath(options.InvoicesPath))
                : options.OutputFolder;
            if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

            List<SapVendor> vendors = LoadSapVendors(options.SapVendorsPath, config);
            log("Loaded " + vendors.Count + " SAP vendor records from " + options.SapVendorsPath);

            List<InvoiceRecord> invoices = LoadInvoices(options.InvoicesPath, config);
            log("Loaded " + invoices.Count + " invoice records from " + options.InvoicesPath);

            var engine = new VendorMatchEngine(vendors, config);
            log("Built blocking index over " + engine.VendorCount + " active vendors.");

            List<MatchResult> results = engine.MatchAll(invoices);

            var summary = new RunSummary
            {
                StartedUtc = startedUtc.ToString("o", CultureInfo.InvariantCulture),
                InvoiceRows = invoices.Count,
                SapVendorRows = vendors.Count,
                AutoMatchThreshold = config.Thresholds.AutoMatch,
                ReviewThreshold = config.Thresholds.Review,
                ResultsCsvPath = Path.Combine(outputFolder, ResultsFileName),
                ExceptionsCsvPath = Path.Combine(outputFolder, ExceptionsFileName),
                SummaryJsonPath = Path.Combine(outputFolder, SummaryFileName)
            };

            var resultRows = new List<string[]> { ResultHeader };
            var exceptionRows = new List<string[]> { ResultHeader };

            foreach (MatchResult result in results)
            {
                string[] row = ToRow(result);
                resultRows.Add(row);

                switch (result.Decision)
                {
                    case MatchDecision.AutoMatch:
                        summary.AutoMatched++;
                        break;
                    case MatchDecision.NeedsReview:
                        summary.NeedsReview++;
                        exceptionRows.Add(row);
                        break;
                    default:
                        summary.NoMatch++;
                        exceptionRows.Add(row);
                        break;
                }

                if (result.IsAmbiguous) summary.Ambiguous++;

                if (result.Best != null && result.Decision != MatchDecision.NoMatch)
                {
                    switch (result.Best.Type)
                    {
                        case MatchType.TaxId: summary.TaxIdMatches++; break;
                        case MatchType.ExactNormalized: summary.ExactMatches++; break;
                        case MatchType.Acronym: summary.AcronymMatches++; break;
                        default: summary.FuzzyMatches++; break;
                    }
                }
            }

            Csv.Write(summary.ResultsCsvPath, resultRows);
            Csv.Write(summary.ExceptionsCsvPath, exceptionRows);

            stopwatch.Stop();
            summary.DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3);

            WriteSummaryJson(summary);

            log("Auto-matched " + summary.AutoMatched + ", needs review " + summary.NeedsReview
                + ", no match " + summary.NoMatch + " in " + summary.DurationSeconds + "s.");

            return summary;
        }

        public static void WriteSummaryJson(RunSummary summary)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(summary.SummaryJsonPath, JsonSerializer.Serialize(summary, options));
        }

        public static List<SapVendor> LoadSapVendors(string path, MatchConfig config)
        {
            CsvTable table = Csv.Read(path);
            var vendors = new List<SapVendor>(table.Rows.Count);

            int codeIndex = ResolveColumn(table, config.SapColumns, "code", path, true);
            int nameIndex = ResolveColumn(table, config.SapColumns, "name", path, true);
            int name2Index = ResolveColumn(table, config.SapColumns, "name2", path, false);
            int taxIndex = ResolveColumn(table, config.SapColumns, "taxId", path, false);
            int vatIndex = ResolveColumn(table, config.SapColumns, "vatId", path, false);
            int countryIndex = ResolveColumn(table, config.SapColumns, "country", path, false);
            int cityIndex = ResolveColumn(table, config.SapColumns, "city", path, false);
            int blockedIndex = ResolveColumn(table, config.SapColumns, "blocked", path, false);

            for (int i = 0; i < table.Rows.Count; i++)
            {
                string[] row = table.Rows[i];
                var vendor = new SapVendor
                {
                    RowNumber = i + 2,
                    VendorCode = CsvTable.Value(row, codeIndex).Trim(),
                    Name = CsvTable.Value(row, nameIndex).Trim(),
                    Name2 = CsvTable.Value(row, name2Index).Trim(),
                    TaxId = CsvTable.Value(row, taxIndex).Trim(),
                    VatId = CsvTable.Value(row, vatIndex).Trim(),
                    Country = CsvTable.Value(row, countryIndex).Trim(),
                    City = CsvTable.Value(row, cityIndex).Trim(),
                    IsBlocked = IsTruthy(CsvTable.Value(row, blockedIndex))
                };

                if (string.IsNullOrWhiteSpace(vendor.Name) && string.IsNullOrWhiteSpace(vendor.Name2)) continue;

                vendor.Normalized = TextNormalizer.Normalize(vendor.DisplayName, config);
                vendor.NormalizedPrimary = TextNormalizer.Normalize(vendor.Name, config);
                vendors.Add(vendor);
            }

            return vendors;
        }

        public static List<InvoiceRecord> LoadInvoices(string path, MatchConfig config)
        {
            CsvTable table = Csv.Read(path);
            var invoices = new List<InvoiceRecord>(table.Rows.Count);

            int idIndex = ResolveColumn(table, config.InvoiceColumns, "id", path, false);
            int nameIndex = ResolveColumn(table, config.InvoiceColumns, "vendorName", path, true);
            int taxIndex = ResolveColumn(table, config.InvoiceColumns, "taxId", path, false);
            int vatIndex = ResolveColumn(table, config.InvoiceColumns, "vatId", path, false);
            int countryIndex = ResolveColumn(table, config.InvoiceColumns, "country", path, false);
            int cityIndex = ResolveColumn(table, config.InvoiceColumns, "city", path, false);

            for (int i = 0; i < table.Rows.Count; i++)
            {
                string[] row = table.Rows[i];
                int rowNumber = i + 2;

                string id = CsvTable.Value(row, idIndex).Trim();
                if (id.Length == 0) id = "ROW-" + rowNumber.ToString(CultureInfo.InvariantCulture);

                var invoice = new InvoiceRecord
                {
                    RowNumber = rowNumber,
                    InvoiceId = id,
                    VendorName = CsvTable.Value(row, nameIndex).Trim(),
                    TaxId = CsvTable.Value(row, taxIndex).Trim(),
                    VatId = CsvTable.Value(row, vatIndex).Trim(),
                    Country = CsvTable.Value(row, countryIndex).Trim(),
                    City = CsvTable.Value(row, cityIndex).Trim()
                };

                invoice.Normalized = TextNormalizer.Normalize(invoice.VendorName, config);
                invoices.Add(invoice);
            }

            return invoices;
        }

        private static int ResolveColumn(CsvTable table, Dictionary<string, string[]> columnMap, string field, string path, bool required)
        {
            string[] candidates;
            if (columnMap == null || !columnMap.TryGetValue(field, out candidates)) candidates = new[] { field };

            int index = table.IndexOfAny(candidates);
            if (index < 0 && required)
            {
                throw new InvalidDataException(
                    "Column '" + field + "' was not found in " + path + ". Looked for: "
                    + string.Join(", ", candidates) + ". Found headers: " + string.Join(", ", table.Header) + ".");
            }
            return index;
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string trimmed = value.Trim();
            return string.Equals(trimmed, "X", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "Y", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "YES", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "TRUE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "1", StringComparison.Ordinal);
        }

        private static string[] ToRow(MatchResult result)
        {
            InvoiceRecord invoice = result.Invoice;
            MatchCandidate best = result.Best;
            ScoreBreakdown breakdown = best != null ? best.Breakdown : new ScoreBreakdown();

            MatchCandidate runnerUp1 = result.Alternatives.Count > 0 ? result.Alternatives[0] : null;
            MatchCandidate runnerUp2 = result.Alternatives.Count > 1 ? result.Alternatives[1] : null;

            return new[]
            {
                invoice.InvoiceId,
                invoice.RowNumber.ToString(CultureInfo.InvariantCulture),
                invoice.VendorName,
                invoice.Normalized != null ? invoice.Normalized.Core : string.Empty,
                result.Decision.ToString(),
                best != null ? best.Type.ToString() : MatchType.None.ToString(),
                Number(best != null ? best.Score : 0.0),
                result.IsAmbiguous ? "TRUE" : "FALSE",
                best != null ? best.Vendor.VendorCode : string.Empty,
                best != null ? best.Vendor.DisplayName : string.Empty,
                best != null ? best.Vendor.Country : string.Empty,
                best != null ? best.Vendor.City : string.Empty,
                Number(breakdown.Levenshtein),
                Number(breakdown.JaroWinkler),
                Number(breakdown.TokenSort),
                Number(breakdown.TokenSet),
                Number(breakdown.Partial),
                Number(breakdown.Bonus),
                Number(breakdown.Penalty),
                result.CandidatesEvaluated.ToString(CultureInfo.InvariantCulture),
                runnerUp1 != null ? runnerUp1.Vendor.VendorCode : string.Empty,
                runnerUp1 != null ? runnerUp1.Vendor.DisplayName : string.Empty,
                runnerUp1 != null ? Number(runnerUp1.Score) : string.Empty,
                runnerUp2 != null ? runnerUp2.Vendor.VendorCode : string.Empty,
                runnerUp2 != null ? runnerUp2.Vendor.DisplayName : string.Empty,
                runnerUp2 != null ? Number(runnerUp2.Score) : string.Empty,
                result.Reason ?? string.Empty
            };
        }

        private static string Number(double value)
        {
            return Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
