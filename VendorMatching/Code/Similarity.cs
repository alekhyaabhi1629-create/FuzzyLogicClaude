using System;
using System.Collections.Generic;
using System.Text;

namespace VendorMatching.Code
{
    /// <summary>
    /// String distance / similarity primitives. Every public Ratio-style method returns
    /// a score in the 0..100 range so the weighted composite in <see cref="VendorMatchEngine"/>
    /// can mix them without further scaling.
    /// </summary>
    public static class Similarity
    {
        /// <summary>Classic Levenshtein edit distance, two-row dynamic programming.</summary>
        public static int Levenshtein(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? 0 : b.Length;
            if (string.IsNullOrEmpty(b)) return a.Length;

            // Keep the shorter string on the inner axis to minimise allocations.
            if (a.Length > b.Length)
            {
                string swap = a;
                a = b;
                b = swap;
            }

            int n = a.Length;
            int m = b.Length;
            int[] prev = new int[n + 1];
            int[] cur = new int[n + 1];

            for (int i = 0; i <= n; i++) prev[i] = i;

            for (int j = 1; j <= m; j++)
            {
                cur[0] = j;
                char cb = b[j - 1];
                for (int i = 1; i <= n; i++)
                {
                    int cost = a[i - 1] == cb ? 0 : 1;
                    int deletion = prev[i] + 1;
                    int insertion = cur[i - 1] + 1;
                    int substitution = prev[i - 1] + cost;
                    int min = deletion < insertion ? deletion : insertion;
                    cur[i] = min < substitution ? min : substitution;
                }
                int[] tmp = prev;
                prev = cur;
                cur = tmp;
            }

            return prev[n];
        }

        /// <summary>Levenshtein expressed as a 0..100 similarity.</summary>
        public static double Ratio(string a, string b)
        {
            bool aEmpty = string.IsNullOrEmpty(a);
            bool bEmpty = string.IsNullOrEmpty(b);
            if (aEmpty && bEmpty) return 100.0;
            if (aEmpty || bEmpty) return 0.0;
            if (string.Equals(a, b, StringComparison.Ordinal)) return 100.0;

            int distance = Levenshtein(a, b);
            int longest = a.Length > b.Length ? a.Length : b.Length;
            double score = 100.0 * (1.0 - (double)distance / longest);
            return score < 0.0 ? 0.0 : score;
        }

        /// <summary>Jaro similarity in the 0..1 range.</summary>
        public static double Jaro(string s1, string s2)
        {
            if (s1 == null || s2 == null) return 0.0;
            if (string.Equals(s1, s2, StringComparison.Ordinal)) return s1.Length == 0 ? 0.0 : 1.0;

            int len1 = s1.Length;
            int len2 = s2.Length;
            if (len1 == 0 || len2 == 0) return 0.0;

            int window = (len1 > len2 ? len1 : len2) / 2 - 1;
            if (window < 0) window = 0;

            bool[] matched1 = new bool[len1];
            bool[] matched2 = new bool[len2];
            int matches = 0;

            for (int i = 0; i < len1; i++)
            {
                int start = i - window;
                if (start < 0) start = 0;
                int end = i + window + 1;
                if (end > len2) end = len2;

                for (int j = start; j < end; j++)
                {
                    if (matched2[j]) continue;
                    if (s1[i] != s2[j]) continue;
                    matched1[i] = true;
                    matched2[j] = true;
                    matches++;
                    break;
                }
            }

            if (matches == 0) return 0.0;

            double transpositions = 0.0;
            int k = 0;
            for (int i = 0; i < len1; i++)
            {
                if (!matched1[i]) continue;
                while (k < len2 && !matched2[k]) k++;
                if (k < len2 && s1[i] != s2[k]) transpositions++;
                k++;
            }
            transpositions /= 2.0;

            double m = matches;
            return (m / len1 + m / len2 + (m - transpositions) / m) / 3.0;
        }

        /// <summary>Jaro-Winkler as a 0..100 similarity; rewards a shared prefix.</summary>
        public static double JaroWinklerRatio(string s1, string s2)
        {
            bool aEmpty = string.IsNullOrEmpty(s1);
            bool bEmpty = string.IsNullOrEmpty(s2);
            if (aEmpty && bEmpty) return 100.0;
            if (aEmpty || bEmpty) return 0.0;

            double jaro = Jaro(s1, s2);
            if (jaro < 0.7) return jaro * 100.0;

            int limit = s1.Length < s2.Length ? s1.Length : s2.Length;
            if (limit > 4) limit = 4;
            int prefix = 0;
            while (prefix < limit && s1[prefix] == s2[prefix]) prefix++;

            double jw = jaro + prefix * 0.1 * (1.0 - jaro);
            if (jw > 1.0) jw = 1.0;
            return jw * 100.0;
        }

        /// <summary>Ratio after sorting the tokens of each side; immune to word order.</summary>
        public static double TokenSortRatio(string[] tokens1, string[] tokens2)
        {
            return Ratio(JoinSorted(tokens1), JoinSorted(tokens2));
        }

        /// <summary>
        /// FuzzyWuzzy-style token set ratio: compares the shared tokens against each full
        /// side, so "ACME" scores highly against "ACME EUROPE DISTRIBUTION".
        /// </summary>
        public static double TokenSetRatio(string[] tokens1, string[] tokens2)
        {
            if (tokens1 == null || tokens2 == null) return 0.0;
            if (tokens1.Length == 0 && tokens2.Length == 0) return 100.0;
            if (tokens1.Length == 0 || tokens2.Length == 0) return 0.0;

            var set1 = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string t in tokens1) set1.Add(t);
            var set2 = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string t in tokens2) set2.Add(t);

            var intersection = new List<string>();
            var only1 = new List<string>();
            var only2 = new List<string>();

            foreach (string t in set1)
            {
                if (set2.Contains(t)) intersection.Add(t);
                else only1.Add(t);
            }
            foreach (string t in set2)
            {
                if (!set1.Contains(t)) only2.Add(t);
            }

            string shared = string.Join(" ", intersection.ToArray());
            string combined1 = (shared + " " + string.Join(" ", only1.ToArray())).Trim();
            string combined2 = (shared + " " + string.Join(" ", only2.ToArray())).Trim();

            double a = Ratio(shared, combined1);
            double b = Ratio(shared, combined2);
            double c = Ratio(combined1, combined2);

            double best = a > b ? a : b;
            return best > c ? best : c;
        }

        /// <summary>
        /// Best alignment of the shorter string inside the longer one. Catches cases where the
        /// invoice carries a trading name that is a substring of the SAP legal name.
        /// </summary>
        public static double PartialRatio(string a, string b)
        {
            bool aEmpty = string.IsNullOrEmpty(a);
            bool bEmpty = string.IsNullOrEmpty(b);
            if (aEmpty && bEmpty) return 100.0;
            if (aEmpty || bEmpty) return 0.0;

            string shorter = a.Length <= b.Length ? a : b;
            string longer = a.Length <= b.Length ? b : a;

            if (shorter.Length == longer.Length) return Ratio(shorter, longer);

            // Guard against pathological inputs; vendor names are short in practice.
            const int MaxLongLength = 256;
            if (longer.Length > MaxLongLength) longer = longer.Substring(0, MaxLongLength);

            double best = 0.0;
            int windows = longer.Length - shorter.Length;
            for (int start = 0; start <= windows; start++)
            {
                double score = Ratio(shorter, longer.Substring(start, shorter.Length));
                if (score > best) best = score;
                if (best >= 100.0) break;
            }
            return best;
        }

        /// <summary>Soundex code of a single token, used as a phonetic blocking key.</summary>
        public static string Soundex(string token)
        {
            if (string.IsNullOrEmpty(token)) return string.Empty;

            var sb = new StringBuilder();
            char first = char.ToUpperInvariant(token[0]);
            if (first < 'A' || first > 'Z') return string.Empty;
            sb.Append(first);

            char previousCode = SoundexCode(first);
            for (int i = 1; i < token.Length && sb.Length < 4; i++)
            {
                char c = char.ToUpperInvariant(token[i]);
                if (c < 'A' || c > 'Z') continue;

                char code = SoundexCode(c);
                if (code != '0' && code != previousCode) sb.Append(code);

                // H and W are transparent: they do not reset the "previous code" state.
                if (c != 'H' && c != 'W') previousCode = code;
            }

            while (sb.Length < 4) sb.Append('0');
            return sb.ToString();
        }

        private static char SoundexCode(char c)
        {
            switch (c)
            {
                case 'B':
                case 'F':
                case 'P':
                case 'V':
                    return '1';
                case 'C':
                case 'G':
                case 'J':
                case 'K':
                case 'Q':
                case 'S':
                case 'X':
                case 'Z':
                    return '2';
                case 'D':
                case 'T':
                    return '3';
                case 'L':
                    return '4';
                case 'M':
                case 'N':
                    return '5';
                case 'R':
                    return '6';
                default:
                    return '0';
            }
        }

        /// <summary>
        /// True when one side is the initialism of the other, e.g. "IBM" against
        /// "INTERNATIONAL BUSINESS MACHINES".
        /// </summary>
        public static bool IsAcronymMatch(NormalizedName a, NormalizedName b)
        {
            return IsAcronymOf(a, b) || IsAcronymOf(b, a);
        }

        private static bool IsAcronymOf(NormalizedName candidateAcronym, NormalizedName expansion)
        {
            if (candidateAcronym == null || expansion == null) return false;
            if (candidateAcronym.Tokens == null || expansion.Tokens == null) return false;
            if (candidateAcronym.Tokens.Length != 1) return false;
            if (expansion.Tokens.Length < 2) return false;

            string acronym = candidateAcronym.Tokens[0];
            if (acronym.Length < 2 || acronym.Length > 6) return false;

            return string.Equals(acronym, expansion.Acronym, StringComparison.Ordinal);
        }

        private static string JoinSorted(string[] tokens)
        {
            if (tokens == null || tokens.Length == 0) return string.Empty;
            string[] copy = new string[tokens.Length];
            Array.Copy(tokens, copy, tokens.Length);
            Array.Sort(copy, StringComparer.Ordinal);
            return string.Join(" ", copy);
        }
    }
}
