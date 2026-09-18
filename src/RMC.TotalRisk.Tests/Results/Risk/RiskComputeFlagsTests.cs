using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="RiskComputeFlags"/> — the per-realization computational-warning
/// flags and their sequential OR-reduction.
/// </summary>
[TestClass]
public class RiskComputeFlagsTests
{
    /// <summary>Verifies the OR-merge semantics and the aggregate query.</summary>
    [TestMethod]
    public void Test_MergeWith_OrSemantics()
    {
        // Arrange
        var target = new RiskComputeFlags { HasNegativeFailureConsequence = true };
        var other = new RiskComputeFlags { HasProbabilityGreaterThanOne = true };

        // Act
        target.MergeWith(other);
        target.MergeWith(null);

        // Assert
        Assert.IsTrue(target.HasNegativeFailureConsequence);
        Assert.IsTrue(target.HasProbabilityGreaterThanOne);
        Assert.IsFalse(target.HasNegativeNonFailureConsequence);
        Assert.IsFalse(target.HasNegativeExcessConsequence);
        Assert.IsTrue(target.Any);
        Assert.IsFalse(new RiskComputeFlags().Any);
    }
}
