using System;
using System.Collections.Generic;

namespace VendorMatching.Code
{
    /// <summary>What the automation should do with a match.</summary>
    public enum MatchDecision
    {
        AutoMatch = 0,
        NeedsReview = 1,
        NoMatch = 2
    }

    /// <summary>How the winning candidate was identified.</summary>
    public enum MatchType
    {
        None = 0,
        TaxId = 1,
        ExactNormalized = 2,
        Acronym = 3,
        Fuzzy = 4
    }

    /// <summary>
    /// The set of derived forms of a vendor name that the matcher compares against.
    /// Produced once per name by <see cref="TextNormalizer"/> and cached on the record.
    /// </summary>
    public sealed class NormalizedName
    {
        public string Original { get; set; }

        /// <summary>Upper-cased, diacritics folded, punctuation removed, aliases expanded.</summary>
        public string Normalized { get; set; }

        /// <summary>Normalized minus legal forms (GMBH, LTD, ...) and noise words.</summary>
        public string Core { get; set; }

        /// <summary>Tokens of <see cref="Core"/>.</summary>
        public string[] Tokens { get; set; }

        /// <summary>Tokens of <see cref="Core"/> sorted alphabetically and re-joined.</summary>
        public string TokenSorted { get; set; }

        /// <summary>Initials of the core tokens, e.g. "INTERNATIONAL BUSINESS MACHINES" -> "IBM".</summary>
        public string Acronym { get; set; }

        /// <summary>Soundex codes of the first core tokens, used as a blocking key.</summary>
        public string Phonetic { get; set; }

        /// <summary>Digit runs found in the name, e.g. "SIGMA 3 LABS" -> ["3"].</summary>
        public string[] Digits { get; set; }

        public bool IsEmpty
        {
            get { return string.IsNullOrEmpty(Core) && string.IsNullOrEmpty(Normalized); }
        }

        public static NormalizedName Empty(string original)
        {
            return new NormalizedName
            {
                Original = original ?? string.Empty,
                Normalized = string.Empty,
                Core = string.Empty,
                Tokens = new string[0],
                TokenSorted = string.Empty,
                Acronym = string.Empty,
                Phonetic = string.Empty,
                Digits = new string[0]
            };
        }
    }

    /// <summary>A vendor master record extracted from SAP (LFA1 and friends).</summary>
    public sealed class SapVendor
    {
        public string VendorCode { get; set; }
        public string Name { get; set; }
        public string Name2 { get; set; }
        public string TaxId { get; set; }
        public string VatId { get; set; }
        public string Country { get; set; }
        public string City { get; set; }
        public bool IsBlocked { get; set; }
        public int RowNumber { get; set; }

        /// <summary>Normalized form of NAME1 (+ NAME2 when present).</summary>
        public NormalizedName Normalized { get; set; }

        /// <summary>Normalized form of NAME1 alone, used when NAME2 is address noise.</summary>
        public NormalizedName NormalizedPrimary { get; set; }

        public string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(Name2)) return Name ?? string.Empty;
                return ((Name ?? string.Empty) + " " + Name2).Trim();
            }
        }
    }

    /// <summary>A vendor as it appears on an incoming invoice.</summary>
    public sealed class InvoiceRecord
    {
        public string InvoiceId { get; set; }
        public string VendorName { get; set; }
        public string TaxId { get; set; }
        public string VatId { get; set; }
        public string Country { get; set; }
        public string City { get; set; }
        public int RowNumber { get; set; }

        public NormalizedName Normalized { get; set; }
    }

    /// <summary>Per-algorithm scores behind a single candidate, kept for auditability.</summary>
    public sealed class ScoreBreakdown
    {
        public double Levenshtein { get; set; }
        public double JaroWinkler { get; set; }
        public double TokenSort { get; set; }
        public double TokenSet { get; set; }
        public double Partial { get; set; }
        public double Base { get; set; }
        public double Bonus { get; set; }
        public double Penalty { get; set; }
        public double Final { get; set; }
    }

    /// <summary>One SAP vendor scored against one invoice vendor name.</summary>
    public sealed class MatchCandidate
    {
        public SapVendor Vendor { get; set; }
        public double Score { get; set; }
        public MatchType Type { get; set; }
        public ScoreBreakdown Breakdown { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>The outcome for a single invoice row.</summary>
    public sealed class MatchResult
    {
        public MatchResult()
        {
            Alternatives = new List<MatchCandidate>();
        }

        public InvoiceRecord Invoice { get; set; }
        public MatchCandidate Best { get; set; }
        public List<MatchCandidate> Alternatives { get; set; }
        public MatchDecision Decision { get; set; }
        public bool IsAmbiguous { get; set; }
        public string Reason { get; set; }
        public int CandidatesEvaluated { get; set; }
    }

    /// <summary>Aggregate result of one run, also serialized to summary.json.</summary>
    public sealed class RunSummary
    {
        public string StartedUtc { get; set; }
        public double DurationSeconds { get; set; }
        public int InvoiceRows { get; set; }
        public int SapVendorRows { get; set; }
        public int AutoMatched { get; set; }
        public int NeedsReview { get; set; }
        public int NoMatch { get; set; }
        public int Ambiguous { get; set; }
        public int TaxIdMatches { get; set; }
        public int ExactMatches { get; set; }
        public int AcronymMatches { get; set; }
        public int FuzzyMatches { get; set; }
        public double AutoMatchThreshold { get; set; }
        public double ReviewThreshold { get; set; }
        public string ResultsCsvPath { get; set; }
        public string ExceptionsCsvPath { get; set; }
        public string SummaryJsonPath { get; set; }
    }
}
