# Vendor Matching — Invoices vs SAP

A UiPath process that matches the vendor name on an incoming invoice to the right record in the
SAP vendor master, using fuzzy name matching rather than exact comparison. Built and deployed
with the **UiPath CLI** (`uipcli`), so it runs from Studio, from a robot, or from a pipeline.

It answers the question an AP clerk asks a hundred times a day: *invoice says
`MUELLER PRAEZISIONSTECHNIK GMBH`, which SAP vendor is that?*

Every invoice lands in one of three buckets:

| Decision | Meaning |
| --- | --- |
| `AutoMatch` | Confident enough to post without a human |
| `NeedsReview` | Plausible, or ambiguous between two vendors — route to Action Center |
| `NoMatch` | Nothing credible in the master |

---

## Repository layout

```
VendorMatching/                 UiPath project (coded workflows)
  project.json                  Process definition, entry point, dependencies
  Main.cs                       Entry point workflow
  Tests/RunSelfTests.cs         Runs the test suite as a workflow
  Code/                         The engine — plain C#, no UiPath dependencies
    TextNormalizer.cs             Name normalization
    Similarity.cs                 Levenshtein, Jaro-Winkler, token ratios, Soundex
    VendorMatchEngine.cs          Blocking index, scoring, decisions
    MatchRunner.cs                End-to-end run and output files
    MatchConfig.cs                Configuration and defaults
    Csv.cs                        Dependency-free RFC 4180 reader/writer
    SelfTests.cs                  Behavioural test suite
  Config/matching-config.json   Tunable thresholds, weights, aliases, column mappings
  Data/*.sample.csv             Sample invoice and SAP extracts

build/                          UiPath CLI wrappers
  install-uipcli.ps1              Installs uipcli from the official feed
  build.ps1                       restore → analyze → pack
  deploy.ps1                      Publish to Orchestrator
  test.ps1                        Run the self-tests (locally or as an Orchestrator job)
  run-local.ps1                   Run the matcher without Orchestrator

tools/EngineHarness/            .NET console harness — same engine, no Studio needed
.github/workflows/uipath-ci.yml GitHub Actions pipeline
azure-pipelines.yml             Azure DevOps pipeline
docs/matching-logic.md          How the matching actually works
```

The engine sources are deliberately free of UiPath types. `tools/EngineHarness` links the same
files rather than copying them, so the logic can be compiled, tested and debugged on any machine
with the .NET SDK — and there is still only one implementation.

---

## Quick start

### With the UiPath CLI

```powershell
./build/install-uipcli.ps1          # installs uipcli into .uipcli/
./build/build.ps1                   # restore, Workflow Analyzer, pack → artifacts/*.nupkg
./build/test.ps1                    # run the self-tests
./build/run-local.ps1               # match the sample data, no Orchestrator needed
```

Deploy:

```powershell
$env:UIPATH_APP_SECRET = '<external application secret>'
./build/deploy.ps1 `
    -OrchestratorUrl 'https://cloud.uipath.com/acme/DefaultTenant/orchestrator_' `
    -Tenant 'DefaultTenant' -AccountName 'acme' -ApplicationId '<app id>' `
    -OrganizationUnit 'Finance/AccountsPayable' -CreateProcess
```

Each script prints the exact `uipcli` command before running it, with secrets masked, so a
failing pipeline step shows what was actually executed.

### Without UiPath

```bash
dotnet run --project tools/EngineHarness -- test
dotnet run --project tools/EngineHarness -- match \
    --invoices VendorMatching/Data/invoices.sample.csv \
    --sap      VendorMatching/Data/sap_vendors.sample.csv \
    --config   VendorMatching/Config/matching-config.json \
    --output   artifacts/output
```

### In Studio

Open `VendorMatching/project.json`, then run `Main.cs`. All six arguments are optional — with
none supplied it matches the sample data using the project's config.

---

## Inputs

Both inputs are delimited text; the delimiter (`,` `;` tab `|`) is detected from the header.
Column names are resolved through `matching-config.json`, so a raw SAP extract works unchanged:

| Logical field | Accepted headers (first hit wins) |
| --- | --- |
| Vendor code | `LIFNR`, `VendorCode`, `VendorNo`, `SupplierCode`, `Account` |
| Vendor name | `NAME1`, `VendorName`, `Name`, `SupplierName` |
| Second name line | `NAME2`, `VendorName2` |
| Tax number | `STCD1`, `TaxId`, `TaxNumber`, `TIN` |
| VAT number | `STCEG`, `VatId`, `VATNumber` |
| Country / city | `LAND1` / `ORT01`, or `Country` / `City` |
| Blocked flag | `LOEVM`, `SPERR`, `Blocked`, `DeletionFlag` |

Only the vendor code and name are required on the SAP side, and only the vendor name on the
invoice side. Everything else improves accuracy when present.

## Outputs

Three files in the output folder:

| File | Contents |
| --- | --- |
| `vendor-match-results.csv` | One row per invoice: decision, score, matched vendor, all five component scores, bonus/penalty, both runner-ups, and a reason |
| `vendor-match-exceptions.csv` | Only the review and no-match rows, for Action Center or a queue |
| `vendor-match-summary.json` | Counts, thresholds and duration for the run |

The component scores are there so a disputed match can be explained without re-running anything.

---

## What it handles

These are the sample cases in `VendorMatching/Data`, and the outcomes the test suite asserts:

| Invoice name | SAP name | Result |
| --- | --- | --- |
| `MUELLER PRAEZISIONSTECHNIK GMBH` | `Müller Präzisionstechnik GmbH` | 100 — transliteration |
| `Acme Europe Distribution BV` | `Acme Europe Distribution B.V.` | 100 — punctuation and legal form |
| `Acme Europe` | `Acme Europe Distribution B.V.` | 94 — shortened trading name |
| `IBM` | `International Business Machines Corporation` | 97 — acronym |
| `Nordwind Logistik GmbH und Co KG` | `Nordwind Logistik GmbH & Co. KG` | 100 — `&` vs `und` |
| `Bright Consultancy Partners` | `Bright Consulting Partners LLC` | 100 — abbreviation alias |
| `Contoso Pharma AG` | `Contoso Pharmaceuticals AG` | 100 — abbreviation alias |
| `Etablissements Dupont et Fils` | `Établissements Dupont & Fils SARL` | 100 — accents and noise words |
| `Kowalski i Synowie Sp z o o` | `Kowalski i Synowie Sp. z o.o.` | 100 — dotted legal form |
| `Fabrikam Manufacturing Holdings Pte Ltd` | `Fabrikam Manufacturing` + `Holdings Pte Ltd` | 100 — NAME1/NAME2 split |
| `Sicmens AG` | `Siemens Aktiengesellschaft` | 88 — OCR typo → **review** |
| `Sigma 3 Labs Inc` | `Sigma 3 Laboratories Inc.` | 100, with `Sigma 5` pushed to 86 by the digit penalty |
| `Helix Industrial Group Ltd` | `Old Name Industries Ltd` | 100 — matched on VAT after a rename |
| `Global Tech Solutions Ltd` | `Global Tech Solutions Ltd` **and** `Global Tech Solution Ltd` | Two perfect matches → **review**, ambiguous |
| `Zenith Elektro Technik GmbH` | (vendor is blocked in SAP) | **No match** — blocked records are excluded |
| `Quantum Widgets Unlimited` | — | **No match** |

Totals: 16 auto-matched, 2 to review, 2 unmatched.

`docs/matching-logic.md` explains the normalization pipeline, the blocking index, the scoring
formula and how to tune it.

---

## Configuration

Everything tunable lives in `VendorMatching/Config/matching-config.json`. Any section you omit
falls back to the built-in defaults, so a three-line file that only overrides thresholds is
valid.

```jsonc
{
  "thresholds": { "autoMatch": 90.0, "review": 75.0, "ambiguityMargin": 2.0 },
  "weights":    { "levenshtein": 0.20, "jaroWinkler": 0.20,
                  "tokenSort": 0.25, "tokenSet": 0.25, "partial": 0.10 },
  "adjustments": { "digitMismatchPenalty": 15.0, "excludeBlockedVendors": true },
  "aliases":     { "PHARMA": "PHARMACEUTICALS" },
  "legalForms":  [ "GMBH", "LTD", "SARL" ],
  "noiseWords":  [ "THE", "AND", "UND" ]
}
```

`autoMatchThreshold` and `reviewThreshold` can also be passed as workflow arguments to override
the file per run, which is the practical way to A/B a threshold change from Orchestrator.

Tune against a few hundred rows a human has already decided — not against the sample data.

---

## CI/CD

Both pipelines follow the same shape:

1. **Engine self-tests** on a Linux agent — no Studio, no Orchestrator, seconds to run. This is
   the gate that catches matching regressions.
2. **Analyze and pack** on Windows via `uipcli package analyze` and `uipcli package pack`.
3. **Deploy** via `uipcli package deploy` — manual in GitHub Actions (tick *deploy* when running
   the workflow), `main`-only in Azure DevOps.

Credentials come from `UIPATH_APP_ID` / `UIPATH_APP_SECRET` (an Orchestrator External
Application), never from the command line.

---

## Requirements and verification status

- **UiPath Studio 2023.10+** — the project uses coded workflows (`.cs`), not XAML.
- **UiPath CLI** — installed by `build/install-uipcli.ps1`; `UiPath.CLI.Windows` on Windows,
  `UiPath.CLI` elsewhere.
- **.NET 8 SDK** — only for the harness and the self-tests, not for the UiPath process.

Two things to check on first run, because they could not be verified in the environment this was
written in:

- `project.json` pins `UiPath.System.Activities` to `[24.10.7]`. If `uipcli package restore`
  cannot find that version on your feed, change it to one your feed carries.
- The `uipcli` flags in `build/` target CLI 24.10. If a flag was renamed in your version, the
  scripts print the failing command and point you at `--help` for that verb.

The matching behaviour itself is pinned by `Code/SelfTests.cs`, which asserts every outcome in
the table above end to end. Run `./build/test.ps1` first — if the engine and the documentation
ever disagree, that suite is what tells you.
