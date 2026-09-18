using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.Systems.Components.Graph;

/// <summary>
/// Fast integration tests for expanded event-tree outputs across graph authoring, projection,
/// persistence, canonical identity, materialization, and stale-connection policy.
/// </summary>
[TestClass]
public class ComponentGraphEventTreeTests
{
    /// <summary>Verifies arbitrary n-way leaves are exposed without changing the ordinary binary view.</summary>
    [TestMethod]
    public void Test_ExpandedOutputs_DiscoverNWayLeaves_OrdinaryViewRemainsBinary()
    {
        var fixture = NWayComponent();
        ResponseElement element = fixture.ResponseElement;
        element.ExpandBranchOutputs = false;

        Assert.AreEqual(2, element.OutputCount);
        Assert.ThrowsException<InvalidOperationException>(() =>
            element.CreateBranchConnection(fixture.Branches[0].Id));

        element.ExpandBranchOutputs = true;
        IReadOnlyList<ResponseBranchDescriptor> branches = element.GetAvailableBranches();
        Assert.AreEqual(5, branches.Count, "Three explicit leaves, one remainder, and the stable implicit-unmodeled leaf must be discoverable.");
        Assert.AreEqual(2, branches.Single(branch => branch.Name == "Unmodeled").OutputPort);
        Assert.IsTrue(branches.Where(branch => branch.Name != "Unmodeled")
            .All(branch => branch.OutputPort >= 3));
        Assert.AreEqual(branches.Max(branch => branch.OutputPort) + 1, element.OutputCount);

        RiskConnection selected = element.CreateBranchConnection(fixture.Branches[1].Id);
        Assert.AreEqual(fixture.Branches[1].Id, selected.SourceBranchId);
        Assert.AreEqual(branches.Single(branch => branch.Id == fixture.Branches[1].Id).OutputPort,
            selected.SourcePort);
    }

    /// <summary>Verifies branch addresses survive metadata, order, XML-mode, factory, and resolver paths.</summary>
    [TestMethod]
    public void Test_ExpandedConnections_AreStableAcrossMetadataOrderAndBothXmlModes()
    {
        var fixture = NWayComponent();
        Dictionary<Guid, int> baseline = fixture.Response.GetBranches()
            .ToDictionary(branch => branch.Id, branch => branch.OutputPort);
        byte[] hash = fixture.Component.CanonicalHash();
        int seed = SeedHelpers.HashCombine(2468, hash, 0);

        fixture.Branches[0].Name = "Renamed alpha";
        fixture.Branches[0].Description = "presentation metadata";
        fixture.Response.EventTree.Move(fixture.Branches[2].Id,
            fixture.Response.EventTree.Root.Id, fixture.Branches[0].Id);

        foreach (ResponseBranchDescriptor branch in fixture.Response.GetBranches())
            Assert.AreEqual(baseline[branch.Id], branch.OutputPort);
        CollectionAssert.AreEqual(hash, fixture.Component.CanonicalHash());
        Assert.AreEqual(seed, SeedHelpers.HashCombine(2468, fixture.Component.CanonicalHash(), 0));

        SystemComponent selfContained = new SystemComponent(
            fixture.Component.ToXElement(RiskSerializationMode.SelfContained));
        CollectionAssert.AreEqual(hash, selfContained.CanonicalHash());
        AssertBranchConnections(selfContained, baseline);

        IRiskFunction[] functions = fixture.Component.GetReferencedFunctions().ToArray();
        var byId = functions.ToDictionary(function => function.Id);
        var resolver = new RiskFunctionResolver(
            id => byId.TryGetValue(id, out IRiskFunction? function) ? function : null,
            name => functions.FirstOrDefault(function => function.Name == name));
        SystemComponent byReference = new SystemComponent(
            fixture.Component.ToXElement(RiskSerializationMode.ByReference), resolver);
        CollectionAssert.AreEqual(hash, byReference.CanonicalHash());
        AssertBranchConnections(byReference, baseline);
        Assert.AreSame(fixture.Response,
            byReference.Graph.GetElements<ResponseElement>().Single().Function);

        XElement migrationXml = fixture.Component.ToXElement(RiskSerializationMode.SelfContained);
        XElement migrationConnection = migrationXml.Descendants(nameof(ConsequenceElement))
            .Single(element => (string?)element.Attribute("SourceBranch") == "Renamed alpha");
        migrationConnection.Attribute("SourceBranchId")!.Remove();
        SystemComponent migrated = new SystemComponent(migrationXml);
        RiskConnection migratedConnection = migrated.Graph.GetElements<ConsequenceElement>()
            .Single(element => element.Input?.SourceBranchId == fixture.Branches[0].Id).Input!;
        Assert.AreEqual(fixture.Branches[0].Id, migratedConnection.SourceBranchId,
            "The exact-name migration fallback must repair and retain the stable branch id.");

        ResponseBranchDescriptor selected = fixture.Response.GetBranches()
            .Single(branch => branch.Id == fixture.Branches[0].Id);
        var stage = new ResponseStage(new List<ITransformFunction>(), fixture.Response, selected);
        XElement stageXml = stage.ToXElement();
        stageXml.Attribute(nameof(ResponseStage.SelectedBranchId))!.Remove();
        var restoredStage = new ResponseStage(stageXml);
        Assert.AreEqual(selected.Id, restoredStage.SelectedBranchId);
        Assert.AreEqual("Renamed alpha", restoredStage.ToXElement()
            .Attribute(nameof(ResponseStage.SelectedBranchName))!.Value);
    }

    /// <summary>Verifies each selected n-way leaf projects its exact terminal probability.</summary>
    [TestMethod]
    public void Test_Projection_UsesExactNWayLeafProbabilitiesAndExclusiveGrouping()
    {
        var fixture = NWayComponent();
        IReadOnlyList<FailureMode> modes = fixture.Component.FailureModes;

        Assert.AreEqual(5, modes.Count);
        Assert.AreEqual(0d, modes[0].Sample(null).SRP(0d), 0d);
        Assert.AreEqual(0.2d, modes[1].Sample(null).SRP(0d), 1e-14d);
        Assert.AreEqual(0.3d, modes[2].Sample(null).SRP(0d), 1e-14d);
        Assert.AreEqual(0.1d, modes[3].Sample(null).SRP(0d), 1e-14d);
        Assert.AreEqual(0.4d, modes[4].Sample(null).SRP(0d), 1e-14d);

        EndStateGroupLayout layout = EndStateGroupLayout.Build(modes);
        Assert.AreEqual(1, layout.CombinationUnitCount,
            "The two failure-classified leaves of one n-way response are disjoint members of one exclusive unit.");
        CollectionAssert.AreEqual(new[] { 1, 2 }, layout.CombinationUnitStates[0]);
    }

    /// <summary>Verifies reject and failed-materialize restore every observable tree and graph field.</summary>
    [TestMethod]
    public void Test_DeleteConnectedDirectTerminal_RejectAndMaterializeRollbackExactly()
    {
        var fixture = NWayComponent();
        fixture.Response.SetupSampler(16, 9876, SamplingScheme.LatinHypercube);
        string graphBefore = fixture.Component.Graph.ToXElement().ToString(SaveOptions.DisableFormatting);
        byte[] hashBefore = fixture.Component.CanonicalHash();
        double sampleBefore = fixture.Response.SampleResponseFunction(0)[0].Y;
        Dictionary<Guid, int> portsBefore = fixture.Response.GetBranches()
            .ToDictionary(branch => branch.Id, branch => branch.OutputPort);
        RiskConnection connectionBefore = fixture.Terminals[1].Input!;

        InvalidOperationException rejected = Assert.ThrowsException<InvalidOperationException>(() =>
            fixture.Component.Graph.DeleteEventTreeNode(fixture.ResponseElement,
                fixture.Branches[0].Id, TreeDeletePolicy.RejectIfReferenced));
        StringAssert.Contains(rejected.Message, "connected");
        AssertRollback(fixture, graphBefore, hashBefore, sampleBefore, portsBefore,
            connectionBefore);

        InvalidOperationException materialize = Assert.ThrowsException<InvalidOperationException>(() =>
            fixture.Component.Graph.DeleteEventTreeNode(fixture.ResponseElement,
                fixture.Branches[0].Id, TreeDeletePolicy.MaterializeLinks));
        StringAssert.Contains(materialize.Message, "materialization did not preserve");
        AssertRollback(fixture, graphBefore, hashBefore, sampleBefore, portsBefore,
            connectionBefore);
    }

    /// <summary>Verifies cascade disconnects only consumers of the removed stable terminal.</summary>
    [TestMethod]
    public void Test_DeleteConnectedDirectTerminal_CascadeDisconnectsStaleConsumers()
    {
        var fixture = NWayComponent();
        RiskConnection unaffected = fixture.Terminals[2].Input!;
        RiskConnection selected = fixture.ResponseElement.CreateBranchConnection(fixture.Branches[0].Id);
        var transform = new TransformElement("Branch transform")
        {
            Input = selected,
            SecondaryInput = selected,
        };
        var downstreamResponse = new ResponseElement("Downstream response")
        {
            Function = fixture.Response,
            Input = selected,
            SecondaryInput = selected,
        };
        fixture.Terminals[1].HazardSource = selected;
        fixture.Terminals[1].SecondaryInput = selected;
        fixture.Component.Graph.AddElement(transform);
        fixture.Component.Graph.AddElement(downstreamResponse);

        fixture.Component.Graph.DeleteEventTreeNode(fixture.ResponseElement,
            fixture.Branches[0].Id, TreeDeletePolicy.CascadeLinks);

        Assert.IsNull(fixture.Response.EventTree.FindById(fixture.Branches[0].Id));
        Assert.IsNull(fixture.Terminals[1].Input);
        Assert.IsNull(fixture.Terminals[1].HazardSource);
        Assert.IsNull(fixture.Terminals[1].SecondaryInput);
        Assert.IsNull(transform.Input);
        Assert.IsNull(transform.SecondaryInput);
        Assert.IsNull(downstreamResponse.Input);
        Assert.IsNull(downstreamResponse.SecondaryInput);
        Assert.AreSame(unaffected, fixture.Terminals[2].Input);
        Assert.IsFalse(fixture.Component.Graph.Validate().ValidationMessages
            .Any(message => message.Contains("stale branch id", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Verifies a rejected graph-aware edit leaves every secondary slot exactly as captured —
    /// including that the consequence element's three restored slots (input, binding, secondary)
    /// land back in their own fields, never swapped.
    /// </summary>
    [TestMethod]
    public void Test_DeleteRejected_RestoresSecondarySlotsUnswapped()
    {
        // Arrange — distinct connections in every slot kind.
        var fixture = NWayComponent();
        RiskConnection branchConnection = fixture.ResponseElement.CreateBranchConnection(fixture.Branches[0].Id);
        var hazardElement = fixture.Component.Graph.GetElements<HazardElement>().Single();
        var plainConnection = new RiskConnection(hazardElement);
        var transform = new TransformElement("Chain transform")
        {
            Input = plainConnection,
            SecondaryInput = branchConnection,
        };
        fixture.Component.Graph.AddElement(transform);
        fixture.Terminals[1].HazardSource = branchConnection;
        fixture.Terminals[1].SecondaryInput = plainConnection;

        // Act — the referenced branch rejects the delete; the catch path restores every slot.
        Assert.ThrowsException<InvalidOperationException>(() =>
            fixture.Component.Graph.DeleteEventTreeNode(fixture.ResponseElement,
                fixture.Branches[0].Id, TreeDeletePolicy.RejectIfReferenced));

        // Assert — each slot holds its own captured connection.
        Assert.AreSame(plainConnection, transform.Input);
        Assert.AreSame(branchConnection, transform.SecondaryInput);
        Assert.AreSame(branchConnection, fixture.Terminals[1].HazardSource);
        Assert.AreSame(plainConnection, fixture.Terminals[1].SecondaryInput);
    }

    /// <summary>Verifies uncontrolled mutation is diagnosed and linked materialization preserves addresses.</summary>
    [TestMethod]
    public void Test_ValidationDiagnosesStaleBranch_AndMaterializationPreservesConnectedAddress()
    {
        var stale = NWayComponent();
        stale.Response.EventTree.Delete(stale.Branches[0].Id);
        Assert.IsTrue(stale.Component.Graph.Validate().ValidationMessages.Any(message =>
            message.Contains("stale branch id", StringComparison.Ordinal)));

        var targetTree = new EventTree();
        var targetLeaf = new ChanceNode("Target leaf", new ProbabilitySource(0.25d));
        targetTree.Add(targetTree.Root.Id, targetLeaf);
        var target = Response(targetTree, "Target");
        var ownerTree = new EventTree();
        Guid linkId = ownerTree.LinkIndependent(ownerTree.Root.Id, target, targetLeaf.Id,
            "Linked occurrence");
        ownerTree.Add(ownerTree.Root.Id, new RemainderNode("Survive"));
        EventTreeResponse owner = Response(ownerTree, "Owner");
        var component = Component(owner);
        ResponseElement responseElement = component.Graph.GetElements<ResponseElement>().Single();
        ResponseBranchDescriptor linked = owner.GetBranches().Single(branch => branch.Name == "Linked occurrence");
        var terminal = AddTerminal(component, responseElement.CreateBranchConnection(linked.Id),
            "Linked consequence");

        ownerTree.MaterializeLink(linkId);

        ResponseBranchDescriptor materialized = owner.GetBranches().Single(branch => branch.Id == linked.Id);
        Assert.AreEqual(linked.OutputPort, materialized.OutputPort);
        Assert.AreEqual(linked.Id, terminal.Input!.SourceBranchId);
        Assert.IsFalse(component.Graph.Validate().ValidationMessages.Any(message =>
            message.Contains("stale branch", StringComparison.Ordinal)));

        SystemComponent restored = new SystemComponent(component.ToXElement());
        RiskConnection restoredConnection = restored.Graph.GetElements<ConsequenceElement>()
            .Single().Input!;
        Assert.AreEqual(linked.Id, restoredConnection.SourceBranchId);
        Assert.AreEqual(linked.OutputPort, restoredConnection.SourcePort);
    }

    /// <summary>Verifies observer exceptions cannot leave a partially disconnected cascade edit.</summary>
    [TestMethod]
    public void Test_CascadeObserverFailure_RollsBackTreeConnectionsPortsHashAndSampler()
    {
        var fixture = NWayComponent();
        fixture.Response.SetupSampler(16, 112233, SamplingScheme.LatinHypercube);
        string graphBefore = fixture.Component.Graph.ToXElement()
            .ToString(SaveOptions.DisableFormatting);
        byte[] hashBefore = fixture.Component.CanonicalHash();
        double sampleBefore = fixture.Response.SampleResponseFunction(0)[0].Y;
        Dictionary<Guid, int> portsBefore = fixture.Response.GetBranches()
            .ToDictionary(branch => branch.Id, branch => branch.OutputPort);
        RiskConnection connectionBefore = fixture.Terminals[1].Input!;
        PropertyChangedEventHandler observer = (_, _) =>
            throw new InvalidOperationException("observer failure");
        fixture.Component.Graph.PropertyChanged += observer;

        InvalidOperationException exception;
        try
        {
            exception = Assert.ThrowsException<InvalidOperationException>(() =>
                fixture.Component.Graph.DeleteEventTreeNode(fixture.ResponseElement,
                    fixture.Branches[0].Id, TreeDeletePolicy.CascadeLinks));
        }
        finally
        {
            fixture.Component.Graph.PropertyChanged -= observer;
        }

        StringAssert.Contains(exception.Message, "observer failure");
        AssertRollback(fixture, graphBefore, hashBefore, sampleBefore, portsBefore,
            connectionBefore);
    }

    /// <summary>Verifies existing branch connections survive copy/paste and unreachable pruning.</summary>
    [TestMethod]
    public void Test_ExpandedConnection_SurvivesCopyPasteAndPrune()
    {
        var fixture = NWayComponent();
        RiskConnection selected = fixture.Terminals[1].Input!;
        int selectedPort = selected.SourcePort;
        TreeFragment fragment = fixture.Response.EventTree.Copy(fixture.Branches[1].Id);

        Guid pastedId = fixture.Response.EventTree.PasteClone(
            fixture.Response.EventTree.Root.Id, fragment);

        Assert.AreEqual(selectedPort, fixture.Terminals[1].Input!.SourcePort);
        Assert.AreEqual(fixture.Branches[0].Id, fixture.Terminals[1].Input!.SourceBranchId);
        ResponseBranchDescriptor pasted = fixture.Response.GetBranches()
            .Single(branch => branch.Id == pastedId);
        Assert.IsTrue(pasted.OutputPort > fixture.Response.GetBranches()
            .Where(branch => branch.Id != pasted.Id)
            .Max(branch => branch.OutputPort));
        Assert.IsFalse(fixture.Component.Graph.Validate().ValidationMessages.Any(message =>
            message.Contains("stale branch", StringComparison.Ordinal)));

        XElement treeXml = fixture.Response.EventTree.ToXElement();
        XElement sourceNode = treeXml.Element("Nodes")!.Elements(nameof(ChanceNode))
            .Single(element => element.Attribute("Id")!.Value == fixture.Branches[1].Id.ToString("D"));
        var orphan = new XElement(sourceNode);
        Guid orphanId = Guid.NewGuid();
        orphan.SetAttributeValue("Id", orphanId.ToString("D"));
        orphan.SetAttributeValue("Name", "Disconnected orphan");
        orphan.SetAttributeValue("OutputPort", "999");
        treeXml.Element("Nodes")!.Add(orphan);
        var prunableResponse = Response(new EventTree(treeXml), "Prunable response");
        SystemComponent prunableComponent = Component(prunableResponse);
        ResponseElement prunableElement = prunableComponent.Graph.GetElements<ResponseElement>().Single();
        ResponseBranchDescriptor prunableSelected = prunableResponse.GetBranches()
            .Single(branch => branch.Id == fixture.Branches[0].Id);
        ConsequenceElement terminal = AddTerminal(prunableComponent,
            prunableElement.CreateBranchConnection(prunableSelected.Id), "Selected consequence");

        IReadOnlyList<Guid> removed = prunableResponse.EventTree.PruneUnreachable();

        CollectionAssert.AreEqual(new[] { orphanId }, removed.ToArray());
        Assert.AreEqual(prunableSelected.Id, terminal.Input!.SourceBranchId);
        Assert.AreEqual(prunableSelected.OutputPort, terminal.Input.SourcePort);
        Assert.IsFalse(prunableComponent.Graph.Validate().ValidationMessages.Any(message =>
            message.Contains("stale branch", StringComparison.Ordinal)));
    }

    /// <summary>Verifies compute-identical sibling reorder cannot move selected-branch hash or seed identity.</summary>
    [TestMethod]
    public void Test_SelectedBranchIdentity_IsInvariantToIdenticalSiblingReorder()
    {
        var tree = new EventTree();
        var first = new ChanceNode("First", new ProbabilitySource(0.2d));
        var second = new ChanceNode("Second", new ProbabilitySource(0.2d));
        tree.Add(tree.Root.Id, first);
        tree.Add(tree.Root.Id, second);
        tree.Add(tree.Root.Id, new RemainderNode("Survival") { IsFailure = false });
        EventTreeResponse response = Response(tree, "Repeated branches");
        SystemComponent component = Component(response);
        ResponseElement responseElement = component.Graph.GetElements<ResponseElement>().Single();
        _ = AddTerminal(component, responseElement.CreateBranchConnection(first.Id),
            "Selected consequence");
        byte[] hash = component.CanonicalHash();
        int seed = SeedHelpers.HashCombine(13579, hash, 0);
        int port = response.GetBranches().Single(branch => branch.Id == first.Id).OutputPort;

        tree.Move(second.Id, tree.Root.Id, first.Id);

        CollectionAssert.AreEqual(hash, component.CanonicalHash());
        Assert.AreEqual(seed, SeedHelpers.HashCombine(13579, component.CanonicalHash(), 0));
        Assert.AreEqual(port, response.GetBranches().Single(branch => branch.Id == first.Id).OutputPort);
    }

    /// <summary>Verifies selected-branch identity is sensitive to the chosen compute path.</summary>
    [TestMethod]
    public void Test_SelectedBranchIdentity_DistinguishesComputePaths()
    {
        var tree = new EventTree();
        var alpha = new ChanceNode("Alpha", new ProbabilitySource(0.2d));
        var beta = new ChanceNode("Beta", new ProbabilitySource(0.3d));
        tree.Add(tree.Root.Id, alpha);
        tree.Add(tree.Root.Id, beta);
        tree.Add(tree.Root.Id, new RemainderNode("Survival") { IsFailure = false });
        EventTreeResponse response = Response(tree, "Distinct branches");
        SystemComponent alphaComponent = Component(response);
        SystemComponent betaComponent = Component(response);
        _ = AddTerminal(alphaComponent,
            alphaComponent.Graph.GetElements<ResponseElement>().Single()
                .CreateBranchConnection(alpha.Id), "Selected consequence");
        _ = AddTerminal(betaComponent,
            betaComponent.Graph.GetElements<ResponseElement>().Single()
                .CreateBranchConnection(beta.Id), "Selected consequence");

        Assert.IsFalse(alphaComponent.CanonicalHash().SequenceEqual(
            betaComponent.CanonicalHash()));
    }

    /// <summary>Verifies indexed projected leaves equal the event tree's own per-branch samples.</summary>
    [TestMethod]
    public void Test_Projection_IndexedLeavesEqualBranchSamplesAtEveryHazard()
    {
        var uncertainty = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(1d, new Uniform(0.4d, 0.6d)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
        var tree = new EventTree();
        tree.Add(tree.Root.Id, new ChanceNode("Uncertain failure",
            new ProbabilitySource(uncertainty)));
        tree.Add(tree.Root.Id, new RemainderNode("Survival") { IsFailure = false });
        EventTreeResponse response = Response(tree, "Uncertain branches");
        SystemComponent component = Component(response);
        ResponseElement responseElement = component.Graph.GetElements<ResponseElement>().Single();
        foreach (ResponseBranchDescriptor branch in response.GetBranches())
            _ = AddTerminal(component, responseElement.CreateBranchConnection(branch.Id), branch.Name);
        component.SetupSamplers(16, 445566, SamplingScheme.LatinHypercube);
        IReadOnlyList<FailureMode> modes = component.SampledProjection!;

        for (int realization = 0; realization < 16; realization++)
        {
            ResponseBranchSample expected = response.SampleBranches(realization);
            var sampled = component.Sample(realization);
            for (int modeIndex = 0; modeIndex < modes.Count; modeIndex++)
            {
                Guid branchId = modes[modeIndex].ResponseStages[0]!.SelectedBranchId!.Value;
                int branchIndex = Enumerable.Range(0, expected.Branches.Count)
                    .Single(index => expected.Branches[index].Id == branchId);
                Assert.AreEqual(expected.Probabilities[branchIndex][0],
                    sampled.FailureModes[modeIndex].SRP(0d), 0d);
                Assert.AreEqual(expected.Probabilities[branchIndex][1],
                    sampled.FailureModes[modeIndex].SRP(1d), 0d);
            }
        }
    }

    /// <summary>Asserts complete observable state restoration after a failed graph-aware edit.</summary>
    private static void AssertRollback((SystemComponent Component, EventTreeResponse Response,
        ResponseElement ResponseElement, EventNodeBase[] Branches, ConsequenceElement[] Terminals) fixture,
        string graphBefore, byte[] hashBefore, double sampleBefore,
        Dictionary<Guid, int> portsBefore, RiskConnection connectionBefore)
    {
        Assert.AreEqual(graphBefore,
            fixture.Component.Graph.ToXElement().ToString(SaveOptions.DisableFormatting));
        CollectionAssert.AreEqual(hashBefore, fixture.Component.CanonicalHash());
        Assert.AreEqual(sampleBefore, fixture.Response.SampleResponseFunction(0)[0].Y);
        Assert.AreSame(connectionBefore, fixture.Terminals[1].Input);
        Assert.IsNotNull(fixture.Response.EventTree.FindById(fixture.Branches[0].Id));
        foreach (ResponseBranchDescriptor branch in fixture.Response.GetBranches())
            Assert.AreEqual(portsBefore[branch.Id], branch.OutputPort);
    }

    /// <summary>Asserts restored graph connections retain the expected stable branch ports.</summary>
    private static void AssertBranchConnections(SystemComponent component,
        IReadOnlyDictionary<Guid, int> baseline)
    {
        ResponseElement response = component.Graph.GetElements<ResponseElement>().Single();
        Assert.IsTrue(response.ExpandBranchOutputs);
        foreach (ConsequenceElement terminal in component.Graph.GetElements<ConsequenceElement>())
        {
            RiskConnection connection = terminal.Input!;
            Assert.IsTrue(connection.SourceBranchId.HasValue);
            Assert.AreEqual(baseline[connection.SourceBranchId.Value], connection.SourcePort);
        }
    }

    /// <summary>Builds an arbitrary n-way response with a consequence on every exposed branch.</summary>
    private static (SystemComponent Component, EventTreeResponse Response,
        ResponseElement ResponseElement, EventNodeBase[] Branches, ConsequenceElement[] Terminals)
        NWayComponent()
    {
        var tree = new EventTree();
        var alpha = new ChanceNode("Alpha failure", new ProbabilitySource(0.2d));
        var beta = new ChanceNode("Beta failure", new ProbabilitySource(0.3d));
        var gamma = new ChanceNode("Gamma survival", new ProbabilitySource(0.1d))
        {
            IsFailure = false,
        };
        var remainder = new RemainderNode("Other survival") { IsFailure = false };
        tree.Add(tree.Root.Id, alpha);
        tree.Add(tree.Root.Id, beta);
        tree.Add(tree.Root.Id, gamma);
        tree.Add(tree.Root.Id, remainder);
        EventTreeResponse response = Response(tree, "N-way response");
        SystemComponent component = Component(response);
        ResponseElement responseElement = component.Graph.GetElements<ResponseElement>().Single();
        ResponseBranchDescriptor[] descriptors = response.GetBranches().ToArray();
        var terminals = new List<ConsequenceElement>();
        foreach (ResponseBranchDescriptor descriptor in descriptors)
        {
            terminals.Add(AddTerminal(component,
                responseElement.CreateBranchConnection(descriptor.Id), descriptor.Name));
        }
        return (component, response, responseElement,
            new EventNodeBase[] { alpha, beta, gamma, remainder }, terminals.ToArray());
    }

    /// <summary>Builds a component with one expanded event-tree response element.</summary>
    private static SystemComponent Component(EventTreeResponse response)
    {
        var hazard = new TabularHazard
        {
            Name = "Stage frequency",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
        var component = new SystemComponent(hazard) { Name = "Event-tree component" };
        HazardElement root = component.Graph.GetElements<HazardElement>().Single();
        var responseElement = new ResponseElement("Event tree")
        {
            Function = response,
            ExpandBranchOutputs = true,
            Input = new RiskConnection(root),
        };
        component.Graph.AddElement(responseElement);
        return component;
    }

    /// <summary>Adds one graph terminal using the supplied branch-addressed connection.</summary>
    private static ConsequenceElement AddTerminal(SystemComponent component,
        RiskConnection input, string name)
    {
        var terminal = new ConsequenceElement(name) { Input = input };
        terminal.Functions.Add(new TabularConsequence
        {
            Name = name + " consequence",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damage",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(0d, new Deterministic(0d)),
                    new UncertainOrdinate(1d, new Deterministic(1d)),
                }, true, SortOrder.Ascending, false, SortOrder.None,
                UnivariateDistributionType.Deterministic),
        });
        component.Graph.AddElement(terminal);
        return terminal;
    }

    /// <summary>Builds an event-tree response over the common two-knot hazard axis.</summary>
    private static EventTreeResponse Response(EventTree tree, string name)
    {
        return new EventTreeResponse(new[] { 0d, 1d }, tree)
        {
            Name = name,
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
        };
    }
}
