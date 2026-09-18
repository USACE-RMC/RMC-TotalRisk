using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>Unit tests for <see cref="ConvergenceDiagnostics"/> result storage.</summary>
/// <remarks>
/// <para><b>Authors:</b> Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil</para>
/// </remarks>
[TestClass]
public class ConvergenceDiagnosticsTests
{
    /// <summary>Verifies aggregate and indicator fields preserve diagnostic values.</summary>
    [TestMethod]
    public void Test_Properties_PreserveValues()
    {
        var diagnostics = new ConvergenceDiagnostics
        {
            TotalFunctionEvaluations = 100d,
            MeanFunctionEvaluations = 50d,
            MaxFunctionEvaluations = 60d,
            MeanStandardError = 0.1d,
            MedianStandardError = 0.09d,
            MaxStandardError = 0.2d,
            MeanChiSquared = 1.1d,
            MaxChiSquared = 1.4d,
        };
        diagnostics.Indicators.Add(new ConvergenceIndicator
        {
            Label = "Mean Total Risk",
            Mean = 20d,
            EnsembleStandardError = 1d,
            RelativeStandardError = 0.05d,
            CiHalfWidth = 2d,
            RelativeCiHalfWidth = 0.1d,
        });

        Assert.AreEqual(100d, diagnostics.TotalFunctionEvaluations);
        Assert.AreEqual(0.09d, diagnostics.MedianStandardError);
        Assert.AreEqual(1.4d, diagnostics.MaxChiSquared);
        Assert.AreEqual("Mean Total Risk", diagnostics.Indicators[0].Label);
        Assert.AreEqual(0.1d, diagnostics.Indicators[0].RelativeCiHalfWidth);
    }
}
