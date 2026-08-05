using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FuzzyMatch
{
    /// <summary>
    /// Pure string-similarity logic — no UiPath services, no I/O, no state.
    /// Kept separate from the workflow so it can be unit tested directly
    /// (see TestFuzzyMatchEngine.cs).
    /// </summary>
    public static class FuzzyMatchEngine
    {
        private static readonly Regex NonAlphanumericPattern =
            new Regex("[^A-Z0-9 ]", RegexOptions.Compiled);

        private static readonly Regex MultiSpacePattern =
            new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// Legal-entity suffixes and filler words that carry no identifying signal.
        /// "Acme Ltd" and "ACME LIMITED" should score 100, not 82.
        /// </summary>
        private static readonly HashSet<string> DefaultNoiseTokens =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "LTD", "LTDA", "LIMITED", "LLC", "LLP", "INC", "INCORPORATED",
                "CO", "COMPANY", "CORP", "CORPORATION", "PLC", "GMBH", "AG",
                "BV", "NV", "SA", "SAS", "SRL", "SPA", "PTY", "PVT", "PRIVATE",
                "INTL", "INTERNATIONAL", "GROUP", "HOLDING", "HOLDINGS",
                "THE", "AND", "OF"
            };

        /// <summary>
        /// Canonicalizes a value so that cosmetic differences stop mattering:
        /// case, accents, punctuation, ampersands, repeated whitespace and
        /// (optionally) legal-entity noise tokens.
        /// </summary>
        public static string Normalize(string value, bool stripNoiseTokens = true)
        {
            return Normalize(value, stripNoiseTokens, null);
        }

        /// <summary>
        /// Normalize with an extra caller-supplied noise list.
        /// </summary>
        public static string Normalize(string value, bool stripNoiseTokens, IEnumerable<string> additionalNoiseTokens)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string working = RemoveDiacritics(value.Trim().ToUpperInvariant());
            working = working.Replace("&", " AND ");
            working = NonAlphanumericPattern.Replace(working, " ");
            working = MultiSpacePattern.Replace(working, " ").Trim();

            if (!stripNoiseTokens || working.Length == 0)
            {
                return working;
            }

            HashSet<string> noise = DefaultNoiseTokens;
            if (additionalNoiseTokens != null)
            {
                noise = new HashSet<string>(DefaultNoiseTokens, StringComparer.Ordinal);
                foreach (string extra in additionalNoiseTokens)
                {
                    if (!string.IsNullOrWhiteSpace(extra))
                    {
                        noise.Add(extra.Trim().ToUpperInvariant());
                    }
                }
            }

            string[] tokens = working.Split(' ');
            List<string> kept = tokens.Where(t => t.Length > 0 && !noise.Contains(t)).ToList();

            // Never normalize a value down to nothing — "The Group Ltd" is all noise
            // but still has to match itself, so fall back to the un-stripped form.
            return kept.Count == 0 ? working : string.Join(" ", kept);
        }

        /// <summary>Strips combining marks so "Ácmé" and "Acme" compare equal.</summary>
        public static string RemoveDiacritics(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string decomposed = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);

            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>
        /// Levenshtein edit distance, two-row dynamic programming
        /// (O(n*m) time, O(min(n,m)) space).
        /// </summary>
        public static int LevenshteinDistance(string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;

            if (left.Length == 0) return right.Length;
            if (right.Length == 0) return left.Length;

            // Keep the shorter string on the inner axis to bound memory.
            if (left.Length > right.Length)
            {
                string swap = left;
                left = right;
                right = swap;
            }

            int[] previous = new int[left.Length + 1];
            int[] current = new int[left.Length + 1];

            for (int i = 0; i <= left.Length; i++)
            {
                previous[i] = i;
            }

            for (int j = 1; j <= right.Length; j++)
            {
                current[0] = j;

                for (int i = 1; i <= left.Length; i++)
                {
                    int substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;

                    current[i] = Math.Min(
                        Math.Min(current[i - 1] + 1, previous[i] + 1),
                        previous[i - 1] + substitutionCost);
                }

                int[] swapRow = previous;
                previous = current;
                current = swapRow;
            }

            return previous[left.Length];
        }

        /// <summary>Levenshtein distance expressed as a 0-1 similarity ratio.</summary>
        public static double LevenshteinRatio(string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;

            int longest = Math.Max(left.Length, right.Length);
            if (longest == 0)
            {
                return 1d;
            }

            return 1d - ((double)LevenshteinDistance(left, right) / longest);
        }

        /// <summary>
        /// Jaro-Winkler similarity (0-1). Rewards shared prefixes, which makes it
        /// good at typos near the start of a name and at truncated values.
        /// </summary>
        public static double JaroWinklerSimilarity(string left, string right, double prefixScale = 0.1d)
        {
            double jaro = JaroSimilarity(left, right);

            if (jaro <= 0.7d)
            {
                return jaro;
            }

            left = left ?? string.Empty;
            right = right ?? string.Empty;

            int prefixLength = 0;
            int maxPrefix = Math.Min(4, Math.Min(left.Length, right.Length));
            while (prefixLength < maxPrefix && left[prefixLength] == right[prefixLength])
            {
                prefixLength++;
            }

            return jaro + (prefixLength * prefixScale * (1d - jaro));
        }

        /// <summary>Plain Jaro similarity (0-1) — the base for Jaro-Winkler.</summary>
        public static double JaroSimilarity(string left, string right)
        {
            left = left ?? string.Empty;
            right = right ?? string.Empty;

            if (left.Length == 0 && right.Length == 0) return 1d;
            if (left.Length == 0 || right.Length == 0) return 0d;
            if (string.Equals(left, right, StringComparison.Ordinal)) return 1d;

            int matchWindow = Math.Max(0, (Math.Max(left.Length, right.Length) / 2) - 1);

            bool[] leftMatched = new bool[left.Length];
            bool[] rightMatched = new bool[right.Length];
            int matches = 0;

            for (int i = 0; i < left.Length; i++)
            {
                int start = Math.Max(0, i - matchWindow);
                int end = Math.Min(i + matchWindow + 1, right.Length);

                for (int j = start; j < end; j++)
                {
                    if (rightMatched[j] || left[i] != right[j])
                    {
                        continue;
                    }

                    leftMatched[i] = true;
                    rightMatched[j] = true;
                    matches++;
                    break;
                }
            }

            if (matches == 0)
            {
                return 0d;
            }

            // Count transpositions: matched characters that appear out of order.
            int transpositions = 0;
            int rightIndex = 0;

            for (int i = 0; i < left.Length; i++)
            {
                if (!leftMatched[i])
                {
                    continue;
                }

                while (!rightMatched[rightIndex])
                {
                    rightIndex++;
                }

                if (left[i] != right[rightIndex])
                {
                    transpositions++;
                }

                rightIndex++;
            }

            double m = matches;
            return ((m / left.Length) + (m / right.Length) + ((m - (transpositions / 2d)) / m)) / 3d;
        }

        /// <summary>
        /// Order-insensitive token comparison (0-1). Handles "Smith, John" vs
        /// "John Smith" and partial token overlap, which the character-level
        /// scorers both punish heavily.
        /// </summary>
        public static double TokenSetRatio(string left, string right)
        {
            string[] leftTokens = SplitTokens(left);
            string[] rightTokens = SplitTokens(right);

            if (leftTokens.Length == 0 && rightTokens.Length == 0) return 1d;
            if (leftTokens.Length == 0 || rightTokens.Length == 0) return 0d;

            var leftSet = new HashSet<string>(leftTokens, StringComparer.Ordinal);
            var rightSet = new HashSet<string>(rightTokens, StringComparer.Ordinal);

            var intersection = new HashSet<string>(leftSet, StringComparer.Ordinal);
            intersection.IntersectWith(rightSet);

            var union = new HashSet<string>(leftSet, StringComparer.Ordinal);
            union.UnionWith(rightSet);

            double jaccard = union.Count == 0 ? 0d : (double)intersection.Count / union.Count;

            // Sorting both token sets turns a reordering into an exact match.
            string leftSorted = string.Join(" ", leftSet.OrderBy(t => t, StringComparer.Ordinal));
            string rightSorted = string.Join(" ", rightSet.OrderBy(t => t, StringComparer.Ordinal));
            double sortedRatio = LevenshteinRatio(leftSorted, rightSorted);

            return Math.Max(jaccard, sortedRatio);
        }

        /// <summary>
        /// Composite similarity (0-100) of two already-normalized values.
        /// Blends the three scorers using the weights from <paramref name="options"/>.
        /// </summary>
        public static double ScoreNormalized(string normalizedLeft, string normalizedRight, FuzzyMatchOptions options)
        {
            options = options ?? new FuzzyMatchOptions();

            normalizedLeft = normalizedLeft ?? string.Empty;
            normalizedRight = normalizedRight ?? string.Empty;

            if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
            {
                return 0d;
            }

            if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal))
            {
                return 100d;
            }

            double weightSum = options.LevenshteinWeight + options.JaroWinklerWeight + options.TokenSetWeight;
            if (weightSum <= 0d)
            {
                throw new ArgumentException("At least one scorer weight must be greater than zero.", nameof(options));
            }

            double tokenSet = TokenSetRatio(normalizedLeft, normalizedRight);

            // Levenshtein and Jaro-Winkler are both position-sensitive, so a pure
            // reordering ("Smith, John" vs "John Smith") tanks them even though the
            // values are the same entity. Score the token-sorted forms as well and
            // keep whichever alignment reads better.
            double direct = Blend(
                LevenshteinRatio(normalizedLeft, normalizedRight),
                JaroWinklerSimilarity(normalizedLeft, normalizedRight),
                tokenSet,
                options,
                weightSum);

            string leftSorted = SortTokens(normalizedLeft);
            string rightSorted = SortTokens(normalizedRight);

            double sorted = string.Equals(leftSorted, normalizedLeft, StringComparison.Ordinal)
                            && string.Equals(rightSorted, normalizedRight, StringComparison.Ordinal)
                ? direct
                : Blend(
                    LevenshteinRatio(leftSorted, rightSorted),
                    JaroWinklerSimilarity(leftSorted, rightSorted),
                    tokenSet,
                    options,
                    weightSum);

            double blended = Math.Max(direct, sorted);

            return Math.Round(Math.Max(0d, Math.Min(1d, blended)) * 100d, 2);
        }

        /// <summary>
        /// Composite similarity (0-100) of two raw values — normalizes both first.
        /// </summary>
        public static double Score(string left, string right, FuzzyMatchOptions options)
        {
            options = options ?? new FuzzyMatchOptions();

            return ScoreNormalized(
                Normalize(left, options.StripNoiseTokens, options.AdditionalNoiseTokens),
                Normalize(right, options.StripNoiseTokens, options.AdditionalNoiseTokens),
                options);
        }

        /// <summary>
        /// Scores one source value against every reference record and classifies the
        /// best candidate. Also reports the runner-up so ambiguous matches are visible.
        /// </summary>
        public static MatchResult FindBestMatch(
            string sourceValue,
            int sourceRowNumber,
            IList<ReferenceRecord> referenceRecords,
            FuzzyMatchOptions options)
        {
            options = options ?? new FuzzyMatchOptions();

            var result = new MatchResult
            {
                SourceRowNumber = sourceRowNumber,
                SourceValue = sourceValue ?? string.Empty,
                Outcome = MatchOutcome.NoMatch
            };

            string normalizedSource = Normalize(sourceValue, options.StripNoiseTokens, options.AdditionalNoiseTokens);
            if (normalizedSource.Length == 0 || referenceRecords == null || referenceRecords.Count == 0)
            {
                return result;
            }

            ReferenceRecord best = null;
            double bestScore = -1d;
            double runnerUpScore = -1d;
            string runnerUpValue = string.Empty;

            foreach (ReferenceRecord candidate in referenceRecords)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.NormalizedValue))
                {
                    continue;
                }

                // Cheap length pre-filter: a 4-character value can never be a
                // meaningful match for a 40-character one, so skip the DP entirely.
                if (options.LengthGuardRatio > 0d)
                {
                    double shorter = Math.Min(normalizedSource.Length, candidate.NormalizedValue.Length);
                    double longer = Math.Max(normalizedSource.Length, candidate.NormalizedValue.Length);

                    if (longer > 0d && (shorter / longer) < options.LengthGuardRatio)
                    {
                        continue;
                    }
                }

                double score = ScoreNormalized(normalizedSource, candidate.NormalizedValue, options);

                if (score > bestScore)
                {
                    runnerUpScore = bestScore;
                    runnerUpValue = best == null ? string.Empty : best.RawValue;
                    bestScore = score;
                    best = candidate;
                }
                else if (score > runnerUpScore)
                {
                    runnerUpScore = score;
                    runnerUpValue = candidate.RawValue;
                }
            }

            if (best == null)
            {
                return result;
            }

            result.RunnerUpValue = runnerUpValue;
            result.RunnerUpScore = runnerUpScore < 0d ? 0d : runnerUpScore;

            if (bestScore >= options.ReviewThreshold)
            {
                result.MatchedValue = best.RawValue;
                result.MatchedRowNumber = best.RowNumber;
                result.Score = bestScore;
                result.Outcome = bestScore >= options.AutoMatchThreshold
                    ? MatchOutcome.AutoMatched
                    : MatchOutcome.NeedsReview;
            }
            else
            {
                // Below the floor: report the score for triage, but claim no match.
                result.Score = bestScore;
                result.Outcome = MatchOutcome.NoMatch;
            }

            return result;
        }

        private static double Blend(
            double levenshteinRatio,
            double jaroWinkler,
            double tokenSet,
            FuzzyMatchOptions options,
            double weightSum)
        {
            return ((levenshteinRatio * options.LevenshteinWeight) +
                    (jaroWinkler * options.JaroWinklerWeight) +
                    (tokenSet * options.TokenSetWeight)) / weightSum;
        }

        /// <summary>Reorders a normalized value's tokens alphabetically.</summary>
        private static string SortTokens(string normalizedValue)
        {
            string[] tokens = SplitTokens(normalizedValue);

            if (tokens.Length < 2)
            {
                return normalizedValue ?? string.Empty;
            }

            Array.Sort(tokens, StringComparer.Ordinal);
            return string.Join(" ", tokens);
        }

        private static string[] SplitTokens(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
