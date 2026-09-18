using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>Unit tests for <see cref="RiskContribution"/> shares and detached copies.</summary>
/// <remarks>
/// <para><b>Authors:</b> Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil</para>
/// </remarks>
[TestClass]
public class RiskContributionTests
{
    /// <summary>Verifies share behavior for positive, zero, and negative parents.</summary>
    [TestMethod]
    public void Test_ShareOf_HandlesZeroParent()
    {
        Assert.AreEqual(0.25d, RiskContribution.ShareOf(2d, 8d));
        Assert.AreEqual(0d, RiskContribution.ShareOf(2d, 0d));
        Assert.AreEqual(0d, RiskContribution.ShareOf(2d, -1d));
    }

    /// <summary>Verifies Copy returns a detached value and preserves null.</summary>
    [TestMethod]
    public void Test_Copy_IsDetached()
    {
        var source = new RiskContribution { FailureProbability = 0.1d, FailureMean = 2d, ExcessMean = 1d };
        var copy = RiskContribution.Copy(source);

        Assert.IsNotNull(copy);
        Assert.AreNotSame(source, copy);
        Assert.AreEqual(source.FailureProbability, copy!.FailureProbability);
        Assert.AreEqual(source.FailureMean, copy.FailureMean);
        Assert.AreEqual(source.ExcessMean, copy.ExcessMean);
        Assert.IsNull(RiskContribution.Copy(null));
    }
}
