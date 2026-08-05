using System;
using System.Collections.Generic;
using System.Diagnostics;
using UiPath.CodedWorkflows;
using UiPath.Core;

namespace FuzzyMatch
{
    /// <summary>
    /// Matches a single string value against a delimited list of candidate strings.
    /// Everything in and out is a plain string or number — no workbooks, no files,
    /// no DataTables — so this can be driven straight from workflow variables,
    /// an Orchestrator asset, a queue item field, or a scraped UI value.
    /// </summary>
    public class MatchValue : CodedWorkflow
    {
        [Workflow]
        public (string MatchedValue, double Score, string Outcome, string RunnerUpValue, double Margin) Execute(
            string sourceValue,
            string referenceValues,
            string delimiter,
            double autoMatchThreshold,
            double reviewThreshold)
        {
            // --- Input validation -------------------------------------------------
            // A blank source value or an empty candidate list is bad input data, not a
            // transient fault: retrying will not fix it, so it needs a human.
            if (string.IsNullOrWhiteSpace(sourceValue))
            {
                throw new BusinessRuleException("sourceValue is empty — there is nothing to match.");
            }

            if (string.IsNullOrWhiteSpace(referenceValues))
            {
                throw new BusinessRuleException("referenceValues is empty — there is nothing to match against.");
            }

            string separator = string.IsNullOrEmpty(delimiter) ? ";" : delimiter;

            var options = new FuzzyMatchOptions
            {
                AutoMatchThreshold = autoMatchThreshold,
                ReviewThreshold = reviewThreshold
            };

            try
            {
                options.Validate();
            }
            catch (Exception ex)
            {
                throw new BusinessRuleException($"Invalid matching thresholds: {ex.Message}", ex);
            }

            // --- Build the candidate list ----------------------------------------
            List<ReferenceRecord> references = SplitReferences(referenceValues, separator, options);

            if (references.Count == 0)
            {
                throw new BusinessRuleException(
                    $"referenceValues contained no usable entries after splitting on '{separator}'.");
            }

            Log($"Matching \"{sourceValue.Trim()}\" against {references.Count} candidate(s) " +
                $"(auto-match >= {options.AutoMatchThreshold}, review >= {options.ReviewThreshold}).");

            // --- Match ------------------------------------------------------------
            var stopwatch = Stopwatch.StartNew();
            MatchResult result = FuzzyMatchEngine.FindBestMatch(sourceValue.Trim(), 1, references, options);
            stopwatch.Stop();

            Log($"Best match: \"{(string.IsNullOrEmpty(result.MatchedValue) ? "(none)" : result.MatchedValue)}\" " +
                $"— score {result.Score}, outcome {result.Outcome}, margin {result.Margin} " +
                $"({stopwatch.ElapsedMilliseconds} ms).");

            // Outcome is returned as a string so the caller can branch on it in a
            // XAML If/Switch without needing the enum type in scope.
            return (result.MatchedValue,
                    result.Score,
                    result.Outcome.ToString(),
                    result.RunnerUpValue,
                    result.Margin);
        }

        private static List<ReferenceRecord> SplitReferences(
            string referenceValues, string separator, FuzzyMatchOptions options)
        {
            string[] parts = referenceValues.Split(new[] { separator }, StringSplitOptions.None);
            var records = new List<ReferenceRecord>(parts.Length);

            for (int i = 0; i < parts.Length; i++)
            {
                string raw = parts[i];

                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                records.Add(new ReferenceRecord
                {
                    // 1-based position in the supplied list, so the caller can tell
                    // which candidate won without string-comparing the result.
                    RowNumber = i + 1,
                    RawValue = raw.Trim(),
                    NormalizedValue = FuzzyMatchEngine.Normalize(
                        raw, options.StripNoiseTokens, options.AdditionalNoiseTokens)
                });
            }

            return records;
        }
    }
}
