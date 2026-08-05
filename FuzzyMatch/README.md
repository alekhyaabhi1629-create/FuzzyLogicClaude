# FuzzyMatch — RPA flow for fuzzy matching

Reconciles a **source** list against a **reference (master)** list when the two do not
share a key and the names do not match exactly. Every source row gets a best-guess
match, a 0–100 similarity score, and a classification a human can act on.

## Scenario this implements

Two spreadsheets, no shared ID:

- `Data/Source.xlsx` — the incoming records (invoices, statements, a supplier feed)
- `Data/Reference.xlsx` — the master list to match against

The process scores each source name against every reference name and writes
`Output/MatchResults.xlsx` with one row per source record:

| Outcome | Meaning | Default band |
|---|---|---|
| `AutoMatched` | Safe to post without a human | score ≥ 90 |
| `NeedsReview` | Plausible, but a person should confirm | 75 ≤ score < 90 |
| `NoMatch` | Nothing credible in the reference list | score < 75 |

Both thresholds are workflow arguments — no code change to retune them.

## Project layout

```
FuzzyMatch/
├── Main.xaml                  # Orchestration: read → match → write → log
├── MatchRecords.cs            # Coded workflow: DataTable in, results + counts out
├── FuzzyMatchEngine.cs        # Coded source file: normalization + the three scorers
├── FuzzyMatchModels.cs        # Coded source file: options, records, results, enum
├── TestFuzzyMatchEngine.cs    # Coded test case: 14 assertions on the scoring logic
├── Data/Source.xlsx           # Sample input — exercises all three outcomes
├── Data/Reference.xlsx        # Sample master list
└── Output/                    # MatchResults.xlsx is written here
```

The split is deliberate: `Main.xaml` stays readable as a visual flow, while the
algorithmic part (three string scorers, a weighted blend, a pre-filter and a
runner-up track) lives in C#, where it is also unit-testable. Expressing it in
XAML would take dozens of `Assign` and `If` activities.

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

Each result also carries the **runner-up** value and score, and the `Margin`
between them. A 96 with a margin of 1 means two reference rows look alike — worth
a human eye even though it cleared the auto-match bar.

## Results columns

`SourceRow`, `SourceValue`, `MatchedValue`, `MatchedReferenceRow`, `Score`,
`Outcome`, `RunnerUpValue`, `RunnerUpScore`, `Margin`.

Row numbers are spreadsheet row numbers (header counted), so a reviewer can jump
straight to the row.

## Run it

```bash
uip rpa run "Main.xaml" --project-dir "." --output json
```

Sample data runs end to end with no arguments. To point it at real files:

```bash
uip rpa run "Main.xaml" --project-dir "." --output json \
  --input-arguments '{"in_SourceWorkbook":"C:\\data\\suppliers.xlsx","in_SourceMatchColumn":"Supplier","in_ReferenceWorkbook":"C:\\data\\master.xlsx","in_ReferenceMatchColumn":"Name","in_AutoMatchThreshold":92,"in_ReviewThreshold":80}'
```

### Arguments

| Argument | Default | Notes |
|---|---|---|
| `in_SourceWorkbook` | `Data\Source.xlsx` | Local path |
| `in_SourceSheet` | `Sheet1` | |
| `in_SourceMatchColumn` | `CustomerName` | Column header to match on |
| `in_ReferenceWorkbook` | `Data\Reference.xlsx` | |
| `in_ReferenceSheet` | `Sheet1` | |
| `in_ReferenceMatchColumn` | `CustomerName` | |
| `in_OutputWorkbook` | `Output\MatchResults.xlsx` | Created if absent; folder must exist |
| `in_OutputSheet` | `MatchResults` | Created if absent |
| `in_AutoMatchThreshold` | `90` | |
| `in_ReviewThreshold` | `75` | Must be ≤ auto-match threshold |

Outputs: `out_AutoMatchedCount`, `out_NeedsReviewCount`, `out_NoMatchCount`.

## Expected result on the sample data

| Source | Best match | Score | Outcome |
|---|---|---|---|
| `ACME FOODS LTD.` | Acme Foods Limited | 100.00 | AutoMatched |
| `Zenith Logistics` | Zenith Logistics LLC | 100.00 | AutoMatched |
| `Nordwind Handels G.m.b.H.` | Nordwind Handels GmbH | 76.00 | NeedsReview |
| `Sakura Trading Company` | Sakura Trading Co | 100.00 | AutoMatched |
| `Olivera e Filhos` | Olivera & Filhos Lda | 75.10 | NeedsReview |
| `Bluewater Marine Svcs` | Bluewater Marine Services | 88.48 | NeedsReview |
| `Acme Fabrcs Limited` | Acme Fabrics Limited | 94.00 | AutoMatched |
| `Helios Renewable` | Helios Renewables PLC | 95.76 | AutoMatched |
| `Quantum Dynamics Inc` | — | 39.20 | NoMatch |
| *(blank)* | — | — | skipped |

`G.m.b.H.` lands in review because punctuation splits it into four single-letter
tokens rather than one. That is the intended behaviour: the process flags it
rather than guessing. Add `"GMBH"`-style variants to
`FuzzyMatchOptions.AdditionalNoiseTokens` if you want them auto-matched.

## Tuning

- **Too many false positives** → raise `in_AutoMatchThreshold`.
- **Too many rows in review** → lower it, or add domain noise tokens
  (`AdditionalNoiseTokens`) so cosmetic differences stop costing points.
- **Long reference lists** → `FuzzyMatchOptions.LengthGuardRatio` (default `0.34`)
  skips candidates whose length is wildly different before running the O(n·m)
  edit-distance loop. Raise it to trade recall for speed.
- **Different data shape** → the scorer weights are on `FuzzyMatchOptions`.
  Product codes favour Levenshtein; free-text company names favour token-set.

## Error handling

Bad input data (missing column, blank reference column, `ReviewThreshold`
above `AutoMatchThreshold`) throws `BusinessRuleException` — retrying will not fix
a misnamed column, so it needs a human. `Main.xaml` wraps the whole flow in a
Try/Catch that logs at `Error` level and rethrows, preserving the stack trace.

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
the engine before commit.
