# How the vendor matching works

The problem: an invoice says `MUELLER PRAEZISIONSTECHNIK GMBH`, SAP says
`Müller Präzisionstechnik GmbH`, and they are the same company. Multiply that by
transliteration, legal forms, trading names, OCR typos, abbreviations, renamed vendors and
duplicate master records, and exact string comparison matches almost nothing useful.

Every stage below exists to remove one class of difference before two names are compared.

---

## 1. Normalization

`TextNormalizer.Normalize` turns a raw name into the forms the scorer uses. In order:

| Step | Example |
| --- | --- |
| Transliterate DACH/Nordic characters | `Müller` → `MUELLER`, `Straße` → `STRASSE` |
| Fold remaining diacritics (Unicode NFD) | `Établissements` → `ETABLISSEMENTS` |
| Collapse dotted initials | `B.V.` → `BV`, `J.P. Morgan` → `JP MORGAN` |
| Expand meaningful symbols | `&` and `+` → the word `AND` |
| Strip punctuation, upper-case, split | `Acme, Inc.` → `ACME INC` |
| Expand abbreviations via the alias table | `Pharma` → `PHARMACEUTICALS`, `Intl` → `INTERNATIONAL` |
| Remove legal forms | `GMBH`, `LTD`, `SARL`, `PTE`, `SP Z OO`, … |
| Remove noise words | `THE`, `AND`, `UND`, `ET`, `OF`, `DE`, `LA`, … |
| Drop stray single letters | `ACME EUROPE DISTRIBUTION B V` → `ACME EUROPE DISTRIBUTION` |

Two guards keep the pipeline from erasing a name entirely:

- If every token is a legal form (`"GmbH"` alone), the tokens are kept rather than emptied —
  otherwise the record would match everything.
- Single letters are only dropped when at least two multi-character tokens survive, so
  `A & E Networks` stays `A E NETWORKS`.

The result carries several derived forms, each used later:

| Field | Purpose |
| --- | --- |
| `Core` | The comparable name, e.g. `ACME EUROPE DISTRIBUTION` |
| `Tokens` | Core split into words |
| `TokenSorted` | Tokens sorted alphabetically — word-order-independent key |
| `Acronym` | Token initials, e.g. `IBM` |
| `Phonetic` | Soundex of the first two tokens — blocking key |
| `Digits` | Digit runs, e.g. `SIGMA 3 LABS` → `["3"]` |

---

## 2. Candidate generation (blocking)

Comparing 20,000 invoices against a 50,000-row vendor master is 10⁹ string comparisons. The
engine instead indexes each SAP vendor under several cheap keys and only scores vendors that
share at least one key with the invoice name.

Keys, queried strongest-first so the candidate cap can only ever discard weak candidates:

| Prefix | Key | Catches |
| --- | --- | --- |
| — | Normalized tax / VAT identifier | Renamed vendors, identical companies |
| `X:` | Exact core name | Clean matches |
| `S:` | Sorted tokens | Word-order differences |
| `A:` | Acronym | `IBM` ↔ `International Business Machines` |
| `F:` | First 4 characters of the compact core | Typos in the tail |
| `E:` | Last 4 characters of the compact core | Typos in the head (`Sicmens` ↔ `Siemens`) |
| `P:` | Soundex of the first two tokens | Phonetic spelling variants |
| `T:` | Individual rare tokens | Partial and reordered names |

Token keys are filtered by document frequency: a token appearing in more than
`maxTokenDocumentFrequency` of the master (5% by default) is too common to narrow anything
down and is skipped. `minTokenDocumentFrequency` floors that cut-off so a small vendor master
does not turn 5% into 1 and drop nearly every token from the index.

If no key matches at all, and the master is smaller than `fullScanMaxVendors`, the engine falls
back to scoring every vendor. On the sample data that fallback triggers only for a genuinely
unknown vendor.

**On the sample data, blocking retrieves the correct best match for all 20 invoices while
scoring 1–3 candidates each instead of all 19 vendors.**

---

## 3. Scoring a pair

### Short-circuits

1. **Shared tax / VAT identifier** (normalized to alphanumerics, at least 5 characters, checked
   across both fields on both sides) → score `100`, type `TaxId`. This is the only signal that
   survives a rebrand, and it outranks the name entirely.
2. **Identical core names** → score `100`, type `ExactNormalized`.
3. **Acronym relationship** — one side is a single token of 2–6 characters equal to the other
   side's initials → base score `acronymScore` (95), type `Acronym`.

### The weighted composite

Otherwise five algorithms score the pair from 0 to 100 and are blended:

| Algorithm | Weight | Contributes |
| --- | --- | --- |
| Levenshtein ratio | 0.20 | Overall edit distance |
| Jaro-Winkler | 0.20 | Transpositions, shared prefixes |
| Token sort ratio | 0.25 | Immunity to word order |
| Token set ratio | 0.25 | Immunity to extra words on one side |
| Partial ratio | 0.10 | Best substring alignment |

```
mean = Σ(score × weight) / Σ(weight)
```

No single algorithm is trustworthy alone. Levenshtein calls `Bright Consulting Partners` and
`Partners Consulting Bright` different companies; token set calls `Europe Ltd` and
`Acme Europe Distribution` a match. Blending them makes each one's blind spot someone else's
strength.

### The subset shortcut

A mean punishes a name that is *correct but shortened*. `Acme Europe` against
`Acme Europe Distribution B.V.` scores 100 on token set and 100 on partial, but ~43 on
Levenshtein — the mean lands around 70 and the true match is thrown away.

So a single strong order/subset signal may override the mean, discounted so it never outranks a
genuinely complete match:

```
shortcut = max(tokenSet × 0.92, tokenSort × 0.97, partial × 0.87)
base     = max(mean, shortcut)
```

The shortcut only applies when **both** names have at least `minimumTokens` (2) core tokens.
Without that guard a single shared low-information word (`EUROPE`) would carry a match on its
own.

### Adjustments

Applied to the base score, then clamped to 0–100:

| Adjustment | Default | When |
| --- | --- | --- |
| Country match bonus | +2 | Both countries present and equal |
| Country mismatch penalty | −6 | Both are two-letter ISO codes and differ (`DE` vs `Germany` is not treated as a conflict) |
| City match bonus | +3 | Both cities present and equal |
| Tax id suffix bonus | +5 | Identifiers differ but share their last 6 characters — a VAT number written with and without its country prefix |
| Digit mismatch penalty | −15 | The digit runs in the two names differ |

The digit penalty is what separates `Sigma 3 Laboratories` from `Sigma 5 Laboratories`: those
names are 95% identical as strings, and are different legal entities.

### SAP NAME1 / NAME2

SAP frequently splits a long name across `NAME1` and `NAME2`. Each vendor is scored twice — once
against `NAME1 + NAME2`, once against `NAME1` alone — and the better representation wins, so
neither a split name nor a `NAME2` holding address noise breaks the match.

---

## 4. Decisions

```
score ≥ autoMatchThreshold (90)  → AutoMatch
score ≥ reviewThreshold   (75)  → NeedsReview
otherwise                       → NoMatch
```

Then the **ambiguity guard**: if the runner-up is a *different* vendor within
`ambiguityMargin` (2 points) of the winner, the result is flagged ambiguous and an `AutoMatch`
is downgraded to `NeedsReview`.

This matters more than the raw score. SAP vendor masters accumulate duplicates, and
`Global Tech Solutions Ltd` and `Global Tech Solution Ltd` both normalize to
`GLOBAL TECH SOLUTIONS` — two perfect 100s. Posting to either one is a coin flip, so the row
goes to a human instead.

Blocked vendors (`LOEVM` / `SPERR` set) are excluded from the index entirely when
`excludeBlockedVendors` is on, so they can never be proposed.

---

## 5. Tuning

Start with the score distribution in `vendor-match-results.csv`, which carries every component
score, both runner-ups and a reason per row.

| Symptom | Change |
| --- | --- |
| Wrong vendors auto-matched | Raise `thresholds.autoMatch`; raise `adjustments.digitMismatchPenalty` |
| Correct matches sitting in review | Lower `thresholds.autoMatch`; add missing abbreviations to `aliases` |
| Industry words dominating the score | Add them to `noiseWords` (e.g. `HOLDING`, `GROUP`) |
| Country-specific entity types not stripped | Add them to `legalForms` |
| Near-duplicate masters slipping through | Raise `thresholds.ambiguityMargin` to 5 or more |
| Matching too slow on a large master | Lower `index.maxCandidatesPerInvoice`; set `index.fullScanFallback` to false |
| True matches missed entirely | Raise `index.maxCandidatesPerInvoice`; keep `fullScanFallback` on |

Retune against your own extract, not the sample. Thresholds that suit a clean German master are
wrong for a mixed-language one, and the only honest calibration is a few hundred rows a human
has already decided.

---

## Known limits

- Blocking can miss a true match when a name shares no key with its SAP counterpart — a heavily
  garbled OCR read of a short name is the realistic case. `fullScanFallback` covers it below
  `fullScanMaxVendors`; above that size, expect recall loss on the worst rows.
- Country and city are compared as exact strings after folding. `DE` vs `Germany` scores neither
  bonus nor penalty; map country names to ISO codes upstream if you want that signal.
- The alias and legal-form lists are Latin-script and Europe-weighted. Names in non-Latin scripts
  fold to nothing useful and need a transliteration step before this engine.
- Scores are not probabilities. A 92 is not "92% likely correct" — it is a position on a scale
  you calibrate with your own thresholds.
