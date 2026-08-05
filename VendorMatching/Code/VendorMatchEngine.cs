using System;
using System.Collections.Generic;

namespace VendorMatching.Code
{
    /// <summary>
    /// Matches invoice vendor names against the SAP vendor master.
    ///
    /// The engine is built once per run over the vendor master and then queried per invoice.
    /// Instead of comparing every invoice against every vendor (which is O(N*M) and unusable on a
    /// real master of 50k+ records), it builds a blocking index of cheap keys - exact name,
    /// sorted tokens, acronym, name prefix, Soundex and rare tokens - and only scores the vendors
    /// that share at least one key with the invoice name.
    /// </summary>
    public sealed class VendorMatchEngine
    {
        private const string KeyExact = "X:";
        private const string KeySorted = "S:";
        private const string KeyAcronym = "A:";
        private const string KeyPrefix = "F:";
        private const string KeySuffix = "E:";
        private const string KeyPhonetic = "P:";
        private const string KeyToken = "T:";

        private readonly MatchConfig _config;
        private readonly List<SapVendor> _vendors;
        private readonly Dictionary<string, List<int>> _index;
        private readonly Dictionary<string, List<int>> _identifierIndex;

        public VendorMatchEngine(IEnumerable<SapVendor> vendors, MatchConfig config)
        {
            if (vendors == null) throw new ArgumentNullException("vendors");
            if (config == null) throw new ArgumentNullException("config");

            _config = config;
            _vendors = new List<SapVendor>();
            _index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            _identifierIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);

            foreach (SapVendor vendor in vendors)
            {
                if (vendor == null) continue;
                if (_config.Adjustments.ExcludeBlockedVendors && vendor.IsBlocked) continue;
                if (vendor.Normalized == null) vendor.Normalized = TextNormalizer.Normalize(vendor.DisplayName, _config);
                if (vendor.NormalizedPrimary == null) vendor.NormalizedPrimary = TextNormalizer.Normalize(vendor.Name, _config);
                _vendors.Add(vendor);
            }

            BuildIndex();
        }

        public int VendorCount
        {
            get { return _vendors.Count; }
        }

        private void BuildIndex()
        {
            // Tokens that appear all over the master (SERVICES, GROUP, EUROPE) make terrible
            // blocking keys, so measure document frequency first and index only the rare ones.
            var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SapVendor vendor in _vendors)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                CollectTokens(vendor.Normalized, seen);
                CollectTokens(vendor.NormalizedPrimary, seen);
                foreach (string token in seen)
                {
                    int count;
                    documentFrequency.TryGetValue(token, out count);
                    documentFrequency[token] = count + 1;
                }
            }

            int maxDocumentFrequency = (int)Math.Ceiling(_config.Index.MaxTokenDocumentFrequency * _vendors.Count);
            if (maxDocumentFrequency < _config.Index.MinTokenDocumentFrequency)
            {
                maxDocumentFrequency = _config.Index.MinTokenDocumentFrequency;
            }
            if (maxDocumentFrequency < 1) maxDocumentFrequency = 1;

            for (int position = 0; position < _vendors.Count; position++)
            {
                SapVendor vendor = _vendors[position];

                AddNameKeys(vendor.Normalized, position, documentFrequency, maxDocumentFrequency);
                AddNameKeys(vendor.NormalizedPrimary, position, documentFrequency, maxDocumentFrequency);

                AddIdentifier(vendor.TaxId, position);
                AddIdentifier(vendor.VatId, position);
            }
        }

        private static void CollectTokens(NormalizedName name, HashSet<string> into)
        {
            if (name == null || name.Tokens == null) return;
            foreach (string token in name.Tokens)
            {
                if (token.Length >= 3) into.Add(token);
            }
        }

        private void AddNameKeys(NormalizedName name, int position, Dictionary<string, int> documentFrequency, int maxDocumentFrequency)
        {
            if (name == null || name.IsEmpty) return;

            if (!string.IsNullOrEmpty(name.Core)) AddToIndex(KeyExact + name.Core, position);
            if (!string.IsNullOrEmpty(name.TokenSorted)) AddToIndex(KeySorted + name.TokenSorted, position);
            if (!string.IsNullOrEmpty(name.Acronym) && name.Acronym.Length >= 2) AddToIndex(KeyAcronym + name.Acronym, position);
            if (!string.IsNullOrEmpty(name.Phonetic)) AddToIndex(KeyPhonetic + name.Phonetic, position);

            string prefix = BuildPrefixKey(name);
            if (!string.IsNullOrEmpty(prefix)) AddToIndex(KeyPrefix + prefix, position);

            string suffix = BuildSuffixKey(name);
            if (!string.IsNullOrEmpty(suffix)) AddToIndex(KeySuffix + suffix, position);

            if (name.Tokens != null)
            {
                foreach (string token in name.Tokens)
                {
                    if (token.Length < 3) continue;
                    int frequency;
                    if (documentFrequency.TryGetValue(token, out frequency) && frequency > maxDocumentFrequency) continue;
                    AddToIndex(KeyToken + token, position);
                }
            }
        }

        private string BuildPrefixKey(NormalizedName name)
        {
            if (name == null || string.IsNullOrEmpty(name.Core)) return string.Empty;
            string compact = name.Core.Replace(" ", string.Empty);
            int length = _config.Index.PrefixKeyLength;
            if (compact.Length < length) return compact;
            return compact.Substring(0, length);
        }

        /// <summary>
        /// Mirror of the prefix key taken from the end of the name. A typo in the first characters
        /// ("Sicmens" for "Siemens") destroys both the prefix key and the Soundex code; the suffix
        /// key keeps such rows reachable without resorting to a full scan.
        /// </summary>
        private string BuildSuffixKey(NormalizedName name)
        {
            if (name == null || string.IsNullOrEmpty(name.Core)) return string.Empty;
            string compact = name.Core.Replace(" ", string.Empty);
            int length = _config.Index.PrefixKeyLength;
            if (compact.Length < length) return string.Empty;
            return compact.Substring(compact.Length - length);
        }

        private void AddToIndex(string key, int position)
        {
            List<int> bucket;
            if (!_index.TryGetValue(key, out bucket))
            {
                bucket = new List<int>();
                _index[key] = bucket;
            }
            if (bucket.Count == 0 || bucket[bucket.Count - 1] != position) bucket.Add(position);
        }

        private void AddIdentifier(string rawIdentifier, int position)
        {
            string identifier = TextNormalizer.NormalizeIdentifier(rawIdentifier);
            if (identifier.Length < 5) return;

            List<int> bucket;
            if (!_identifierIndex.TryGetValue(identifier, out bucket))
            {
                bucket = new List<int>();
                _identifierIndex[identifier] = bucket;
            }
            if (!bucket.Contains(position)) bucket.Add(position);
        }

        public List<MatchResult> MatchAll(IEnumerable<InvoiceRecord> invoices)
        {
            var results = new List<MatchResult>();
            if (invoices == null) return results;
            foreach (InvoiceRecord invoice in invoices)
            {
                results.Add(Match(invoice));
            }
            return results;
        }

        public MatchResult Match(InvoiceRecord invoice)
        {
            if (invoice == null) throw new ArgumentNullException("invoice");
            if (invoice.Normalized == null) invoice.Normalized = TextNormalizer.Normalize(invoice.VendorName, _config);

            var result = new MatchResult { Invoice = invoice };

            if (string.IsNullOrWhiteSpace(invoice.VendorName))
            {
                result.Decision = MatchDecision.NoMatch;
                result.Reason = "Invoice row has no vendor name.";
                return result;
            }

            List<int> candidatePositions = GatherCandidates(invoice);
            result.CandidatesEvaluated = candidatePositions.Count;

            if (candidatePositions.Count == 0)
            {
                result.Decision = MatchDecision.NoMatch;
                result.Reason = "No candidate vendor shared a blocking key with this name.";
                return result;
            }

            var scored = new List<MatchCandidate>(candidatePositions.Count);
            foreach (int position in candidatePositions)
            {
                scored.Add(ScorePair(invoice, _vendors[position]));
            }

            scored.Sort(CompareCandidatesDescending);

            result.Best = scored[0];
            int alternatives = _config.AlternativesToKeep;
            for (int i = 1; i < scored.Count && result.Alternatives.Count < alternatives; i++)
            {
                result.Alternatives.Add(scored[i]);
            }

            ApplyDecision(result, scored);
            return result;
        }

        private static int CompareCandidatesDescending(MatchCandidate a, MatchCandidate b)
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;

            // Deterministic tie-break so reruns over the same data produce identical output.
            int byType = ((int)a.Type).CompareTo((int)b.Type);
            if (byType != 0) return byType;

            return string.CompareOrdinal(a.Vendor.VendorCode, b.Vendor.VendorCode);
        }

        private void ApplyDecision(MatchResult result, List<MatchCandidate> scored)
        {
            MatchCandidate best = result.Best;

            if (best.Score >= _config.Thresholds.AutoMatch)
            {
                result.Decision = MatchDecision.AutoMatch;
                result.Reason = best.Reason;
            }
            else if (best.Score >= _config.Thresholds.Review)
            {
                result.Decision = MatchDecision.NeedsReview;
                result.Reason = "Score " + Format(best.Score) + " is below the auto-match threshold of "
                    + Format(_config.Thresholds.AutoMatch) + ".";
            }
            else
            {
                result.Decision = MatchDecision.NoMatch;
                result.Reason = "Best score " + Format(best.Score) + " is below the review threshold of "
                    + Format(_config.Thresholds.Review) + ".";
                return;
            }

            // A clear winner matters as much as a high score: if the runner-up is a different
            // vendor sitting within the ambiguity margin, a human has to pick.
            for (int i = 1; i < scored.Count; i++)
            {
                MatchCandidate runnerUp = scored[i];
                if (string.Equals(runnerUp.Vendor.VendorCode, best.Vendor.VendorCode, StringComparison.OrdinalIgnoreCase)) continue;

                if (best.Score - runnerUp.Score < _config.Thresholds.AmbiguityMargin)
                {
                    result.IsAmbiguous = true;
                    if (result.Decision == MatchDecision.AutoMatch) result.Decision = MatchDecision.NeedsReview;
                    result.Reason = "Ambiguous: " + runnerUp.Vendor.VendorCode + " scores " + Format(runnerUp.Score)
                        + " against " + Format(best.Score) + " for " + best.Vendor.VendorCode + ".";
                }
                break;
            }
        }

        private List<int> GatherCandidates(InvoiceRecord invoice)
        {
            var ordered = new List<int>();
            var seen = new HashSet<int>();
            int limit = _config.Index.MaxCandidatesPerInvoice;

            // Strongest keys first, so the candidate cap can only ever drop weak candidates.
            AddIdentifierCandidates(invoice.TaxId, ordered, seen, limit);
            AddIdentifierCandidates(invoice.VatId, ordered, seen, limit);

            NormalizedName name = invoice.Normalized;
            if (name != null && !name.IsEmpty)
            {
                AddBucket(KeyExact + name.Core, ordered, seen, limit);
                AddBucket(KeySorted + name.TokenSorted, ordered, seen, limit);

                if (!string.IsNullOrEmpty(name.Acronym) && name.Acronym.Length >= 2)
                {
                    AddBucket(KeyAcronym + name.Acronym, ordered, seen, limit);
                }
                if (name.Tokens != null && name.Tokens.Length == 1 && name.Tokens[0].Length >= 2 && name.Tokens[0].Length <= 6)
                {
                    // The invoice may carry the acronym while SAP carries the expansion.
                    AddBucket(KeyAcronym + name.Tokens[0], ordered, seen, limit);
                }

                string prefix = BuildPrefixKey(name);
                if (!string.IsNullOrEmpty(prefix)) AddBucket(KeyPrefix + prefix, ordered, seen, limit);

                string suffix = BuildSuffixKey(name);
                if (!string.IsNullOrEmpty(suffix)) AddBucket(KeySuffix + suffix, ordered, seen, limit);

                AddBucket(KeyPhonetic + name.Phonetic, ordered, seen, limit);

                if (name.Tokens != null)
                {
                    foreach (string token in name.Tokens)
                    {
                        if (token.Length < 3) continue;
                        AddBucket(KeyToken + token, ordered, seen, limit);
                    }
                }
            }

            if (ordered.Count == 0 && _config.Index.FullScanFallback && _vendors.Count <= _config.Index.FullScanMaxVendors)
            {
                for (int i = 0; i < _vendors.Count && ordered.Count < limit; i++)
                {
                    ordered.Add(i);
                }
            }

            return ordered;
        }

        private void AddIdentifierCandidates(string rawIdentifier, List<int> ordered, HashSet<int> seen, int limit)
        {
            string identifier = TextNormalizer.NormalizeIdentifier(rawIdentifier);
            if (identifier.Length < 5) return;

            List<int> bucket;
            if (!_identifierIndex.TryGetValue(identifier, out bucket)) return;

            foreach (int position in bucket)
            {
                if (ordered.Count >= limit) return;
                if (seen.Add(position)) ordered.Add(position);
            }
        }

        private void AddBucket(string key, List<int> ordered, HashSet<int> seen, int limit)
        {
            if (string.IsNullOrEmpty(key) || key.Length <= 2) return;

            List<int> bucket;
            if (!_index.TryGetValue(key, out bucket)) return;

            foreach (int position in bucket)
            {
                if (ordered.Count >= limit) return;
                if (seen.Add(position)) ordered.Add(position);
            }
        }

        /// <summary>Scores one invoice name against one SAP vendor and explains the outcome.</summary>
        public MatchCandidate ScorePair(InvoiceRecord invoice, SapVendor vendor)
        {
            var breakdown = new ScoreBreakdown();
            var candidate = new MatchCandidate
            {
                Vendor = vendor,
                Breakdown = breakdown,
                Type = MatchType.Fuzzy
            };

            string sharedIdentifier = FindSharedIdentifier(invoice, vendor);
            if (sharedIdentifier != null)
            {
                breakdown.Base = 100.0;
                breakdown.Final = 100.0;
                candidate.Score = 100.0;
                candidate.Type = MatchType.TaxId;
                candidate.Reason = "Tax/VAT identifier " + sharedIdentifier + " matches exactly.";
                return candidate;
            }

            // SAP frequently splits a long name over NAME1/NAME2; score against both forms and
            // keep whichever representation of the vendor fits the invoice better.
            ScoreBreakdown viaCombined = ScoreNames(invoice.Normalized, vendor.Normalized);
            ScoreBreakdown viaPrimary = ScoreNames(invoice.Normalized, vendor.NormalizedPrimary);
            ScoreBreakdown nameScore = viaCombined.Base >= viaPrimary.Base ? viaCombined : viaPrimary;

            breakdown.Levenshtein = nameScore.Levenshtein;
            breakdown.JaroWinkler = nameScore.JaroWinkler;
            breakdown.TokenSort = nameScore.TokenSort;
            breakdown.TokenSet = nameScore.TokenSet;
            breakdown.Partial = nameScore.Partial;
            breakdown.Base = nameScore.Base;

            bool exact = IsExactCore(invoice.Normalized, vendor.Normalized) || IsExactCore(invoice.Normalized, vendor.NormalizedPrimary);
            bool acronym = !exact && (Similarity.IsAcronymMatch(invoice.Normalized, vendor.Normalized)
                || Similarity.IsAcronymMatch(invoice.Normalized, vendor.NormalizedPrimary));

            if (exact)
            {
                candidate.Type = MatchType.ExactNormalized;
                breakdown.Base = 100.0;
                candidate.Reason = "Names are identical after normalization.";
            }
            else if (acronym)
            {
                candidate.Type = MatchType.Acronym;
                if (_config.Adjustments.AcronymScore > breakdown.Base) breakdown.Base = _config.Adjustments.AcronymScore;
                candidate.Reason = "One name is the initialism of the other.";
            }

            ApplyAdjustments(invoice, vendor, breakdown);

            double final = breakdown.Base + breakdown.Bonus - breakdown.Penalty;
            if (final > 100.0) final = 100.0;
            if (final < 0.0) final = 0.0;
            breakdown.Final = final;
            candidate.Score = final;

            if (string.IsNullOrEmpty(candidate.Reason))
            {
                candidate.Reason = "Fuzzy name similarity " + Format(breakdown.Base) + ".";
            }

            return candidate;
        }

        private static bool IsExactCore(NormalizedName a, NormalizedName b)
        {
            if (a == null || b == null) return false;
            if (string.IsNullOrEmpty(a.Core) || string.IsNullOrEmpty(b.Core)) return false;
            return string.Equals(a.Core, b.Core, StringComparison.Ordinal);
        }

        private ScoreBreakdown ScoreNames(NormalizedName invoiceName, NormalizedName vendorName)
        {
            var breakdown = new ScoreBreakdown();
            if (invoiceName == null || vendorName == null || invoiceName.IsEmpty || vendorName.IsEmpty)
            {
                return breakdown;
            }

            breakdown.Levenshtein = Similarity.Ratio(invoiceName.Core, vendorName.Core);
            breakdown.JaroWinkler = Similarity.JaroWinklerRatio(invoiceName.Core, vendorName.Core);
            breakdown.TokenSort = Similarity.TokenSortRatio(invoiceName.Tokens, vendorName.Tokens);
            breakdown.TokenSet = Similarity.TokenSetRatio(invoiceName.Tokens, vendorName.Tokens);
            breakdown.Partial = Similarity.PartialRatio(invoiceName.Core, vendorName.Core);

            Weights weights = _config.Weights;
            double weighted = breakdown.Levenshtein * weights.Levenshtein
                + breakdown.JaroWinkler * weights.JaroWinkler
                + breakdown.TokenSort * weights.TokenSort
                + breakdown.TokenSet * weights.TokenSet
                + breakdown.Partial * weights.Partial;

            double mean = weighted / weights.Total;

            // The weighted mean is deliberately conservative, which under-scores a name that is a
            // correct prefix/subset of the SAP name. Let a single strong order-independent signal
            // override it, discounted so it stays below what a full match would earn.
            double shortcut = 0.0;
            ShortcutFactors factors = _config.ShortcutFactors;
            int minimumTokens = invoiceName.Tokens.Length < vendorName.Tokens.Length
                ? invoiceName.Tokens.Length
                : vendorName.Tokens.Length;

            if (minimumTokens >= factors.MinimumTokens)
            {
                shortcut = breakdown.TokenSet * factors.TokenSet;
                double bySort = breakdown.TokenSort * factors.TokenSort;
                if (bySort > shortcut) shortcut = bySort;
                double byPartial = breakdown.Partial * factors.Partial;
                if (byPartial > shortcut) shortcut = byPartial;
            }

            breakdown.Base = mean > shortcut ? mean : shortcut;
            return breakdown;
        }

        private void ApplyAdjustments(InvoiceRecord invoice, SapVendor vendor, ScoreBreakdown breakdown)
        {
            Adjustments adjustments = _config.Adjustments;

            string invoiceCountry = Canonical(invoice.Country);
            string vendorCountry = Canonical(vendor.Country);
            if (invoiceCountry.Length > 0 && vendorCountry.Length > 0)
            {
                if (string.Equals(invoiceCountry, vendorCountry, StringComparison.Ordinal))
                {
                    breakdown.Bonus += adjustments.CountryMatchBonus;
                }
                else if (invoiceCountry.Length == 2 && vendorCountry.Length == 2)
                {
                    // Only penalise when both sides use ISO codes; "DE" vs "Germany" is not a conflict.
                    breakdown.Penalty += adjustments.CountryMismatchPenalty;
                }
            }

            string invoiceCity = Canonical(invoice.City);
            string vendorCity = Canonical(vendor.City);
            if (invoiceCity.Length > 0 && vendorCity.Length > 0
                && string.Equals(invoiceCity, vendorCity, StringComparison.Ordinal))
            {
                breakdown.Bonus += adjustments.CityMatchBonus;
            }

            if (HasCommonIdentifierSuffix(invoice, vendor))
            {
                breakdown.Bonus += adjustments.TaxIdSuffixBonus;
            }

            if (DigitsConflict(invoice.Normalized, vendor.Normalized) && DigitsConflict(invoice.Normalized, vendor.NormalizedPrimary))
            {
                breakdown.Penalty += adjustments.DigitMismatchPenalty;
            }
        }

        private static string Canonical(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return TextNormalizer.NormalizeIdentifier(TextNormalizer.FoldDiacritics(value));
        }

        /// <summary>Returns the identifier shared by both sides, or null when there is none.</summary>
        private static string FindSharedIdentifier(InvoiceRecord invoice, SapVendor vendor)
        {
            string[] invoiceIds = { TextNormalizer.NormalizeIdentifier(invoice.TaxId), TextNormalizer.NormalizeIdentifier(invoice.VatId) };
            string[] vendorIds = { TextNormalizer.NormalizeIdentifier(vendor.TaxId), TextNormalizer.NormalizeIdentifier(vendor.VatId) };

            foreach (string invoiceId in invoiceIds)
            {
                if (invoiceId.Length < 5) continue;
                foreach (string vendorId in vendorIds)
                {
                    if (vendorId.Length < 5) continue;
                    if (string.Equals(invoiceId, vendorId, StringComparison.Ordinal)) return invoiceId;
                }
            }
            return null;
        }

        /// <summary>
        /// True when the identifiers differ but end in the same six characters - typical of a
        /// VAT number written with and without its country prefix or check digits.
        /// </summary>
        private static bool HasCommonIdentifierSuffix(InvoiceRecord invoice, SapVendor vendor)
        {
            const int SuffixLength = 6;
            string[] invoiceIds = { TextNormalizer.NormalizeIdentifier(invoice.TaxId), TextNormalizer.NormalizeIdentifier(invoice.VatId) };
            string[] vendorIds = { TextNormalizer.NormalizeIdentifier(vendor.TaxId), TextNormalizer.NormalizeIdentifier(vendor.VatId) };

            foreach (string invoiceId in invoiceIds)
            {
                if (invoiceId.Length < SuffixLength) continue;
                string invoiceSuffix = invoiceId.Substring(invoiceId.Length - SuffixLength);

                foreach (string vendorId in vendorIds)
                {
                    if (vendorId.Length < SuffixLength) continue;
                    if (string.Equals(invoiceId, vendorId, StringComparison.Ordinal)) continue;

                    string vendorSuffix = vendorId.Substring(vendorId.Length - SuffixLength);
                    if (string.Equals(invoiceSuffix, vendorSuffix, StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        private static bool DigitsConflict(NormalizedName a, NormalizedName b)
        {
            if (a == null || b == null) return false;
            string[] left = a.Digits ?? new string[0];
            string[] right = b.Digits ?? new string[0];
            if (left.Length == 0 && right.Length == 0) return false;
            if (left.Length == 0 || right.Length == 0) return true;

            var leftSet = new HashSet<string>(left, StringComparer.Ordinal);
            var rightSet = new HashSet<string>(right, StringComparer.Ordinal);
            return !leftSet.SetEquals(rightSet);
        }

        private static string Format(double value)
        {
            return value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
