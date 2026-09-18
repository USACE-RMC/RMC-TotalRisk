using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>Unit tests for <see cref="ConvergenceIndicator"/> adequacy evidence storage.</summary>
/// <remarks>
/// <para><b>Authors:</b> Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil</para>
/// </remarks>
[TestClass]
public class ConvergenceIndicatorTests
{
    /// <summary>Verifies all headline adequacy measures remain independent.</summary>
    [TestMethod]
    public void Test_Properties_PreserveValues()
    {
        var indicator = new ConvergenceIndicator
        {
            Label = "Annualized Failure Probability",
            Mean = 0.01d,
            EnsembleStandardError = 0.001d,
            RelativeStandardError = 0.1d,
            CiHalfWidth = 0.002d,
            RelativeCiHalfWidth = 0.2d,
        };

        Assert.AreEqual("Annualized Failure Probability", indicator.Label);
        Assert.AreEqual(0.01d, indicator.Mean);
        Assert.AreEqual(0.001d, indicator.EnsembleStandardError);
        Assert.AreEqual(0.1d, indicator.RelativeStandardError);
        Assert.AreEqual(0.002d, indicator.CiHalfWidth);
        Assert.AreEqual(0.2d, indicator.RelativeCiHalfWidth);
    }
}
