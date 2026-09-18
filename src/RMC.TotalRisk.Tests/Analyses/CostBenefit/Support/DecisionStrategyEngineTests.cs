using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit.Support;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit.Support;

/// <summary>
/// Tests the ranking core: competition ranks with shared better ranks on ties, the NaN and
/// eligibility exclusions, the first-row tie rule, the withheld recommendation, and the
/// pinned layer and discipline labels.
/// </summary>
[TestClass]
public class DecisionStrategyEngineTests
{
    /// <summary>Verifies competition ranks, tie sharing, and the first-row tie rule.</summary>
    [TestMethod]
    public void Test_Rank_CompetitionRanksAndTies()
    {
        // Act — two exact ties at the best Minimize value.
        var ranking = DecisionStrategyEngine.Rank(DecisionStrategy.ExpectedValue, 1,
            DecisionStrategyEngine.LayerAleatory, DecisionStrategyEngine.DisciplineAleatory,
            "criterion", "", ObjectiveDirection.Minimize,
            new[] { "A", "B", "C", "D" }, new[] { 5d, 3d, 3d, 7d }, new[] { true, true, true, true });

        // Assert — ties share rank one; the recommendation is the first tied row.
        CollectionAssert.AreEqual(new[] { 3, 1, 1, 4 }, ranking.Ranks.ToArray());
        Assert.AreEqual(1, ranking.RecommendedIndex, "Ties must break to the first results-order row.");
        Assert.AreEqual("B", ranking.RecommendedAlternative);
    }

    /// <summary>Verifies the NaN and eligibility exclusions and the Maximize direction.</summary>
    [TestMethod]
    public void Test_Rank_ExclusionsAndDirection()
    {
        // Act — a NaN row, an ineligible row, and a Maximize criterion.
        var ranking = DecisionStrategyEngine.Rank(DecisionStrategy.NetPresentValue, 1,
            DecisionStrategyEngine.LayerExact, DecisionStrategyEngine.DisciplineExact,
            "criterion", "", ObjectiveDirection.Maximize,
            new[] { "A", "B", "C", "D" }, new[] { double.NaN, 4d, 9d, 2d },
            new[] { true, true, false, true });

        // Assert — excluded rows are unranked (rank zero) and never recommended; the best
        // eligible measurable row wins even when an excluded row carries a better value.
        CollectionAssert.AreEqual(new[] { true, false, false, false }, ranking.IsExcludedForNaN.ToArray());
        CollectionAssert.AreEqual(new[] { false, false, true, false }, ranking.IsExcludedFromRecommendation.ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 0, 2 }, ranking.Ranks.ToArray());
        Assert.AreEqual(1, ranking.RecommendedIndex);
        Assert.AreEqual(9d, ranking.CriterionValues[2], "Values are facts — excluded rows keep theirs.");
    }

    /// <summary>Verifies the withheld recommendation when no row is eligible and measurable.</summary>
    [TestMethod]
    public void Test_Rank_Withheld()
    {
        // Act
        var allNaN = DecisionStrategyEngine.Rank(DecisionStrategy.Laplace, 2,
            DecisionStrategyEngine.LayerEpistemic, DecisionStrategyEngine.DisciplineEpistemic,
            "criterion", "", ObjectiveDirection.Minimize, new[] { "A", "B" },
            new[] { double.NaN, double.NaN }, new[] { true, true });
        var allIneligible = DecisionStrategyEngine.Rank(DecisionStrategy.Laplace, 2,
            DecisionStrategyEngine.LayerEpistemic, DecisionStrategyEngine.DisciplineEpistemic,
            "criterion", "", ObjectiveDirection.Minimize, new[] { "A", "B" },
            new[] { 1d, 2d }, new[] { false, false });

        // Assert
        Assert.IsTrue(allNaN.RecommendationWithheld);
        Assert.IsTrue(allIneligible.RecommendationWithheld);
        CollectionAssert.AreEqual(new[] { 0, 0 }, allIneligible.Ranks.ToArray());
    }

    /// <summary>Pins the layer and discipline label constants — the honesty vocabulary.</summary>
    [TestMethod]
    public void Test_LayerAndDisciplineLabels_Pinned()
    {
        // Assert
        Assert.AreEqual("Exact", DecisionStrategyEngine.LayerExact);
        Assert.AreEqual("Aleatory", DecisionStrategyEngine.LayerAleatory);
        Assert.AreEqual("Epistemic", DecisionStrategyEngine.LayerEpistemic);
        Assert.AreEqual("Shared-state", DecisionStrategyEngine.LayerSharedState);
        Assert.AreEqual("exact (mean-only twin deltas)", DecisionStrategyEngine.DisciplineExact);
        Assert.AreEqual("aleatory-from-mean-LEC", DecisionStrategyEngine.DisciplineAleatory);
        Assert.AreEqual("epistemic-weighted", DecisionStrategyEngine.DisciplineEpistemic);
        Assert.AreEqual("block-noise k·SE/√M", DecisionStrategyEngine.DisciplineBlockNoise);
    }
}
