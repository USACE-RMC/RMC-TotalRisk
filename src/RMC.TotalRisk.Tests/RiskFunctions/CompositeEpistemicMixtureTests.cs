using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Tests.RiskFunctions;

/// <summary>
/// Unit tests for the <c>EpistemicMixture</c> composite mode across the four clusters: the
/// per-realization branch selection semantics, the declared selector dimension and its exact
/// generation recipe, the percentile-path composition sampling, the mean-path blend, the
/// conditional-presence serialization and deliberate hash events of the mode and the shared
/// epistemic variable, the validation matrices, and the shared-selector overwrite under an
/// ambient sharing scope.
/// </summary>
[TestClass]
public class CompositeEpistemicMixtureTests
{
    #region Fixtures

    /// <summary>Builds a labeled deterministic parametric hazard child over a Normal parent.</summary>
    private static ParametricUnivariateHazard HazardChild(string name, double mean, double sd)
    {
        var child = new ParametricUnivariateHazard
        {
            Name = name,
            SpecifiedHazard = "Peak Flow",
            HazardUnit = "cfs",
            ParentDistribution = new Normal(mean, sd),
            IsUncertain = false,
        };
        child.Estimate();
        return child;
    }

    /// <summary>Builds a labeled epistemic hazard composite over three Normal children (means 10/20/30, weights 0.3/0.4/0.3).</summary>
    private static CompositeHazard EpistemicHazard()
    {
        return new CompositeHazard(new[]
        {
            new WeightedHazardFunction(HazardChild("Low", 10d, 1d), 0.3d),
            new WeightedHazardFunction(HazardChild("Mid", 20d, 1d), 0.4d),
            new WeightedHazardFunction(HazardChild("High", 30d, 1d), 0.3d),
        })
        {
            Name = "Hazard Tree",
            SpecifiedHazard = "Peak Flow",
            HazardUnit = "cfs",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };
    }

    /// <summary>Builds a labeled deterministic tabular fragility rising (10 → 0) to (top → 1).</summary>
    private static TabularResponse ResponseChild(string name, double top)
    {
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Deterministic(0d)), new UncertainOrdinate(top, new Deterministic(1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a labeled uncertain tabular fragility (Triangular ordinates).</summary>
    private static TabularResponse UncertainResponseChild(string name)
    {
        return new TabularResponse
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(10d, new Triangular(0d, 0.05d, 0.1d)), new UncertainOrdinate(20d, new Triangular(0.7d, 0.9d, 1d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Triangular),
        };
    }

    /// <summary>Builds a labeled epistemic response composite over the given children at the given weights.</summary>
    private static CompositeResponse EpistemicResponse(params (IResponseFunction Function, double Weight)[] entries)
    {
        return new CompositeResponse(entries.Select(e => new WeightedResponseFunction(e.Function, e.Weight)))
        {
            Name = "Fragility Tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };
    }

    /// <summary>Builds a labeled Normal-uncertain tabular transform: (0 → N(low, 2)), (100 → N(high, 2)).</summary>
    private static TabularTransform TransformChild(string name, double low, double high)
    {
        return new TabularTransform
        {
            Name = name,
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Normal(low, 2d)), new UncertainOrdinate(100d, new Normal(high, 2d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal),
        };
    }

    /// <summary>Builds a labeled composite transform over three uncertain rating curves at 0.3/0.4/0.3.</summary>
    private static CompositeTransform TransformComposite(CompositeFunctionType type)
    {
        return new CompositeTransform(new[]
        {
            new WeightedTransformFunction(TransformChild("Rating A", 5d, 15d), 0.3d),
            new WeightedTransformFunction(TransformChild("Rating B", 10d, 20d), 0.4d),
            new WeightedTransformFunction(TransformChild("Rating C", 15d, 25d), 0.3d),
        })
        {
            Name = "Rating Tree",
            SpecifiedHazard = "Flow",
            HazardUnit = "cfs",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            CompositeFunctionType = type,
        };
    }

    /// <summary>Builds a labeled deterministic tabular consequence: (0 → 0), (10 → top).</summary>
    private static TabularConsequence ConsequenceChild(string name, double top)
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(10d, new Deterministic(top)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Labels a consequence composite with the shared axis metadata.</summary>
    private static CompositeConsequence LabelConsequence(CompositeConsequence composite)
    {
        composite.SpecifiedHazard = "Stage";
        composite.HazardUnit = "ft";
        composite.SpecifiedConsequence = "Life Loss";
        composite.ConsequenceUnit = "lives";
        return composite;
    }

    #endregion

    #region Hazard

    /// <summary>
    /// Verifies the epistemic index path: the composite declares one selector dimension whose
    /// matrix is exactly the declared recipe, each realization returns the selected child's own
    /// sample, and Latin hypercube stratification allocates branches essentially exactly N·ω —
    /// 3/4/3 at ten realizations under 0.3/0.4/0.3 (the cumulative boundaries sit on strata
    /// edges).
    /// </summary>
    [TestMethod]
    public void Test_Hazard_Epistemic_IndexPath_SelectsBranchWithDeclaredSelector()
    {
        // Arrange
        var composite = EpistemicHazard();
        Assert.AreEqual(1, composite.SamplingDimensions);
        Assert.IsFalse(composite.IsDeterministic, "Two or more positively weighted branches are a real knowledge draw.");
        composite.SetupSampler(10, 777, SamplingScheme.LatinHypercube);

        // Assert — the selector matrix is the exact declared recipe.
        var expected = LatinHypercube.Random(10, 1, SeedHelpers.ToPositiveSeed(777));
        var counts = new int[3];
        double[] means = { 10d, 20d, 30d };
        for (int i = 0; i < 10; i++)
        {
            Assert.AreEqual(expected[i, 0], composite.SampledPercentile(i, 0), 0d);
            int branch = composite.SelectedBranchIndex(i);
            counts[branch]++;
            Assert.AreEqual(means[branch], composite.SampleFunction(i).Mean, 1e-12,
                "The realization must carry the selected child's own distribution.");
        }
        CollectionAssert.AreEqual(new[] { 3, 4, 3 }, counts,
            "LHS stratification allocates branches exactly N·ω when the cumulative weights sit on strata edges.");
    }

    /// <summary>
    /// Verifies the epistemic mean path is the analytic blend — identical to the aleatory
    /// Mixture blend over the same children — and that the percentile path selects rather than
    /// blends.
    /// </summary>
    [TestMethod]
    public void Test_Hazard_Epistemic_MeanIsBlend_PercentileSelects()
    {
        // Arrange
        var epistemic = EpistemicHazard();
        var aleatory = EpistemicHazard();
        aleatory.CompositeCombinationType = CompositeCombinationType.Mixture;

        // Assert — the mean overload blends identically in both readings.
        var epistemicMean = epistemic.SampleFunction();
        var aleatoryMean = aleatory.SampleFunction();
        foreach (double x in new[] { 10d, 18d, 25d, 32d })
        {
            Assert.AreEqual(aleatoryMean.CDF(x), epistemicMean.CDF(x), 1e-12);
        }

        // The percentile path selects: 0.6 lands in the middle branch (cumulative 0.3/0.7).
        Assert.AreEqual(20d, epistemic.SampleFunction(0.6d).Mean, 1e-12);
        Assert.AreEqual(10d, epistemic.SampleFunction(0.1d).Mean, 1e-12);
        Assert.AreEqual(30d, epistemic.SampleFunction(0.95d).Mean, 1e-12);
    }

    /// <summary>
    /// Verifies the serialization and hash contract: selecting the epistemic mode is a
    /// deliberate hash event; the unbound form carries no <c>EpistemicVariable</c> attribute
    /// and set-then-clear is byte-inert; binding is a further hash event that round-trips in
    /// both serialization modes.
    /// </summary>
    [TestMethod]
    public void Test_Hazard_Epistemic_ConditionalPresenceAndHashEvents()
    {
        // Arrange — the aleatory baseline.
        var composite = EpistemicHazard();
        composite.CompositeCombinationType = CompositeCombinationType.Mixture;
        byte[] mixtureHash = composite.CanonicalHash();

        // Selecting the mode moves the hash.
        composite.CompositeCombinationType = CompositeCombinationType.EpistemicMixture;
        byte[] epistemicHash = composite.CanonicalHash();
        CollectionAssert.AreNotEqual(mixtureHash, epistemicHash, "Selecting EpistemicMixture is compute-relevant hashed content.");

        // The unbound form carries no attribute; set-then-clear is byte-inert.
        string unboundXml = composite.ToXElement().ToString();
        Assert.IsNull(composite.ToXElement().Attribute(nameof(CompositeHazard.EpistemicVariable)));
        composite.EpistemicVariable = "Flood Model";
        composite.EpistemicVariable = string.Empty;
        Assert.AreEqual(unboundXml, composite.ToXElement().ToString());
        CollectionAssert.AreEqual(epistemicHash, composite.CanonicalHash());

        // Binding is a hash event, present in both serialization modes, and round-trips.
        composite.EpistemicVariable = "Flood Model";
        CollectionAssert.AreNotEqual(epistemicHash, composite.CanonicalHash(), "Binding a shared variable is compute-relevant hashed content.");
        Assert.AreEqual("Flood Model", composite.ToXElement().Attribute(nameof(CompositeHazard.EpistemicVariable))?.Value);
        Assert.AreEqual("Flood Model", composite.ToXElement(RiskSerializationMode.ByReference).Attribute(nameof(CompositeHazard.EpistemicVariable))?.Value);
        var restored = new CompositeHazard(composite.ToXElement());
        Assert.AreEqual(CompositeCombinationType.EpistemicMixture, restored.CompositeCombinationType);
        Assert.AreEqual("Flood Model", restored.EpistemicVariable);
        CollectionAssert.AreEqual(composite.CanonicalHash(), restored.CanonicalHash());
    }

    /// <summary>
    /// Verifies the validation matrix: a variable named outside the epistemic mode is an Error
    /// (in Validate and the sampling gate alike), a single positively weighted epistemic branch
    /// is a degeneracy Warning, the weight rules apply in epistemic mode, and branch
    /// attribution refuses outside the mode.
    /// </summary>
    [TestMethod]
    public void Test_Hazard_Epistemic_ValidationMatrix()
    {
        // A variable on a non-epistemic composite is dead hashed content — Error + gate.
        var wrongMode = EpistemicHazard();
        wrongMode.CompositeCombinationType = CompositeCombinationType.Mixture;
        wrongMode.EpistemicVariable = "Flood Model";
        var (isValid, messages) = wrongMode.Validate();
        Assert.IsFalse(isValid);
        Assert.IsTrue(messages.Any(m => m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("Flood Model")));
        Assert.ThrowsException<InvalidOperationException>(() => wrongMode.SampleFunction());

        // A degenerate epistemic mixture warns.
        var degenerate = new CompositeHazard(new[]
        {
            new WeightedHazardFunction(HazardChild("Only", 10d, 1d), 1d),
            new WeightedHazardFunction(HazardChild("Unreachable", 20d, 1d), 0d),
        })
        {
            Name = "Degenerate",
            SpecifiedHazard = "Peak Flow",
            HazardUnit = "cfs",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
        };
        var (degenerateValid, degenerateMessages) = degenerate.Validate();
        Assert.IsTrue(degenerateValid);
        Assert.IsTrue(degenerateMessages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("degenerates")));

        // Epistemic weights must sum to one.
        var badWeights = EpistemicHazard();
        badWeights.HazardFunctions[0].Weight = 0.5d;
        Assert.IsFalse(badWeights.Validate().IsValid);

        // Branch attribution is an epistemic-only query.
        var aleatory = EpistemicHazard();
        aleatory.CompositeCombinationType = CompositeCombinationType.Mixture;
        Assert.ThrowsException<InvalidOperationException>(() => aleatory.SelectedBranchIndex(0));
    }

    /// <summary>
    /// Verifies the shared-selector overwrite: two bound composites with different child
    /// content and different seeds select identical branch sequences inside a sharing scope,
    /// equal to the scope column's own selection; an unbound sibling keeps its own draws; and
    /// without a scope a bound composite falls back to its own content-seeded selector.
    /// </summary>
    [TestMethod]
    public void Test_Hazard_Epistemic_SharedScope_AlignsBinders()
    {
        // Arrange — two binders of one variable, one unbound sibling.
        var first = EpistemicHazard();
        first.EpistemicVariable = "Flood Model";
        var second = new CompositeHazard(new[]
        {
            new WeightedHazardFunction(HazardChild("Alt Low", 100d, 5d), 0.3d),
            new WeightedHazardFunction(HazardChild("Alt Mid", 200d, 5d), 0.4d),
            new WeightedHazardFunction(HazardChild("Alt High", 300d, 5d), 0.3d),
        })
        {
            Name = "Second Tree",
            SpecifiedHazard = "Peak Flow",
            HazardUnit = "cfs",
            CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
            EpistemicVariable = "Flood Model",
        };
        var unbound = EpistemicHazard();

        var columns = EpistemicSharingScope.BuildColumns(new[] { "Flood Model" }, 12345, 16, SamplingScheme.LatinHypercube);
        using (EpistemicSharingScope.Enter(columns))
        {
            first.SetupSampler(16, 111, SamplingScheme.LatinHypercube);
            second.SetupSampler(16, 999, SamplingScheme.LatinHypercube);
            unbound.SetupSampler(16, 111, SamplingScheme.LatinHypercube);
        }

        // Assert — binders share the column bit-exactly and select together.
        bool anyDifference = false;
        for (int i = 0; i < 16; i++)
        {
            Assert.AreEqual(columns["Flood Model"][i], first.SampledPercentile(i, 0), 0d);
            Assert.AreEqual(columns["Flood Model"][i], second.SampledPercentile(i, 0), 0d);
            Assert.AreEqual(first.SelectedBranchIndex(i), second.SelectedBranchIndex(i));
            if (unbound.SampledPercentile(i, 0) != columns["Flood Model"][i]) anyDifference = true;
        }
        Assert.IsTrue(anyDifference, "The unbound sibling must keep its own content-seeded draws.");

        // Without a scope the bound composite degrades to its own selector.
        var standalone = EpistemicHazard();
        standalone.EpistemicVariable = "Flood Model";
        standalone.SetupSampler(16, 111, SamplingScheme.LatinHypercube);
        var own = LatinHypercube.Random(16, 1, SeedHelpers.ToPositiveSeed(111));
        for (int i = 0; i < 16; i++)
        {
            Assert.AreEqual(own[i, 0], standalone.SampledPercentile(i, 0), 0d);
        }
    }

    #endregion

    #region Response

    /// <summary>
    /// Verifies the epistemic response semantics: index-path selection returns the selected
    /// fragility whole, and the percentile path selects and rescales into the branch (the
    /// single-uniform composition convention) — pinned against the child sampled directly at
    /// the rescaled percentile.
    /// </summary>
    [TestMethod]
    public void Test_Response_Epistemic_IndexAndPercentilePaths()
    {
        // Arrange — 0.3/0.4/0.3 over distinguishable deterministic fragilities plus an
        // uncertain middle branch so the rescale is observable.
        var middle = UncertainResponseChild("Middle");
        var composite = EpistemicResponse(
            (ResponseChild("Steep", 12d), 0.3d),
            (middle, 0.4d),
            (ResponseChild("Shallow", 30d), 0.3d));
        composite.SetupSampler(10, 4242, SamplingScheme.LatinHypercube);

        // Assert — index parity with the selected child.
        for (int i = 0; i < 10; i++)
        {
            int branch = composite.SelectedBranchIndex(i);
            var expected = ((IResponseFunction)composite.ResponseFunctions[branch].ResponseFunction!).SampleFunction(i);
            Assert.AreEqual(expected.CDF(15d), composite.SampleFunction(i).CDF(15d), 0d);
        }

        // The percentile path: 0.6 selects the middle branch at the rescaled (0.6 − 0.3)/0.4 = 0.75.
        var viaComposite = composite.SampleFunction(0.6d);
        var viaChild = middle.SampleFunction(0.75d);
        Assert.AreEqual(viaChild.CDF(15d), viaComposite.CDF(15d), 1e-12);
    }

    /// <summary>
    /// Verifies the response conditional-presence and hash contract mirrors the hazard's:
    /// mode selection and binding are hash events, the unbound form is unchanged, and the
    /// bound form round-trips.
    /// </summary>
    [TestMethod]
    public void Test_Response_Epistemic_ConditionalPresenceAndHashEvents()
    {
        // Arrange
        var composite = EpistemicResponse((ResponseChild("A", 20d), 0.5d), (ResponseChild("B", 25d), 0.5d));
        composite.CompositeCombinationType = CompositeCombinationType.Mixture;
        byte[] mixtureHash = composite.CanonicalHash();
        string unboundXml = composite.ToXElement().ToString();

        // Act / Assert
        composite.CompositeCombinationType = CompositeCombinationType.EpistemicMixture;
        CollectionAssert.AreNotEqual(mixtureHash, composite.CanonicalHash());
        composite.EpistemicVariable = "Breach Model";
        composite.EpistemicVariable = "";
        composite.CompositeCombinationType = CompositeCombinationType.Mixture;
        Assert.AreEqual(unboundXml, composite.ToXElement().ToString());
        CollectionAssert.AreEqual(mixtureHash, composite.CanonicalHash());

        composite.CompositeCombinationType = CompositeCombinationType.EpistemicMixture;
        composite.EpistemicVariable = "Breach Model";
        var restored = new CompositeResponse(composite.ToXElement());
        Assert.AreEqual("Breach Model", restored.EpistemicVariable);
        CollectionAssert.AreEqual(composite.CanonicalHash(), restored.CanonicalHash());
    }

    #endregion

    #region Transform

    /// <summary>
    /// Verifies the transform legalization: EpistemicMixture validates and samples while the
    /// aleatory Mixture and Additive stay validation errors with the recorded reasons, and the
    /// sampling gate matches.
    /// </summary>
    [TestMethod]
    public void Test_Transform_Epistemic_LegalizedWhileAleatoryMixtureStaysRejected()
    {
        // Assert — the epistemic mode is legal end to end.
        var epistemic = TransformComposite(CompositeFunctionType.EpistemicMixture);
        Assert.IsTrue(epistemic.Validate().IsValid);
        Assert.AreEqual(1, epistemic.SamplingDimensions);
        Assert.IsFalse(epistemic.IsDeterministic);
        epistemic.SetupSampler(8, 55, SamplingScheme.LatinHypercube);
        Assert.IsNotNull(epistemic.SampleFunction(0));

        // The aleatory Mixture keeps its recorded blocker; Additive keeps its rejection.
        var mixture = TransformComposite(CompositeFunctionType.Mixture);
        var (mixtureValid, mixtureMessages) = mixture.Validate();
        Assert.IsFalse(mixtureValid);
        Assert.IsTrue(mixtureMessages.Any(m => m.Contains("within-realization branch enumeration")));
        Assert.ThrowsException<InvalidOperationException>(() => mixture.SampleFunction());
        Assert.IsFalse(TransformComposite(CompositeFunctionType.Additive).Validate().IsValid);
    }

    /// <summary>
    /// Verifies the epistemic transform semantics: the realization chains the selected rating
    /// curve whole (index parity), the percentile path rescales into the branch, and the mean
    /// path stays the weighted-average blend.
    /// </summary>
    [TestMethod]
    public void Test_Transform_Epistemic_SelectionSemantics()
    {
        // Arrange
        var composite = TransformComposite(CompositeFunctionType.EpistemicMixture);
        composite.SetupSampler(10, 321, SamplingScheme.LatinHypercube);

        // Assert — index parity with the selected child.
        for (int i = 0; i < 10; i++)
        {
            int branch = composite.SelectedBranchIndex(i);
            var expected = ((ITransformFunction)composite.TransformFunctions[branch].TransformFunction!).SampleFunction(i);
            Assert.AreEqual(expected.Function(50d), composite.SampleFunction(i).Function(50d), 0d);
        }

        // The percentile path: 0.6 selects Rating B at the rescaled 0.75.
        var viaComposite = composite.SampleFunction(0.6d);
        var viaChild = ((ITransformFunction)composite.TransformFunctions[1].TransformFunction!).SampleFunction(0.75d);
        Assert.AreEqual(viaChild.Function(50d), viaComposite.Function(50d), 1e-12);

        // The mean path blends: Σω·fᵢ(50) over the child mean curves.
        double expectedMean = 0d;
        double[] weights = { 0.3d, 0.4d, 0.3d };
        for (int c = 0; c < 3; c++)
        {
            expectedMean += weights[c] * ((ITransformFunction)composite.TransformFunctions[c].TransformFunction!).SampleFunction().Function(50d);
        }
        Assert.AreEqual(expectedMean, composite.SampleFunction().Function(50d), 1e-12);
    }

    /// <summary>
    /// Verifies the transform conditional-presence and hash contract, including that the
    /// variable-outside-mode combination is an Error under the Average mode.
    /// </summary>
    [TestMethod]
    public void Test_Transform_Epistemic_ConditionalPresenceAndVariableGate()
    {
        // Arrange
        var average = TransformComposite(CompositeFunctionType.Average);
        byte[] averageHash = average.CanonicalHash();
        string averageXml = average.ToXElement().ToString();

        // Selecting the epistemic mode moves the hash; set-then-clear restores bytes.
        average.CompositeFunctionType = CompositeFunctionType.EpistemicMixture;
        CollectionAssert.AreNotEqual(averageHash, average.CanonicalHash());
        average.EpistemicVariable = "Rating Model";
        average.EpistemicVariable = null!;
        average.CompositeFunctionType = CompositeFunctionType.Average;
        Assert.AreEqual(averageXml, average.ToXElement().ToString());
        CollectionAssert.AreEqual(averageHash, average.CanonicalHash());

        // A bound variable under Average is dead hashed content — Error + gate.
        average.EpistemicVariable = "Rating Model";
        Assert.IsFalse(average.Validate().IsValid);
        Assert.ThrowsException<InvalidOperationException>(() => average.SampleFunction());

        // The bound epistemic form round-trips.
        var bound = TransformComposite(CompositeFunctionType.EpistemicMixture);
        bound.EpistemicVariable = "Rating Model";
        var restored = new CompositeTransform(bound.ToXElement());
        Assert.AreEqual(CompositeFunctionType.EpistemicMixture, restored.CompositeFunctionType);
        Assert.AreEqual("Rating Model", restored.EpistemicVariable);
        CollectionAssert.AreEqual(bound.CanonicalHash(), restored.CanonicalHash());
    }

    #endregion

    #region Consequence

    /// <summary>
    /// Verifies the epistemic consequence exposure-branch contract: the percentile overload
    /// selects one branch (the coupling draw is the knowledge channel), a nested aleatory
    /// mixture still enumerates inside the chosen branch, the mean overload returns the single
    /// blended branch, and the branch-count guardrail prices the worst selectable child.
    /// </summary>
    [TestMethod]
    public void Test_Consequence_Epistemic_ExposureBranchesSelectOne()
    {
        // Arrange — an epistemic choice between a plain curve (0.3) and an aleatory day/night
        // mixture (0.7).
        var dayNight = LabelConsequence(new CompositeConsequence(new[]
        {
            new WeightedConsequenceFunction(ConsequenceChild("Day", 100d), 0.5d),
            new WeightedConsequenceFunction(ConsequenceChild("Night", 200d), 0.5d),
        })
        { Name = "Day-Night", CompositeFunctionType = CompositeFunctionType.Mixture });
        var parent = LabelConsequence(new CompositeConsequence(new[]
        {
            new WeightedConsequenceFunction(ConsequenceChild("Plain", 10d), 0.3d),
            new WeightedConsequenceFunction(dayNight, 0.7d),
        })
        { Name = "Model Tree", CompositeFunctionType = CompositeFunctionType.EpistemicMixture });
        Assert.IsTrue(parent.Validate().IsValid);
        Assert.AreEqual(0, parent.SamplingDimensions, "Consequences are never walked; the coupling draw selects.");
        Assert.IsFalse(parent.IsDeterministic);

        // A percentile in the plain span selects the single plain branch.
        var plainBranches = parent.SampleExposureBranches(0.2d);
        Assert.AreEqual(1, plainBranches.Count);
        Assert.AreEqual(1d, plainBranches[0].Weight, 0d);
        Assert.AreEqual(10d, plainBranches[0].Function.Function(10d), 1e-12);

        // A percentile in the mixture span selects it — and its aleatory branches enumerate.
        var mixedBranches = parent.SampleExposureBranches(0.65d);
        Assert.AreEqual(2, mixedBranches.Count);
        Assert.AreEqual(0.5d, mixedBranches[0].Weight, 0d);
        Assert.AreEqual(0.5d, mixedBranches[1].Weight, 0d);
        Assert.AreEqual(100d, mixedBranches[0].Function.Function(10d), 1e-12);
        Assert.AreEqual(200d, mixedBranches[1].Function.Function(10d), 1e-12);

        // The mean overload blends: 0.3·10 + 0.7·(0.5·100 + 0.5·200) = 108.
        var meanBranches = parent.SampleExposureBranches();
        Assert.AreEqual(1, meanBranches.Count);
        Assert.AreEqual(108d, meanBranches[0].Function.Function(10d), 1e-12);

        // The guardrail prices the worst selectable child, not the sum of alternatives.
        Assert.AreEqual(2, parent.CountExposureBranches());
    }

    /// <summary>
    /// Verifies the standalone epistemic index path rides the internal selector exactly like
    /// the aleatory Mixture (the shared machinery), and the degenerate-branch Warning fires.
    /// </summary>
    [TestMethod]
    public void Test_Consequence_Epistemic_StandaloneSelectorAndWarnings()
    {
        // Arrange
        var parent = LabelConsequence(new CompositeConsequence(new[]
        {
            new WeightedConsequenceFunction(ConsequenceChild("A", 10d), 0.3d),
            new WeightedConsequenceFunction(ConsequenceChild("B", 20d), 0.7d),
        })
        { Name = "Tree", CompositeFunctionType = CompositeFunctionType.EpistemicMixture });
        parent.SetupSampler(8, 99, SamplingScheme.LatinHypercube);

        // Assert — each realization carries exactly one child's curve.
        for (int i = 0; i < 8; i++)
        {
            double value = parent.SampleFunction(i).Function(10d);
            Assert.IsTrue(value == 10d || value == 20d, "The realization must carry one selected branch, never a blend.");
        }

        // The degenerate epistemic warning.
        var degenerate = LabelConsequence(new CompositeConsequence(new[]
        {
            new WeightedConsequenceFunction(ConsequenceChild("Only", 10d), 1d),
        })
        { Name = "Degenerate", CompositeFunctionType = CompositeFunctionType.EpistemicMixture });
        var (isValid, messages) = degenerate.Validate();
        Assert.IsTrue(isValid);
        Assert.IsTrue(messages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal) && m.Contains("degenerates")));
    }

    #endregion
}
