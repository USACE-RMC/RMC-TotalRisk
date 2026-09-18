using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.Verification.RiskFunctions.Responses.EventTrees;

/// <summary>Legacy-conversion and shipped-template cases in the event-tree family.</summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// </remarks>
public partial class EventTreeVerification
{
    /// <summary>Converts the exact legacy TestIO Basic shape and asserts every terminal analytically.</summary>
    [TestMethod]
    public void Test_LegacyTestIoShape_ConvertsCanonicalAndEqualsAnalytic()
    {
        XElement legacy = LoadLegacyTemplates().Single(template =>
            template.Attribute("TemplateName")?.Value == "Basic");
        EventTreeResponse converted = ImportLegacy(legacy);

        AssertTemplateMatchesIndependentMeanOracle(legacy, converted);
        ResponseBranchSample branches = converted.SampleBranches();
        int failure = BranchIndex(branches,
            Guid.Parse("1cbacacd-dc6b-48bc-a9f1-58318fdce2a8"));
        int remainder = BranchIndex(branches,
            Guid.Parse("5b9f7613-ec9e-4fd1-8943-a8ebc3d9743c"));
        Assert.AreEqual(0d, branches.Probabilities[failure][0]);
        Assert.AreEqual(0.5d, branches.Probabilities[failure][1]);
        Assert.AreEqual(1d, branches.Probabilities[remainder][0]);
        Assert.AreEqual(0.5d, branches.Probabilities[remainder][1]);

        XElement current = converted.ToXElement(RiskSerializationMode.SelfContained);
        Assert.IsFalse(current.Descendants("Node").Any());
        var replay = new EventTreeResponse(current);
        CollectionAssert.AreEqual(converted.CanonicalHash(), replay.CanonicalHash());
        AssertCurvesEqual(converted.SampleResponseFunction(), replay.SampleResponseFunction());
    }

    /// <summary>Converts representative shipped small and recursive templates against an independent path oracle.</summary>
    [TestMethod]
    public void Test_LegacyShippedTemplates_EveryTerminalAndAggregateEqualIndependentOracle()
    {
        XElement[] templates = LoadLegacyTemplates();
        CollectionAssert.AreEquivalent(
            new[] { "Basic", "Concrete Dam Gate Failure" },
            templates.Select(template => template.Attribute("TemplateName")!.Value).ToArray());

        foreach (XElement legacy in templates)
        {
            EventTreeResponse converted = ImportLegacy(legacy);
            AssertTemplateMatchesIndependentMeanOracle(legacy, converted);
            XElement selfXml = converted.ToXElement(RiskSerializationMode.SelfContained);
            XElement referenceXml = converted.ToXElement(RiskSerializationMode.ByReference);
            var selfContained = new EventTreeResponse(selfXml);
            var byReference = new EventTreeResponse(referenceXml);

            Assert.IsFalse(selfXml.Descendants("Node").Any());
            Assert.IsFalse(referenceXml.Descendants("Node").Any());
            CollectionAssert.AreEqual(converted.CanonicalHash(), selfContained.CanonicalHash());
            CollectionAssert.AreEqual(converted.CanonicalHash(), byReference.CanonicalHash());
            AssertCurvesEqual(converted.SampleResponseFunction(), selfContained.SampleResponseFunction());
            AssertCurvesEqual(converted.SampleResponseFunction(), byReference.SampleResponseFunction());

            if (legacy.Attribute("TemplateName")!.Value == "Basic")
            {
                CollectionAssert.AreEqual(new[] { 0d, 0.5d },
                    converted.SampleResponseFunction().Select(point => point.Y).ToArray());
            }
            else
            {
                CollectionAssert.AreEqual(new[] { 0d, 0.125d, 0.125d, 0.125d, 0.125d },
                    converted.SampleResponseFunction().Select(point => point.Y).ToArray());
            }
        }
    }

    /// <summary>
    /// Converts a legacy response-name edge to a nested legacy event tree and verifies every LHS
    /// realization against the source table and caller-hazard interpolation in both current modes.
    /// </summary>
    [TestMethod]
    public void Test_LegacyNestedResponse_LhsRealizationsAndModesEqualIndependentOracle()
    {
        var table = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0.1d, 0.3d)),
                new UncertainOrdinate(2d, new Uniform(0.4d, 0.8d)),
            }, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Uniform);
        XElement nestedRoot = LegacyRoot("Stored nested", "0|2",
            LegacyTableChance("Nested failure", table), LegacyRemainder("Nested survival"));
        EventTreeResponse nested = ImportLegacy(nestedRoot, "Stored nested");
        IRiskFunctionResolver resolver = new RiskFunctionResolver(
            id => id == nested.Id ? nested : null,
            name => name == nested.Name ? nested : null);
        XElement outerRoot = LegacyRoot("Outer hazard", "0|1|2",
            LegacyResponseChance("Outer nested source", nested.Name),
            LegacyRemainder("Outer survival"));
        EventTreeResponse original = ImportLegacy(outerRoot, "Converted outer", resolver);
        XElement selfXml = original.ToXElement(RiskSerializationMode.SelfContained);
        XElement referenceXml = original.ToXElement(RiskSerializationMode.ByReference);
        var selfContained = new EventTreeResponse(selfXml);
        var byReference = new EventTreeResponse(referenceXml, resolver);

        Assert.AreEqual(nested.Id.ToString("D"), referenceXml.Descendants("FunctionReference")
            .Single().Attribute("Id")!.Value);
        CollectionAssert.AreEqual(original.CanonicalHash(), selfContained.CanonicalHash());
        CollectionAssert.AreEqual(original.CanonicalHash(), byReference.CanonicalHash());
        original.SetupSampler(256, 11235813, SamplingScheme.LatinHypercube);
        selfContained.SetupSampler(256, 11235813, SamplingScheme.LatinHypercube);
        byReference.SetupSampler(256, 11235813, SamplingScheme.LatinHypercube);

        Assert.AreEqual(1, original.SamplingDimensions);
        for (int realization = 0; realization < 256; realization++)
        {
            double percentile = original.SampledPercentile(realization, 0);
            OrderedPairedData direct = table.CurveSample(percentile);
            OrderedPairedData actual = original.SampleResponseFunction(realization);
            OrderedPairedData self = selfContained.SampleResponseFunction(realization);
            OrderedPairedData reference = byReference.SampleResponseFunction(realization);
            ResponseBranchSample branches = original.SampleBranches(realization);
            int failure = Enumerable.Range(0, branches.Branches.Count)
                .Single(index => branches.Branches[index].IsFailure);

            for (int h = 0; h < original.HazardLevels.Count; h++)
            {
                double expected = InterpolateResponseCurve(direct, original.HazardLevels[h]);
                Assert.AreEqual(expected, actual[h].Y, 1e-14d);
                Assert.AreEqual(expected, branches.Probabilities[failure][h], 1e-14d);
                Assert.AreEqual(actual[h].Y, self[h].Y);
                Assert.AreEqual(actual[h].Y, reference[h].Y);
                Assert.AreEqual(1d, branches.Probabilities.Sum(row => row[h]), 1e-14d);
            }
        }
    }

    /// <summary>Loads the repository-owned copies of the audited shipped legacy templates.</summary>
    private static XElement[] LoadLegacyTemplates()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Data", "LegacyEventTreeTemplates.xml");
        return XDocument.Load(path).Root!.Elements("Node")
            .Select(template => new XElement(template)).ToArray();
    }

    /// <summary>Imports one direct legacy root and supplies consuming-layer response metadata.</summary>
    private static EventTreeResponse ImportLegacy(XElement root, string? name = null,
        IRiskFunctionResolver? resolver = null)
    {
        EventTreeResponse response;
        if (name == null)
        {
            response = new EventTreeResponse(new XElement(root), resolver);
        }
        else
        {
            var wrapper = new XElement(nameof(EventTreeResponse));
            wrapper.SetAttributeValue("Name", name);
            wrapper.SetAttributeValue("SpecifiedHazard", "Stage");
            wrapper.SetAttributeValue("HazardUnit", "ft");
            wrapper.Add(new XElement(root));
            response = new EventTreeResponse(wrapper, resolver);
        }
        response.SpecifiedHazard = "Stage";
        response.HazardUnit = "ft";
        return response;
    }

    /// <summary>Asserts every terminal branch and aggregate against a separate recursive path-product walk.</summary>
    private static void AssertTemplateMatchesIndependentMeanOracle(
        XElement legacy, EventTreeResponse converted)
    {
        var expected = LegacyMeanPathOracle(legacy, converted.HazardLevels);
        ResponseBranchSample actual = converted.SampleBranches();
        for (int branch = 0; branch < actual.Branches.Count; branch++)
        {
            ResponseBranchDescriptor descriptor = actual.Branches[branch];
            for (int h = 0; h < actual.Hazards.Count; h++)
            {
                double value = descriptor.Name == "Unmodeled"
                    ? 0d
                    : expected[descriptor.Id][h];
                Assert.AreEqual(value, actual.Probabilities[branch][h], 1e-14d,
                    $"Template '{converted.Name}', terminal '{descriptor.Name}', hazard index {h}.");
            }
        }

        OrderedPairedData aggregate = converted.SampleResponseFunction();
        for (int h = 0; h < aggregate.Count; h++)
        {
            double expectedFailure = actual.Branches
                .Select((branch, index) => new { branch.IsFailure, Value = actual.Probabilities[index][h] })
                .Where(item => item.IsFailure)
                .Sum(item => item.Value);
            Assert.AreEqual(expectedFailure, aggregate[h].Y, 1e-14d);
        }
    }

    /// <summary>Computes terminal mean probabilities directly from legacy recursive XML.</summary>
    private static Dictionary<Guid, double[]> LegacyMeanPathOracle(
        XElement root, IReadOnlyList<double> hazards)
    {
        var result = root.Descendants("Node")
            .Where(node => !node.Elements("Node").Any())
            .ToDictionary(LegacyNodeId, _ => new double[hazards.Count]);
        for (int h = 0; h < hazards.Count; h++)
            WalkLegacyChildren(root, 1d, h, result);
        return result;
    }

    /// <summary>Recursively applies raw/normalized sibling and residual rules for one hazard.</summary>
    private static void WalkLegacyChildren(XElement parent, double pathProbability, int hazardIndex,
        IDictionary<Guid, double[]> terminals)
    {
        XElement[] children = parent.Elements("Node").ToArray();
        XElement[] explicitChildren = children.Where(child =>
            child.Attribute("Type")?.Value != nameof(RemainderNode)).ToArray();
        double[] raw = explicitChildren.Select(child => LegacyChanceMean(child, hazardIndex)).ToArray();
        double sum = CompensatedSum(raw);
        double scale = sum > 1d ? 1d / sum : 1d;
        for (int i = 0; i < explicitChildren.Length; i++)
            WalkLegacyNode(explicitChildren[i], pathProbability * raw[i] * scale,
                hazardIndex, terminals);

        XElement? remainder = children.SingleOrDefault(child =>
            child.Attribute("Type")?.Value == nameof(RemainderNode));
        if (remainder != null)
            WalkLegacyNode(remainder, pathProbability * (sum > 1d ? 0d : 1d - sum),
                hazardIndex, terminals);
    }

    /// <summary>Records a terminal probability or continues through one legacy node.</summary>
    private static void WalkLegacyNode(XElement node, double probability, int hazardIndex,
        IDictionary<Guid, double[]> terminals)
    {
        if (node.Elements("Node").Any())
            WalkLegacyChildren(node, probability, hazardIndex, terminals);
        else
            terminals[LegacyNodeId(node)][hazardIndex] = probability;
    }

    /// <summary>Evaluates one legacy chance source mean without using the event-tree converter.</summary>
    private static double LegacyChanceMean(XElement node, int hazardIndex)
    {
        string kind = node.Attribute("SourceProbability")?.Value ?? "MultiValue";
        if (kind == "SingleValue")
        {
            XElement? distribution = node.Element("AllHazardsDistribution");
            return distribution == null
                ? 0.5d
                : UnivariateDistributionFactory.CreateDistribution(distribution).Mean;
        }
        if (kind == "MultiValue")
        {
            var table = new UncertainOrderedPairedData(node.Element("IntervalDistributions")!);
            return table.CurveSample()[hazardIndex].Y;
        }
        throw new InvalidOperationException($"The independent shipped-template oracle does not support '{kind}'.");
    }

    /// <summary>Reads one preserved legacy node identity.</summary>
    private static Guid LegacyNodeId(XElement node)
    {
        return Guid.Parse(node.Attribute("NodeGuid")?.Value
            ?? node.Attribute("NodeGUID")?.Value
            ?? throw new InvalidOperationException("The verification fixture node has no legacy id."));
    }

    /// <summary>Computes one sibling sum with an independent Kahan accumulator.</summary>
    private static double CompensatedSum(IEnumerable<double> values)
    {
        double sum = 0d;
        double compensation = 0d;
        foreach (double value in values)
        {
            double adjusted = value - compensation;
            double next = sum + adjusted;
            compensation = (next - sum) - adjusted;
            sum = next;
        }
        return sum;
    }

    /// <summary>Returns one sampled branch index by persistent identity.</summary>
    private static int BranchIndex(ResponseBranchSample sample, Guid id)
    {
        return Enumerable.Range(0, sample.Branches.Count)
            .Single(index => sample.Branches[index].Id == id);
    }

    /// <summary>Asserts bit-for-bit curve equality.</summary>
    private static void AssertCurvesEqual(OrderedPairedData expected, OrderedPairedData actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].X, actual[i].X);
            Assert.AreEqual(expected[i].Y, actual[i].Y);
        }
    }

    /// <summary>Builds one inline legacy root for recursive response-source verification.</summary>
    private static XElement LegacyRoot(string name, string hazards, params XElement[] children)
    {
        var root = new XElement("Node");
        root.SetAttributeValue("Type", nameof(InitiatingNode));
        root.SetAttributeValue("Name", name);
        root.SetAttributeValue("NodeGuid", Guid.Parse("50000000-0000-0000-0000-000000000001"));
        root.SetAttributeValue("HazardLevels", hazards);
        root.Add(children);
        return root;
    }

    /// <summary>Builds one inline legacy MultiValue chance node.</summary>
    private static XElement LegacyTableChance(string name, UncertainOrderedPairedData table)
    {
        var node = LegacyChance(name, "MultiValue",
            Guid.Parse("50000000-0000-0000-0000-000000000002"));
        XElement content = table.SaveToXElement();
        content.Name = "IntervalDistributions";
        node.Add(content);
        return node;
    }

    /// <summary>Builds one inline legacy response-reference chance node.</summary>
    private static XElement LegacyResponseChance(string name, string functionName)
    {
        var node = LegacyChance(name, "ResponseFunction",
            Guid.Parse("50000000-0000-0000-0000-000000000004"));
        node.SetAttributeValue("ResponseFunction", functionName);
        return node;
    }

    /// <summary>Builds common inline legacy chance content.</summary>
    private static XElement LegacyChance(string name, string kind, Guid id)
    {
        var node = new XElement("Node");
        node.SetAttributeValue("Type", nameof(ChanceNode));
        node.SetAttributeValue("Name", name);
        node.SetAttributeValue("SourceProbability", kind);
        node.SetAttributeValue("NodeGuid", id);
        node.SetAttributeValue("ReferenceNode", string.Empty);
        return node;
    }

    /// <summary>Builds one inline legacy remainder terminal.</summary>
    private static XElement LegacyRemainder(string name)
    {
        var node = new XElement("Node");
        node.SetAttributeValue("Type", nameof(RemainderNode));
        node.SetAttributeValue("Name", name);
        node.SetAttributeValue("NodeGuid",
            name == "Nested survival"
                ? Guid.Parse("50000000-0000-0000-0000-000000000003")
                : Guid.Parse("50000000-0000-0000-0000-000000000005"));
        return node;
    }
}
