using System;
using System.Collections.Generic;
using System.IO;

namespace VendorMatching.Code
{
    /// <summary>
    /// Behavioural tests for the matching engine, written without a test framework so they can be
    /// executed from a UiPath workflow, from the command-line harness, or from CI on any platform.
    /// Every check returns a message instead of throwing, so one run reports every failure.
    /// </summary>
    public static class SelfTests
    {
        /// <summary>
        /// Runs every test. Pass the folder holding the sample CSV files to include the
        /// end-to-end checks; pass null or an empty string to run the unit checks only.
        /// </summary>
        public static List<string> RunAll(string sampleDataFolder)
        {
            var failures = new List<string>();

            NormalizationTests(failures);
            SimilarityTests(failures);
            EngineTests(failures);
            ConfigTests(failures);
            CsvTests(failures);

            if (!string.IsNullOrWhiteSpace(sampleDataFolder))
            {
                EndToEndTests(sampleDataFolder, failures);
            }

            return failures;
        }

        // ---- normalization -------------------------------------------------------------------

        private static void NormalizationTests(List<string> failures)
        {
            MatchConfig config = MatchConfig.CreateDefault();

            AreEqual(failures, "legal form and punctuation are removed",
                "ACME EUROPE DISTRIBUTION", TextNormalizer.Normalize("Acme Europe Distribution B.V.", config).Core);

            AreEqual(failures, "umlauts transliterate the way SAP and OCR write them",
                TextNormalizer.Normalize("MUELLER PRAEZISIONSTECHNIK GMBH", config).Core,
                TextNormalizer.Normalize("Müller Präzisionstechnik GmbH", config).Core);

            AreEqual(failures, "abbreviations expand through the alias table",
                "CONTOSO PHARMACEUTICALS", TextNormalizer.Normalize("Contoso Pharma AG", config).Core);

            AreEqual(failures, "ampersand and its translations are dropped as noise",
                "NORDWIND LOGISTIK", TextNormalizer.Normalize("Nordwind Logistik GmbH & Co. KG", config).Core);

            AreEqual(failures, "'und' spelled out behaves like '&'",
                TextNormalizer.Normalize("Nordwind Logistik GmbH & Co. KG", config).Core,
                TextNormalizer.Normalize("Nordwind Logistik GmbH und Co KG", config).Core);

            AreEqual(failures, "dotted initials collapse and stray letters are dropped",
                "KOWALSKI SYNOWIE", TextNormalizer.Normalize("Kowalski i Synowie Sp. z o.o.", config).Core);

            AreEqual(failures, "dotted initials stay joined",
                "JP MORGAN", TextNormalizer.Normalize("J.P. Morgan", config).Core);

            AreEqual(failures, "a name of only legal forms keeps its tokens rather than emptying",
                "GMBH", TextNormalizer.Normalize("GmbH", config).Core);

            AreEqual(failures, "short names keep their initials",
                "A E NETWORKS", TextNormalizer.Normalize("A & E Networks", config).Core);

            AreEqual(failures, "acronyms are derived from the core tokens",
                "IBM", TextNormalizer.Normalize("International Business Machines Corporation", config).Acronym);

            AreEqual(failures, "digits are extracted for the mismatch penalty",
                "3", string.Join(",", TextNormalizer.Normalize("Sigma 3 Laboratories Inc.", config).Digits));

            AreEqual(failures, "identifiers reduce to alphanumerics",
                "DE811234567", TextNormalizer.NormalizeIdentifier(" de 811-234.567 "));

            IsTrue(failures, "a blank name normalizes to the empty form",
                TextNormalizer.Normalize("   ", config).IsEmpty);
        }

        // ---- similarity ----------------------------------------------------------------------

        private static void SimilarityTests(List<string> failures)
        {
            AreEqual(failures, "one substitution is one edit", 1, Similarity.Levenshtein("SICMENS", "SIEMENS"));
            AreEqual(failures, "distance to an empty string is the length", 5, Similarity.Levenshtein("ACME1", ""));
            AreEqual(failures, "identical strings score 100", 100.0, Similarity.Ratio("SIEMENS", "SIEMENS"));
            AreEqual(failures, "an empty side scores 0", 0.0, Similarity.Ratio("SIEMENS", ""));
            AreEqual(failures, "two empty sides score 100", 100.0, Similarity.Ratio("", ""));

            IsTrue(failures, "Jaro-Winkler rewards a shared prefix",
                Similarity.JaroWinklerRatio("SIEMENS", "SIEMENZ") > Similarity.JaroWinklerRatio("SIEMENS", "ZIEMENS"));

            AreEqual(failures, "Soundex matches the standard encoding", "S552", Similarity.Soundex("Siemens"));
            AreEqual(failures, "Soundex ignores vowels after the first letter", "R163", Similarity.Soundex("Robert"));
            AreEqual(failures, "Soundex treats H as transparent", "A261", Similarity.Soundex("Ashcraft"));

            AreEqual(failures, "token sort ignores word order", 100.0,
                Similarity.TokenSortRatio(new[] { "PARTNERS", "BRIGHT" }, new[] { "BRIGHT", "PARTNERS" }));

            AreEqual(failures, "token set ignores extra words on one side", 100.0,
                Similarity.TokenSetRatio(new[] { "ACME", "EUROPE" }, new[] { "ACME", "EUROPE", "DISTRIBUTION" }));

            AreEqual(failures, "partial ratio finds a contained substring", 100.0,
                Similarity.PartialRatio("ACME EUROPE", "ACME EUROPE DISTRIBUTION"));

            MatchConfig config = MatchConfig.CreateDefault();
            IsTrue(failures, "an initialism matches its expansion",
                Similarity.IsAcronymMatch(
                    TextNormalizer.Normalize("IBM", config),
                    TextNormalizer.Normalize("International Business Machines Corporation", config)));

            IsTrue(failures, "unrelated single words are not treated as initialisms",
                !Similarity.IsAcronymMatch(
                    TextNormalizer.Normalize("XYZ", config),
                    TextNormalizer.Normalize("International Business Machines Corporation", config)));
        }

        // ---- engine --------------------------------------------------------------------------

        private static void EngineTests(List<string> failures)
        {
            MatchConfig config = MatchConfig.CreateDefault();

            var vendors = new List<SapVendor>
            {
                Vendor("V001", "Acme Europe Distribution B.V.", "", "NL812345678B01", "NL", "Rotterdam", false),
                Vendor("V002", "Sigma 3 Laboratories Inc.", "", "US451122334", "US", "Boston", false),
                Vendor("V003", "Sigma 5 Laboratories Inc.", "", "US451122999", "US", "Boston", false),
                Vendor("V004", "Old Name Industries Ltd", "", "GB556677889", "GB", "Birmingham", false),
                Vendor("V005", "Zenith Elektro-Technik GmbH", "", "DE817778899", "DE", "Koeln", true),
                Vendor("V006", "Global Tech Solutions Ltd", "", "IE9876543A", "IE", "Dublin", false),
                Vendor("V007", "Global Tech Solution Ltd", "", "GB334455667", "GB", "London", false),
                Vendor("V008", "Fabrikam Manufacturing", "Holdings Pte Ltd", "SG200312345K", "SG", "Singapore", false)
            };

            var engine = new VendorMatchEngine(vendors, config);

            AreEqual(failures, "blocked vendors are excluded from the index", vendors.Count - 1, engine.VendorCount);

            MatchResult exact = engine.Match(Invoice("I1", "Acme Europe Distribution BV", "", "NL", "Rotterdam"));
            AreEqual(failures, "a normalized-identical name is an exact match", MatchType.ExactNormalized, exact.Best.Type);
            AreEqual(failures, "an exact match auto-matches", MatchDecision.AutoMatch, exact.Decision);
            AreEqual(failures, "an exact match picks the right vendor", "V001", exact.Best.Vendor.VendorCode);

            MatchResult subset = engine.Match(Invoice("I2", "Acme Europe", "", "NL", ""));
            AreEqual(failures, "a shortened trading name still resolves", "V001", subset.Best.Vendor.VendorCode);
            IsTrue(failures, "the subset shortcut lifts a partial name to auto-match",
                subset.Decision == MatchDecision.AutoMatch);

            MatchResult taxId = engine.Match(Invoice("I3", "Helix Industrial Group Ltd", "GB556677889", "GB", "Birmingham"));
            AreEqual(failures, "a shared tax identifier wins over the name", MatchType.TaxId, taxId.Best.Type);
            AreEqual(failures, "a tax identifier match scores 100", 100.0, taxId.Best.Score);
            AreEqual(failures, "a renamed vendor is found by its tax identifier", "V004", taxId.Best.Vendor.VendorCode);

            MatchResult digits = engine.Match(Invoice("I4", "Sigma 3 Labs Inc", "", "US", "Boston"));
            AreEqual(failures, "digits in the name decide between near-identical vendors", "V002", digits.Best.Vendor.VendorCode);
            IsTrue(failures, "the digit mismatch penalty separates the two Sigma records",
                digits.Best.Score - digits.Alternatives[0].Score > 10.0);

            MatchResult blocked = engine.Match(Invoice("I5", "Zenith Elektro Technik GmbH", "", "DE", "Koeln"));
            AreEqual(failures, "a blocked vendor is never returned", MatchDecision.NoMatch, blocked.Decision);

            MatchResult ambiguous = engine.Match(Invoice("I6", "Global Tech Solutions Ltd", "", "", ""));
            IsTrue(failures, "two vendors normalizing to the same name are ambiguous", ambiguous.IsAmbiguous);
            AreEqual(failures, "an ambiguous winner is downgraded to review", MatchDecision.NeedsReview, ambiguous.Decision);

            MatchResult split = engine.Match(Invoice("I7", "Fabrikam Manufacturing Holdings Pte Ltd", "", "SG", "Singapore"));
            AreEqual(failures, "NAME1 and NAME2 are matched as one name", "V008", split.Best.Vendor.VendorCode);
            AreEqual(failures, "the combined name matches exactly", MatchDecision.AutoMatch, split.Decision);

            MatchResult unknown = engine.Match(Invoice("I8", "Quantum Widgets Unlimited", "", "ZA", "Cape Town"));
            AreEqual(failures, "an unknown vendor produces no match", MatchDecision.NoMatch, unknown.Decision);

            MatchResult empty = engine.Match(Invoice("I9", "", "", "", ""));
            AreEqual(failures, "a row without a vendor name produces no match", MatchDecision.NoMatch, empty.Decision);

            IsTrue(failures, "every result carries an explanation", !string.IsNullOrEmpty(exact.Reason));
        }

        // ---- config --------------------------------------------------------------------------

        private static void ConfigTests(List<string> failures)
        {
            MatchConfig defaults = MatchConfig.CreateDefault();
            AreEqual(failures, "the default weights are normalized to 1.0", 1.0, Math.Round(defaults.Weights.Total, 6));

            AreEqual(failures, "an absent config path yields the defaults",
                defaults.Thresholds.AutoMatch, MatchConfig.Load(null).Thresholds.AutoMatch);

            var partial = new MatchConfig { Thresholds = new Thresholds { AutoMatch = 95.0, Review = 80.0, AmbiguityMargin = 1.0 } };
            partial.ApplyDefaultsForMissingSections();
            AreEqual(failures, "a partial config keeps its own thresholds", 95.0, partial.Thresholds.AutoMatch);
            IsTrue(failures, "a partial config inherits the default alias table",
                partial.Aliases != null && partial.Aliases.ContainsKey("PHARMA"));

            bool rejected = false;
            try
            {
                var invalid = new MatchConfig { Thresholds = new Thresholds { AutoMatch = 50.0, Review = 90.0 } };
                invalid.ApplyDefaultsForMissingSections();
                invalid.Validate();
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            IsTrue(failures, "thresholds in the wrong order are rejected", rejected);
        }

        // ---- csv -----------------------------------------------------------------------------

        private static void CsvTests(List<string> failures)
        {
            CsvTable quoted = Csv.Parse("Code,Name\r\n1,\"Acme, Inc.\"\r\n2,\"He said \"\"hi\"\"\"\r\n", ',');
            AreEqual(failures, "quoted delimiters do not split a field", "Acme, Inc.", quoted.Rows[0][1]);
            AreEqual(failures, "doubled quotes unescape", "He said \"hi\"", quoted.Rows[1][1]);

            CsvTable semicolon = Csv.Parse("LIFNR;NAME1;LAND1\n1;Muster GmbH;DE\n", '\0');
            AreEqual(failures, "the delimiter is detected", ';', semicolon.Delimiter);
            AreEqual(failures, "detected delimiters parse the header", 3, semicolon.Header.Length);

            CsvTable blanks = Csv.Parse("A,B\n1,2\n\n3,4\n", ',');
            AreEqual(failures, "blank lines are skipped", 2, blanks.Rows.Count);

            AreEqual(failures, "values containing the delimiter are quoted on write",
                "\"Acme, Inc.\"", Csv.Escape("Acme, Inc.", ','));
            AreEqual(failures, "plain values are written unquoted", "Acme", Csv.Escape("Acme", ','));
        }

        // ---- end to end ----------------------------------------------------------------------

        private static void EndToEndTests(string sampleDataFolder, List<string> failures)
        {
            string invoices = Path.Combine(sampleDataFolder, "invoices.sample.csv");
            string sapVendors = Path.Combine(sampleDataFolder, "sap_vendors.sample.csv");

            if (!File.Exists(invoices) || !File.Exists(sapVendors))
            {
                failures.Add("end-to-end: sample data was not found under " + sampleDataFolder);
                return;
            }

            string outputFolder = Path.Combine(Path.GetTempPath(), "vendor-match-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                RunSummary summary = MatchRunner.Run(new RunOptions
                {
                    InvoicesPath = invoices,
                    SapVendorsPath = sapVendors,
                    OutputFolder = outputFolder
                });

                AreEqual(failures, "every sample invoice is processed", 20, summary.InvoiceRows);
                AreEqual(failures, "every sample vendor is loaded", 20, summary.SapVendorRows);
                AreEqual(failures, "the sample data auto-matches 16 invoices", 16, summary.AutoMatched);
                AreEqual(failures, "the sample data routes 2 invoices to review", 2, summary.NeedsReview);
                AreEqual(failures, "the sample data leaves 2 invoices unmatched", 2, summary.NoMatch);
                AreEqual(failures, "one sample invoice is ambiguous", 1, summary.Ambiguous);
                AreEqual(failures, "one sample invoice matches on its tax identifier", 1, summary.TaxIdMatches);

                AreEqual(failures, "every decision is accounted for",
                    summary.InvoiceRows, summary.AutoMatched + summary.NeedsReview + summary.NoMatch);

                IsTrue(failures, "the results file is written", File.Exists(summary.ResultsCsvPath));
                IsTrue(failures, "the exceptions file is written", File.Exists(summary.ExceptionsCsvPath));
                IsTrue(failures, "the summary file is written", File.Exists(summary.SummaryJsonPath));

                CsvTable results = Csv.Read(summary.ResultsCsvPath);
                AreEqual(failures, "the results file holds one row per invoice", summary.InvoiceRows, results.Rows.Count);

                CsvTable exceptions = Csv.Read(summary.ExceptionsCsvPath);
                AreEqual(failures, "the exceptions file holds the review and no-match rows",
                    summary.NeedsReview + summary.NoMatch, exceptions.Rows.Count);

                int decisionIndex = results.IndexOfAny(new[] { "Decision" });
                int codeIndex = results.IndexOfAny(new[] { "SapVendorCode" });
                int idIndex = results.IndexOfAny(new[] { "InvoiceId" });

                AssertRow(failures, results, idIndex, "INV-1016", codeIndex, "0000100016", "a renamed vendor matches on VAT");
                AssertRow(failures, results, idIndex, "INV-1001", codeIndex, "0000100001", "transliterated umlauts match");
                AssertRow(failures, results, idIndex, "INV-1019", decisionIndex, "NeedsReview", "duplicate vendor records go to review");
                AssertRow(failures, results, idIndex, "INV-1020", decisionIndex, "NoMatch", "an unknown vendor stays unmatched");
                AssertRow(failures, results, idIndex, "INV-1017", decisionIndex, "NoMatch", "a blocked vendor is not matched");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, true);
                }
                catch (IOException)
                {
                    // A leftover temp folder must never fail the test run.
                }
            }
        }

        private static void AssertRow(List<string> failures, CsvTable table, int keyIndex, string key,
            int valueIndex, string expected, string description)
        {
            foreach (string[] row in table.Rows)
            {
                if (!string.Equals(CsvTable.Value(row, keyIndex), key, StringComparison.Ordinal)) continue;
                AreEqual(failures, description + " (" + key + ")", expected, CsvTable.Value(row, valueIndex));
                return;
            }
            failures.Add(description + " (" + key + "): row not found in the results file");
        }

        // ---- helpers -------------------------------------------------------------------------

        private static SapVendor Vendor(string code, string name, string name2, string taxId, string country, string city, bool blocked)
        {
            return new SapVendor
            {
                VendorCode = code,
                Name = name,
                Name2 = name2,
                TaxId = taxId,
                VatId = string.Empty,
                Country = country,
                City = city,
                IsBlocked = blocked
            };
        }

        private static InvoiceRecord Invoice(string id, string vendorName, string taxId, string country, string city)
        {
            return new InvoiceRecord
            {
                InvoiceId = id,
                VendorName = vendorName,
                TaxId = taxId,
                VatId = string.Empty,
                Country = country,
                City = city
            };
        }

        private static void AreEqual(List<string> failures, string description, object expected, object actual)
        {
            if (Equals(expected, actual)) return;
            failures.Add(description + ": expected <" + Describe(expected) + "> but got <" + Describe(actual) + ">");
        }

        private static void IsTrue(List<string> failures, string description, bool condition)
        {
            if (condition) return;
            failures.Add(description + ": expected the condition to hold");
        }

        private static string Describe(object value)
        {
            return value == null ? "null" : value.ToString();
        }
    }
}
