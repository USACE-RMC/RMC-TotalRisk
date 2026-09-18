using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="LogicTreeEnumerationMap"/> — the enumeration design's counts,
/// lexicographic combination decoding, per-realization weights, and constructor guards.
/// </summary>
[TestClass]
public class LogicTreeEnumerationMapTests
{
    /// <summary>Builds a two-axis design: 2 branches (0.5/0.5) × 3 branches (0.3/0.4/0.3).</summary>
    private static LogicTreeEnumerationMap TwoByThree(int realizationsPerCombination)
    {
        var hazardAxis = new LogicTreeAxis("Hazard Model", true, null, new List<LogicTreeBranch>
        {
            new LogicTreeBranch(0, 0.5d, 0.25d),
            new LogicTreeBranch(1, 0.5d, 0.75d),
        });
        var fragilityAxis = new LogicTreeAxis("Fragility Model", false, Guid.NewGuid(), new List<LogicTreeBranch>
        {
            new LogicTreeBranch(0, 0.3d, 0.15d),
            new LogicTreeBranch(1, 0.4d, 0.5d),
            new LogicTreeBranch(2, 0.3d, 0.85d),
        });
        var weights = new double[6];
        double[] fragilityWeights = { 0.3d, 0.4d, 0.3d };
        for (int h = 0; h < 2; h++)
        {
            for (int f = 0; f < 3; f++)
            {
                weights[(h * 3) + f] = 0.5d * fragilityWeights[f];
            }
        }
        return new LogicTreeEnumerationMap(new List<LogicTreeAxis> { hazardAxis, fragilityAxis },
            realizationsPerCombination, weights);
    }

    /// <summary>
    /// Verifies the counts: K is the branch-count product, N is K·M, and the axes and weights
    /// are exposed unchanged.
    /// </summary>
    [TestMethod]
    public void Test_Construction_Counts()
    {
        // Arrange & Act
        var map = TwoByThree(4);

        // Assert
        Assert.AreEqual(2, map.Axes.Count);
        Assert.AreEqual(6, map.CombinationCount);
        Assert.AreEqual(4, map.RealizationsPerCombination);
        Assert.AreEqual(24, map.RealizationCount);
        Assert.AreEqual(6, map.CombinationWeights.Count);
        Assert.AreEqual(0.5d * 0.4d, map.CombinationWeights[1], 0d);
    }

    /// <summary>
    /// Verifies the lexicographic decode with axis 0 most significant: combination c selects
    /// branch c/3 on the first axis and branch c%3 on the second, and every realization maps to
    /// combination index/M.
    /// </summary>
    [TestMethod]
    public void Test_Decode_LexicographicAssignment()
    {
        // Arrange
        var map = TwoByThree(4);

        // Act & Assert
        for (int c = 0; c < 6; c++)
        {
            Assert.AreEqual(c / 3, map.BranchIndexOf(c, 0), $"Combination {c}: first axis digit.");
            Assert.AreEqual(c % 3, map.BranchIndexOf(c, 1), $"Combination {c}: second axis digit.");
        }
        for (int i = 0; i < map.RealizationCount; i++)
        {
            Assert.AreEqual(i / 4, map.CombinationOf(i), $"Realization {i}: block assignment.");
        }
    }

    /// <summary>
    /// Verifies the per-realization weight is the combination weight divided by the block size.
    /// </summary>
    [TestMethod]
    public void Test_RealizationWeight_DividesBlockWeight()
    {
        // Arrange
        var map = TwoByThree(4);

        // Act & Assert
        for (int i = 0; i < map.RealizationCount; i++)
        {
            Assert.AreEqual(map.CombinationWeights[i / 4] / 4d, map.RealizationWeight(i), 0d);
        }
    }

    /// <summary>
    /// Verifies the constructor and query guards: null or empty axes, a mismatched weight
    /// count, a non-positive block size, and out-of-range query indices all throw.
    /// </summary>
    [TestMethod]
    public void Test_Guards_Throw()
    {
        // Arrange
        var axis = new LogicTreeAxis("Axis", true, null, new List<LogicTreeBranch>
        {
            new LogicTreeBranch(0, 0.5d, 0.25d),
            new LogicTreeBranch(1, 0.5d, 0.75d),
        });
        var map = new LogicTreeEnumerationMap(new List<LogicTreeAxis> { axis }, 2, new[] { 0.5d, 0.5d });

        // Act & Assert
        Assert.ThrowsException<ArgumentNullException>(() => new LogicTreeEnumerationMap(null!, 1, new[] { 1d }));
        Assert.ThrowsException<ArgumentNullException>(() => new LogicTreeEnumerationMap(new List<LogicTreeAxis> { axis }, 1, null!));
        Assert.ThrowsException<ArgumentException>(() => new LogicTreeEnumerationMap(new List<LogicTreeAxis>(), 1, new double[0]));
        Assert.ThrowsException<ArgumentException>(() => new LogicTreeEnumerationMap(new List<LogicTreeAxis> { axis }, 1, new[] { 1d }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new LogicTreeEnumerationMap(new List<LogicTreeAxis> { axis }, 0, new[] { 0.5d, 0.5d }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => map.CombinationOf(-1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => map.CombinationOf(4));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => map.BranchIndexOf(2, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => map.BranchIndexOf(0, 1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => map.RealizationWeight(4));
    }
}
