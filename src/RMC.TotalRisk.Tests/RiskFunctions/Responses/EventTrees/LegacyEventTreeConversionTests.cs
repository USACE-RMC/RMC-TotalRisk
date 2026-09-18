using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.EventTrees;

/// <summary>
/// Tests the import-only v1.0 recursive event-node XML adapter, current-write normalization,
/// resolver repair, deterministic diagnostics, canonical identity, and sampler preservation.
/// </summary>
[TestClass]
public class LegacyEventTreeConversionTests
{
    /// <summary>Converts the legacy TestIO Basic shape through the stock factory and writes only v1.1 XML.</summary>
    [TestMethod]
    public void Test_LegacyTestIoShape_FactoryConvertsAndWritesOnlyCurrentXml()
    {
        XElement legacy = LegacyTestIoRoot();

        var imported = RiskFunctionFactory.CreateResponseFunction(legacy) as EventTreeResponse;
        Assert.IsNotNull(imported);
        MakeValid(imported);
        XElement current = imported.ToXElement();
        var replay = new EventTreeResponse(current);

        Assert.AreEqual("Basic", imported.Name);
        CollectionAssert.AreEqual(new[] { 0d, 1d }, imported.HazardLevels.ToArray());
        Assert.AreEqual(3, imported.EventTree.Nodes.Count);
        Assert.AreEqual("Hazard", imported.EventTree.Root.Name);
        Assert.AreEqual("Hazard", imported.EventTree.Nodes.Single(node => node.Name == "Fail").Parent!.Name);
        Assert.AreEqual("Hazard", imported.EventTree.Nodes.Single(node => node.Name == "Non-Fail").Parent!.Name);
        Assert.IsFalse(current.Descendants("Node").Any());
        Assert.AreEqual(1, current.Descendants(nameof(InitiatingNode)).Count());
        Assert.AreEqual(1, current.Descendants(nameof(ChanceNode)).Count());
        Assert.AreEqual(1, current.Descendants(nameof(RemainderNode)).Count());
        Assert.AreEqual(0d, imported.SampleResponseFunction()[0].Y);
        Assert.AreEqual(0.5d, imported.SampleResponseFunction()[1].Y);
        CollectionAssert.AreEqual(imported.CanonicalHash(), replay.CanonicalHash());
    }

    /// <summary>Preserves legacy scalar, uncertain all-hazards, and aligned-table source semantics.</summary>
    [TestMethod]
    public void Test_LegacyProbabilitySources_PreserveMeanPercentileAndLhsRealizations()
    {
        XElement deterministicRoot = LegacyRoot("HazardLevels", "0|1",
            LegacySingleValueNode("Fixed", new Deterministic(0.3d)),
            LegacyRemainder("Fixed survival"));
        EventTreeResponse deterministic = ImportWrapped(deterministicRoot);
        Assert.AreEqual(ProbabilitySourceKind.DeterministicScalar,
            deterministic.EventTree.Nodes.OfType<ChanceNode>().Single().ProbabilitySource.Kind);
        Assert.AreEqual(0.3d, deterministic.SampleResponseFunction()[0].Y);
        Assert.AreEqual(0.3d, deterministic.SampleResponseFunction()[1].Y);

        XElement uncertainRoot = LegacyRoot("HazardIntervals", "0|1",
            LegacySingleValueNode("One draw at every hazard", new Uniform(0.1d, 0.5d), "NodeGUID",
                Guid.Parse("40000000-0000-0000-0000-000000000001")),
            LegacyRemainder("Uncertain survival", "NodeGUID",
                Guid.Parse("40000000-0000-0000-0000-000000000002")));
        EventTreeResponse uncertain = ImportTreeEnvelope(uncertainRoot);
        ChanceNode uncertainChance = uncertain.EventTree.Nodes.OfType<ChanceNode>().Single();
        Assert.AreEqual(ProbabilitySourceKind.UncertainTabular, uncertainChance.ProbabilitySource.Kind);
        Assert.AreEqual(0.3d, uncertain.SampleResponseFunction()[0].Y);
        Assert.AreEqual(0.2d, uncertain.SampleResponseFunction(0.25d)[0].Y);
        Assert.AreEqual(uncertain.SampleResponseFunction(0.25d)[0].Y,
            uncertain.SampleResponseFunction(0.25d)[1].Y);

        XElement alignedRoot = LegacyRoot("HazardLevels", "0|1",
            LegacyMultiValueNode("Aligned", new[] { 0.1d, 0.4d }, new[] { 0.3d, 0.8d }),
            LegacyRemainder("Aligned survival"));
        EventTreeResponse aligned = ImportWrapped(alignedRoot);
        Assert.AreEqual(0.2d, aligned.SampleResponseFunction()[0].Y);
        Assert.AreEqual(0.6d, aligned.SampleResponseFunction()[1].Y, 1e-14d);
        Assert.AreEqual(0.15d, aligned.SampleResponseFunction(0.25d)[0].Y);
        Assert.AreEqual(0.5d, aligned.SampleResponseFunction(0.25d)[1].Y);

        var replay = new EventTreeResponse(aligned.ToXElement());
        aligned.SetupSampler(32, 24680, SamplingScheme.LatinHypercube);
        replay.SetupSampler(32, 24680, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < 32; realization++)
        {
            for (int h = 0; h < aligned.HazardLevels.Count; h++)
                Assert.AreEqual(aligned.SampleResponseFunction(realization)[h].Y,
                    replay.SampleResponseFunction(realization)[h].Y);
        }
    }

    /// <summary>Repairs legacy name-only ordinary and nested response references through the stock resolver.</summary>
    [TestMethod]
    public void Test_LegacyResponseReferences_NameFallbackRepairsIdAndPreservesCallerHazard()
    {
        var ordinary = new TabularResponse
        {
            Name = "Stored ordinary",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = DeterministicTable(
                new[] { 0d, 2d }, new[] { 0.2d, 0.8d }),
        };
        EventTreeResponse nested = ImportWrapped(LegacyRoot("HazardLevels", "0|1",
            LegacyMultiValueNode("Nested failure", new[] { 0.2d, 0.8d }, new[] { 0.2d, 0.8d }),
            LegacyRemainder("Nested survival")), "Stored nested");
        IRiskFunction[] store = { ordinary, nested };
        IRiskFunctionResolver resolver = Resolver(store);

        EventTreeResponse ordinaryOwner = ImportWrapped(LegacyRoot("HazardLevels", "0|1|2",
            LegacyResponseNode("Ordinary source", ordinary.Name),
            LegacyRemainder("Ordinary survival")), "Ordinary owner", resolver);
        EventTreeResponse nestedOwner = ImportWrapped(LegacyRoot("HazardLevels", "0|0.5|1",
            LegacyResponseNode("Nested source", nested.Name),
            LegacyRemainder("Nested survival")), "Nested owner", resolver);

        Assert.AreSame(ordinary, ordinaryOwner.EventTree.Nodes.OfType<ChanceNode>().Single()
            .ProbabilitySource.ResponseFunction);
        Assert.AreSame(nested, nestedOwner.EventTree.Nodes.OfType<ChanceNode>().Single()
            .ProbabilitySource.ResponseFunction);
        Assert.AreEqual(0.5d, ordinaryOwner.SampleResponseFunction()[1].Y, 1e-14d);
        Assert.AreEqual(0.5d, nestedOwner.SampleResponseFunction()[1].Y, 1e-14d);

        XElement repaired = nestedOwner.ToXElement(RiskSerializationMode.ByReference);
        XElement marker = repaired.Descendants("FunctionReference").Single();
        Assert.AreEqual(nested.Id.ToString("D"), marker.Attribute("Id")!.Value);
        Assert.AreEqual(nested.Name, marker.Attribute("Name")!.Value);

        var byReference = new EventTreeResponse(repaired, resolver);
        var selfContained = new EventTreeResponse(
            nestedOwner.ToXElement(RiskSerializationMode.SelfContained));
        CollectionAssert.AreEqual(nestedOwner.CanonicalHash(), byReference.CanonicalHash());
        CollectionAssert.AreEqual(nestedOwner.CanonicalHash(), selfContained.CanonicalHash());
        Assert.AreEqual(nestedOwner.SampleResponseFunction()[1].Y,
            byReference.SampleResponseFunction()[1].Y);
        Assert.AreEqual(nestedOwner.SampleResponseFunction()[1].Y,
            selfContained.SampleResponseFunction()[1].Y);

        XElement nameFallback = new XElement(repaired);
        nameFallback.Descendants("FunctionReference").Single().Attribute("Id")!.Remove();
        var repairedAgain = new EventTreeResponse(nameFallback, resolver);
        Assert.AreEqual(nested.Id.ToString("D"), repairedAgain
            .ToXElement(RiskSerializationMode.ByReference)
            .Descendants("FunctionReference").Single().Attribute("Id")!.Value);
    }

    /// <summary>Reports missing, malformed, and unsupported legacy content with stable node paths.</summary>
    [TestMethod]
    public void Test_LegacyMalformedAndUnsupportedContent_ReportsDeterministicPaths()
    {
        Guid duplicate = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var cases = new List<(XElement Xml, string Expected)>
        {
            (new XElement("Node"), "no Type discriminator"),
            (new XElement(nameof(EventTreeResponse),
                LegacyRoot("HazardLevels", "0|1", LegacyRemainder("One")),
                LegacyRoot("HazardLevels", "0|1", LegacyRemainder("Two"))),
                "more than one recursive Node root"),
            (new XElement("Node", new XAttribute("Type", "InitiatingNode")), "HazardLevels or HazardIntervals"),
            (LegacyRoot("HazardLevels", "0|bad", LegacyRemainder("Survival")), "hazard level 'bad'"),
            (LegacyRoot("HazardLevels", "0|1", new XElement("Node",
                new XAttribute("Type", "ChanceNode"), new XAttribute("Name", "Missing table"),
                new XAttribute("SourceProbability", "MultiValue"))), "Missing table"),
            (LegacyRoot("HazardLevels", "0|1", new XElement("Node",
                new XAttribute("Type", "ChanceNode"), new XAttribute("Name", "Bad distribution"),
                new XAttribute("SourceProbability", "SingleValue"),
                new XElement("AllHazardsDistribution", new XAttribute("Type", "Uniform"),
                    new XAttribute("Min", "bad"), new XAttribute("Max", "1")))), "Bad distribution"),
            (LegacyRoot("HazardLevels", "0|1", new XElement("Node",
                new XAttribute("Type", "SecondaryHazardNode"), new XAttribute("Name", "Secondary"))), "SecondaryHazardNode"),
            (LegacyRoot("HazardLevels", "0|1", new XElement("Node",
                new XAttribute("Type", "WeightedHazardLevel"), new XAttribute("Name", "Bivariate"))), "bivariate-response"),
            (LegacyRoot("HazardLevels", "0|1", new XElement("Node",
                new XAttribute("Type", "UnknownNode"), new XAttribute("Name", "Mystery"))), "UnknownNode"),
            (LegacyRoot("HazardLevels", "0|1", new XElement("Node",
                new XAttribute("Type", "ChanceNode"), new XAttribute("Name", "Missing event node"),
                new XAttribute("SourceProbability", "EventNode"))), "no ReferenceNode id"),
            (LegacyRoot("HazardLevels", "0|1", new XElement("Node",
                new XAttribute("Type", "ChanceNode"), new XAttribute("Name", "Invalid event node"),
                new XAttribute("SourceProbability", "EventNode"),
                new XAttribute("ReferenceNode", "not-a-guid"))), "ReferenceNode id 'not-a-guid' is invalid"),
            (LegacyRoot("HazardLevels", "0|1", new XElement("Node",
                new XAttribute("Type", "ChanceNode"), new XAttribute("Name", "Shared legacy probability"),
                new XAttribute("SourceProbability", "EventNode"),
                new XAttribute("ReferenceNode", "60000000-0000-0000-0000-000000000001"))), "Shared legacy probability"),
            (LegacyRoot("HazardLevels", "0|1", new XElement("Node",
                new XAttribute("Type", "ChanceNode"), new XAttribute("Name", "Bad id"),
                new XAttribute("NodeGuid", "not-a-guid"), new XAttribute("SourceProbability", "SingleValue"))), "Bad id"),
            (LegacyRoot("HazardLevels", "0|1",
                LegacySingleValueNode("Duplicate A", new Deterministic(0.2d), id: duplicate),
                LegacySingleValueNode("Duplicate B", new Deterministic(0.3d), id: duplicate)), "duplicate legacy node id"),
            (LegacyRoot("HazardLevels", "0|1",
                LegacyRemainder("Remainder first"),
                LegacySingleValueNode("Chance last", new Deterministic(0.2d))), "remainder child must be serialized last"),
        };

        foreach ((XElement xml, string expected) in cases)
        {
            InvalidOperationException error = Assert.ThrowsException<InvalidOperationException>(
                () => new EventTreeResponse(xml), expected);
            StringAssert.Contains(error.Message, "Legacy event-tree conversion failed at '");
            StringAssert.Contains(error.Message, expected);
        }

        XElement missingReference = LegacyRoot("HazardLevels", "0|1",
            LegacyResponseNode("Missing stored function", "Not in store"),
            LegacyRemainder("Survival"));
        EventTreeResponse unresolved = ImportWrapped(missingReference, "Unresolved",
            new RiskFunctionResolver(_ => null, _ => null));
        Assert.IsFalse(unresolved.Validate().IsValid);
        Assert.IsTrue(unresolved.Validate().ValidationMessages.Any(message =>
            message.Contains("Missing stored function", StringComparison.Ordinal)
            && message.Contains("Not in store", StringComparison.Ordinal)));
    }

    /// <summary>Proves legacy metadata, ids, order, envelopes, and current modes do not move hash or seed streams.</summary>
    [TestMethod]
    public void Test_LegacyMetadataOrderIdsWrappersAndModes_AreHashAndSeedInert()
    {
        XElement a = LegacyMultiValueNode("A", new[] { 0.1d, 0.2d }, new[] { 0.3d, 0.4d },
            Guid.Parse("10000000-0000-0000-0000-000000000001"));
        XElement b = LegacyMultiValueNode("B", new[] { 0.05d, 0.1d }, new[] { 0.15d, 0.2d },
            Guid.Parse("10000000-0000-0000-0000-000000000002"));
        EventTreeResponse first = ImportWrapped(LegacyRoot("HazardLevels", "0|1",
            new XElement(a), new XElement(b), LegacyRemainder("Survival")), "First");

        XElement renamedA = new XElement(a);
        renamedA.SetAttributeValue("Name", "Renamed A");
        renamedA.SetAttributeValue("NodeGuid", Guid.Parse("20000000-0000-0000-0000-000000000001"));
        XElement renamedB = new XElement(b);
        renamedB.SetAttributeValue("Name", "Renamed B");
        renamedB.SetAttributeValue("NodeGuid", Guid.Parse("20000000-0000-0000-0000-000000000002"));
        XElement secondRoot = LegacyRoot("HazardIntervals", "0|1",
            renamedB, renamedA, LegacyRemainder("Renamed survival", id:
                Guid.Parse("20000000-0000-0000-0000-000000000003")));
        secondRoot.SetAttributeValue("Name", "Renamed hazard");
        secondRoot.SetAttributeValue("NodeGuid", Guid.Parse("20000000-0000-0000-0000-000000000004"));
        EventTreeResponse second = ImportTreeEnvelope(secondRoot, "Second");

        CollectionAssert.AreEqual(first.CanonicalHash(), second.CanonicalHash());
        first.SetupSampler(64, 97531, SamplingScheme.LatinHypercube);
        second.SetupSampler(64, 97531, SamplingScheme.LatinHypercube);
        for (int realization = 0; realization < 64; realization++)
        {
            for (int h = 0; h < first.HazardLevels.Count; h++)
                Assert.AreEqual(first.SampleResponseFunction(realization)[h].Y,
                    second.SampleResponseFunction(realization)[h].Y);
        }

        var selfContained = new EventTreeResponse(first.ToXElement(RiskSerializationMode.SelfContained));
        var byReference = new EventTreeResponse(first.ToXElement(RiskSerializationMode.ByReference));
        CollectionAssert.AreEqual(first.CanonicalHash(), selfContained.CanonicalHash());
        CollectionAssert.AreEqual(first.CanonicalHash(), byReference.CanonicalHash());
    }

    /// <summary>Uses converted response edges in cycle diagnostics and preserves live sampler state after failures.</summary>
    [TestMethod]
    public void Test_LegacyFailedConversionAndRecursiveCycle_PreserveLiveSamplerState()
    {
        EventTreeResponse stored = ImportWrapped(LegacyRoot("HazardLevels", "0|1",
            LegacyMultiValueNode("Stored uncertainty", new[] { 0.1d, 0.2d }, new[] { 0.3d, 0.4d }),
            LegacySingleValueNode("A to B", new Deterministic(0.1d)),
            LegacyRemainder("Stored survival")), "Stored A");
        stored.SetupSampler(16, 86420, SamplingScheme.LatinHypercube);
        double percentileBefore = stored.SampledPercentile(0, 0);
        double valueBefore = stored.SampleResponseFunction(0)[0].Y;
        IRiskFunctionResolver resolver = Resolver(stored);

        XElement failsAfterReference = LegacyRoot("HazardLevels", "0|1",
            LegacyResponseNode("Resolved first", stored.Name),
            new XElement("Node", new XAttribute("Type", "ChanceNode"),
                new XAttribute("Name", "Malformed second"),
                new XAttribute("SourceProbability", "MultiValue")));
        _ = Assert.ThrowsException<InvalidOperationException>(() =>
            ImportWrapped(failsAfterReference, "Failed import", resolver));
        Assert.AreEqual(16, stored.SampleSize);
        Assert.AreEqual(percentileBefore, stored.SampledPercentile(0, 0));
        Assert.AreEqual(valueBefore, stored.SampleResponseFunction(0)[0].Y);

        EventTreeResponse convertedB = ImportWrapped(LegacyRoot("HazardLevels", "0|1",
            LegacyResponseNode("B to A", stored.Name),
            LegacyRemainder("B survival")), "Stored B", resolver);
        ChanceNode aToB = stored.EventTree.Nodes.OfType<ChanceNode>()
            .Single(node => node.Name == "A to B");
        ProbabilitySource original = aToB.ProbabilitySource;
        aToB.ProbabilitySource = new ProbabilitySource(convertedB);

        InvalidOperationException cycle = Assert.ThrowsException<InvalidOperationException>(
            () => convertedB.CanonicalHash());
        StringAssert.Contains(cycle.Message, "Cross-function event-tree cycle detected");
        StringAssert.Contains(cycle.Message, "Stored A");
        StringAssert.Contains(cycle.Message, "A to B");
        StringAssert.Contains(cycle.Message, "Stored B");
        StringAssert.Contains(cycle.Message, "B to A");

        aToB.ProbabilitySource = original;
        Assert.AreEqual(16, stored.SampleSize);
        Assert.AreEqual(percentileBefore, stored.SampledPercentile(0, 0));
        Assert.AreEqual(valueBefore, stored.SampleResponseFunction(0)[0].Y);
    }

    /// <summary>Builds the exact recursive Basic shape exercised by legacy Test_EventTree.TestIO.</summary>
    private static XElement LegacyTestIoRoot()
    {
        return XElement.Parse(
            "<Node Type=\"InitiatingNode\" Name=\"Hazard\" Description=\"\" " +
            "NodeGuid=\"1c64b73c-daa4-4883-829f-d6dcd900e0aa\" HazardLevels=\"0|1\" " +
            "TemplateName=\"Basic\" TemplateDescription=\"Basic Event Tree\">" +
            "<Node Type=\"ChanceNode\" Name=\"Fail\" Description=\"\" SourceProbability=\"MultiValue\" " +
            "NodeGuid=\"1cbacacd-dc6b-48bc-a9f1-58318fdce2a8\" ReferenceNode=\"\">" +
            "<IntervalDistributions X_Strict=\"True\" Y_Strict=\"True\" X_Order=\"Ascending\" " +
            "Y_Order=\"None\" Distribution=\"Uniform\"><Ordinates>" +
            "<Ordinate X=\"0\" Min=\"0\" Max=\"0\"/><Ordinate X=\"1\" Min=\"0\" Max=\"1\"/>" +
            "</Ordinates></IntervalDistributions></Node>" +
            "<Node Type=\"RemainderNode\" Name=\"Non-Fail\" Description=\"\" " +
            "NodeGuid=\"5b9f7613-ec9e-4fd1-8943-a8ebc3d9743c\"/></Node>");
    }

    /// <summary>Builds one legacy initiating root with deterministic metadata.</summary>
    private static XElement LegacyRoot(string hazardAttribute, string hazards, params XElement[] children)
    {
        var root = new XElement("Node");
        root.SetAttributeValue("Type", nameof(InitiatingNode));
        root.SetAttributeValue("Name", "Stage");
        root.SetAttributeValue("Description", "Legacy root");
        root.SetAttributeValue("NodeGuid", "30000000-0000-0000-0000-000000000001");
        root.SetAttributeValue(hazardAttribute, hazards);
        root.Add(children);
        return root;
    }

    /// <summary>Builds one legacy single-value chance node.</summary>
    private static XElement LegacySingleValueNode(string name, UnivariateDistributionBase distribution,
        string idAttribute = "NodeGuid", Guid? id = null)
    {
        var node = LegacyChance(name, "SingleValue", idAttribute, id);
        XElement content = distribution.ToXElement();
        content.Name = "AllHazardsDistribution";
        node.Add(content);
        return node;
    }

    /// <summary>Builds one legacy aligned uncertain-table chance node.</summary>
    private static XElement LegacyMultiValueNode(string name, double[] minimums, double[] maximums,
        Guid? id = null)
    {
        var node = LegacyChance(name, "MultiValue", "NodeGuid", id);
        var table = new UncertainOrderedPairedData(
            minimums.Select((minimum, index) => new UncertainOrdinate(index,
                new Uniform(minimum, maximums[index]))).ToArray(),
            true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
        XElement content = table.SaveToXElement();
        content.Name = "IntervalDistributions";
        node.Add(content);
        return node;
    }

    /// <summary>Builds one legacy name-only response-function chance node.</summary>
    private static XElement LegacyResponseNode(string name, string functionName)
    {
        var node = LegacyChance(name, "ResponseFunction");
        node.SetAttributeValue("ResponseFunction", functionName);
        return node;
    }

    /// <summary>Builds common legacy chance-node attributes.</summary>
    private static XElement LegacyChance(string name, string sourceKind,
        string idAttribute = "NodeGuid", Guid? id = null)
    {
        var node = new XElement("Node");
        node.SetAttributeValue("Type", nameof(ChanceNode));
        node.SetAttributeValue("Name", name);
        node.SetAttributeValue("Description", $"{name} description");
        node.SetAttributeValue("SourceProbability", sourceKind);
        if (id != null)
            node.SetAttributeValue(idAttribute, id.Value.ToString("D"));
        node.SetAttributeValue("ReferenceNode", string.Empty);
        return node;
    }

    /// <summary>Builds one legacy remainder node.</summary>
    private static XElement LegacyRemainder(string name, string idAttribute = "NodeGuid", Guid? id = null)
    {
        var node = new XElement("Node");
        node.SetAttributeValue("Type", nameof(RemainderNode));
        node.SetAttributeValue("Name", name);
        node.SetAttributeValue("Description", string.Empty);
        if (id != null)
            node.SetAttributeValue(idAttribute, id.Value.ToString("D"));
        return node;
    }

    /// <summary>Imports a legacy root in the direct current-function envelope.</summary>
    private static EventTreeResponse ImportWrapped(XElement root, string name = "Converted",
        IRiskFunctionResolver? resolver = null)
    {
        var wrapper = new XElement(nameof(EventTreeResponse));
        wrapper.SetAttributeValue("Name", name);
        wrapper.SetAttributeValue("Description", "Converted legacy fixture");
        wrapper.SetAttributeValue("SpecifiedHazard", "Stage");
        wrapper.SetAttributeValue("HazardUnit", "ft");
        wrapper.Add(new XElement(root));
        return new EventTreeResponse(wrapper, resolver);
    }

    /// <summary>Imports a legacy root inside an EventTree-named persistence envelope.</summary>
    private static EventTreeResponse ImportTreeEnvelope(XElement root, string name = "Converted")
    {
        var wrapper = new XElement(nameof(EventTreeResponse));
        wrapper.SetAttributeValue("Name", name);
        wrapper.SetAttributeValue("SpecifiedHazard", "Stage");
        wrapper.SetAttributeValue("HazardUnit", "ft");
        wrapper.Add(new XElement(nameof(EventTree), new XElement(root)));
        return new EventTreeResponse(wrapper);
    }

    /// <summary>Adds the metadata required by response validation to a direct legacy import.</summary>
    private static void MakeValid(EventTreeResponse response)
    {
        response.SpecifiedHazard = "Stage";
        response.HazardUnit = "ft";
    }

    /// <summary>Builds an aligned deterministic response table.</summary>
    private static UncertainOrderedPairedData DeterministicTable(double[] hazards, double[] probabilities)
    {
        return new UncertainOrderedPairedData(
            hazards.Select((hazard, index) =>
                new UncertainOrdinate(hazard, new Deterministic(probabilities[index]))).ToArray(),
            true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Deterministic);
    }

    /// <summary>Builds the stock two-key resolver over live stored functions.</summary>
    private static IRiskFunctionResolver Resolver(params IRiskFunction[] functions)
    {
        return new RiskFunctionResolver(
            id => functions.FirstOrDefault(function => function.Id == id),
            name => functions.FirstOrDefault(function => function.Name == name));
    }
}
