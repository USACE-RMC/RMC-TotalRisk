using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Verification.RiskFunctions.Responses;

/// <summary>
/// Parametric common-cause failure verification. The anchor is an exhaustive Boolean
/// enumeration over the expanded derived events with every multiplicity probability re-derived
/// independently from the published non-staggered alpha-factor formulas — a complete
/// re-implementation of the split arithmetic, never a call into the group's own kernel. The
/// rare-event doctrine (a member's marginal recovers its total probability to first order and
/// never exceeds it), the exact beta/Greek-letter facade identity, and the engine-scale
/// closed form with full-run byte reproducibility close the family.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// The exhaustive oracle evaluates every state of the derived-event product space directly, so
/// it verifies the expansion structure (which combinations exist and which members they touch),
/// the factor arithmetic, and the exact diagram evaluation in one comparison held to 1e-14
/// (operation-order roundoff between the enumeration and the decision diagram). The
/// beta-versus-Greek-letter identity is bit-exact because both facades compute the same
/// factor doubles for a two-member group.
/// </para>
/// </remarks>
[TestClass]
public class FaultTreeCcfVerification
{
    /// <summary>
    /// Verifies the alpha-factor expansion against an exhaustive enumeration of the derived
    /// product space: a three-member group with an independently re-derived multiplicity split
    /// under OR(AND(A, B), C, D) matches the exact state sum at every hazard level.
    /// </summary>
    [TestMethod]
    public void Test_AlphaFactor_ExhaustiveEnumeration_Exact()
    {
        // Arrange — Q = 0.2, α = {0.9, 0.08, 0.02}, plain event D = 0.15.
        var tree = new FaultTree();
        var and = new FaultTreeGateNode("Both", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, and);
        var memberA = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d));
        var memberB = new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d));
        var memberC = new FaultTreeBasicEventNode("C", new ProbabilitySource(0.2d));
        tree.Add(and.Id, memberA);
        tree.Add(and.Id, memberB);
        tree.Add(tree.Root.Id, memberC);
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("D", new ProbabilitySource(0.15d)));
        tree.AddCcfGroup(new FaultTreeCcfGroup("Alpha", FaultTreeCcfModel.AlphaFactor,
            new[] { 0.9d, 0.08d, 0.02d }, new[] { memberA.Id, memberB.Id, memberC.Id }));
        FaultTreeResponse response = ValidResponse(tree);

        // The independent re-derivation of the non-staggered multiplicity split:
        // Q_k = [k / C(n−1, k−1)] · (α_k / α_t) · Q with α_t = Σ k·α_k.
        double q = 0.2d;
        double alphaTotal = 1d * 0.9d + 2d * 0.08d + 3d * 0.02d;
        double q1 = (1d / 1d) * (0.9d / alphaTotal) * q;
        double q2 = (2d / 2d) * (0.08d / alphaTotal) * q;
        double q3 = (3d / 1d) * (0.02d / alphaTotal) * q;

        // The derived product space: three independent events, three pairs, one triple, one
        // plain event — enumerated exhaustively.
        double[] probabilities = { q1, q1, q1, q2, q2, q2, q3, 0.15d };
        double expected = 0d;
        for (int state = 0; state < 1 << 8; state++)
        {
            bool aIndependent = (state & 1) != 0;
            bool bIndependent = (state & 2) != 0;
            bool cIndependent = (state & 4) != 0;
            bool pairAb = (state & 8) != 0;
            bool pairAc = (state & 16) != 0;
            bool pairBc = (state & 32) != 0;
            bool triple = (state & 64) != 0;
            bool plain = (state & 128) != 0;
            bool a = aIndependent || pairAb || pairAc || triple;
            bool b = bIndependent || pairAb || pairBc || triple;
            bool c = cIndependent || pairAc || pairBc || triple;
            if (!((a && b) || c || plain)) continue;
            double product = 1d;
            for (int e = 0; e < 8; e++)
            {
                product *= (state & (1 << e)) != 0 ? probabilities[e] : 1d - probabilities[e];
            }
            expected += product;
        }

        // Act
        OrderedPairedData curve = response.SampleResponseFunction();

        // Assert
        Assert.AreEqual(expected, curve[0].Y, 1e-14d);
        Assert.AreEqual(expected, curve[1].Y, 1e-14d);
    }

    /// <summary>
    /// Verifies the Greek-letter union against an independent exact closed form and the
    /// rare-event doctrine: at a small total probability the exact three-member union recovers
    /// the first-order coefficient 3f₁ + 3f₂ + f₃ (each common event fires the union once, so
    /// the grouped union sits below three independent totals — the common-cause discount).
    /// </summary>
    [TestMethod]
    public void Test_GreekLetterUnion_ClosedFormAndRareEventDoctrine()
    {
        // Arrange — OR(A, B, C), Q = 1e-8, β = 0.1, γ = 0.5.
        var tree = new FaultTree();
        var memberA = new FaultTreeBasicEventNode("A", new ProbabilitySource(1e-8d));
        var memberB = new FaultTreeBasicEventNode("B", new ProbabilitySource(1e-8d));
        var memberC = new FaultTreeBasicEventNode("C", new ProbabilitySource(1e-8d));
        tree.Add(tree.Root.Id, memberA);
        tree.Add(tree.Root.Id, memberB);
        tree.Add(tree.Root.Id, memberC);
        tree.AddCcfGroup(new FaultTreeCcfGroup("Greek", FaultTreeCcfModel.MultipleGreekLetter,
            new[] { 0.1d, 0.5d }, new[] { memberA.Id, memberB.Id, memberC.Id }));
        FaultTreeResponse response = ValidResponse(tree);

        // The independent split re-derivation: f₁ = 0.9, f₂ = 0.1·0.5/2, f₃ = 0.1·0.5.
        double q = 1e-8d;
        double q1 = 0.9d * q;
        double q2 = 0.1d * 0.5d / 2d * q;
        double q3 = 0.1d * 0.5d * q;

        // Act
        double union = response.SampleResponseFunction()[0].Y;

        // Assert — the exact union closed form over the seven derived events, then the
        // first-order coefficient 3·0.9 + 3·0.025 + 0.05 = 2.825 within second-order error.
        double expected = 1d - Math.Pow(1d - q1, 3) * Math.Pow(1d - q2, 3) * (1d - q3);
        Assert.AreEqual(expected, union, 1e-14d);
        Assert.AreEqual(2.825d, union / q, 1e-6d);
        Assert.IsTrue(union < 3d * q,
            "The grouped union must sit below three independent totals — the common-cause discount.");
    }

    /// <summary>
    /// Verifies the facade identity: for a two-member group the beta-factor and Greek-letter
    /// parameterizations compute identical factor doubles, so their expanded responses are
    /// bit-identical curve for curve.
    /// </summary>
    [TestMethod]
    public void Test_BetaAndGreekLetterFacades_TwoMembers_BitIdentical()
    {
        // Arrange
        FaultTreeResponse beta = GroupedPair(new FaultTreeCcfGroup("Beta",
            FaultTreeCcfModel.BetaFactor, new[] { 0.1d }, Array.Empty<Guid>()));
        FaultTreeResponse greek = GroupedPair(new FaultTreeCcfGroup("Greek",
            FaultTreeCcfModel.MultipleGreekLetter, new[] { 0.1d }, Array.Empty<Guid>()));

        // Act
        OrderedPairedData betaCurve = beta.SampleResponseFunction();
        OrderedPairedData greekCurve = greek.SampleResponseFunction();

        // Assert
        for (int h = 0; h < betaCurve.Count; h++)
            Assert.AreEqual(betaCurve[h].Y, greekCurve[h].Y);
    }

    /// <summary>
    /// Verifies the engine-scale behavior: the mean-only annual failure probability of a flat
    /// grouped And pair equals the exact common-cause closed form, the group registers as one
    /// knowledge quantity in the node-importance sweep, and two re-authored full-uncertainty
    /// twins publish byte-identical results.
    /// </summary>
    [TestMethod]
    public void Test_Engine_ClosedFormAndReproducibility()
    {
        // Arrange — And of a Q = 0.2, β = 0.1 pair: top = 0.02 + 0.98·0.18² exactly, and a flat
        // response makes the annual failure probability equal the top.
        RiskAnalysis meanAnalysis = BuildAnalysis(uncertain: false);
        meanAnalysis.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(0.02d + 0.98d * 0.18d * 0.18d,
            meanAnalysis.MeanRiskResults!.Curves.Fail.TotalProbability, 1e-12d);

        // The group is one knowledge quantity: exactly one uncertain importance entry (the
        // shared basis), every derived event deterministic.
        RiskAnalysis importanceCarrier = BuildAnalysis(uncertain: true);
        var faultResponse = (FaultTreeResponse)importanceCarrier.Components[0].FailureModes[0]
            .ResponseStages[0]!.Response!;
        var importance = TreeNodeImportance.Compute(faultResponse, new TreeNodeImportanceOptions(0d)
        {
            Iterations = 64,
            Seed = 8675309,
        });
        Assert.AreEqual(1, importance.Entries.Count(entry => entry.HasUncertainty));
        Assert.IsTrue(importance.Entries.Single(entry => entry.HasUncertainty).Name.Contains("CCF basis"));

        // Full-run byte reproducibility of the shared-basis stream.
        RiskAnalysis first = BuildAnalysis(uncertain: true);
        first.Options.EstimateMeanRiskOnly = false;
        first.Options.Realizations = 200;
        first.RunAsync().GetAwaiter().GetResult();
        RiskAnalysis second = BuildAnalysis(uncertain: true);
        second.Options.EstimateMeanRiskOnly = false;
        second.Options.Realizations = 200;
        second.RunAsync().GetAwaiter().GetResult();
        Assert.AreEqual(first.RiskResults!.ToJson(), second.RiskResults!.ToJson(),
            "Re-authored grouped twins must publish byte-identical results.");
    }

    /// <summary>Builds a two-member grouped pair under an Or root with the supplied group's model and parameters.</summary>
    /// <param name="prototype">The group carrying the model and parameters; members are assigned here.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse GroupedPair(FaultTreeCcfGroup prototype)
    {
        var tree = new FaultTree();
        var pump = new FaultTreeBasicEventNode("Pump", new ProbabilitySource(0.2d));
        var valve = new FaultTreeBasicEventNode("Valve", new ProbabilitySource(0.2d));
        tree.Add(tree.Root.Id, pump);
        tree.Add(tree.Root.Id, valve);
        prototype.MemberNodeIds = new[] { pump.Id, valve.Id };
        tree.AddCcfGroup(prototype);
        return ValidResponse(tree);
    }

    /// <summary>Builds a one-component analysis over an And of a grouped exchangeable pair.</summary>
    /// <param name="uncertain">Whether the shared member source carries uncertainty.</param>
    /// <returns>The analysis.</returns>
    private static RiskAnalysis BuildAnalysis(bool uncertain)
    {
        UncertainOrderedPairedData Build()
        {
            return uncertain
                ? new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                        new UncertainOrdinate(1d, new Uniform(0.1d, 0.3d)),
                    }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform)
                : new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0d, new Deterministic(0.2d)),
                        new UncertainOrdinate(1d, new Deterministic(0.2d)),
                    }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);
        }

        var tree = new FaultTree();
        var and = new FaultTreeGateNode("Pair", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, and);
        var pump = new FaultTreeBasicEventNode("Pump", new ProbabilitySource(Build()));
        var valve = new FaultTreeBasicEventNode("Valve", new ProbabilitySource(Build()));
        tree.Add(and.Id, pump);
        tree.Add(and.Id, valve);
        tree.AddCcfGroup(new FaultTreeCcfGroup("Pair group", FaultTreeCcfModel.BetaFactor,
            new[] { 0.1d }, new[] { pump.Id, valve.Id }));
        FaultTreeResponse response = ValidResponse(tree);

        var component = new SystemComponent { Name = "Dam" };
        component.HazardFunction = new TabularHazard
        {
            Name = "Stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            NoUncertaintyFunction = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0.999d, new Deterministic(0d)),
                    new UncertainOrdinate(0.5d, new Deterministic(0.5d)),
                    new UncertainOrdinate(0.001d, new Deterministic(1d)),
                },
                true, SortOrder.Descending, true, SortOrder.Ascending,
                UnivariateDistributionType.Deterministic),
        };
        component.AddFailureMode(new FailureMode(null, null, response, new TabularConsequence
        {
            Name = "Failure loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(500d)),
                    new UncertainOrdinate(1d, new Deterministic(1000d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        }));
        return new RiskAnalysis(new[] { component })
        {
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    /// <summary>Wraps a tree in a valid two-level response.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse ValidResponse(FaultTree tree)
    {
        return new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = "CCF fault tree",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }
}
