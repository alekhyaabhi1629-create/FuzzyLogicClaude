using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using UiPath.CodedWorkflows;
using UiPath.Core;

namespace FuzzyMatch
{
    /// <summary>
    /// Matches every row of a source table against a reference (master) table using
    /// approximate string similarity, and returns a result table plus per-outcome counts.
    ///
    /// This step is coded rather than XAML because it is algorithmic: three string
    /// scorers, a blend, a pre-filter and a runner-up track would take dozens of
    /// Assign / If activities to express visually.
    /// </summary>
    public class MatchRecords : CodedWorkflow
    {
        internal const string ColumnSourceRow = "SourceRow";
        internal const string ColumnSourceValue = "SourceValue";
        internal const string ColumnMatchedValue = "MatchedValue";
        internal const string ColumnMatchedRow = "MatchedReferenceRow";
        internal const string ColumnScore = "Score";
        internal const string ColumnOutcome = "Outcome";
        internal const string ColumnRunnerUpValue = "RunnerUpValue";
        internal const string ColumnRunnerUpScore = "RunnerUpScore";
        internal const string ColumnMargin = "Margin";

        [Workflow]
        public (DataTable Results, int AutoMatched, int NeedsReview, int NoMatch) Execute(
            DataTable sourceTable,
            string sourceColumn,
            DataTable referenceTable,
            string referenceColumn,
            double autoMatchThreshold,
            double reviewThreshold)
        {
            // --- Input validation -------------------------------------------------
            // Bad input data is a business rule failure, not a transient system fault:
            // retrying will not fix a misnamed column, so this needs a human.
            if (sourceTable == null)
            {
                throw new BusinessRuleException("Source table is null — the source workbook was not read.");
            }

            if (referenceTable == null)
            {
                throw new BusinessRuleException("Reference table is null — the reference workbook was not read.");
            }

            if (string.IsNullOrWhiteSpace(sourceColumn))
            {
                throw new BusinessRuleException("sourceColumn was not supplied.");
            }

            if (string.IsNullOrWhiteSpace(referenceColumn))
            {
                throw new BusinessRuleException("referenceColumn was not supplied.");
            }

            if (!sourceTable.Columns.Contains(sourceColumn))
            {
                throw new BusinessRuleException(
                    $"Source sheet has no column named '{sourceColumn}'. Columns found: {DescribeColumns(sourceTable)}.");
            }

            if (!referenceTable.Columns.Contains(referenceColumn))
            {
                throw new BusinessRuleException(
                    $"Reference sheet has no column named '{referenceColumn}'. Columns found: {DescribeColumns(referenceTable)}.");
            }

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

            // --- Build the reference index once -----------------------------------
            List<ReferenceRecord> referenceRecords = BuildReferenceIndex(referenceTable, referenceColumn, options);

            if (referenceRecords.Count == 0)
            {
                throw new BusinessRuleException(
                    $"Reference column '{referenceColumn}' contains no usable values — every row is blank.");
            }

            Log($"Matching {sourceTable.Rows.Count} source rows against {referenceRecords.Count} reference records " +
                $"(auto-match >= {options.AutoMatchThreshold}, review >= {options.ReviewThreshold}).");

            // --- Match ------------------------------------------------------------
            DataTable results = CreateResultsTable();

            int autoMatched = 0;
            int needsReview = 0;
            int noMatch = 0;
            int skippedBlank = 0;

            var stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < sourceTable.Rows.Count; i++)
            {
                // +2 converts a 0-based row index into the spreadsheet row a human
                // sees: +1 for 1-based rows, +1 for the header row.
                int sourceRowNumber = i + 2;
                string sourceValue = Convert.ToString(sourceTable.Rows[i][sourceColumn], CultureInfo.InvariantCulture) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(sourceValue))
                {
                    skippedBlank++;
                    continue;
                }

                MatchResult match = FuzzyMatchEngine.FindBestMatch(
                    sourceValue.Trim(), sourceRowNumber, referenceRecords, options);

                switch (match.Outcome)
                {
                    case MatchOutcome.AutoMatched:
                        autoMatched++;
                        break;
                    case MatchOutcome.NeedsReview:
                        needsReview++;
                        break;
                    default:
                        noMatch++;
                        break;
                }

                AppendResultRow(results, match);
            }

            stopwatch.Stop();

            if (skippedBlank > 0)
            {
                Log($"Skipped {skippedBlank} source row(s) with a blank '{sourceColumn}' value.");
            }

            Log($"Matching finished in {stopwatch.ElapsedMilliseconds} ms — " +
                $"auto-matched: {autoMatched}, needs review: {needsReview}, no match: {noMatch}.");

            return (results, autoMatched, needsReview, noMatch);
        }

        private static List<ReferenceRecord> BuildReferenceIndex(
            DataTable referenceTable, string referenceColumn, FuzzyMatchOptions options)
        {
            var records = new List<ReferenceRecord>(referenceTable.Rows.Count);

            for (int i = 0; i < referenceTable.Rows.Count; i++)
            {
                string raw = Convert.ToString(referenceTable.Rows[i][referenceColumn], CultureInfo.InvariantCulture) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                records.Add(new ReferenceRecord
                {
                    RowNumber = i + 2,
                    RawValue = raw.Trim(),
                    NormalizedValue = FuzzyMatchEngine.Normalize(
                        raw, options.StripNoiseTokens, options.AdditionalNoiseTokens)
                });
            }

            return records;
        }

        private static DataTable CreateResultsTable()
        {
            var table = new DataTable("MatchResults");

            table.Columns.Add(ColumnSourceRow, typeof(int));
            table.Columns.Add(ColumnSourceValue, typeof(string));
            table.Columns.Add(ColumnMatchedValue, typeof(string));
            table.Columns.Add(ColumnMatchedRow, typeof(int));
            table.Columns.Add(ColumnScore, typeof(double));
            table.Columns.Add(ColumnOutcome, typeof(string));
            table.Columns.Add(ColumnRunnerUpValue, typeof(string));
            table.Columns.Add(ColumnRunnerUpScore, typeof(double));
            table.Columns.Add(ColumnMargin, typeof(double));

            return table;
        }

        private static void AppendResultRow(DataTable table, MatchResult match)
        {
            DataRow row = table.NewRow();

            row[ColumnSourceRow] = match.SourceRowNumber;
            row[ColumnSourceValue] = match.SourceValue;
            row[ColumnMatchedValue] = match.MatchedValue;
            row[ColumnMatchedRow] = match.MatchedRowNumber;
            row[ColumnScore] = match.Score;
            row[ColumnOutcome] = match.Outcome.ToString();
            row[ColumnRunnerUpValue] = match.RunnerUpValue;
            row[ColumnRunnerUpScore] = match.RunnerUpScore;
            row[ColumnMargin] = match.Margin;

            table.Rows.Add(row);
        }

        private static string DescribeColumns(DataTable table)
        {
            var names = new List<string>(table.Columns.Count);

            foreach (DataColumn column in table.Columns)
            {
                names.Add($"'{column.ColumnName}'");
            }

            return names.Count == 0 ? "(none)" : string.Join(", ", names);
        }
    }
}
