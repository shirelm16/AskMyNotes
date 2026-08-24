using AskMyNotes.Domain;
using AskMyNotes.Retrieval;

namespace AskMyNotes.Tests;

/// <summary>
/// Pairing passages with the scores the model returned.
///
/// The prompt asks for a score for every passage and the reply is schema-checked, but nothing
/// requires every id to be present. What happens when one is missing is a known limitation, and
/// these tests pin the current behaviour so that changing it has to be a decision.
/// </summary>
public class RerankMergeTests
{
    private static List<ScoredChunk> Batch(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ScoredChunk($"file{i}.md", $"content {i}", 0.1 * i))
            .ToList();

    private static RerankResult Scores(params (int Id, int Score)[] scores) =>
        new(scores.Select(s => new RerankScore(s.Id, s.Score)).ToList());

    [Fact]
    public void Each_passage_gets_the_score_returned_for_its_position()
    {
        var merged = LlmReranker.MergeScores(Batch(3), Scores((0, 9), (1, 4), (2, 0)));

        Assert.Equal(9, merged[0].Score);
        Assert.Equal(4, merged[1].Score);
        Assert.Equal(0, merged[2].Score);
    }

    [Fact]
    public void Scores_returned_out_of_order_still_land_on_the_right_passage()
    {
        // They are matched by id, not by the order the model happened to reply in.
        var merged = LlmReranker.MergeScores(Batch(3), Scores((2, 7), (0, 1), (1, 5)));

        Assert.Equal(1, merged[0].Score);
        Assert.Equal(5, merged[1].Score);
        Assert.Equal(7, merged[2].Score);
    }

    [Fact]
    public void Every_passage_comes_back_even_when_the_model_scores_none_of_them()
    {
        var merged = LlmReranker.MergeScores(Batch(3), Scores());

        Assert.Equal(3, merged.Count);
        Assert.All(merged, m => Assert.Equal(LlmReranker.Unscored, m.Score));
    }

    [Fact]
    public void A_passage_the_model_skipped_currently_sorts_below_one_it_scored_zero()
    {
        // The known limitation, stated as a test: "not scored" and "scored zero" are different
        // things, and only one of them is a judgement. Today the first sorts worse than the
        // second, so a passage the model overlooked is buried beneath one it actively rejected.
        var merged = LlmReranker.MergeScores(Batch(2), Scores((0, 0)));   // nothing for id 1

        var scoredZero = merged[0].Score;
        var neverScored = merged[1].Score;

        Assert.Equal(0, scoredZero);
        Assert.True(neverScored < scoredZero,
            "if this fails the limitation has been fixed — update NOTES.md limitation 6 with what replaced it");
    }

    [Fact]
    public void An_extra_id_the_batch_does_not_contain_is_ignored()
    {
        var merged = LlmReranker.MergeScores(Batch(2), Scores((0, 8), (1, 6), (99, 10)));

        Assert.Equal(2, merged.Count);
        Assert.Equal(8, merged[0].Score);
        Assert.Equal(6, merged[1].Score);
    }

    [Fact]
    public void A_repeated_id_does_not_throw()
    {
        // ToDictionary on a duplicate key throws, which would fail the whole request over one
        // malformed reply. The last value wins instead.
        var merged = LlmReranker.MergeScores(Batch(1), Scores((0, 3), (0, 9)));

        Assert.Single(merged);
        Assert.Equal(9, merged[0].Score);
    }

    [Fact]
    public void The_passages_themselves_are_passed_through_untouched()
    {
        var batch = Batch(2);
        var merged = LlmReranker.MergeScores(batch, Scores((0, 5), (1, 5)));

        Assert.Equal(batch[0], merged[0].Chunk);
        Assert.Equal(batch[1], merged[1].Chunk);
    }
}
