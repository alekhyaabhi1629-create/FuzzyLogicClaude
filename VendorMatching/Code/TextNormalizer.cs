using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VendorMatching.Code
{
    /// <summary>
    /// Turns a raw vendor name into the comparable forms held by <see cref="NormalizedName"/>.
    /// Normalisation is where most of the accuracy comes from: by the time two names reach the
    /// scorer, casing, punctuation, accents, legal forms and common abbreviations are gone.
    /// </summary>
    public static class TextNormalizer
    {
        public static NormalizedName Normalize(string raw, MatchConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (string.IsNullOrWhiteSpace(raw)) return NormalizedName.Empty(raw);

            string folded = FoldDiacritics(raw);
            string collapsed = CollapseDottedInitials(folded);
            string expanded = ExpandSymbols(collapsed);
            string cleaned = StripPunctuation(expanded);

            string[] rawTokens = cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var normalizedTokens = new List<string>(rawTokens.Length);
            foreach (string token in rawTokens)
            {
                string upper = token.ToUpperInvariant();
                string alias;
                if (config.Aliases != null && config.Aliases.TryGetValue(upper, out alias) && !string.IsNullOrEmpty(alias))
                {
                    upper = alias;
                }
                normalizedTokens.Add(upper);
            }

            var coreTokens = new List<string>(normalizedTokens.Count);
            foreach (string token in normalizedTokens)
            {
                if (config.LegalFormSet.Contains(token)) continue;
                if (config.NoiseWordSet.Contains(token)) continue;
                coreTokens.Add(token);
            }

            coreTokens = DropStrayInitials(coreTokens);

            // A name made up only of legal forms (rare, but it happens with bad extracts)
            // would otherwise normalise to nothing and match everything.
            if (coreTokens.Count == 0) coreTokens.AddRange(normalizedTokens);

            string[] tokens = coreTokens.ToArray();
            string core = string.Join(" ", tokens);

            var result = new NormalizedName
            {
                Original = raw,
                Normalized = string.Join(" ", normalizedTokens.ToArray()),
                Core = core,
                Tokens = tokens,
                TokenSorted = JoinSorted(tokens),
                Acronym = BuildAcronym(tokens),
                Phonetic = BuildPhoneticKey(tokens),
                Digits = ExtractDigitRuns(core)
            };

            return result;
        }

        /// <summary>
        /// Removes accents so "Müller" and "Mueller"/"Muller" converge. German sharp s and the
        /// umlauts get their conventional two-letter expansions before the generic fold, because
        /// SAP masters and invoice OCR disagree on which form they use.
        /// </summary>
        public static string FoldDiacritics(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var pre = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case 'ß': pre.Append("SS"); break;
                    case 'Ä': pre.Append("AE"); break;
                    case 'ä': pre.Append("ae"); break;
                    case 'Ö': pre.Append("OE"); break;
                    case 'ö': pre.Append("oe"); break;
                    case 'Ü': pre.Append("UE"); break;
                    case 'ü': pre.Append("ue"); break;
                    case 'Æ': pre.Append("AE"); break;
                    case 'æ': pre.Append("ae"); break;
                    case 'Ø': pre.Append("O"); break;
                    case 'ø': pre.Append("o"); break;
                    case 'Å': pre.Append("AA"); break;
                    case 'å': pre.Append("aa"); break;
                    case 'Đ': pre.Append("D"); break;
                    case 'đ': pre.Append("d"); break;
                    case 'Ł': pre.Append("L"); break;
                    case 'ł': pre.Append("l"); break;
                    default: pre.Append(c); break;
                }
            }

            string decomposed = pre.ToString().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(c);
                }
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>
        /// Joins runs of dotted initials so "Acme B.V." and "Acme BV" agree, and "J.P. Morgan"
        /// lines up with "JP Morgan". Only runs of two or more letter-dot pairs are collapsed,
        /// which leaves ordinary abbreviations such as "Co." alone.
        /// </summary>
        public static string CollapseDottedInitials(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length);
            int i = 0;
            while (i < value.Length)
            {
                bool atWordStart = i == 0 || !char.IsLetterOrDigit(value[i - 1]);
                if (atWordStart && char.IsLetter(value[i]))
                {
                    var letters = new StringBuilder(4);
                    int j = i;
                    while (j + 1 < value.Length && char.IsLetter(value[j]) && value[j + 1] == '.')
                    {
                        letters.Append(value[j]);
                        j += 2;
                    }

                    if (letters.Length >= 2)
                    {
                        sb.Append(letters);
                        i = j;
                        continue;
                    }
                }

                sb.Append(value[i]);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Removes leftover single letters ("ACME EUROPE DISTRIBUTION B V", "KOWALSKI I SYNOWIE Z O O")
        /// once enough real words survive. Guarding on two remaining multi-character tokens keeps
        /// genuinely short names such as "A &amp; E Networks" intact.
        /// </summary>
        private static List<string> DropStrayInitials(List<string> tokens)
        {
            int substantialTokens = 0;
            foreach (string token in tokens)
            {
                if (token.Length >= 2) substantialTokens++;
            }
            if (substantialTokens < 2) return tokens;

            var kept = new List<string>(tokens.Count);
            foreach (string token in tokens)
            {
                if (token.Length == 1 && char.IsLetter(token[0])) continue;
                kept.Add(token);
            }
            return kept;
        }

        /// <summary>Turns symbols that carry meaning into words before punctuation is dropped.</summary>
        private static string ExpandSymbols(string value)
        {
            var sb = new StringBuilder(value.Length + 16);
            foreach (char c in value)
            {
                if (c == '&' || c == '+')
                {
                    sb.Append(" AND ");
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>Keeps letters and digits, turns everything else into a separator.</summary>
        private static string StripPunctuation(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else sb.Append(' ');
            }
            return sb.ToString();
        }

        private static string JoinSorted(string[] tokens)
        {
            if (tokens == null || tokens.Length == 0) return string.Empty;
            string[] copy = new string[tokens.Length];
            Array.Copy(tokens, copy, tokens.Length);
            Array.Sort(copy, StringComparer.Ordinal);
            return string.Join(" ", copy);
        }

        private static string BuildAcronym(string[] tokens)
        {
            if (tokens == null || tokens.Length == 0) return string.Empty;
            var sb = new StringBuilder(tokens.Length);
            foreach (string token in tokens)
            {
                if (!string.IsNullOrEmpty(token)) sb.Append(token[0]);
            }
            return sb.ToString();
        }

        /// <summary>Soundex of the first two core tokens - enough to block on, cheap to compute.</summary>
        private static string BuildPhoneticKey(string[] tokens)
        {
            if (tokens == null || tokens.Length == 0) return string.Empty;
            var sb = new StringBuilder(9);
            int take = tokens.Length < 2 ? tokens.Length : 2;
            for (int i = 0; i < take; i++)
            {
                if (i > 0) sb.Append('-');
                sb.Append(Similarity.Soundex(tokens[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// "SIGMA 3 LABS" -> ["3"]. Numbers are highly discriminating in vendor names
        /// (subsidiary numbering, plant numbers), so a mismatch is penalised later.
        /// </summary>
        private static string[] ExtractDigitRuns(string value)
        {
            if (string.IsNullOrEmpty(value)) return new string[0];

            var runs = new List<string>();
            var current = new StringBuilder();
            foreach (char c in value)
            {
                if (char.IsDigit(c))
                {
                    current.Append(c);
                }
                else if (current.Length > 0)
                {
                    runs.Add(current.ToString());
                    current.Length = 0;
                }
            }
            if (current.Length > 0) runs.Add(current.ToString());
            return runs.ToArray();
        }

        /// <summary>
        /// Reduces a tax / VAT identifier to comparable characters: upper case, alphanumerics only.
        /// Country prefixes on VAT numbers are kept because they are part of the identifier.
        /// </summary>
        public static string NormalizeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }
    }
}
