using System;
using System.Collections.Generic;
using UiPath.CodedWorkflows;

namespace FuzzyMatch
{
    /// <summary>
    /// Unit tests for the scoring logic. The engine is a pure function of its
    /// inputs, so these run with no workbooks, no Excel and no Orchestrator.
    /// </summary>
    public class TestFuzzyMatchEngine : CodedWorkflow
    {
        [TestCase]
        public void Execute()
        {
            VerifyNormalization();
            VerifyIdenticalValuesScoreFull();
            VerifyTyposScoreHigh();
            VerifyReorderedTokensScoreHigh();
            VerifyUnrelatedValuesScoreLow();
            VerifyBestMatchSelection();
            VerifyThresholdClassification();
        }

        private void VerifyNormalization()
        {
            // GIVEN a value with accents, punctuation, doubled spaces and a legal suffix
            const string messy = "  Ácmé  Foods, Ltd. ";

            // WHEN it is normalized
            string normalized = FuzzyMatchEngine.Normalize(messy);

            // THEN only the identifying tokens survive
            testing.VerifyAreEqual("ACME FOODS", normalized,
                "Normalization should strip accents, punctuation, extra spaces and the LTD suffix.");

            // AND a value made entirely of noise tokens is not normalized away to nothing
            testing.VerifyAreEqual("THE GROUP LTD", FuzzyMatchEngine.Normalize("The Group Ltd"),
                "An all-noise value must keep its tokens so it can still match itself.");
        }

        private void VerifyIdenticalValuesScoreFull()
        {
            // GIVEN default options
            var options = new FuzzyMatchOptions();

            // WHEN two values differ only by case, punctuation and legal suffix
            double score = FuzzyMatchEngine.Score("Acme Ltd", "ACME LIMITED", options);

            // THEN they are treated as the same entity
            testing.VerifyAreEqual(100d, score,
                "'Acme Ltd' and 'ACME LIMITED' normalize to the same value and must score 100.");
        }

        private void VerifyTyposScoreHigh()
        {
            var options = new FuzzyMatchOptions();

            double score = FuzzyMatchEngine.Score("Jon Smithe", "John Smith", options);

            testing.VerifyExpression(score >= 80d,
                $"A typo'd name should score at least 80, got {score}.");
        }

        private void VerifyReorderedTokensScoreHigh()
        {
            var options = new FuzzyMatchOptions();

            double score = FuzzyMatchEngine.Score("Smith, John", "John Smith", options);

            testing.VerifyExpression(score >= 90d,
                $"Reordered tokens should still score at least 90, got {score}.");
        }

        private void VerifyUnrelatedValuesScoreLow()
        {
            var options = new FuzzyMatchOptions();

            double score = FuzzyMatchEngine.Score("Acme Foods", "Zenith Logistics", options);

            testing.VerifyExpression(score < 60d,
                $"Unrelated names should score below 60, got {score}.");
        }

        private void VerifyBestMatchSelection()
        {
            // GIVEN a small reference list with one near-duplicate
            var options = new FuzzyMatchOptions();
            List<ReferenceRecord> references = BuildReferences(options,
                "Zenith Logistics",
                "Acme Foods Limited",
                "Acme Fabrics Limited");

            // WHEN a source value is matched against it
            MatchResult result = FuzzyMatchEngine.FindBestMatch("Acme Foods Ltd", 2, references, options);

            // THEN the correct reference wins, and the near-duplicate is reported as runner-up
            testing.VerifyAreEqual("Acme Foods Limited", result.MatchedValue,
                "The best match should be 'Acme Foods Limited'.");
            testing.VerifyAreEqual(3, result.MatchedRowNumber,
                "The matched row number should point at the reference sheet row.");
            testing.VerifyExpression(result.RunnerUpScore < result.Score,
                "The runner-up must score below the winner.");
            testing.VerifyExpression(result.Margin > 0d,
                $"Margin should be positive when one candidate clearly wins, got {result.Margin}.");
        }

        private void VerifyThresholdClassification()
        {
            var references = new List<ReferenceRecord>();
            var strictOptions = new FuzzyMatchOptions { AutoMatchThreshold = 90d, ReviewThreshold = 75d };
            references = BuildReferences(strictOptions, "Acme Foods Limited");

            // An exact (post-normalization) hit clears the auto-match bar.
            MatchResult exact = FuzzyMatchEngine.FindBestMatch("ACME FOODS LTD", 2, references, strictOptions);
            testing.VerifyAreEqual(MatchOutcome.AutoMatched.ToString(), exact.Outcome.ToString(),
                "An exact normalized hit should be auto-matched.");

            // Nothing similar at all falls below the review floor.
            MatchResult miss = FuzzyMatchEngine.FindBestMatch("Zenith Logistics", 3, references, strictOptions);
            testing.VerifyAreEqual(MatchOutcome.NoMatch.ToString(), miss.Outcome.ToString(),
                "An unrelated value should be classified as NoMatch.");
            testing.VerifyAreEqual(string.Empty, miss.MatchedValue,
                "A NoMatch result must not claim a matched value.");

            // Raising the bar to 100 pushes a near-hit down into review.
            var pickyOptions = new FuzzyMatchOptions { AutoMatchThreshold = 100d, ReviewThreshold = 60d };
            var pickyReferences = BuildReferences(pickyOptions, "Acme Foods Limited");
            MatchResult review = FuzzyMatchEngine.FindBestMatch("Acme Food Ltd", 2, pickyReferences, pickyOptions);
            testing.VerifyAreEqual(MatchOutcome.NeedsReview.ToString(), review.Outcome.ToString(),
                "A near hit below a 100 auto-match threshold should need review.");
        }

        private static List<ReferenceRecord> BuildReferences(FuzzyMatchOptions options, params string[] values)
        {
            var records = new List<ReferenceRecord>(values.Length);

            for (int i = 0; i < values.Length; i++)
            {
                records.Add(new ReferenceRecord
                {
                    RowNumber = i + 2,
                    RawValue = values[i],
                    NormalizedValue = FuzzyMatchEngine.Normalize(
                        values[i], options.StripNoiseTokens, options.AdditionalNoiseTokens)
                });
            }

            return records;
        }
    }
}
