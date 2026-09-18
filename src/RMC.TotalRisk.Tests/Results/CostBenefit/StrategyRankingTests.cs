using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the strategy-ranking container: guards, the echoes, the defensive copies, and the
/// derived recommendation members.
/// </summary>
[TestClass]
public class StrategyRankingTests
{
    /// <summary>Builds a two-row ranking.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="recommendedIndex">The recommended index.</param>
    /// <returns>The ranking.</returns>
    private static StrategyRanking Build(int tier = 1, int recommendedIndex = 1)
    {
        return new StrategyRanking(DecisionStrategy.ExpectedValue, tier, "Aleatory",
            "aleatory-from-mean-LEC", "Mean of Total type 0", "k = 1", ObjectiveDirection.Minimize,
            new[] { "Baseline", "Alt A" }, new[] { 10d, 5d }, new[] { 2, 1 },
            new[] { false, false }, new[] { false, false }, recommendedIndex);
    }

    /// <summary>Verifies the guards: tier bounds, parallel lists, and the index range.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => Build(tier: 0));
        Assert.ThrowsException<ArgumentException>(() => Build(tier: 4));
        Assert.ThrowsException<ArgumentException>(() => Build(recommendedIndex: 2));
        Assert.ThrowsException<ArgumentException>(() => Build(recommendedIndex: -2));
        Assert.ThrowsException<ArgumentNullException>(() => new StrategyRanking(
            DecisionStrategy.ExpectedValue, 1, null!, "d", "c", "", ObjectiveDirection.Minimize,
            new[] { "A" }, new[] { 1d }, new[] { 1 }, new[] { false }, new[] { false }, 0));
        Assert.ThrowsException<ArgumentException>(() => new StrategyRanking(
            DecisionStrategy.ExpectedValue, 1, "L", "d", "c", "", ObjectiveDirection.Minimize,
            new[] { "A", "B" }, new[] { 1d }, new[] { 1, 2 }, new[] { false, false },
            new[] { false, false }, 0));
    }

    /// <summary>Verifies the echoes and the derived recommendation members.</summary>
    [TestMethod]
    public void Test_Ctor_EchoesAndRecommendation()
    {
        // Act
        var ranking = Build();

        // Assert
        Assert.AreEqual(DecisionStrategy.ExpectedValue, ranking.Strategy);
        Assert.AreEqual(1, ranking.Tier);
        Assert.AreEqual("Aleatory", ranking.Layer);
        Assert.AreEqual("aleatory-from-mean-LEC", ranking.Discipline);
        Assert.AreEqual("Mean of Total type 0", ranking.CriterionLabel);
        Assert.AreEqual("k = 1", ranking.ParameterEcho);
        Assert.AreEqual(ObjectiveDirection.Minimize, ranking.Direction);
        Assert.AreEqual(5d, ranking.CriterionValues[1]);
        Assert.AreEqual(1, ranking.Ranks[1]);
        Assert.AreEqual(1, ranking.RecommendedIndex);
        Assert.IsFalse(ranking.RecommendationWithheld);
        Assert.AreEqual("Alt A", ranking.RecommendedAlternative);
    }

    /// <summary>Verifies the withheld recommendation: index −1, empty name, flag set.</summary>
    [TestMethod]
    public void Test_Ctor_WithheldRecommendation()
    {
        // Act
        var ranking = Build(recommendedIndex: -1);

        // Assert
        Assert.IsTrue(ranking.RecommendationWithheld);
        Assert.AreEqual(string.Empty, ranking.RecommendedAlternative);
    }
}
