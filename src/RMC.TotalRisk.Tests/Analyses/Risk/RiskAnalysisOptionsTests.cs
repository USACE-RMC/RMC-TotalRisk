using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for <see cref="RiskAnalysisOptions"/> — the v1.0 defaults matrix, validation
/// ranges, the XML round trip, the canonical-hash recipe (every compute field moves the hash;
/// <c>UseDefaults</c> is inert), integration-default derivation, and change notification.
/// </summary>
[TestClass]
public class RiskAnalysisOptionsTests
{
    /// <summary>Verifies every default of the v1.0 option surface and the v1.1 additions.</summary>
    [TestMethod]
    public void Test_Defaults_MatchContract()
    {
        // Act
        var options = new RiskAnalysisOptions();

        // Assert — the D-3 defaults table, row by row.
        Assert.IsTrue(options.EstimateMeanRiskOnly);
        Assert.AreEqual(1000, options.Realizations);
        Assert.AreEqual(0.9d, options.ConfidenceIntervalWidth, 0d);
        Assert.AreEqual(12345, options.PRNGSeed);
        Assert.AreEqual(200, options.LECOutputLength);
        Assert.AreEqual(SamplingScheme.LatinHypercube, options.SamplingScheme);
        Assert.AreEqual(RiskAnalysisMode.Risk, options.Mode);
        Assert.AreEqual(RiskIntegrand.MeanTotalRisk, options.RiskIntegrand);
        Assert.AreEqual(SystemRiskType.AdditiveRiskMethod, options.SystemRiskMethod);
        Assert.AreEqual(JointConsequenceType.Additive, options.JointConsequences);
        Assert.AreEqual(DependencyType.Independent, options.ComponentHazardDependency);
        Assert.IsNull(options.HazardCorrelationMatrix);
        Assert.AreEqual(0d, options.ConsequenceThreshold, 0d);
        Assert.AreEqual(0.01d, options.Alpha, 0d);
        Assert.AreEqual(1_000_000, options.MaxEvaluations);
        Assert.AreEqual(100, options.MaxDepth);
        Assert.AreEqual(1e-8, options.Tolerance, 0d);
        Assert.AreEqual(1000, options.WarmupEvaluations);
        Assert.AreEqual(5, options.WarmupCycles);
        Assert.AreEqual(10_000, options.FinalEvaluations);
        Assert.IsTrue(options.UseDefaults);
        Assert.AreEqual(VegasTailFocusMode.Automatic, options.VegasTailFocusMode);
        Assert.AreEqual(1d, options.VegasTailFocusParameter, 0d);
        Assert.AreEqual(4096, options.SystemConvolutionPoints);
        Assert.AreEqual(1e-4, options.EnsembleTolerance, 0d);
        Assert.AreEqual(0, options.EnsembleMinDepth);
        Assert.IsTrue(options.Validate().IsValid);
    }

    /// <summary>Verifies the validation range matrix: every out-of-range field errors.</summary>
    [TestMethod]
    public void Test_Validate_RangeMatrix()
    {
        // Arrange — each mutation should produce exactly one error family.
        var cases = new (Action<RiskAnalysisOptions> Mutate, string Fragment)[]
        {
            (o => o.Realizations = 99, "realizations"),
            (o => o.Realizations = 10_001, "realizations"),
            (o => o.ConfidenceIntervalWidth = 0d, "confidence interval"),
            (o => o.ConfidenceIntervalWidth = 1d, "confidence interval"),
            (o => o.PRNGSeed = 0, "PRNG seed"),
            (o => o.LECOutputLength = 49, "LEC output length"),
            (o => o.LECOutputLength = 1001, "LEC output length"),
            (o => o.Alpha = 0d, "alpha"),
            (o => o.MaxEvaluations = 9_999, "evaluations"),
            (o => o.MaxDepth = 9, "depth"),
            (o => o.Tolerance = 0.1d, "tolerance"),
            (o => o.WarmupEvaluations = 99, "warm-up evaluations"),
            (o => o.WarmupCycles = 0, "warm-up cycles"),
            (o => o.FinalEvaluations = 999, "final evaluations"),
            (o => o.VegasTailFocusParameter = 0.5d, "tail focus parameter"),
            (o => o.SystemConvolutionPoints = 4095, "convolution points"),
            (o => o.EnsembleTolerance = 0.1d, "ensemble integrator tolerance"),
            (o => o.EnsembleMinDepth = 11, "ensemble integrator minimum depth"),
        };

        // Act / Assert
        foreach (var (mutate, fragment) in cases)
        {
            var options = new RiskAnalysisOptions();
            mutate(options);
            var (isValid, messages) = options.Validate();
            Assert.IsFalse(isValid, $"Expected invalid for '{fragment}'.");
            Assert.IsTrue(messages.Any(m => m.StartsWith("Error:", StringComparison.Ordinal)
                    && m.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
                $"Expected an error mentioning '{fragment}'; got: {string.Join(" | ", messages)}");
        }

        // A thin ensemble is advisory only.
        var thin = new RiskAnalysisOptions { Realizations = 500 };
        var (thinValid, thinMessages) = thin.Validate();
        Assert.IsTrue(thinValid);
        Assert.IsTrue(thinMessages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal)));
    }

    /// <summary>Verifies the XML round trip preserves every field, including the correlation matrix mode.</summary>
    [TestMethod]
    public void Test_ToXElement_RoundTrip()
    {
        // Arrange
        var options = new RiskAnalysisOptions
        {
            EstimateMeanRiskOnly = false,
            Realizations = 2500,
            ConfidenceIntervalWidth = 0.8d,
            PRNGSeed = 999,
            LECOutputLength = 300,
            SamplingScheme = SamplingScheme.MonteCarlo,
            RiskIntegrand = RiskIntegrand.TailConditionalRisk,
            SystemRiskMethod = SystemRiskType.JointRiskMethod,
            JointConsequences = JointConsequenceType.Maximum,
            ComponentHazardDependency = DependencyType.CorrelationMatrix,
            HazardCorrelationMatrix = new[,] { { 1d, 0.5d }, { 0.5d, 1d } },
            ConsequenceThreshold = 10d,
            Alpha = 0.02d,
            UseDefaults = false,
            MaxEvaluations = 50_000,
            MaxDepth = 50,
            Tolerance = 1e-6,
            WarmupEvaluations = 2000,
            WarmupCycles = 7,
            FinalEvaluations = 20_000,
            VegasTailFocusMode = VegasTailFocusMode.Manual,
            VegasTailFocusParameter = 4d,
            SystemConvolutionPoints = 8192,
            EnsembleTolerance = 1e-6,
            EnsembleMinDepth = 2,
        };

        // Act
        var restored = new RiskAnalysisOptions(options.ToXElement());

        // Assert
        Assert.IsFalse(restored.EstimateMeanRiskOnly);
        Assert.AreEqual(2500, restored.Realizations);
        Assert.AreEqual(0.8d, restored.ConfidenceIntervalWidth, 0d);
        Assert.AreEqual(999, restored.PRNGSeed);
        Assert.AreEqual(300, restored.LECOutputLength);
        Assert.AreEqual(SamplingScheme.MonteCarlo, restored.SamplingScheme);
        Assert.AreEqual(RiskIntegrand.TailConditionalRisk, restored.RiskIntegrand);
        Assert.AreEqual(SystemRiskType.JointRiskMethod, restored.SystemRiskMethod);
        Assert.AreEqual(JointConsequenceType.Maximum, restored.JointConsequences);
        Assert.AreEqual(DependencyType.CorrelationMatrix, restored.ComponentHazardDependency);
        Assert.IsNotNull(restored.HazardCorrelationMatrix);
        Assert.AreEqual(0.5d, restored.HazardCorrelationMatrix![0, 1], 0d);
        Assert.AreEqual(10d, restored.ConsequenceThreshold, 0d);
        Assert.AreEqual(0.02d, restored.Alpha, 0d);
        Assert.IsFalse(restored.UseDefaults);
        Assert.AreEqual(50_000, restored.MaxEvaluations);
        Assert.AreEqual(50, restored.MaxDepth);
        Assert.AreEqual(1e-6, restored.Tolerance, 0d);
        Assert.AreEqual(2000, restored.WarmupEvaluations);
        Assert.AreEqual(7, restored.WarmupCycles);
        Assert.AreEqual(20_000, restored.FinalEvaluations);
        Assert.AreEqual(VegasTailFocusMode.Manual, restored.VegasTailFocusMode);
        Assert.AreEqual(4d, restored.VegasTailFocusParameter, 0d);
        Assert.AreEqual(8192, restored.SystemConvolutionPoints);
        Assert.AreEqual(1e-6, restored.EnsembleTolerance, 0d);
        Assert.AreEqual(2, restored.EnsembleMinDepth);
        Assert.AreEqual(RiskAnalysisMode.Risk, restored.Mode);
    }

    /// <summary>
    /// Verifies the canonical-hash recipe: every compute-relevant field moves the hash, while
    /// <c>UseDefaults</c> is stripped and the correlation matrix hashes only under the
    /// correlation-matrix dependency mode.
    /// </summary>
    [TestMethod]
    public void Test_CanonicalHash_Recipe()
    {
        // Arrange — mutations that must each move the hash. UseDefaults is set false first so
        // integration-setting mutations are not overwritten by the defaults reset.
        var computeMutations = new (string Field, Action<RiskAnalysisOptions> Mutate)[]
        {
            (nameof(RiskAnalysisOptions.EstimateMeanRiskOnly), o => o.EstimateMeanRiskOnly = false),
            (nameof(RiskAnalysisOptions.Realizations), o => o.Realizations = 2000),
            (nameof(RiskAnalysisOptions.ConfidenceIntervalWidth), o => o.ConfidenceIntervalWidth = 0.8d),
            (nameof(RiskAnalysisOptions.PRNGSeed), o => o.PRNGSeed = 54321),
            (nameof(RiskAnalysisOptions.LECOutputLength), o => o.LECOutputLength = 400),
            (nameof(RiskAnalysisOptions.SamplingScheme), o => o.SamplingScheme = SamplingScheme.MonteCarlo),
            (nameof(RiskAnalysisOptions.Mode), o => o.Mode = RiskAnalysisMode.Reliability),
            (nameof(RiskAnalysisOptions.RiskIntegrand), o => o.RiskIntegrand = RiskIntegrand.Balanced),
            (nameof(RiskAnalysisOptions.SystemRiskMethod), o => o.SystemRiskMethod = SystemRiskType.JointRiskMethod),
            (nameof(RiskAnalysisOptions.JointConsequences), o => o.JointConsequences = JointConsequenceType.Minimum),
            (nameof(RiskAnalysisOptions.ComponentHazardDependency), o => o.ComponentHazardDependency = DependencyType.PerfectlyPositive),
            (nameof(RiskAnalysisOptions.ConsequenceThreshold), o => o.ConsequenceThreshold = 5d),
            (nameof(RiskAnalysisOptions.Alpha), o => o.Alpha = 0.05d),
            (nameof(RiskAnalysisOptions.MaxEvaluations), o => o.MaxEvaluations = 100_000),
            (nameof(RiskAnalysisOptions.MaxDepth), o => o.MaxDepth = 55),
            (nameof(RiskAnalysisOptions.Tolerance), o => o.Tolerance = 1e-9),
            (nameof(RiskAnalysisOptions.WarmupEvaluations), o => o.WarmupEvaluations = 1500),
            (nameof(RiskAnalysisOptions.WarmupCycles), o => o.WarmupCycles = 9),
            (nameof(RiskAnalysisOptions.FinalEvaluations), o => o.FinalEvaluations = 12_345),
            (nameof(RiskAnalysisOptions.VegasTailFocusMode), o => o.VegasTailFocusMode = VegasTailFocusMode.None),
            (nameof(RiskAnalysisOptions.VegasTailFocusParameter), o => o.VegasTailFocusParameter = 3d),
            (nameof(RiskAnalysisOptions.SystemConvolutionPoints), o => o.SystemConvolutionPoints = 8192),
            (nameof(RiskAnalysisOptions.EnsembleTolerance), o => o.EnsembleTolerance = 1e-6),
            (nameof(RiskAnalysisOptions.EnsembleMinDepth), o => o.EnsembleMinDepth = 2),
        };

        // Act / Assert — each compute field moves the hash.
        byte[] Baseline() => new RiskAnalysisOptions { UseDefaults = false }.CanonicalHash();
        foreach (var (field, mutate) in computeMutations)
        {
            var options = new RiskAnalysisOptions { UseDefaults = false };
            mutate(options);
            CollectionAssert.AreNotEqual(Baseline(), options.CanonicalHash(), $"Mutating {field} must move the hash.");
        }

        // UseDefaults is convenience metadata — inert (compare with identical integration
        // settings, since flipping it true re-applies the defaults).
        var withDefaultsFlag = new RiskAnalysisOptions();
        var withoutDefaultsFlag = new RiskAnalysisOptions { UseDefaults = false };
        CollectionAssert.AreEqual(withDefaultsFlag.CanonicalHash(), withoutDefaultsFlag.CanonicalHash(),
            "UseDefaults records who wrote the settings, not what they are.");

        // The correlation matrix hashes only under the correlation-matrix dependency mode.
        var independentWithMatrix = new RiskAnalysisOptions { HazardCorrelationMatrix = new[,] { { 1d, 0.3d }, { 0.3d, 1d } } };
        CollectionAssert.AreEqual(Baseline(), independentWithMatrix.CanonicalHash(),
            "Under an automatic dependency mode the matrix is derived state.");
        var correlationA = new RiskAnalysisOptions
        {
            ComponentHazardDependency = DependencyType.CorrelationMatrix,
            HazardCorrelationMatrix = new[,] { { 1d, 0.3d }, { 0.3d, 1d } },
        };
        var correlationB = new RiskAnalysisOptions
        {
            ComponentHazardDependency = DependencyType.CorrelationMatrix,
            HazardCorrelationMatrix = new[,] { { 1d, 0.7d }, { 0.7d, 1d } },
        };
        CollectionAssert.AreNotEqual(correlationA.CanonicalHash(), correlationB.CanonicalHash(),
            "User matrix content must hash under the correlation-matrix mode.");
    }

    /// <summary>Verifies the integration-default derivation and the UseDefaults reset behavior.</summary>
    [TestMethod]
    public void Test_SetIntegrationDefaults_ComponentScaling()
    {
        // Arrange
        var options = new RiskAnalysisOptions { UseDefaults = false, WarmupEvaluations = 111, FinalEvaluations = 2222 };

        // Act / Assert — explicit derivation at five components.
        options.SetIntegrationDefaults(componentCount: 5);
        Assert.AreEqual(5000, options.WarmupEvaluations);
        Assert.AreEqual(5, options.WarmupCycles);
        Assert.AreEqual(50_000, options.FinalEvaluations);
        Assert.AreEqual(1_000_000, options.MaxEvaluations);

        // The caps bind at large component counts.
        options.SetIntegrationDefaults(componentCount: 100);
        Assert.AreEqual(50_000, options.WarmupEvaluations);
        Assert.AreEqual(100_000, options.FinalEvaluations);

        // Setting UseDefaults true re-applies the most recently supplied owning component count.
        options.WarmupEvaluations = 777;
        options.UseDefaults = true;
        Assert.AreEqual(50_000, options.WarmupEvaluations);
        Assert.AreEqual(100_000, options.FinalEvaluations);
    }


    /// <summary>
    /// Explicit integration assignments disable default tracking, including an assignment of the
    /// current value, while materialized and explicit effective settings hash identically.
    /// </summary>
    [TestMethod]
    public void Test_UseDefaults_ExplicitAssignmentAndEffectiveHash()
    {
        var assigned = new RiskAnalysisOptions();
        assigned.MaxEvaluations = assigned.MaxEvaluations;
        Assert.IsFalse(assigned.UseDefaults, "An explicit assignment must opt out even when the numeric value is unchanged.");

        var materialized = new RiskAnalysisOptions();
        materialized.SetDefaultComponentCount(5);
        Assert.IsTrue(materialized.UseDefaults);
        Assert.AreEqual(5000, materialized.WarmupEvaluations);
        Assert.AreEqual(50_000, materialized.FinalEvaluations);

        var explicitValues = new RiskAnalysisOptions { UseDefaults = false };
        explicitValues.MaxEvaluations = materialized.MaxEvaluations;
        explicitValues.MaxDepth = materialized.MaxDepth;
        explicitValues.Tolerance = materialized.Tolerance;
        explicitValues.WarmupEvaluations = materialized.WarmupEvaluations;
        explicitValues.WarmupCycles = materialized.WarmupCycles;
        explicitValues.FinalEvaluations = materialized.FinalEvaluations;
        explicitValues.EnsembleTolerance = materialized.EnsembleTolerance;
        explicitValues.EnsembleMinDepth = materialized.EnsembleMinDepth;

        CollectionAssert.AreEqual(materialized.CanonicalHash(), explicitValues.CanonicalHash(),
            "UseDefaults may be hash-inert only after both instances carry the same effective settings.");
        Assert.AreEqual(materialized.WarmupEvaluations, explicitValues.WarmupEvaluations);
        Assert.AreEqual(materialized.FinalEvaluations, explicitValues.FinalEvaluations);

        materialized.SetDefaultComponentCount(2);
        CollectionAssert.AreNotEqual(materialized.CanonicalHash(), explicitValues.CanonicalHash(),
            "Changing an owning component count must materialize and hash the changed computation before execution.");
    }
    /// <summary>Verifies property change notification across representative fields.</summary>
    [TestMethod]
    public void Test_PropertyChange_Notifies()
    {
        // Arrange
        var options = new RiskAnalysisOptions();
        var raised = new List<string>();
        options.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        // Act
        options.Realizations = 5000;
        options.RiskIntegrand = RiskIntegrand.SecondMoment;
        options.Realizations = 5000; // no-op

        // Assert
        CollectionAssert.AreEqual(new[]
        {
            nameof(RiskAnalysisOptions.Realizations),
            nameof(RiskAnalysisOptions.RiskIntegrand),
        }, raised);
    }

    /// <summary>Verifies the constructor argument contract.</summary>
    [TestMethod]
    public void Test_Constructor_NullElement_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new RiskAnalysisOptions(null!));
    }

    /// <summary>
    /// Verifies the tolerable-risk criteria's conditional serialization: an empty collection
    /// leaves the serialized form and canonical hash of the criteria-free configuration
    /// unchanged, configured criteria round-trip with hash equality, and clearing them restores
    /// the original form and hash exactly.
    /// </summary>
    [TestMethod]
    public void Test_TolerableRiskCriteria_ConditionalPresenceAndHash()
    {
        // Arrange — the criteria-free baseline form and hash.
        var options = new RiskAnalysisOptions();
        byte[] baseline = options.CanonicalHash();
        string baselineXml = options.ToXElement().ToString();
        Assert.IsNull(options.ToXElement().Element(nameof(RiskAnalysisOptions.TolerableRiskCriteria)));

        // Act — configure two criteria.
        options.TolerableRiskCriteria.Add(new TolerableRiskCriterion());
        options.TolerableRiskCriteria.Add(new TolerableRiskCriterion(RiskMeasure.TotalProbability, RiskType.Fail, 0, 1e-4));

        // Assert — a deliberate hash event when present, with a lossless round trip.
        CollectionAssert.AreNotEqual(baseline, options.CanonicalHash(), "Configured criteria are compute-relevant hashed content.");
        var restored = new RiskAnalysisOptions(options.ToXElement());
        Assert.AreEqual(2, restored.TolerableRiskCriteria.Count);
        Assert.AreEqual(RiskMeasure.Mean, restored.TolerableRiskCriteria[0].Measure);
        Assert.AreEqual(1e-3, restored.TolerableRiskCriteria[0].Threshold, 0d);
        Assert.AreEqual(RiskMeasure.TotalProbability, restored.TolerableRiskCriteria[1].Measure);
        Assert.AreEqual(RiskType.Fail, restored.TolerableRiskCriteria[1].RiskType);
        CollectionAssert.AreEqual(options.CanonicalHash(), restored.CanonicalHash());

        // Clearing restores the criteria-free form and hash byte-for-byte.
        options.TolerableRiskCriteria.Clear();
        CollectionAssert.AreEqual(baseline, options.CanonicalHash());
        Assert.AreEqual(baselineXml, options.ToXElement().ToString());
    }

    /// <summary>
    /// Verifies the joint Sobol driver option's conditional serialization: the default writes
    /// no attribute and a set-then-clear restores the original form and hash byte-for-byte,
    /// while enabling it is a deliberate hash event that round-trips faithfully.
    /// </summary>
    [TestMethod]
    public void Test_UseSobolJointSampling_ConditionalPresenceAndHash()
    {
        // Arrange — the default baseline form and hash.
        var options = new RiskAnalysisOptions();
        byte[] baseline = options.CanonicalHash();
        string baselineXml = options.ToXElement().ToString();
        Assert.IsNull(options.ToXElement().Attribute(nameof(RiskAnalysisOptions.UseSobolJointSampling)));

        // The set-then-clear path is byte-inert.
        options.UseSobolJointSampling = true;
        options.UseSobolJointSampling = false;
        CollectionAssert.AreEqual(baseline, options.CanonicalHash());
        Assert.AreEqual(baselineXml, options.ToXElement().ToString());

        // Enabling is a deliberate hash event with a lossless round trip.
        options.UseSobolJointSampling = true;
        Assert.IsNotNull(options.ToXElement().Attribute(nameof(RiskAnalysisOptions.UseSobolJointSampling)));
        CollectionAssert.AreNotEqual(baseline, options.CanonicalHash(),
            "The enabled Sobol joint driver is compute-relevant hashed content.");
        var restored = new RiskAnalysisOptions(options.ToXElement());
        Assert.IsTrue(restored.UseSobolJointSampling);
        CollectionAssert.AreEqual(options.CanonicalHash(), restored.CanonicalHash());
    }

    /// <summary>
    /// Verifies criteria validation aggregation and change notification: an invalid criterion
    /// is an indexed Error on the options, and collection edits raise the property change the
    /// owning analysis invalidates on.
    /// </summary>
    [TestMethod]
    public void Test_TolerableRiskCriteria_ValidationAndNotification()
    {
        // Arrange
        var options = new RiskAnalysisOptions();
        var raised = new List<string?>();
        options.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Act — one valid, one invalid criterion.
        options.TolerableRiskCriteria.Add(new TolerableRiskCriterion());
        options.TolerableRiskCriteria.Add(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, double.NaN));

        // Assert — the indexed Error and the notifications.
        var (isValid, messages) = options.Validate();
        Assert.IsFalse(isValid);
        Assert.IsTrue(messages.Exists(m => m.StartsWith("Error:") && m.Contains("finite") && m.Contains("[Criterion 2]")),
            string.Join(Environment.NewLine, messages));
        Assert.AreEqual(2, raised.FindAll(n => n == nameof(RiskAnalysisOptions.TolerableRiskCriteria)).Count);

        // Removing the invalid entry restores validity.
        options.TolerableRiskCriteria.RemoveAt(1);
        Assert.IsTrue(options.Validate().IsValid);
    }
}
