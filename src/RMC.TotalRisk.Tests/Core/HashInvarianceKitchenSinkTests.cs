using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Tests.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.Core;

/// <summary>
/// The kitchen-sink hash-invariance registry — the landing-checklist guard for the content-based
/// seed-identity contract. EVERY concrete model type registers one entry here as it lands
/// (a factory producing a representative configured instance plus one compute-relevant mutation);
/// the tests then prove, for every registered type: metadata edits are hash-inert, every audited
/// stripped attribute is inert anywhere in the serialized tree, serialization is deterministic,
/// and a compute edit moves the hash.
/// </summary>
[TestClass]
public class HashInvarianceKitchenSinkTests
{
    /// <summary>
    /// One registry entry per concrete model type: a display name, a factory for a representative
    /// instance, and a compute-relevant mutation of that instance.
    /// </summary>
    /// <param name="TypeName">The display name reported on assert failures.</param>
    /// <param name="Factory">Creates a representative configured instance.</param>
    /// <param name="ComputeMutation">Mutates compute-relevant state on the instance.</param>
    public sealed record RegistryEntry(string TypeName, Func<IRiskFunction> Factory, Action<IRiskFunction> ComputeMutation);

    /// <summary>
    /// The registry. LANDING CHECKLIST: add one entry per new concrete function type.
    /// (SystemComponent/FailureMode assert the same contract in their own test classes
    /// via the delegate-based <see cref="HashInvariance"/> helpers.)
    /// </summary>
    private static IEnumerable<RegistryEntry> RegisteredTypes()
    {
        // The kernel stub proving the scaffold itself.
        yield return new RegistryEntry(
            nameof(StubRiskFunction),
            () => new StubRiskFunction { Name = "Stub", Description = "Rep", SpecifiedHazard = "Flow", HazardUnit = "cfs", Value = 2.5 },
            f => ((StubRiskFunction)f).Value = 99.5);

        // Core input functions. (NonFailResponse is deliberately absent: it carries no
        // compute content by design; its content-free hashing is pinned in NonFailResponseTests.)
        yield return new RegistryEntry(
            nameof(TabularHazard),
            () => new TabularHazard { Name = "Hazard", SpecifiedHazard = "Stage", HazardUnit = "ft" },
            f => ((TabularHazard)f).UncertaintyValue = FunctionUncertainty.Hazard);

        yield return new RegistryEntry(
            nameof(ParametricUnivariateHazard),
            () => new ParametricUnivariateHazard { Name = "Hazard", SpecifiedHazard = "Flow", HazardUnit = "cfs", ParentDistribution = new Normal(100d, 20d) },
            f => ((ParametricUnivariateHazard)f).PRNGSeed = 999);

        yield return new RegistryEntry(
            nameof(TabularTransform),
            () => new TabularTransform { Name = "Rating", SpecifiedHazard = "Flow", HazardUnit = "cfs", TransformedHazard = "Stage", TransformedHazardUnit = "ft" },
            f => ((TabularTransform)f).UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(2d, new Deterministic(3d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic));

        // The nonparametric hazard (inputs-only identity: the derived table never
        // serializes, so only input edits can move the hash).
        yield return new RegistryEntry(
            nameof(NonparametricHazard),
            () => new NonparametricHazard { Name = "Graphical", SpecifiedHazard = "Flow", HazardUnit = "cfs" },
            f => ((NonparametricHazard)f).EffectiveRecordLength = 200);

        // Closed-form transforms, registered uncertain so the conditional sigma
        // attribute participates in the hash surface.
        yield return new RegistryEntry(
            nameof(LinearTransform),
            () => new LinearTransform { Name = "Stage shift", SpecifiedHazard = "Flow", HazardUnit = "cfs", TransformedHazard = "Stage", TransformedHazardUnit = "ft", Alpha = 2d, Beta = 0.5d, Sigma = 1.5d, IsUncertain = true },
            f => ((LinearTransform)f).Beta = 2d);

        yield return new RegistryEntry(
            nameof(PowerTransform),
            () => new PowerTransform { Name = "Rating", SpecifiedHazard = "Stage", HazardUnit = "ft", TransformedHazard = "Flow", TransformedHazardUnit = "cfs", Alpha = 5d, Beta = 2d, Xi = 1d, Sigma = 0.1d, IsUncertain = true },
            f => ((PowerTransform)f).IsInverse = !((PowerTransform)f).IsInverse);

        yield return new RegistryEntry(
            nameof(TabularResponse),
            () => new TabularResponse { Name = "Fragility", SpecifiedHazard = "Stage", HazardUnit = "ft" },
            f => ((TabularResponse)f).ProbabilityTransform = Transform.NormalZ);

        yield return new RegistryEntry(
            nameof(ParametricResponse),
            () => new ParametricResponse { Name = "Fragility", SpecifiedHazard = "Stage", HazardUnit = "ft", ParentDistribution = new Normal(10d, 2d) },
            f => ((ParametricResponse)f).EffectiveRecordLength = 77);

        yield return new RegistryEntry(
            nameof(TabularConsequence),
            () => new TabularConsequence { Name = "Damages", SpecifiedHazard = "Stage", HazardUnit = "ft", SpecifiedConsequence = "Damages", ConsequenceUnit = "$" },
            f => ((TabularConsequence)f).HazardTransform = Transform.Logarithmic);

        // The parametric consequence — an uncertain instance so the conditional
        // sigma attributes participate in the hash surface.
        yield return new RegistryEntry(
            nameof(ParametricConsequence),
            () => new ParametricConsequence
            {
                Name = "Life loss",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = "Life Loss",
                ConsequenceUnit = "lives",
                Alpha = 10d,
                Beta = 1.5d,
                Threshold = 2d,
                UpperBound = 500d,
                IsUncertain = true,
                SigmaAlpha = 0.3d,
                SigmaBeta = 0.2d,
            },
            f => ((ParametricConsequence)f).Alpha = 2.5d);

        yield return new RegistryEntry(
            nameof(CompositeConsequence),
            () => new CompositeConsequence(new[]
            {
                new WeightedConsequenceFunction(new TabularConsequence { Name = "Day", SpecifiedHazard = "Stage", HazardUnit = "ft", SpecifiedConsequence = "Life Loss", ConsequenceUnit = "lives" }, 0.42d),
                new WeightedConsequenceFunction(new TabularConsequence { Name = "Night", SpecifiedHazard = "Stage", HazardUnit = "ft", SpecifiedConsequence = "Life Loss", ConsequenceUnit = "lives" }, 0.58d),
            })
            {
                Name = "Day/Night",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SpecifiedConsequence = "Life Loss",
                ConsequenceUnit = "lives",
                CompositeFunctionType = CompositeFunctionType.Average,
            },
            f => ((CompositeConsequence)f).CompositeFunctionType = CompositeFunctionType.Mixture);

        // The composite hazard. Registered in Mixture mode so the weights participate in
        // the hash surface; the mutation flips the combination rule, which changes results.
        yield return new RegistryEntry(
            nameof(CompositeHazard),
            () => new CompositeHazard(new[]
            {
                new WeightedHazardFunction(new TabularHazard { Name = "Rain", SpecifiedHazard = "Flow", HazardUnit = "cfs" }, 0.45d),
                new WeightedHazardFunction(new TabularHazard { Name = "Snow", SpecifiedHazard = "Flow", HazardUnit = "cfs" }, 0.55d),
            })
            {
                Name = "Rain/Snow",
                SpecifiedHazard = "Flow",
                HazardUnit = "cfs",
                CompositeCombinationType = CompositeCombinationType.Mixture,
            },
            f => ((CompositeHazard)f).CompositeCombinationType = CompositeCombinationType.CompetingRisks);

        // The composite response, registered in Mixture mode for the same reason.
        yield return new RegistryEntry(
            nameof(CompositeResponse),
            () => new CompositeResponse(new[]
            {
                new WeightedResponseFunction(new TabularResponse { Name = "Overtopping", SpecifiedHazard = "Stage", HazardUnit = "ft" }, 0.45d),
                new WeightedResponseFunction(new TabularResponse { Name = "Piping", SpecifiedHazard = "Stage", HazardUnit = "ft" }, 0.55d),
            })
            {
                Name = "Overtopping/Piping",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                CompositeCombinationType = CompositeCombinationType.Mixture,
            },
            f => ((CompositeResponse)f).ProbabilityTransform = Transform.NormalZ);


        // The event-tree response — a nested tree behind an independent structural link.
        yield return new RegistryEntry(
            nameof(EventTreeResponse),
            () =>
            {
                var nestedTree = new EventTree();
                nestedTree.Add(nestedTree.Root.Id,
                    new ChanceNode("Nested failure", new ProbabilitySource(0.1d)));
                nestedTree.Add(nestedTree.Root.Id, new RemainderNode("Nested survival"));
                var nested = new EventTreeResponse(new[] { 0d, 1d }, nestedTree)
                {
                    Name = "Nested event tree",
                    SpecifiedHazard = "Stage",
                    HazardUnit = "ft",
                };
                var tree = new EventTree();
                var failure = new ChanceNode("Failure", new ProbabilitySource(nested));
                tree.Add(tree.Root.Id, failure);
                tree.LinkIndependent(tree.Root.Id, failure.Id,
                    "Independent nested failure occurrence");
                tree.Add(tree.Root.Id, new RemainderNode("No failure"));
                return new EventTreeResponse(new[] { 0d, 1d }, tree)
                {
                    Name = "Event tree",
                    SpecifiedHazard = "Stage",
                    HazardUnit = "ft",
                };
            },
            f =>
            {
                var tree = (EventTreeResponse)f;
                var outerChance = (ChanceNode)tree.EventTree.Root.Children[0];
                var nested = (EventTreeResponse)outerChance.ProbabilitySource.ResponseFunction!;
                var nestedChance = (ChanceNode)nested.EventTree.Root.Children[0];
                nestedChance.ProbabilitySource = new ProbabilitySource(0.3d);
            });

        // The event-tree response with a shared-logical limb — the class-unified link mode.
        yield return new RegistryEntry(
            nameof(EventTreeResponse) + " shared limb",
            () =>
            {
                var tree = new EventTree();
                var carrier = new ChanceNode("Carrier", new ProbabilitySource(0.5d)) { IsFailure = false };
                tree.Add(tree.Root.Id, carrier);
                var limb = new ChanceNode("Limb", new ProbabilitySource(0.1d));
                tree.Add(carrier.Id, limb);
                var gate = new ChanceNode("Gate", new ProbabilitySource(0.25d)) { IsFailure = false };
                tree.Add(tree.Root.Id, gate);
                tree.LinkShared(gate.Id, limb.Id, "Shared limb occurrence");
                tree.Add(tree.Root.Id, new RemainderNode("No failure"));
                return new EventTreeResponse(new[] { 0d, 1d }, tree)
                {
                    Name = "Shared-limb event tree",
                    SpecifiedHazard = "Stage",
                    HazardUnit = "ft",
                };
            },
            f =>
            {
                var tree = (EventTreeResponse)f;
                var limb = (ChanceNode)tree.EventTree.Nodes.First(node => node.Name == "Limb");
                limb.ProbabilitySource = new ProbabilitySource(0.2d);
            });

        // The fault-tree response with a parametric common-cause group — the group expands into
        // derived events at compile time, so its parameters are compute content while its name
        // and description stay inert.
        yield return new RegistryEntry(
            nameof(FaultTreeResponse) + " CCF group",
            () =>
            {
                var tree = new FaultTree();
                var gate = new FaultTreeGateNode("Pair", FaultTreeGateType.And);
                tree.Add(tree.Root.Id, gate);
                var pump = new FaultTreeBasicEventNode("Pump", new ProbabilitySource(0.2d));
                var valve = new FaultTreeBasicEventNode("Valve", new ProbabilitySource(0.2d));
                tree.Add(gate.Id, pump);
                tree.Add(gate.Id, valve);
                tree.AddCcfGroup(new FaultTreeCcfGroup("Pair group", FaultTreeCcfModel.BetaFactor,
                    new[] { 0.1d }, new[] { pump.Id, valve.Id }));
                return new FaultTreeResponse(new[] { 0d, 1d }, tree)
                {
                    Name = "CCF fault tree",
                    SpecifiedHazard = "Stage",
                    HazardUnit = "ft",
                };
            },
            f =>
            {
                var tree = (FaultTreeResponse)f;
                tree.FaultTree.CcfGroups[0].Parameters = new[] { 0.3d };
            });

        // The event-tree response with a transformed probability source — the hazard-transform
        // chain rides the source identity, so chain content edits must move the hash while the
        // chain transform's metadata stays inert.
        yield return new RegistryEntry(
            nameof(EventTreeResponse) + " transformed source",
            () =>
            {
                var table = new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(2d, new Uniform(0.1d, 0.3d)),
                        new UncertainOrdinate(3d, new Uniform(0.4d, 0.8d)),
                    }, true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
                var map = new LinearTransform
                {
                    Name = "Duration map",
                    SpecifiedHazard = "Stage",
                    HazardUnit = "ft",
                    TransformedHazard = "Duration",
                    TransformedHazardUnit = "hr",
                    Minimum = -100d,
                    Maximum = 100d,
                    Alpha = 2d,
                    Beta = 0.5d,
                    Sigma = 0.25d,
                    IsUncertain = true,
                };
                var tree = new EventTree();
                tree.Add(tree.Root.Id, new ChanceNode("Failure",
                    new ProbabilitySource(table, new ITransformFunction[] { map })));
                tree.Add(tree.Root.Id, new RemainderNode("No failure"));
                return new EventTreeResponse(new[] { 0d, 1d }, tree)
                {
                    Name = "Transformed-source event tree",
                    SpecifiedHazard = "Stage",
                    HazardUnit = "ft",
                };
            },
            f =>
            {
                var tree = (EventTreeResponse)f;
                var chance = (ChanceNode)tree.EventTree.Nodes.First(node => node.Name == "Failure");
                var map = (LinearTransform)chance.ProbabilitySource.HazardTransforms[0]!;
                map.Beta = 0.75d;
            });

        // The fault-tree response — repeated shared events behind a threshold gate.
        yield return new RegistryEntry(
            nameof(FaultTreeResponse),
            () =>
            {
                var tree = new FaultTree();
                var majority = new FaultTreeGateNode("Majority", FaultTreeGateType.KOfN, 2);
                tree.Add(tree.Root.Id, majority);
                var pump = new FaultTreeBasicEventNode("Pump", new ProbabilitySource(0.2d));
                tree.Add(majority.Id, pump);
                tree.Add(majority.Id, new FaultTreeBasicEventNode("Valve", new ProbabilitySource(0.1d)));
                tree.LinkShared(majority.Id, pump.Id, "Shared pump");
                tree.Add(tree.Root.Id, new FaultTreeHouseEventNode("Maintenance bypass", false));
                return new FaultTreeResponse(new[] { 0d, 1d }, tree)
                {
                    Name = "Fault tree",
                    SpecifiedHazard = "Stage",
                    HazardUnit = "ft",
                };
            },
            f =>
            {
                var response = (FaultTreeResponse)f;
                var gate = (FaultTreeGateNode)response.FaultTree.Root.Children[0];
                ((FaultTreeBasicEventNode)gate.Children[0]).ProbabilitySource = new ProbabilitySource(0.35d);
            });

        // The bivariate hazard — linked marginals under an independence copula. The mutation
        // moves the bin count; copula/θ/marginal-content sensitivity is covered in
        // BivariateHazardTests.
        yield return new RegistryEntry(
            nameof(BivariateHazard),
            () => new BivariateHazard(
                new TabularHazard { Name = "PGA Frequency", SpecifiedHazard = "Peak Ground Acceleration", HazardUnit = "g" },
                new TabularHazard { Name = "Pool Duration Curve", SpecifiedHazard = "Pool Duration", HazardUnit = "days" })
            {
                Name = "Seismic-Pool Coupling",
                SpecifiedHazard = "Peak Ground Acceleration",
                HazardUnit = "g",
                SecondarySpecifiedHazard = "Pool Duration",
                SecondaryHazardUnit = "days",
            },
            f => ((BivariateHazard)f).SecondaryIntegrationBins = 50);

        // The composite transform (Average mode; the mutation nudges a weight).
        yield return new RegistryEntry(
            nameof(CompositeTransform),
            () => new CompositeTransform(new[]
            {
                new WeightedTransformFunction(new LinearTransform { Name = "Rating A", SpecifiedHazard = "Flow", HazardUnit = "cfs", TransformedHazard = "Stage", TransformedHazardUnit = "ft", Alpha = 2d, Beta = 3d, IsUncertain = false }, 0.4d),
                new WeightedTransformFunction(new LinearTransform { Name = "Rating B", SpecifiedHazard = "Flow", HazardUnit = "cfs", TransformedHazard = "Stage", TransformedHazardUnit = "ft", Alpha = 10d, Beta = 4d, IsUncertain = false }, 0.6d),
            })
            {
                Name = "Blended rating",
                SpecifiedHazard = "Flow",
                HazardUnit = "cfs",
                TransformedHazard = "Stage",
                TransformedHazardUnit = "ft",
            },
            f =>
            {
                var composite = (CompositeTransform)f;
                composite.TransformFunctions[0].Weight = 0.5d;
                composite.TransformFunctions[1].Weight = 0.5d;
            });

        // The epistemic-mixture variants: the bound shared variable is compute-relevant hashed
        // content, so renaming it (hazard), editing a branch weight (response), and binding a
        // variable (transform) are the compute mutations; metadata edits must stay inert.
        yield return new RegistryEntry(
            $"{nameof(CompositeHazard)} (EpistemicMixture)",
            () => new CompositeHazard(new[]
            {
                new WeightedHazardFunction(new TabularHazard { Name = "Model A", SpecifiedHazard = "Flow", HazardUnit = "cfs" }, 0.45d),
                new WeightedHazardFunction(new TabularHazard { Name = "Model B", SpecifiedHazard = "Flow", HazardUnit = "cfs" }, 0.55d),
            })
            {
                Name = "Flood Tree",
                SpecifiedHazard = "Flow",
                HazardUnit = "cfs",
                CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
                EpistemicVariable = "Flood Model",
            },
            f => ((CompositeHazard)f).EpistemicVariable = "Flood Model 2");

        yield return new RegistryEntry(
            $"{nameof(CompositeResponse)} (EpistemicMixture)",
            () => new CompositeResponse(new[]
            {
                new WeightedResponseFunction(new TabularResponse { Name = "Frag A", SpecifiedHazard = "Stage", HazardUnit = "ft" }, 0.45d),
                new WeightedResponseFunction(new TabularResponse { Name = "Frag B", SpecifiedHazard = "Stage", HazardUnit = "ft" }, 0.55d),
            })
            {
                Name = "Fragility Tree",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                CompositeCombinationType = CompositeCombinationType.EpistemicMixture,
                EpistemicVariable = "Breach Model",
            },
            f => ((CompositeResponse)f).ResponseFunctions[0].Weight = 0.5d);

        yield return new RegistryEntry(
            $"{nameof(CompositeTransform)} (EpistemicMixture)",
            () => new CompositeTransform(new[]
            {
                new WeightedTransformFunction(new LinearTransform { Name = "Rating A", SpecifiedHazard = "Flow", HazardUnit = "cfs", TransformedHazard = "Stage", TransformedHazardUnit = "ft", Alpha = 2d, Beta = 3d, IsUncertain = false }, 0.4d),
                new WeightedTransformFunction(new LinearTransform { Name = "Rating B", SpecifiedHazard = "Flow", HazardUnit = "cfs", TransformedHazard = "Stage", TransformedHazardUnit = "ft", Alpha = 10d, Beta = 4d, IsUncertain = false }, 0.6d),
            })
            {
                Name = "Rating Tree",
                SpecifiedHazard = "Flow",
                HazardUnit = "cfs",
                TransformedHazard = "Stage",
                TransformedHazardUnit = "ft",
                CompositeFunctionType = CompositeFunctionType.EpistemicMixture,
            },
            f => ((CompositeTransform)f).EpistemicVariable = "Rating Model");

        // The bivariate transform — a labeled 3×2 surface. The mutation edits one cell;
        // axis/transform-enum/transpose sensitivity is covered in BivariateTransformTests.
        yield return new RegistryEntry(
            nameof(BivariateTransform),
            () => new BivariateTransform
            {
                Name = "Surge-Pool Stage",
                SpecifiedHazard = "Surge",
                HazardUnit = "ft",
                SecondarySpecifiedHazard = "Pool Elevation",
                SecondaryHazardUnit = "ft",
                TransformedHazard = "Stage",
                TransformedHazardUnit = "ft",
                X1Values = new[] { 0d, 10d, 20d },
                X2Values = new[] { 100d, 200d },
                ZValues = new[,] { { 1d, 2d }, { 3d, 5d }, { 4d, 8d } },
            },
            f => ((BivariateTransform)f).ZValues = new[,] { { 1d, 2d }, { 3d, 5d }, { 4d, 9d } });

        // The bivariate consequence — the same surface shape under consequence labels. The
        // mutation edits one cell; the rest is covered in BivariateConsequenceTests.
        yield return new RegistryEntry(
            nameof(BivariateConsequence),
            () => new BivariateConsequence
            {
                Name = "Stage-Pool Life Loss",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                SecondarySpecifiedHazard = "Pool Elevation",
                SecondaryHazardUnit = "ft",
                SpecifiedConsequence = "Life Loss",
                ConsequenceUnit = "lives",
                X1Values = new[] { 0d, 10d, 20d },
                X2Values = new[] { 100d, 200d },
                ZValues = new[,] { { 1d, 2d }, { 3d, 5d }, { 4d, 8d } },
            },
            f => ((BivariateConsequence)f).ZValues = new[,] { { 1d, 2d }, { 3d, 5d }, { 4d, 9d } });

        // The bivariate response — a labeled 3×2 probability surface over the default weighted
        // secondary levels. The mutation edits one cell; weight/level/transform sensitivity and
        // the provenance strip rows are covered in BivariateResponseTests.
        yield return new RegistryEntry(
            nameof(BivariateResponse),
            () =>
            {
                var response = new BivariateResponse
                {
                    Name = "PGA-Pool Fragility",
                    SpecifiedHazard = "Peak Ground Acceleration",
                    HazardUnit = "g",
                    SecondarySpecifiedHazard = "Pool Elevation",
                    SecondaryHazardUnit = "ft",
                };
                response.PrimaryHazardLevels.Add(2d);
                response.ProbabilityValues = new[,] { { 0d, 0.1d }, { 0.2d, 0.5d }, { 0.4d, 0.8d } };
                return response;
            },
            f => ((BivariateResponse)f).ProbabilityValues = new[,] { { 0d, 0.1d }, { 0.2d, 0.5d }, { 0.4d, 0.9d } });

        // The deteriorating response — a tabular base behind an age→shift law. The first entry's
        // mutation edits the law; the second proves base CONTENT edits move the wrapper hash
        // through its projected identity. EvaluationAge inertness and the provenance rows are
        // covered in DeterioratingResponseTests.
        yield return new RegistryEntry(
            nameof(DeterioratingResponse),
            () => new DeterioratingResponse
            {
                Name = "Aging Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                BaseResponse = new TabularResponse { Name = "Fragility", SpecifiedHazard = "Stage", HazardUnit = "ft" },
                DeteriorationLaw = new UncertainOrderedPairedData(
                    new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(50d, new Deterministic(0.5d)) },
                    true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
            },
            f => ((DeterioratingResponse)f).DeteriorationLaw = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(50d, new Deterministic(0.8d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic));

        yield return new RegistryEntry(
            nameof(DeterioratingResponse) + " base edit",
            () => new DeterioratingResponse
            {
                Name = "Aging Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                BaseResponse = new TabularResponse { Name = "Fragility", SpecifiedHazard = "Stage", HazardUnit = "ft" },
            },
            f => ((TabularResponse)((DeterioratingResponse)f).BaseResponse!).ProbabilityTransform = Transform.NormalZ);
    }

    /// <summary>Verifies metadata edits (rename/re-describe/relabel) never move any registered type's hash.</summary>
    [TestMethod]
    public void Test_KitchenSink_MetadataEdits_HashInvariant()
    {
        foreach (var entry in RegisteredTypes())
        {
            // Arrange
            var instance = entry.Factory();

            // Act / Assert
            try
            {
                HashInvariance.AssertMetadataInvariant(instance);
            }
            catch (Exception ex)
            {
                Assert.Fail($"{entry.TypeName}: {ex.Message}");
            }
        }
    }

    /// <summary>Verifies every audited stripped attribute is inert on every registered type's serialized form.</summary>
    [TestMethod]
    public void Test_KitchenSink_StrippedAttributes_Inert()
    {
        foreach (var entry in RegisteredTypes())
        {
            // Arrange
            var instance = entry.Factory();

            // Act / Assert
            try
            {
                HashInvariance.AssertStrippedAttributesInert(instance.ToXElement());
            }
            catch (Exception ex)
            {
                Assert.Fail($"{entry.TypeName}: {ex.Message}");
            }
        }
    }

    /// <summary>Verifies serialization (and therefore hashing) is deterministic per instance.</summary>
    [TestMethod]
    public void Test_KitchenSink_Hash_Deterministic()
    {
        foreach (var entry in RegisteredTypes())
        {
            // Arrange
            var instance = entry.Factory();

            // Act / Assert — repeated hashing of the same state is bit-identical, and two
            // independently built representative instances hash alike.
            CollectionAssert.AreEqual(instance.CanonicalHash(), instance.CanonicalHash(), $"{entry.TypeName}: hash not stable.");
            CollectionAssert.AreEqual(instance.CanonicalHash(), entry.Factory().CanonicalHash(),
                $"{entry.TypeName}: two identically configured instances must hash alike.");
        }
    }

    /// <summary>Verifies a compute-relevant edit moves every registered type's hash.</summary>
    [TestMethod]
    public void Test_KitchenSink_ComputeEdit_MovesHash()
    {
        foreach (var entry in RegisteredTypes())
        {
            // Arrange
            var instance = entry.Factory();

            // Act / Assert
            try
            {
                HashInvariance.AssertComputeSensitive(instance.CanonicalHash, () => entry.ComputeMutation(instance));
            }
            catch (Exception ex)
            {
                Assert.Fail($"{entry.TypeName}: {ex.Message}");
            }
        }
    }
}
