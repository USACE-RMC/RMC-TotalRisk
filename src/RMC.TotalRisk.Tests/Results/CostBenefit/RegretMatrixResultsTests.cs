using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the regret-matrix container: guards, the rectangular-matrix rules, the pick-index
/// ranges, and the echoes.
/// </summary>
[TestClass]
public class RegretMatrixResultsTests
{
    /// <summary>Builds a 2-alternative × 3-state block.</summary>
    /// <param name="valueColumns">The value-matrix column count (3 = rectangular).</param>
    /// <param name="minimaxIndex">The minimax pick.</param>
    /// <param name="blockSize">The realizations per combination.</param>
    /// <returns>The results block.</returns>
    private static RegretMatrixResults Build(int valueColumns = 3, int minimaxIndex = 1, int blockSize = 4)
    {
        var values = new[] { new double[valueColumns], new double[valueColumns] };
        var errors = new[] { new double[3], new double[3] };
        var regrets = new[] { new double[3], new double[3] };
        return new RegretMatrixResults("Mean of Total type 0", ObjectiveDirection.Minimize,
            new[] { "Baseline", "Alt A" }, new[] { "s1", "s2", "s3" }, new[] { 0.2d, 0.5d, 0.3d },
            values, errors, regrets, new[] { 1d, 0.5d }, new[] { 0.6d, 0.2d }, new[] { 1, 2 },
            minimaxIndex, 1, blockSize, "block-noise k·SE/√M");
    }

    /// <summary>Verifies the guards: rectangular matrices, pick ranges, and the block size.</summary>
    [TestMethod]
    public void Test_Ctor_Guards()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(() => Build(valueColumns: 2));
        Assert.ThrowsException<ArgumentException>(() => Build(minimaxIndex: 2));
        Assert.ThrowsException<ArgumentException>(() => Build(minimaxIndex: -2));
        Assert.ThrowsException<ArgumentException>(() => Build(blockSize: 0));
        Assert.ThrowsException<ArgumentNullException>(() => new RegretMatrixResults(
            null!, ObjectiveDirection.Minimize, new[] { "A" }, new[] { "s" }, new[] { 1d },
            new[] { new[] { 1d } }, new[] { new[] { 1d } }, new[] { new[] { 0d } },
            new[] { 0d }, new[] { 0d }, new[] { 1 }, 0, 0, 1, "n"));
    }

    /// <summary>Verifies the echoes and the frozen matrices.</summary>
    [TestMethod]
    public void Test_Ctor_EchoesAndMatrices()
    {
        // Act
        var block = Build();

        // Assert
        Assert.AreEqual("Mean of Total type 0", block.CriterionLabel);
        Assert.AreEqual(ObjectiveDirection.Minimize, block.Direction);
        Assert.AreEqual(2, block.AlternativeNames.Count);
        Assert.AreEqual(3, block.StateLabels.Count);
        Assert.AreEqual(0.5d, block.StateWeights[1]);
        Assert.AreEqual(3, block.Values[0].Count);
        Assert.AreEqual(3, block.StandardErrors[1].Count);
        Assert.AreEqual(3, block.Regrets[0].Count);
        Assert.AreEqual(1d, block.MaxRegrets[0]);
        Assert.AreEqual(0.2d, block.ExpectedRegrets[1]);
        Assert.AreEqual(2, block.WinCounts[1]);
        Assert.AreEqual(1, block.MinimaxIndex);
        Assert.AreEqual(1, block.ExpectedIndex);
        Assert.AreEqual(4, block.RealizationsPerCombination);
        Assert.AreEqual("block-noise k·SE/√M", block.NoiseLabel);
    }
}
