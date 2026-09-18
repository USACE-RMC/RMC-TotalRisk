using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="JointConvergenceCertificate"/> — the container, and the runtime
/// lifecycle on the analysis: populated by a joint run with the recording-pass values (distinct
/// from the stored warm-up chi-squared), absent on the additive path, and cleared at run start.
/// </summary>
[TestClass]
public class JointConvergenceCertificateTests
{
    /// <summary>Builds one uncertain single-mode component.</summary>
    private static SystemComponent Component(string name)
    {
        var hazard = new TabularHazard
        {
            Name = name + " frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(10d)),
                    new UncertainOrdinate(0.001d, new Deterministic(30d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
        var fragility = new TabularResponse
        {
            Name = name + " fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)),
                    new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
        var consequence = new TabularConsequence
        {
            Name = name + " loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(30d, new Deterministic(1000d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
        var component = new SystemComponent { Name = name };
        component.HazardFunction = hazard;
        component.AddFailureMode(new FailureMode(null, null, fragility, consequence));
        return component;
    }

    /// <summary>
    /// Builds a two-component analysis on the requested system method, with small VEGAS budgets
    /// (the certificate lifecycle needs the passes to run, not to converge tightly).
    /// </summary>
    private static RiskAnalysis Build(SystemRiskType method, bool meanOnly = false)
    {
        var analysis = new RiskAnalysis(new[] { Component("Dam"), Component("Levee") });
        analysis.Options.SystemRiskMethod = method;
        analysis.Options.EstimateMeanRiskOnly = meanOnly;
        if (!meanOnly) analysis.Options.Realizations = 100;
        analysis.Options.UseDefaults = false;
        analysis.Options.WarmupEvaluations = 2000;
        analysis.Options.WarmupCycles = 3;
        analysis.Options.FinalEvaluations = 2000;
        return analysis;
    }

    /// <summary>Verifies the container stores every slot.</summary>
    [TestMethod]
    public void Test_Construction_StoresValues()
    {
        // Act
        var certificate = new JointConvergenceCertificate(100, 0.8d, 0.002d, 0.9d, 1.7d, 0.003d, 0.01d);

        // Assert
        Assert.AreEqual(100, certificate.RealizationCount);
        Assert.AreEqual(0.8d, certificate.MeanPassRecordingChiSquared, 0d);
        Assert.AreEqual(0.002d, certificate.MeanPassRelativeStandardError, 0d);
        Assert.AreEqual(0.9d, certificate.MeanRecordingChiSquared, 0d);
        Assert.AreEqual(1.7d, certificate.MaxRecordingChiSquared, 0d);
        Assert.AreEqual(0.003d, certificate.MeanRelativeStandardError, 0d);
        Assert.AreEqual(0.01d, certificate.MaxRelativeStandardError, 0d);
    }

    /// <summary>
    /// Verifies the lifecycle: a joint run publishes a certificate with finite recording-pass
    /// values whose mean-pass chi-squared is the recording passes' — not the stored warm-up
    /// value — and an additive run of the same system publishes none; a following additive run
    /// on the same analysis clears the joint certificate.
    /// </summary>
    [TestMethod]
    public void Test_Lifecycle_JointPopulates_AdditiveAbsent()
    {
        // Arrange / Act — the joint run.
        var joint = Build(SystemRiskType.JointRiskMethod);
        joint.RunAsync().GetAwaiter().GetResult();
        var certificate = joint.JointCertificate;

        // Assert — populated with the ensemble captures; the full-uncertainty pass assembles
        // its published mean from percentiles and integrates no separate mean pass, so the
        // mean-pass slots stay NaN by contract.
        Assert.IsNotNull(certificate);
        Assert.AreEqual(100, certificate!.RealizationCount);
        Assert.IsTrue(double.IsNaN(certificate.MeanPassRecordingChiSquared));
        Assert.IsTrue(certificate.MeanRecordingChiSquared >= 0d);
        Assert.IsTrue(certificate.MeanRelativeStandardError > 0d);
        Assert.IsTrue(certificate.MaxRecordingChiSquared >= certificate.MeanRecordingChiSquared);
        Assert.IsTrue(certificate.MaxRelativeStandardError >= certificate.MeanRelativeStandardError);

        // The distinction the certificate exists to carry: the recording-pass chi-squared
        // aggregate is not the stored warm-up aggregate (different pass counts and freshly
        // reset accumulators; a bit coincidence does not occur on this fixture).
        double storedWarmupMean = 0d;
        for (int i = 0; i < joint.RiskResults!.Count; i++)
        {
            storedWarmupMean += joint.RiskResults[i]!.ChiSquared;
        }
        storedWarmupMean /= joint.RiskResults.Count;
        Assert.AreNotEqual(storedWarmupMean, certificate.MeanRecordingChiSquared,
            "The certificate must carry the recording passes' chi-squared, not the stored warm-up values.");

        // A mean-only joint run populates the mean-pass slots and records no ensemble.
        var meanOnly = Build(SystemRiskType.JointRiskMethod, meanOnly: true);
        meanOnly.RunAsync().GetAwaiter().GetResult();
        var meanCertificate = meanOnly.JointCertificate;
        Assert.IsNotNull(meanCertificate);
        Assert.AreEqual(0, meanCertificate!.RealizationCount);
        Assert.IsFalse(double.IsNaN(meanCertificate.MeanPassRecordingChiSquared));
        Assert.IsTrue(meanCertificate.MeanPassRelativeStandardError > 0d);

        // The additive method publishes no certificate, and re-running clears the joint one.
        var additive = Build(SystemRiskType.AdditiveRiskMethod);
        additive.RunAsync().GetAwaiter().GetResult();
        Assert.IsNull(additive.JointCertificate);

        joint.Options.SystemRiskMethod = SystemRiskType.AdditiveRiskMethod;
        joint.RunAsync().GetAwaiter().GetResult();
        Assert.IsNull(joint.JointCertificate);
    }
}
