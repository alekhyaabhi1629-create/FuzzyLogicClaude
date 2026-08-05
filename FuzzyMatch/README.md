# FuzzyMatch — RPA flow for fuzzy matching

Matches a **string value** against a **delimited list of candidate strings** and
returns the best match, a 0–100 similarity score, and a verdict a human can act on.

Everything crosses the workflow boundary as plain strings and numbers — no Excel,
no files, no DataTables. Values go in through string variables and come back out
as string arguments, so this drops into any caller: an Orchestrator asset, a queue
item field, a scraped UI value, an API response, or another workflow.

## Verdicts

| Outcome | Meaning | Default band |
|---|---|---|
| `AutoMatched` | Safe to use without a human | score ≥ 90 |
| `NeedsReview` | Plausible, but a person should confirm | 75 ≤ score < 90 |
| `NoMatch` | Nothing credible in the candidate list | score < 75 |

Both thresholds are workflow arguments — no code change to retune them.
`out_Outcome` is returned as a **string**, so a caller can branch on it with a
plain `If` or `Switch` without needing the enum type in scope.

## Project layout

```
FuzzyMatch/
├── Main.xaml                  # Orchestration: string vars → invoke matcher → log verdict
├── MatchValue.cs              # Coded workflow: strings in, match + score + outcome out
├── FuzzyMatchEngine.cs        # Coded source file: normalization + the three scorers
├── FuzzyMatchModels.cs        # Coded source file: options, records, results, enum
└── TestFuzzyMatchEngine.cs    # Coded test case: 14 assertions on the scoring logic
```

`Main.xaml` stays readable as a visual flow; the algorithmic part (three string
scorers, a weighted blend, a pre-filter and a runner-up track) lives in C# where
it is also unit-testable. Expressing it in XAML would take dozens of `Assign` and
`If` activities.

## How the flow works

1. Three `Assign` activities load the inputs into **String variables** —
   `strSourceValue`, `strReferenceValues`, `strDelimiter`. The delimiter falls back
   to `;` when blank. To hard-code a test value in Studio, edit these Assigns and
   ignore the arguments entirely.
2. `Invoke Workflow File` calls `MatchValue.cs`, passing the string variables.
3. The five outputs bind straight to the workflow's out-arguments.
4. The result is logged, and anything that is not `AutoMatched` also logs at
   `Warn` level so it surfaces in Orchestrator.
5. The whole thing sits in a Try/Catch that logs at `Error` and rethrows,
   preserving the stack trace.

## How the score is built

Each value is normalized first — upper-cased, accents stripped, punctuation
removed, `&` expanded to `AND`, whitespace collapsed, and legal-entity noise
tokens (`LTD`, `LIMITED`, `GMBH`, `INC`, `THE`, …) dropped. A value made
*entirely* of noise tokens keeps them, so it can still match itself.

Three scorers then run on the normalized pair:

| Scorer | Weight | What it catches |
|---|---|---|
| Levenshtein ratio | 0.40 | Insertions, deletions, general edit distance |
| Jaro-Winkler | 0.35 | Typos, especially near the start; truncated values |
| Token-set ratio | 0.25 | Missing/extra words, order-independent overlap |

The blend is computed twice — once on the values as-is, once on their
alphabetically sorted tokens — and the better of the two wins. Levenshtein and
Jaro-Winkler are both position-sensitive, so without the sorted pass a pure
reordering like `Smith, John` vs `John Smith` scores ~50 despite being the same
person. With it, that pair scores 100.

Each result also carries the **runner-up** value and the `Margin` between it and
the winner. A 96 with a margin of 1 means two candidates look alike — worth a
human eye even though it cleared the auto-match bar.

## Run it

```bash
uip rpa run "Main.xaml" --project-dir "." --output json
```

The built-in defaults run standalone. To pass your own values:

```bash
uip rpa run "Main.xaml" --project-dir "." --output json \
  --input-arguments '{"in_SourceValue":"Jhon Smith","in_ReferenceValues":"John Smith|Jane Smith|Jon Smyth","in_Delimiter":"|","in_AutoMatchThreshold":92,"in_ReviewThreshold":80}'
```

### Arguments

| Argument | Type | Default | Notes |
|---|---|---|---|
| `in_SourceValue` | String | `ACME FOODS LTD.` | The value to match |
| `in_ReferenceValues` | String | 5 sample companies | Candidates, delimiter-separated |
| `in_Delimiter` | String | `;` | Falls back to `;` when blank |
| `in_AutoMatchThreshold` | Double | `90` | |
| `in_ReviewThreshold` | Double | `75` | Must be ≤ auto-match threshold |

### Outputs

| Argument | Type | Notes |
|---|---|---|
| `out_MatchedValue` | String | Empty when the outcome is `NoMatch` |
| `out_Score` | Double | 0–100 |
| `out_Outcome` | String | `AutoMatched` / `NeedsReview` / `NoMatch` |
| `out_RunnerUpValue` | String | Second-best candidate |
| `out_Margin` | Double | Winner's score minus runner-up's |

## Expected result on the defaults

Source `ACME FOODS LTD.` against the five bundled candidates:

```
matched   : Acme Foods Limited
score     : 100.00
outcome   : AutoMatched
runner-up : Acme Fabrics Limited (67.90)
margin    : 32.10
```

Other values against the same candidate list:

| Source | Best match | Score | Outcome |
|---|---|---|---|
| `Zenith Logistics` | Zenith Logistics LLC | 100.00 | AutoMatched |
| `Sakura Trading Company` | Sakura Trading Co | 100.00 | AutoMatched |
| `Acme Fabrcs Limited` | Acme Fabrics Limited | 94.00 | AutoMatched |
| `Quantum Dynamics Inc` | — | 39.20 | NoMatch |

## Tuning

- **Too many false positives** → raise `in_AutoMatchThreshold`.
- **Too many rows in review** → lower it, or add domain noise tokens via
  `FuzzyMatchOptions.AdditionalNoiseTokens` so cosmetic differences stop costing points.
- **Long candidate lists** → `FuzzyMatchOptions.LengthGuardRatio` (default `0.34`)
  skips candidates whose length is wildly different before running the O(n·m)
  edit-distance loop. Raise it to trade recall for speed.
- **Different data shape** → the scorer weights are on `FuzzyMatchOptions`.
  Product codes favour Levenshtein; free-text company names favour token-set.

## Error handling

Bad input data — blank `sourceValue`, empty `referenceValues`, a candidate list
that splits to nothing, or `ReviewThreshold` above `AutoMatchThreshold` — throws
`BusinessRuleException`. Retrying does not fix a bad argument, so it needs a
human rather than a retry loop.

## Matching a whole spreadsheet instead

This version is deliberately Excel-free. A batch variant that reads two workbooks
and writes a results sheet is in git history at commit `e7ba90f` — restore
`MatchRecords.cs` from there and add `UiPath.Excel.Activities` back to
`project.json`. The scoring engine is identical and unchanged.

## Validation status

Authored against the `uipath-rpa` skill references. **Not yet run through
`uip rpa validate` / `uip rpa build`** — the environment this was written in has no
network route to UiPath's package feeds. Run both before trusting it:

```bash
uip rpa validate --file-path "Main.xaml" --project-dir "." --output json
uip rpa build "." --output json
```

The scoring logic itself *was* verified: all 14 assertions in
`TestFuzzyMatchEngine.cs` were checked against a line-by-line reference port of
the engine, as were the sample results above.
