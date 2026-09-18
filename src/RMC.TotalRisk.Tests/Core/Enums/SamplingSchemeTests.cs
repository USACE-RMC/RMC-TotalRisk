using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Core.Enums;

/// <summary>
/// Unit tests for <see cref="SamplingScheme"/> — pins the members and their order. The scheme is
/// serialized by name on the analysis options and is hashed compute content, so the member set
/// is append-only contract.
/// </summary>
[TestClass]
public class SamplingSchemeTests
{
    /// <summary>Pins the declared members and order.</summary>
    [TestMethod]
    public void Test_Members_Pinned()
    {
        // Assert
        CollectionAssert.AreEqual(
            new[] { "MonteCarlo", "LatinHypercube", "LatinHypercubeMedian", "ScrambledSobol" },
            Enum.GetNames<SamplingScheme>());
        Assert.AreEqual(0, (int)SamplingScheme.MonteCarlo);
        Assert.AreEqual(1, (int)SamplingScheme.LatinHypercube);
        Assert.AreEqual(2, (int)SamplingScheme.LatinHypercubeMedian);
        Assert.AreEqual(3, (int)SamplingScheme.ScrambledSobol);
    }

    /// <summary>The default is the Latin hypercube (the analysis-options default).</summary>
    [TestMethod]
    public void Test_OptionsDefault_IsLatinHypercube()
    {
        // Assert
        Assert.AreEqual(SamplingScheme.LatinHypercube, new RMC.TotalRisk.Analyses.Risk.RiskAnalysisOptions().SamplingScheme);
    }
}
