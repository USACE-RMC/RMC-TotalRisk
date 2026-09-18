using System;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees;

/// <summary>
/// Tests the static fault-tree response: exact gate probabilities, shared-versus-independent
/// repeated events, two-mode serialization, projected hash identity, the runtime-only diagram
/// budget, and the sampler lifecycle.
/// </summary>
[TestClass]
public class FaultTreeResponseTests
{
    /// <summary>Builds a labeled response over hazards 0 and 1.</summary>
    /// <param name="tree">The authored tree.</param>
    /// <param name="name">The function name.</param>
    /// <returns>The response.</returns>
    private static FaultTreeResponse CreateResponse(FaultTree tree, string name = "Fault tree")
    {
        return new FaultTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }

    /// <summary>Builds an aligned deterministic table.</summary>
    /// <param name="hazards">The hazard knots.</param>
    /// <param name="probabilities">The deterministic probabilities.</param>
    /// <returns>The table.</returns>
    private static UncertainOrderedPairedData DeterministicTable(double[] hazards, double[] probabilities)
    {
        return new UncertainOrderedPairedData(
            hazards.Select((hazard, index) =>
                new UncertainOrdinate(hazard, new Deterministic(probabilities[index]))).ToArray(),
            true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Deterministic);
    }

    /// <summary>Builds an aligned co-monotonic uniform table.</summary>
    /// <param name="hazards">The hazard knots.</param>
    /// <param name="minimums">The lower bounds.</param>
    /// <param name="maximums">The upper bounds.</param>
    /// <returns>The table.</returns>
    private static UncertainOrderedPairedData UncertainTable(double[] hazards, double[] minimums, double[] maximums)
    {
        return new UncertainOrderedPairedData(
            hazards.Select((hazard, index) =>
                new UncertainOrdinate(hazard, new Uniform(minimums[index], maximums[index]))).ToArray(),
            true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
    }

    /// <summary>Verifies defaults and the validation matrix.</summary>
    [TestMethod]
    public void Test_DefaultsAndValidationMatrix()
    {
        // Arrange — the default response is a labeled-less empty Or root.
        var response = new FaultTreeResponse();

        // Act
        var validation = response.Validate();

        // Assert
        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.ValidationMessages.Any(message =>
            message.Contains("does not have a specified hazard type")));
        Assert.IsTrue(validation.ValidationMessages.Any(message =>
            message.Contains("has no inputs")));

        // Hazard-axis errors.
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d)));
        FaultTreeResponse descending = CreateResponse(tree);
        descending.SetHazardLevels(new[] { 1d, 0d });
        Assert.IsTrue(descending.Validate().ValidationMessages.Any(message =>
            message.Contains("strictly ascending")));
        descending.SetHazardLevels(Array.Empty<double>());
        Assert.IsTrue(descending.Validate().ValidationMessages.Any(message =>
            message.Contains("requires at least one hazard level")));
        descending.SetHazardLevels(new[] { 0d, 1d });
        Assert.IsTrue(descending.Validate().IsValid);

        // Out-of-range scalar source.
        var badTree = new FaultTree();
        badTree.Add(badTree.Root.Id, new FaultTreeBasicEventNode("Bad", new ProbabilitySource(1.5d)));
        Assert.IsTrue(CreateResponse(badTree).Validate().ValidationMessages.Any(message =>
            message.Contains("Basic event 'Bad' has a scalar probability outside [0, 1]")));

        // Xor arity and K-range diagnostics.
        var xorTree = new FaultTree();
        var xor = new FaultTreeGateNode("X", FaultTreeGateType.Xor);
        xorTree.Add(xorTree.Root.Id, xor);
        xorTree.Add(xor.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d)));
        Assert.IsTrue(CreateResponse(xorTree).Validate().ValidationMessages.Any(message =>
            message.Contains("requires exactly two inputs")));

        var kTree = new FaultTree();
        var voting = new FaultTreeGateNode("V", FaultTreeGateType.KOfN, 5);
        kTree.Add(kTree.Root.Id, voting);
        kTree.Add(voting.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d)));
        kTree.Add(voting.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.3d)));
        Assert.IsTrue(CreateResponse(kTree).Validate().ValidationMessages.Any(message =>
            message.Contains("requires K between 1 and its input count")));
    }

    /// <summary>Verifies exact gate probabilities against closed forms.</summary>
    [TestMethod]
    public void Test_GateProbabilities_MatchClosedForms()
    {
        // Arrange — Top(Or) ← And(A=0.2, B=0.3), Xor(C=0.4, D=0.5) evaluated separately,
        // 2-of-3 voting, and a house-event mix.
        var andTree = new FaultTree();
        var and = new FaultTreeGateNode("And", FaultTreeGateType.And);
        andTree.Add(andTree.Root.Id, and);
        andTree.Add(and.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d)));
        andTree.Add(and.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.3d)));
        Assert.AreEqual(0.2d * 0.3d,
            CreateResponse(andTree).SampleResponseFunction()[0].Y, 1e-15);

        var orTree = new FaultTree();
        orTree.Add(orTree.Root.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d)));
        orTree.Add(orTree.Root.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.3d)));
        Assert.AreEqual(1d - 0.8d * 0.7d,
            CreateResponse(orTree).SampleResponseFunction()[0].Y, 1e-15);

        var xorTree = new FaultTree();
        var xor = new FaultTreeGateNode("Xor", FaultTreeGateType.Xor);
        xorTree.Add(xorTree.Root.Id, xor);
        xorTree.Add(xor.Id, new FaultTreeBasicEventNode("C", new ProbabilitySource(0.4d)));
        xorTree.Add(xor.Id, new FaultTreeBasicEventNode("D", new ProbabilitySource(0.5d)));
        Assert.AreEqual(0.4d * (1d - 0.5d) + (1d - 0.4d) * 0.5d,
            CreateResponse(xorTree).SampleResponseFunction()[0].Y, 1e-15);

        var votingTree = new FaultTree();
        var voting = new FaultTreeGateNode("Voting", FaultTreeGateType.KOfN, 2);
        votingTree.Add(votingTree.Root.Id, voting);
        votingTree.Add(voting.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d)));
        votingTree.Add(voting.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d)));
        votingTree.Add(voting.Id, new FaultTreeBasicEventNode("C", new ProbabilitySource(0.3d)));
        double twoOfThree = 0.1d * 0.2d * 0.7d + 0.1d * 0.8d * 0.3d + 0.9d * 0.2d * 0.3d
            + 0.1d * 0.2d * 0.3d;
        Assert.AreEqual(twoOfThree,
            CreateResponse(votingTree).SampleResponseFunction()[0].Y, 1e-15);

        // House events fold as constants: Or(A, false) = A; And(A, true) = A; And(A, false) = 0.
        var houseTree = new FaultTree();
        var houseAnd = new FaultTreeGateNode("Guarded", FaultTreeGateType.And);
        houseTree.Add(houseTree.Root.Id, houseAnd);
        houseTree.Add(houseAnd.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.25d)));
        var house = new FaultTreeHouseEventNode("Season", true);
        houseTree.Add(houseAnd.Id, house);
        FaultTreeResponse houseResponse = CreateResponse(houseTree);
        Assert.AreEqual(0.25d, houseResponse.SampleResponseFunction()[0].Y, 1e-15);
        house.State = false;
        Assert.AreEqual(0d, houseResponse.SampleResponseFunction()[0].Y, 1e-15);
    }

    /// <summary>
    /// Verifies the repeated-event identities: shared occurrences unify while independent clones
    /// multiply, for both conjunction and disjunction.
    /// </summary>
    [TestMethod]
    public void Test_SharedVersusIndependent_RepeatedEventIdentities()
    {
        // Arrange
        const double p = 0.3d;
        FaultTreeResponse Build(FaultTreeGateType gateType, bool shared)
        {
            var tree = new FaultTree();
            tree.Root.GateType = gateType;
            var basic = new FaultTreeBasicEventNode("A", new ProbabilitySource(p));
            tree.Add(tree.Root.Id, basic);
            if (shared) tree.LinkShared(tree.Root.Id, basic.Id, "Repeat");
            else tree.LinkIndependent(tree.Root.Id, basic.Id, "Repeat");
            return CreateResponse(tree);
        }

        // Act / Assert — AND(A, A) = p shared and p^2 independent; OR duals.
        Assert.AreEqual(p, Build(FaultTreeGateType.And, shared: true)
            .SampleResponseFunction()[0].Y, 1e-15);
        Assert.AreEqual(p * p, Build(FaultTreeGateType.And, shared: false)
            .SampleResponseFunction()[0].Y, 1e-15);
        Assert.AreEqual(p, Build(FaultTreeGateType.Or, shared: true)
            .SampleResponseFunction()[0].Y, 1e-15);
        Assert.AreEqual(1d - (1d - p) * (1d - p), Build(FaultTreeGateType.Or, shared: false)
            .SampleResponseFunction()[0].Y, 1e-15);
    }

    /// <summary>Verifies both serialization modes round-trip identity and values.</summary>
    [TestMethod]
    public void Test_SerializationModes_RoundTripHashAndValues()
    {
        // Arrange — an external shared transfer plus a response-backed source.
        var externalTree = new FaultTree();
        var externalBasic = new FaultTreeBasicEventNode("External basic", new ProbabilitySource(0.15d));
        externalTree.Add(externalTree.Root.Id, externalBasic);
        FaultTreeResponse external = CreateResponse(externalTree, "External fault tree");

        var nestedTree = new FaultTree();
        nestedTree.Add(nestedTree.Root.Id, new FaultTreeBasicEventNode("Nested", new ProbabilitySource(0.1d)));
        FaultTreeResponse nested = CreateResponse(nestedTree, "Nested fault tree");

        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Referenced",
            new ProbabilitySource(nested)));
        tree.LinkShared(tree.Root.Id, external, externalBasic.Id, "External shared");
        FaultTreeResponse response = CreateResponse(tree);
        Assert.IsTrue(response.Validate().IsValid,
            string.Join(" | ", response.Validate().ValidationMessages));
        double expected = 1d - (1d - 0.1d) * (1d - 0.15d);
        Assert.AreEqual(expected, response.SampleResponseFunction()[0].Y, 1e-15);

        // Act / Assert — self-contained.
        var selfContained = new FaultTreeResponse(
            response.ToXElement(RiskSerializationMode.SelfContained));
        CollectionAssert.AreEqual(response.CanonicalHash(), selfContained.CanonicalHash());
        Assert.AreEqual(expected, selfContained.SampleResponseFunction()[0].Y, 1e-15);

        // Act / Assert — by reference re-attaches the live stored instances.
        var resolver = new RiskFunctionResolver(id =>
            id == external.Id ? external : id == nested.Id ? nested : null, _ => null);
        var byReference = new FaultTreeResponse(
            response.ToXElement(RiskSerializationMode.ByReference), resolver);
        CollectionAssert.AreEqual(response.CanonicalHash(), byReference.CanonicalHash());
        Assert.AreEqual(expected, byReference.SampleResponseFunction()[0].Y, 1e-15);
        var restoredTransfer = byReference.FaultTree.Nodes.OfType<FaultTreeTransferNode>().Single();
        Assert.AreSame(external, restoredTransfer.TargetFunction);
    }

    /// <summary>
    /// Verifies repeated self-contained embeds of one external function materialize as one live
    /// instance, so shared-logical occurrences keep unifying after a round trip and the identity
    /// and values match the live and by-reference forms.
    /// </summary>
    [TestMethod]
    public void Test_SelfContainedRoundTrip_RepeatedSharedEmbeds_UnifyOneInstance()
    {
        // Arrange — two shared transfers to the same external event; unified they contribute p once.
        var externalTree = new FaultTree();
        var externalBasic = new FaultTreeBasicEventNode("External basic", new ProbabilitySource(0.2d));
        externalTree.Add(externalTree.Root.Id, externalBasic);
        FaultTreeResponse external = CreateResponse(externalTree, "Shared external");

        var tree = new FaultTree();
        var and = new FaultTreeGateNode("Join", FaultTreeGateType.And);
        tree.Add(tree.Root.Id, and);
        tree.LinkShared(and.Id, external, externalBasic.Id, "Occurrence A");
        tree.LinkShared(and.Id, external, externalBasic.Id, "Occurrence B");
        FaultTreeResponse response = CreateResponse(tree);
        Assert.AreEqual(0.2d, response.SampleResponseFunction()[0].Y, 1e-15);

        // Act
        var roundTripped = new FaultTreeResponse(
            response.ToXElement(RiskSerializationMode.SelfContained));

        // Assert — one materialized instance, unified variables, identical identity and value.
        FaultTreeTransferNode[] transfers =
            roundTripped.FaultTree.Nodes.OfType<FaultTreeTransferNode>().ToArray();
        Assert.AreEqual(2, transfers.Length);
        Assert.AreSame(transfers[0].TargetFunction, transfers[1].TargetFunction);
        Assert.AreEqual(response.CompiledVariableCount, roundTripped.CompiledVariableCount);
        CollectionAssert.AreEqual(response.CanonicalHash(), roundTripped.CanonicalHash());
        Assert.AreEqual(0.2d, roundTripped.SampleResponseFunction()[0].Y, 1e-15);
    }

    /// <summary>Verifies divergent duplicate embeds of one function id are rejected loudly.</summary>
    [TestMethod]
    public void Test_Read_DivergentDuplicateEmbeddedTarget_Throws()
    {
        // Arrange — serialize two shared embeds, then corrupt the second copy's content.
        var externalTree = new FaultTree();
        var externalBasic = new FaultTreeBasicEventNode("External basic", new ProbabilitySource(0.2d));
        externalTree.Add(externalTree.Root.Id, externalBasic);
        FaultTreeResponse external = CreateResponse(externalTree, "Shared external");

        var tree = new FaultTree();
        tree.LinkShared(tree.Root.Id, external, externalBasic.Id, "Occurrence A");
        tree.LinkShared(tree.Root.Id, external, externalBasic.Id, "Occurrence B");
        FaultTreeResponse response = CreateResponse(tree);
        XElement xml = response.ToXElement(RiskSerializationMode.SelfContained);
        XElement[] embedded = xml.Descendants(nameof(FaultTreeResponse)).ToArray();
        Assert.AreEqual(2, embedded.Length);
        embedded[1].Descendants(nameof(ProbabilitySource)).First()
            .SetAttributeValue("ScalarProbability", "0.9");

        // Act / Assert
        Assert.ThrowsException<InvalidOperationException>(() => new FaultTreeResponse(xml));
    }

    /// <summary>Verifies metadata edits are hash-inert while every compute edit moves the hash.</summary>
    [TestMethod]
    public void Test_HashIdentity_MetadataInertComputeSensitive()
    {
        // Arrange
        var tree = new FaultTree();
        var voting = new FaultTreeGateNode("Voting", FaultTreeGateType.KOfN, 2);
        tree.Add(tree.Root.Id, voting);
        var a = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.1d));
        var b = new FaultTreeBasicEventNode("B", new ProbabilitySource(0.2d));
        var house = new FaultTreeHouseEventNode("H", false);
        tree.Add(voting.Id, a);
        tree.Add(voting.Id, b);
        tree.Add(voting.Id, house);
        FaultTreeResponse response = CreateResponse(tree);
        byte[] baseline = response.CanonicalHash();

        // Act / Assert — metadata edits and sibling reorder are inert.
        response.Name = "Renamed function";
        a.Name = "Renamed event";
        a.Description = "Documented";
        tree.Move(b.Id, voting.Id, a.Id);
        CollectionAssert.AreEqual(baseline, response.CanonicalHash());

        // Compute edits each move the hash.
        voting.K = 3;
        CollectionAssert.AreNotEqual(baseline, response.CanonicalHash());
        voting.K = 2;
        CollectionAssert.AreEqual(baseline, response.CanonicalHash());
        voting.GateType = FaultTreeGateType.And;
        CollectionAssert.AreNotEqual(baseline, response.CanonicalHash());
        voting.GateType = FaultTreeGateType.KOfN;
        house.State = true;
        CollectionAssert.AreNotEqual(baseline, response.CanonicalHash());
        house.State = false;
        a.ProbabilitySource = new ProbabilitySource(0.11d);
        CollectionAssert.AreNotEqual(baseline, response.CanonicalHash());
    }

    /// <summary>
    /// Verifies the projected identity distinguishes one shared event from content-identical
    /// independent clones through unified-variable ordinals.
    /// </summary>
    [TestMethod]
    public void Test_SharedVariableIdentity_DistinguishesUnification()
    {
        // Arrange
        FaultTreeResponse Build(bool shared)
        {
            var tree = new FaultTree();
            tree.Root.GateType = FaultTreeGateType.And;
            var basic = new FaultTreeBasicEventNode("A", new ProbabilitySource(0.3d));
            tree.Add(tree.Root.Id, basic);
            if (shared) tree.LinkShared(tree.Root.Id, basic.Id, "Repeat");
            else tree.LinkIndependent(tree.Root.Id, basic.Id, "Repeat");
            return CreateResponse(tree);
        }

        // Act / Assert — the two structures compute differently and must hash differently.
        CollectionAssert.AreNotEqual(Build(shared: true).CanonicalHash(),
            Build(shared: false).CanonicalHash());
    }

    /// <summary>Verifies the diagram budget is runtime-only and fails loudly.</summary>
    [TestMethod]
    public void Test_BddNodeLimit_RuntimeOnlyAndLoudFailure()
    {
        // Arrange
        var tree = new FaultTree();
        var xor = new FaultTreeGateNode("X1", FaultTreeGateType.Xor);
        tree.Add(tree.Root.Id, xor);
        var innerXor = new FaultTreeGateNode("X2", FaultTreeGateType.Xor);
        tree.Add(xor.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d)));
        tree.Add(xor.Id, innerXor);
        tree.Add(innerXor.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.3d)));
        tree.Add(innerXor.Id, new FaultTreeBasicEventNode("C", new ProbabilitySource(0.4d)));
        FaultTreeResponse response = CreateResponse(tree);
        Assert.AreEqual(1000000, response.BddNodeLimit);
        byte[] baseline = response.CanonicalHash();

        // Act / Assert — never serialized, never hashed.
        Assert.IsNull(response.ToXElement().Attribute(nameof(response.BddNodeLimit)));
        response.BddNodeLimit = 2;
        CollectionAssert.AreEqual(baseline, response.CanonicalHash());

        // Loud validation failure with counts and remediation, then recovery.
        var validation = response.Validate();
        Assert.IsFalse(validation.IsValid);
        string budgetError = validation.ValidationMessages.Single(message =>
            message.Contains("decision-diagram resource budget"));
        StringAssert.Contains(budgetError, "BddNodeLimit is 2");
        StringAssert.Contains(budgetError, "Exact evaluation is never approximated");
        Assert.ThrowsException<InvalidOperationException>(() => response.SampleResponseFunction());
        response.BddNodeLimit = 1000000;
        Assert.IsTrue(response.Validate().IsValid);
        Assert.AreEqual(0.2d * (1d - (0.3d + 0.4d - 2d * 0.3d * 0.4d))
            + (1d - 0.2d) * (0.3d + 0.4d - 2d * 0.3d * 0.4d),
            response.SampleResponseFunction()[0].Y, 1e-15);
    }

    /// <summary>Verifies the sampler counts shared variables once and independent clones separately.</summary>
    [TestMethod]
    public void Test_Sampler_SharedOnceIndependentTwice()
    {
        // Arrange
        FaultTreeResponse Build(bool shared)
        {
            var tree = new FaultTree();
            tree.Root.GateType = FaultTreeGateType.And;
            var basic = new FaultTreeBasicEventNode("A",
                new ProbabilitySource(UncertainTable(new[] { 0d, 1d },
                    new[] { 0.1d, 0.2d }, new[] { 0.3d, 0.4d })));
            tree.Add(tree.Root.Id, basic);
            if (shared) tree.LinkShared(tree.Root.Id, basic.Id, "Repeat");
            else tree.LinkIndependent(tree.Root.Id, basic.Id, "Repeat");
            return CreateResponse(tree);
        }
        FaultTreeResponse sharedResponse = Build(shared: true);
        FaultTreeResponse independentResponse = Build(shared: false);

        // Assert dimensions: one unified variable versus two clones.
        Assert.AreEqual(1, sharedResponse.SamplingDimensions);
        Assert.AreEqual(2, independentResponse.SamplingDimensions);
        Assert.IsFalse(sharedResponse.IsDeterministic);

        // Percentile sampling honors unification exactly: median of Uniform(0.1, 0.3) is 0.2.
        Assert.AreEqual(0.2d, sharedResponse.SampleResponseFunction(0.5d)[0].Y, 1e-15);
        Assert.AreEqual(0.2d * 0.2d, independentResponse.SampleResponseFunction(0.5d)[0].Y, 1e-15);

        // Realization sampling: the shared tree is AND(A, A) = A, so every realization stays in
        // the marginal bounds; the independent tree multiplies two draws.
        sharedResponse.SetupSampler(8, 12345, SamplingScheme.LatinHypercube);
        independentResponse.SetupSampler(8, 12345, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < 8; realization++)
        {
            double sharedValue = sharedResponse.SampleResponseFunction(realization)[0].Y;
            double independentValue = independentResponse.SampleResponseFunction(realization)[0].Y;
            Assert.IsTrue(sharedValue >= 0.1d && sharedValue <= 0.3d);
            Assert.IsTrue(independentValue >= 0.1d * 0.1d && independentValue <= 0.3d * 0.3d);
        }

        // A compute edit invalidates the configured sampler.
        sharedResponse.FaultTree.Root.GateType = FaultTreeGateType.Or;
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => sharedResponse.SampleResponseFunction(0)).Message, "SetupSampler() must be called");
    }

    /// <summary>Verifies the non-monotonic aggregate warning is advisory, never a repair.</summary>
    [TestMethod]
    public void Test_NonMonotonicAggregate_WarnsWithoutRepair()
    {
        // Arrange — a deterministic decreasing fragility.
        var tree = new FaultTree();
        tree.Add(tree.Root.Id, new FaultTreeBasicEventNode("Falling",
            new ProbabilitySource(DeterministicTable(new[] { 0d, 1d }, new[] { 0.9d, 0.1d }))));
        FaultTreeResponse response = CreateResponse(tree);

        // Act
        var validation = response.Validate();

        // Assert
        Assert.IsTrue(validation.IsValid);
        Assert.IsTrue(validation.ValidationMessages.Any(message =>
            message.StartsWith("Warning: The response function is not monotonically increasing",
                StringComparison.Ordinal)));
        Assert.IsFalse(response.IsMonotonic());
        Assert.AreEqual(0.9d, response.SampleResponseFunction()[0].Y, 1e-15);
        Assert.AreEqual(0.1d, response.SampleResponseFunction()[1].Y, 1e-15);
        Assert.AreEqual(0.9d, response.MinProbability(), 1e-15);
        Assert.AreEqual(0.1d, response.MaxProbability(), 1e-15);
        Assert.AreEqual(0d, response.MinHazard());
        Assert.AreEqual(1d, response.MaxHazard());
    }

    /// <summary>Verifies coherence reporting and the non-coherent cut-set rejection.</summary>
    [TestMethod]
    public void Test_Coherence_XorRejectsCutSets()
    {
        // Arrange
        var tree = new FaultTree();
        var xor = new FaultTreeGateNode("X", FaultTreeGateType.Xor);
        tree.Add(tree.Root.Id, xor);
        tree.Add(xor.Id, new FaultTreeBasicEventNode("A", new ProbabilitySource(0.2d)));
        tree.Add(xor.Id, new FaultTreeBasicEventNode("B", new ProbabilitySource(0.3d)));
        FaultTreeResponse response = CreateResponse(tree);

        // Act / Assert
        Assert.IsFalse(response.IsCoherent);
        StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(
            () => response.GetMinimalCutSets()).Message,
            "not defined for a non-coherent fault tree");
    }

    /// <summary>Verifies deterministic uncertainty summaries are exact and uncertain ones bounded.</summary>
    [TestMethod]
    public void Test_ComputeUncertaintyResults_ExactAndBounded()
    {
        // Arrange — deterministic: every summary curve equals the sampled curve.
        var deterministicTree = new FaultTree();
        deterministicTree.Add(deterministicTree.Root.Id,
            new FaultTreeBasicEventNode("A", new ProbabilitySource(0.25d)));
        FaultTreeResponse deterministic = CreateResponse(deterministicTree);
        var exact = deterministic.ComputeUncertaintyResults()!;
        Assert.AreEqual(0.25d, exact.MeanCurve![0], 1e-15);
        Assert.AreEqual(0.25d, exact.ConfidenceIntervals![0, 0], 1e-15);
        Assert.AreEqual(0.25d, exact.ConfidenceIntervals[0, 1], 1e-15);

        // Uncertain: the summary stays inside the source bounds and brackets the mean.
        var uncertainTree = new FaultTree();
        uncertainTree.Add(uncertainTree.Root.Id, new FaultTreeBasicEventNode("A",
            new ProbabilitySource(UncertainTable(new[] { 0d, 1d },
                new[] { 0.1d, 0.2d }, new[] { 0.3d, 0.4d }))));
        FaultTreeResponse uncertain = CreateResponse(uncertainTree);
        var summary = uncertain.ComputeUncertaintyResults()!;
        Assert.IsTrue(summary.MeanCurve![0] > 0.19d && summary.MeanCurve[0] < 0.21d);
        Assert.IsTrue(summary.ConfidenceIntervals![0, 0] >= 0.1d);
        Assert.IsTrue(summary.ConfidenceIntervals[0, 1] <= 0.3d);
        Assert.IsTrue(summary.ConfidenceIntervals[0, 0] < summary.ConfidenceIntervals[0, 1]);
    }
}
