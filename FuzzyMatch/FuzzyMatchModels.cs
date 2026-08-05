using System;
using System.Collections.Generic;

namespace FuzzyMatch
{
    /// <summary>
    /// How a source record was classified against the reference list.
    /// </summary>
    public enum MatchOutcome
    {
        /// <summary>Score at or above the auto-match threshold — safe to post without a human.</summary>
        AutoMatched,

        /// <summary>Score between the review and auto-match thresholds — route to a human.</summary>
        NeedsReview,

        /// <summary>Score below the review threshold — no usable candidate found.</summary>
        NoMatch
    }

    /// <summary>
    /// Tunable knobs for the matcher. Defaults are the ones used when the workflow
    /// is run without explicit arguments.
    /// </summary>
    public class FuzzyMatchOptions
    {
        /// <summary>Score (0-100) at or above which a match is accepted automatically.</summary>
        public double AutoMatchThreshold { get; set; } = 90d;

        /// <summary>Score (0-100) at or above which a match is sent for human review.</summary>
        public double ReviewThreshold { get; set; } = 75d;

        /// <summary>
        /// Remove common legal-entity and filler tokens (LTD, INC, GMBH, THE, ...) during
        /// normalization. Turn off when those tokens are meaningful for your data.
        /// </summary>
        public bool StripNoiseTokens { get; set; } = true;

        /// <summary>
        /// Cheap pre-filter: candidates whose length ratio to the source value falls below
        /// this are skipped without scoring. 0 disables the filter.
        /// </summary>
        public double LengthGuardRatio { get; set; } = 0.34d;

        /// <summary>Weight of the Levenshtein ratio in the composite score.</summary>
        public double LevenshteinWeight { get; set; } = 0.40d;

        /// <summary>Weight of the Jaro-Winkler similarity in the composite score.</summary>
        public double JaroWinklerWeight { get; set; } = 0.35d;

        /// <summary>Weight of the token-set ratio in the composite score.</summary>
        public double TokenSetWeight { get; set; } = 0.25d;

        /// <summary>
        /// Extra tokens to strip during normalization, on top of the built-in noise list.
        /// Compared after upper-casing and punctuation removal.
        /// </summary>
        public IList<string> AdditionalNoiseTokens { get; } = new List<string>();

        /// <summary>Throws when the option set cannot produce meaningful scores.</summary>
        public void Validate()
        {
            if (AutoMatchThreshold < 0d || AutoMatchThreshold > 100d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(AutoMatchThreshold),
                    AutoMatchThreshold,
                    "AutoMatchThreshold must be between 0 and 100.");
            }

            if (ReviewThreshold < 0d || ReviewThreshold > 100d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ReviewThreshold),
                    ReviewThreshold,
                    "ReviewThreshold must be between 0 and 100.");
            }

            if (ReviewThreshold > AutoMatchThreshold)
            {
                throw new ArgumentException(
                    $"ReviewThreshold ({ReviewThreshold}) cannot be greater than AutoMatchThreshold ({AutoMatchThreshold}).");
            }

            double weightSum = LevenshteinWeight + JaroWinklerWeight + TokenSetWeight;
            if (weightSum <= 0d)
            {
                throw new ArgumentException("At least one scorer weight must be greater than zero.");
            }
        }
    }

    /// <summary>
    /// One row of the reference (master) list, pre-normalized so the inner matching
    /// loop never re-normalizes the same string.
    /// </summary>
    public class ReferenceRecord
    {
        /// <summary>1-based spreadsheet row number, including the header row.</summary>
        public int RowNumber { get; set; }

        /// <summary>The value exactly as it appears in the reference sheet.</summary>
        public string RawValue { get; set; } = string.Empty;

        /// <summary>The value after normalization — what actually gets scored.</summary>
        public string NormalizedValue { get; set; } = string.Empty;
    }

    /// <summary>
    /// The outcome of matching a single source value against the whole reference list.
    /// </summary>
    public class MatchResult
    {
        /// <summary>1-based spreadsheet row number of the source record.</summary>
        public int SourceRowNumber { get; set; }

        /// <summary>The source value as it appears in the sheet.</summary>
        public string SourceValue { get; set; } = string.Empty;

        /// <summary>Best reference value found, or empty when nothing cleared the floor.</summary>
        public string MatchedValue { get; set; } = string.Empty;

        /// <summary>Row number of the best reference value, or 0 when there is no match.</summary>
        public int MatchedRowNumber { get; set; }

        /// <summary>Composite similarity of the best candidate, 0-100.</summary>
        public double Score { get; set; }

        /// <summary>Second-best reference value — useful for spotting ambiguous matches.</summary>
        public string RunnerUpValue { get; set; } = string.Empty;

        /// <summary>Composite similarity of the second-best candidate, 0-100.</summary>
        public double RunnerUpScore { get; set; }

        /// <summary>Classification driven by the configured thresholds.</summary>
        public MatchOutcome Outcome { get; set; } = MatchOutcome.NoMatch;

        /// <summary>
        /// Gap between the best and second-best candidate. A high score with a small
        /// margin means two reference rows look alike — worth a human eye even when
        /// the score cleared the auto-match threshold.
        /// </summary>
        public double Margin
        {
            get { return Math.Round(Score - RunnerUpScore, 2); }
        }
    }
}
