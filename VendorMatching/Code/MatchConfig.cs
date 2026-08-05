using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VendorMatching.Code
{
    /// <summary>Score cut-offs that turn a numeric score into a decision.</summary>
    public sealed class Thresholds
    {
        /// <summary>At or above this score the match is posted without human review.</summary>
        public double AutoMatch { get; set; }

        /// <summary>At or above this score the match is routed to a human/Action Center.</summary>
        public double Review { get; set; }

        /// <summary>
        /// If the runner-up is within this many points of the winner, the winner is
        /// downgraded to review even when it clears <see cref="AutoMatch"/>.
        /// </summary>
        public double AmbiguityMargin { get; set; }
    }

    /// <summary>Relative contribution of each similarity algorithm to the composite score.</summary>
    public sealed class Weights
    {
        public double Levenshtein { get; set; }
        public double JaroWinkler { get; set; }
        public double TokenSort { get; set; }
        public double TokenSet { get; set; }
        public double Partial { get; set; }

        public double Total
        {
            get { return Levenshtein + JaroWinkler + TokenSort + TokenSet + Partial; }
        }
    }

    /// <summary>
    /// Discount factors for the "subset shortcut". A weighted mean punishes a name that is a
    /// correct but incomplete version of the SAP name ("Acme Europe" vs "Acme Europe Distribution
    /// B.V."), because half the algorithms see a long unmatched tail. The shortcut lets a single
    /// strong order/subset signal carry the score, discounted so it never outranks a real match.
    /// </summary>
    public sealed class ShortcutFactors
    {
        public double TokenSet { get; set; }
        public double TokenSort { get; set; }
        public double Partial { get; set; }

        /// <summary>
        /// Both names need at least this many core tokens before the shortcut applies, so a
        /// single shared low-information word ("Europe") cannot carry a match on its own.
        /// </summary>
        public int MinimumTokens { get; set; }
    }

    /// <summary>Bonuses and penalties applied on top of the weighted composite.</summary>
    public sealed class Adjustments
    {
        public double CountryMatchBonus { get; set; }
        public double CountryMismatchPenalty { get; set; }
        public double CityMatchBonus { get; set; }
        public double TaxIdSuffixBonus { get; set; }
        public double DigitMismatchPenalty { get; set; }

        /// <summary>Score awarded when one name is the initialism of the other.</summary>
        public double AcronymScore { get; set; }

        /// <summary>Skip SAP records flagged as blocked / marked for deletion.</summary>
        public bool ExcludeBlockedVendors { get; set; }
    }

    /// <summary>Blocking / candidate-generation controls.</summary>
    public sealed class IndexOptions
    {
        /// <summary>A token indexed in more than this share of the vendor master is treated as common and skipped.</summary>
        public double MaxTokenDocumentFrequency { get; set; }

        /// <summary>
        /// Floor for the document-frequency cut-off. Without it a small vendor master turns the
        /// percentage into 1 and drops almost every token from the index.
        /// </summary>
        public int MinTokenDocumentFrequency { get; set; }

        /// <summary>Hard upper bound on candidates scored per invoice row.</summary>
        public int MaxCandidatesPerInvoice { get; set; }

        /// <summary>Characters of the core name used for the prefix blocking key.</summary>
        public int PrefixKeyLength { get; set; }

        /// <summary>Compare against the whole vendor master when blocking yields nothing.</summary>
        public bool FullScanFallback { get; set; }

        /// <summary>Upper bound on vendor master size for which a full scan is allowed.</summary>
        public int FullScanMaxVendors { get; set; }
    }

    /// <summary>
    /// Everything the matcher needs, loadable from JSON. Any section left out of the file
    /// falls back to the built-in defaults, so a partial config file is valid.
    /// </summary>
    public sealed class MatchConfig
    {
        /// <summary>Logical field name -> accepted CSV header names, first hit wins.</summary>
        public Dictionary<string, string[]> InvoiceColumns { get; set; }

        /// <summary>Logical field name -> accepted CSV header names, first hit wins.</summary>
        public Dictionary<string, string[]> SapColumns { get; set; }

        public Thresholds Thresholds { get; set; }
        public Weights Weights { get; set; }
        public ShortcutFactors ShortcutFactors { get; set; }
        public Adjustments Adjustments { get; set; }
        public IndexOptions Index { get; set; }

        /// <summary>Legal form tokens stripped from the core name (GMBH, LTD, ...).</summary>
        public string[] LegalForms { get; set; }

        /// <summary>Tokens that carry no identifying signal (THE, AND, ...).</summary>
        public string[] NoiseWords { get; set; }

        /// <summary>Token-level expansions applied before comparison (INTL -> INTERNATIONAL).</summary>
        public Dictionary<string, string> Aliases { get; set; }

        /// <summary>Number of alternative candidates recorded next to the winner.</summary>
        public int AlternativesToKeep { get; set; }

        private HashSet<string> _legalFormSet;
        private HashSet<string> _noiseWordSet;

        public HashSet<string> LegalFormSet
        {
            get
            {
                if (_legalFormSet == null)
                {
                    _legalFormSet = new HashSet<string>(StringComparer.Ordinal);
                    if (LegalForms != null)
                    {
                        foreach (string form in LegalForms)
                        {
                            if (!string.IsNullOrEmpty(form)) _legalFormSet.Add(form.ToUpperInvariant());
                        }
                    }
                }
                return _legalFormSet;
            }
        }

        public HashSet<string> NoiseWordSet
        {
            get
            {
                if (_noiseWordSet == null)
                {
                    _noiseWordSet = new HashSet<string>(StringComparer.Ordinal);
                    if (NoiseWords != null)
                    {
                        foreach (string word in NoiseWords)
                        {
                            if (!string.IsNullOrEmpty(word)) _noiseWordSet.Add(word.ToUpperInvariant());
                        }
                    }
                }
                return _noiseWordSet;
            }
        }

        public static MatchConfig CreateDefault()
        {
            var config = new MatchConfig();

            config.InvoiceColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "id", new[] { "InvoiceId", "InvoiceNo", "InvoiceNumber", "DocumentNo", "BELNR" } },
                { "vendorName", new[] { "VendorName", "Vendor", "SupplierName", "Supplier", "PayeeName", "Name" } },
                { "taxId", new[] { "TaxId", "TaxNumber", "TIN", "STCD1" } },
                { "vatId", new[] { "VatId", "VATNumber", "VAT", "STCEG" } },
                { "country", new[] { "Country", "CountryCode", "LAND1" } },
                { "city", new[] { "City", "Town", "ORT01" } }
            };

            config.SapColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "code", new[] { "LIFNR", "VendorCode", "VendorNo", "SupplierCode", "Account" } },
                { "name", new[] { "NAME1", "VendorName", "Name", "SupplierName" } },
                { "name2", new[] { "NAME2", "VendorName2", "Name2" } },
                { "taxId", new[] { "STCD1", "TaxId", "TaxNumber", "TIN" } },
                { "vatId", new[] { "STCEG", "VatId", "VATNumber", "VAT" } },
                { "country", new[] { "LAND1", "Country", "CountryCode" } },
                { "city", new[] { "ORT01", "City", "Town" } },
                { "blocked", new[] { "LOEVM", "SPERR", "Blocked", "DeletionFlag" } }
            };

            config.Thresholds = new Thresholds
            {
                AutoMatch = 90.0,
                Review = 75.0,
                AmbiguityMargin = 2.0
            };

            config.Weights = new Weights
            {
                Levenshtein = 0.20,
                JaroWinkler = 0.20,
                TokenSort = 0.25,
                TokenSet = 0.25,
                Partial = 0.10
            };

            config.ShortcutFactors = new ShortcutFactors
            {
                TokenSet = 0.92,
                TokenSort = 0.97,
                Partial = 0.87,
                MinimumTokens = 2
            };

            config.Adjustments = new Adjustments
            {
                CountryMatchBonus = 2.0,
                CountryMismatchPenalty = 6.0,
                CityMatchBonus = 3.0,
                TaxIdSuffixBonus = 5.0,
                DigitMismatchPenalty = 15.0,
                AcronymScore = 95.0,
                ExcludeBlockedVendors = true
            };

            config.Index = new IndexOptions
            {
                MaxTokenDocumentFrequency = 0.05,
                MinTokenDocumentFrequency = 20,
                MaxCandidatesPerInvoice = 200,
                PrefixKeyLength = 4,
                FullScanFallback = true,
                FullScanMaxVendors = 20000
            };

            config.AlternativesToKeep = 2;

            config.LegalForms = new[]
            {
                "INC", "INCORPORATED", "LLC", "LLP", "LP", "LTD", "LTDA", "LIMITED",
                "CORP", "CORPORATION", "CO", "COMPANY", "PLC", "PC",
                "GMBH", "MBH", "AG", "AKTIENGESELLSCHAFT", "KG", "KGAA", "OHG", "GBR", "EG", "EV",
                "SA", "SAS", "SARL", "SASU", "SNC", "SCA", "SPA", "SRL", "SRLS",
                "BV", "NV", "CV", "VOF",
                "AB", "ASA", "AS", "OY", "OYJ", "APS",
                "PTE", "PTY", "PVT", "PRIVATE", "SDN", "BHD",
                "KK", "GK", "YK", "ZRT", "KFT", "BT", "DOO", "DD",
                "SP", "ZOO", "SPZOO", "OO", "AD",
                "JSC", "PJSC", "OAO", "OOO", "ZAO", "TOO",
                "EIRL", "SAB", "SAPI", "TBK", "PT"
            };

            // "AND"/"UND"/"ET" also absorb the "&" that ExpandSymbols turns into a word.
            config.NoiseWords = new[] { "THE", "AND", "UND", "ET", "OF", "DE", "DU", "DES", "LA", "LE", "EL" };

            config.Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "INTL", "INTERNATIONAL" },
                { "INTERNAT", "INTERNATIONAL" },
                { "NATL", "NATIONAL" },
                { "MFG", "MANUFACTURING" },
                { "MFRS", "MANUFACTURERS" },
                { "SVC", "SERVICES" },
                { "SVCS", "SERVICES" },
                { "SERVICE", "SERVICES" },
                { "SOLN", "SOLUTIONS" },
                { "SOLNS", "SOLUTIONS" },
                { "SOLUTION", "SOLUTIONS" },
                { "SYS", "SYSTEMS" },
                { "SYSTEM", "SYSTEMS" },
                { "TECHNOLOGY", "TECHNOLOGIES" },
                { "TECHNOLOGIE", "TECHNOLOGIES" },
                { "IND", "INDUSTRIES" },
                { "INDUSTRY", "INDUSTRIES" },
                { "INDUSTRIAL", "INDUSTRIES" },
                { "DIST", "DISTRIBUTION" },
                { "DISTR", "DISTRIBUTION" },
                { "ELEC", "ELECTRIC" },
                { "ELECTRICAL", "ELECTRIC" },
                { "ENG", "ENGINEERING" },
                { "EQUIP", "EQUIPMENT" },
                { "EQP", "EQUIPMENT" },
                { "MGMT", "MANAGEMENT" },
                { "TRANSP", "TRANSPORT" },
                { "TRANSPORTATION", "TRANSPORT" },
                { "PHARMA", "PHARMACEUTICALS" },
                { "PHARMACEUTICAL", "PHARMACEUTICALS" },
                { "LAB", "LABORATORIES" },
                { "LABS", "LABORATORIES" },
                { "LABORATORY", "LABORATORIES" },
                { "CONSULTING", "CONSULTANTS" },
                { "CONSULTANCY", "CONSULTANTS" },
                { "ASSOC", "ASSOCIATES" },
                { "ASSOCIATE", "ASSOCIATES" },
                { "BROS", "BROTHERS" }
            };

            return config;
        }

        /// <summary>
        /// Reads a JSON config file and layers it over the defaults. A missing or empty path
        /// simply yields the defaults, which keeps the workflow argument optional.
        /// </summary>
        public static MatchConfig Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return CreateDefault();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Matching config file was not found.", path);
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            MatchConfig loaded = JsonSerializer.Deserialize<MatchConfig>(File.ReadAllText(path), options);
            if (loaded == null) return CreateDefault();

            loaded.ApplyDefaultsForMissingSections();
            loaded.Validate();
            return loaded;
        }

        /// <summary>Fills in any section the JSON file did not provide.</summary>
        public void ApplyDefaultsForMissingSections()
        {
            MatchConfig defaults = CreateDefault();

            if (InvoiceColumns == null || InvoiceColumns.Count == 0) InvoiceColumns = defaults.InvoiceColumns;
            if (SapColumns == null || SapColumns.Count == 0) SapColumns = defaults.SapColumns;
            if (Thresholds == null) Thresholds = defaults.Thresholds;
            if (Weights == null || Weights.Total <= 0.0) Weights = defaults.Weights;
            if (ShortcutFactors == null) ShortcutFactors = defaults.ShortcutFactors;
            if (Adjustments == null) Adjustments = defaults.Adjustments;
            if (Index == null) Index = defaults.Index;
            if (LegalForms == null) LegalForms = defaults.LegalForms;
            if (NoiseWords == null) NoiseWords = defaults.NoiseWords;
            if (Aliases == null) Aliases = defaults.Aliases;
            if (AlternativesToKeep <= 0) AlternativesToKeep = defaults.AlternativesToKeep;

            if (Index.PrefixKeyLength <= 0) Index.PrefixKeyLength = defaults.Index.PrefixKeyLength;
            if (Index.MaxCandidatesPerInvoice <= 0) Index.MaxCandidatesPerInvoice = defaults.Index.MaxCandidatesPerInvoice;
            if (Index.MaxTokenDocumentFrequency <= 0.0) Index.MaxTokenDocumentFrequency = defaults.Index.MaxTokenDocumentFrequency;
            if (Index.MinTokenDocumentFrequency <= 0) Index.MinTokenDocumentFrequency = defaults.Index.MinTokenDocumentFrequency;
            if (Index.FullScanMaxVendors <= 0) Index.FullScanMaxVendors = defaults.Index.FullScanMaxVendors;

            _legalFormSet = null;
            _noiseWordSet = null;
        }

        public void Validate()
        {
            if (Thresholds.AutoMatch < Thresholds.Review)
            {
                throw new ArgumentException(
                    "Invalid thresholds: autoMatch (" + Thresholds.AutoMatch +
                    ") must be greater than or equal to review (" + Thresholds.Review + ").");
            }
            if (Weights.Total <= 0.0)
            {
                throw new ArgumentException("Invalid weights: the sum of all weights must be greater than zero.");
            }
        }
    }
}
