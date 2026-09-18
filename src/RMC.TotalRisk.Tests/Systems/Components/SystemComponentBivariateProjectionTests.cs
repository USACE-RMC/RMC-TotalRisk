using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Tests.Systems.Components;

/// <summary>
/// Unit tests for the bivariate projection stamping on <see cref="SystemComponent"/> — the root
/// exit-port hazard binding, the secondary-chain collection into
/// <see cref="FailureMode.SecondaryHazardToResponse"/>, the generalized consequence-binding
/// positions, the profile-selector primary-chain guard, the sensitivity marginal columns, the
/// appended secondary-chain sampler ordinals, and the pre-bivariate regression pins (existing
/// models project, serialize, and seed bit-identically).
/// </summary>
[TestClass]
public class SystemComponentBivariateProjectionTests
{
    #region Fixtures

    /// <summary>Builds a valid labeled univariate hazard.</summary>
    private static TabularHazard UnivariateHazard()
    {
        return new TabularHazard { Name = "Surge Frequency", SpecifiedHazard = "Surge", HazardUnit = "ft" };
    }

    /// <summary>Builds a valid labeled bivariate hazard; optionally with uncertain marginals.</summary>
    private static BivariateHazard JointHazard(bool uncertainMarginals = false)
    {
        static TabularHazard Marginal(string name, string hazard, bool uncertain)
        {
            var marginal = new TabularHazard { Name = name, SpecifiedHazard = hazard, HazardUnit = "ft" };
            if (uncertain)
            {
                marginal.UncertaintyValue = FunctionUncertainty.Hazard;
                marginal.HazardUncertainFunction = new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0.999d, new Normal(1d, 0.1d)),
                        new UncertainOrdinate(0.001d, new Normal(10d, 0.5d)),
                    },
                    true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Normal);
            }
            return marginal;
        }

        return new BivariateHazard(
            Marginal("Surge Marginal", "Surge", uncertainMarginals),
            Marginal("Pool Marginal", "Pool Elevation", uncertainMarginals))
        {
            Name = "Joint Hazard",
            SpecifiedHazard = "Surge",
            HazardUnit = "ft",
            SecondarySpecifiedHazard = "Pool Elevation",
            SecondaryHazardUnit = "ft",
        };
    }

    /// <summary>Builds a valid labeled univariate tabular response.</summary>
    private static TabularResponse Fragility(string name = "Fragility", string hazard = "Surge")
    {
        return new TabularResponse { Name = name, SpecifiedHazard = hazard, HazardUnit = "ft" };
    }

    /// <summary>Builds a valid labeled univariate tabular consequence.</summary>
    private static TabularConsequence Damages(string name = "Damages")
    {
        return new TabularConsequence
        {
            Name = name,
            SpecifiedHazard = "Surge",
            HazardUnit = "ft",
            SpecifiedConsequence = "Damages",
            ConsequenceUnit = "$",
        };
    }

    /// <summary>Builds a deterministic univariate transform for the secondary chain.</summary>
    private static TabularTransform PoolTransform(string name)
    {
        return new TabularTransform
        {
            Name = name,
            SpecifiedHazard = "Pool Elevation",
            HazardUnit = "ft",
            TransformedHazard = "Pool Stage",
            TransformedHazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(300d, new Deterministic(150d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds a valid labeled bivariate transform.</summary>
    private static BivariateTransform JointTransform()
    {
        return new BivariateTransform
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
        };
    }

    /// <summary>Adds a univariate consequence terminal wired to an upstream element output.</summary>
    private static ConsequenceElement AddTerminal(SystemComponent component, string name,
        IRiskElement upstream, int port = 0)
    {
        var terminal = new ConsequenceElement(name) { Input = new RiskConnection(upstream, port) };
        terminal.Functions.Add(Damages($"{name} curve"));
        component.Graph.AddElement(terminal);
        return terminal;
    }

    /// <summary>
    /// Builds the cascade component: bivariate root, secondary-chain transform Ty off port 1,
    /// a bivariate transform consuming both dimensions, a univariate response, one failure
    /// terminal, and the non-failure path off the transform.
    /// </summary>
    private static (SystemComponent Component, HazardElement Hazard, TransformElement Ty,
        TransformElement Bivariate, ResponseElement Response, ConsequenceElement Failure) CascadeComponent()
    {
        var component = new SystemComponent(JointHazard()) { Name = "Joint Component" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var ty = new TransformElement("Pool Chain")
        {
            Function = PoolTransform("Pool Rating"),
            Input = new RiskConnection(hazard, 1),
        };
        var bivariate = new TransformElement("Stage Surface")
        {
            Function = JointTransform(),
            Input = new RiskConnection(hazard),
            SecondaryInput = new RiskConnection(ty),
        };
        var response = new ResponseElement("Breach")
        {
            Function = Fragility("Stage Fragility", "Stage"),
            Input = new RiskConnection(bivariate),
        };
        component.Graph.AddElement(ty);
        component.Graph.AddElement(bivariate);
        component.Graph.AddElement(response);
        var failure = AddTerminal(component, "Failure Damages", response);
        AddTerminal(component, "Baseline Damages", bivariate);
        return (component, hazard, ty, bivariate, response, failure);
    }

    #endregion

    #region Projection stamping

    /// <summary>
    /// Verifies the hazard binding stamps from the root exit port: a path leaving the secondary
    /// output projects Secondary-bound, the primary path stays Primary.
    /// </summary>
    [TestMethod]
    public void Test_Projection_RootExitPort_StampsHazardBinding()
    {
        // Arrange — a primary failure path and a Secondary-bound failure path.
        var component = new SystemComponent(JointHazard());
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var primaryResponse = new ResponseElement("Primary Breach")
        {
            Function = Fragility("Primary Fragility"),
            Input = new RiskConnection(hazard),
        };
        var secondaryResponse = new ResponseElement("Pool Breach")
        {
            Function = Fragility("Pool Fragility", "Pool Elevation"),
            Input = new RiskConnection(hazard, 1),
        };
        component.Graph.AddElement(primaryResponse);
        component.Graph.AddElement(secondaryResponse);
        AddTerminal(component, "Primary Damages", primaryResponse);
        AddTerminal(component, "Pool Damages", secondaryResponse);
        AddTerminal(component, "Baseline Damages", hazard);

        // Act
        var modes = component.FailureModes;

        // Assert — terminal declared order: primary, pool, baseline.
        Assert.AreEqual(3, modes.Count);
        Assert.AreEqual(HazardDimension.Primary, modes[0].HazardBinding);
        Assert.AreEqual(HazardDimension.Secondary, modes[1].HazardBinding);
        Assert.AreEqual(HazardDimension.Primary, modes[2].HazardBinding);
    }

    /// <summary>
    /// Verifies the secondary chain projects from the first bivariate element's secondary input,
    /// upstream → downstream, carrying the chain elements' functions.
    /// </summary>
    [TestMethod]
    public void Test_Projection_SecondaryChain_CollectedInOrder()
    {
        // Arrange — a two-transform chain feeding the bivariate transform.
        var fixture = CascadeComponent();
        var ty2 = new TransformElement("Pool Chain 2")
        {
            Function = PoolTransform("Pool Rating 2"),
            Input = new RiskConnection(fixture.Ty),
        };
        fixture.Component.Graph.AddElement(ty2);
        fixture.Bivariate.SecondaryInput = new RiskConnection(ty2);

        // Act
        var modes = fixture.Component.FailureModes;

        // Assert — both modes on the bivariate path carry the same chain, upstream first.
        foreach (var mode in modes)
        {
            Assert.AreEqual(2, mode.SecondaryHazardToResponse.Count);
            Assert.AreSame(fixture.Ty.Function, mode.SecondaryHazardToResponse[0]);
            Assert.AreSame(ty2.Function, mode.SecondaryHazardToResponse[1]);
        }
    }

    /// <summary>
    /// Verifies the generalized consequence-binding stamping: the hazard's port 1 is the raw
    /// secondary signal at position 0, and the k-th secondary-chain transform stamps Secondary
    /// position k + 1.
    /// </summary>
    [TestMethod]
    public void Test_Projection_Binding_SecondaryPositions()
    {
        // Arrange
        var fixture = CascadeComponent();
        fixture.Failure.HazardSource = new RiskConnection(fixture.Hazard, 1);

        // Act / Assert — the raw secondary signal: position 0.
        var rawMode = fixture.Component.FailureModes[0];
        Assert.AreEqual(HazardDimension.Secondary, rawMode.ConsequenceHazardDimension);
        Assert.AreEqual(0, rawMode.ConsequenceHazardPosition);

        // The chain transform: Secondary position k + 1 (Ty is chain index 0).
        fixture.Failure.HazardSource = new RiskConnection(fixture.Ty);
        var chainMode = fixture.Component.FailureModes[0];
        Assert.AreEqual(HazardDimension.Secondary, chainMode.ConsequenceHazardDimension);
        Assert.AreEqual(1, chainMode.ConsequenceHazardPosition);

        // A primary-path target keeps the established Primary semantics.
        fixture.Failure.HazardSource = new RiskConnection(fixture.Bivariate);
        var primaryMode = fixture.Component.FailureModes[0];
        Assert.AreEqual(HazardDimension.Primary, primaryMode.ConsequenceHazardDimension);
        Assert.AreEqual(1, primaryMode.ConsequenceHazardPosition);
    }

    /// <summary>
    /// Verifies an unresolvable secondary chain projects empty without throwing — graph
    /// validation reports the defect; the projection stays total.
    /// </summary>
    [TestMethod]
    public void Test_Projection_UnresolvableChain_ProjectsEmpty()
    {
        // Arrange — re-anchor the bivariate transform's secondary input onto the hazard's
        // PRIMARY output (an axes-crossing defect the walk refuses).
        var fixture = CascadeComponent();
        fixture.Bivariate.SecondaryInput = new RiskConnection(fixture.Hazard);

        // Act
        var modes = fixture.Component.FailureModes;

        // Assert
        Assert.IsTrue(modes.Count > 0);
        Assert.IsTrue(modes.All(mode => mode.SecondaryHazardToResponse.Count == 0));
        Assert.IsFalse(fixture.Component.Validate().IsValid);
    }

    /// <summary>
    /// Verifies the pre-bivariate regression pin: a univariate component's projected modes
    /// serialize without the secondary-chain child, Primary-bound, byte-identical to a mode
    /// whose empty chain was assigned explicitly — the serialized form, hash, and therefore
    /// seeds of every existing model are untouched.
    /// </summary>
    [TestMethod]
    public void Test_Projection_UnivariateModel_SerializedFormUnchanged()
    {
        // Arrange — a univariate component with a stage transform, response, and both paths.
        var component = new SystemComponent(UnivariateHazard());
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var transform = new TransformElement("Rating")
        {
            Function = PoolTransform("Rating"),
            Input = new RiskConnection(hazard),
        };
        var response = new ResponseElement("Breach")
        {
            Function = Fragility(),
            Input = new RiskConnection(transform),
        };
        component.Graph.AddElement(transform);
        component.Graph.AddElement(response);
        AddTerminal(component, "Failure Damages", response);
        AddTerminal(component, "Baseline Damages", hazard);

        // Act
        var modes = component.FailureModes;

        // Assert — no secondary-chain child, Primary bindings, and explicit-empty assignment is
        // byte-inert (the conditional-presence contract).
        foreach (var mode in modes)
        {
            var xml = mode.ToXElement();
            Assert.IsNull(xml.Element(nameof(FailureMode.SecondaryHazardToResponse)));
            Assert.AreEqual(HazardDimension.Primary, mode.HazardBinding);

            string before = xml.ToString();
            byte[] hashBefore = mode.CanonicalHash();
            mode.SecondaryHazardToResponse = new List<RMC.TotalRisk.Core.Interfaces.ITransformFunction>();
            Assert.AreEqual(before, mode.ToXElement().ToString());
            CollectionAssert.AreEqual(hashBefore, mode.CanonicalHash());
        }
    }

    #endregion

    #region Sampler walk

    /// <summary>
    /// Verifies the pre-bivariate sampler-walk regression pin: a univariate component's captured
    /// effective seeds equal the independently constructed content-derived seeds at the
    /// established ordinals — the walk shape without secondary chains is bit-identical.
    /// </summary>
    [TestMethod]
    public void Test_SetupSamplers_UnivariateOrdinals_Unchanged()
    {
        // Arrange
        var component = new SystemComponent(UnivariateHazard());
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var transform = new TransformElement("Rating")
        {
            Function = PoolTransform("Rating"),
            Input = new RiskConnection(hazard),
        };
        var response = new ResponseElement("Breach")
        {
            Function = Fragility(),
            Input = new RiskConnection(transform),
        };
        component.Graph.AddElement(transform);
        component.Graph.AddElement(response);
        AddTerminal(component, "Failure Damages", response);
        AddTerminal(component, "Baseline Damages", hazard);
        var modes = component.FailureModes;
        const int componentSeed = 246810;

        // Act
        int[] captured = component.SetupSamplers(16, componentSeed, SamplingScheme.LatinHypercube,
            new SeedScribe(null));

        // Assert — the established walk: hazard(0); failure mode coupling(1), stage
        // transform(2), response(3); non-failure mode coupling(4), sentinel(5).
        Assert.AreEqual(6, captured.Length);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, component.HazardFunction!.CanonicalHash(), 0), captured[0]);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, modes[0].CanonicalHash(), 1), captured[1]);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, transform.Function!.CanonicalHash(), 2), captured[2]);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, response.Function!.CanonicalHash(), 3), captured[3]);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, modes[1].CanonicalHash(), 4), captured[4]);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, modes[1].ResponseStages[0].Response.CanonicalHash(), 5), captured[5]);
    }

    /// <summary>
    /// Verifies the secondary chain seeds at appended walk positions — after the trailing
    /// transforms — so every pre-chain position keeps its established ordinal, and a shared
    /// chain instance seeds once (later encounters advance the ordinal without re-seeding).
    /// </summary>
    [TestMethod]
    public void Test_SetupSamplers_SecondaryChain_AppendedAfterTrailing()
    {
        // Arrange
        var fixture = CascadeComponent();
        var modes = fixture.Component.FailureModes;
        const int componentSeed = 987654;

        // Act
        int[] captured = fixture.Component.SetupSamplers(16, componentSeed, SamplingScheme.LatinHypercube,
            new SeedScribe(null));

        // Assert — mode 0 (failure): coupling(1), bivariate stage transform(2), response(3),
        // secondary chain appended at 4; mode 1 (baseline): coupling(5), the shared bivariate
        // transform re-encountered at 6 (no re-seed), sentinel(7), the shared chain function
        // re-encountered at 8 (no re-seed).
        Assert.AreEqual(9, captured.Length);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, fixture.Component.HazardFunction!.CanonicalHash(), 0), captured[0]);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, modes[0].CanonicalHash(), 1), captured[1]);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, fixture.Bivariate.Function!.CanonicalHash(), 2), captured[2]);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, fixture.Response.Function!.CanonicalHash(), 3), captured[3]);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, fixture.Ty.Function!.CanonicalHash(), 4), captured[4]);
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, modes[1].CanonicalHash(), 5), captured[5]);
        Assert.AreEqual(0, captured[6], "The shared bivariate transform re-encounter must advance without re-seeding.");
        Assert.AreEqual(SeedHelpers.HashCombine(componentSeed, modes[1].ResponseStages[0].Response.CanonicalHash(), 7), captured[7]);
        Assert.AreEqual(0, captured[8], "The shared chain-function re-encounter must advance without re-seeding.");
    }

    #endregion

    #region Profile guard

    /// <summary>
    /// Verifies the profile selector rejects a secondary-branch transform (its first hazard
    /// edge uses port 1) and accepts a primary-chain transform.
    /// </summary>
    [TestMethod]
    public void Test_Profile_SecondaryBranchTransform_Error()
    {
        // Arrange
        var fixture = CascadeComponent();

        // Act / Assert — the secondary-chain transform reaches the root but rides port 1.
        fixture.Component.SetProfileHazardElement(fixture.Ty);
        var messages = fixture.Component.Validate().ValidationMessages;
        Assert.IsTrue(messages.Any(m => m.Contains("consumes the hazard's secondary output (port 1)")));

        // A primary-chain transform is a legal profile axis.
        fixture.Component.SetProfileHazardElement(fixture.Bivariate);
        Assert.IsFalse(fixture.Component.Validate().ValidationMessages
            .Any(m => m.Contains("consumes the hazard's secondary output (port 1)")));
    }

    #endregion

    #region Sensitivity and referenced functions

    /// <summary>
    /// Verifies the sensitivity collection enumerates a bivariate hazard's marginals (the hazard
    /// itself contributes no column — it samples through them) and walks the secondary chain
    /// after the trailing transforms.
    /// </summary>
    [TestMethod]
    public void Test_Sensitivity_MarginalAndChainColumns()
    {
        // Arrange — uncertain marginals and an uncertain secondary-chain transform.
        var component = new SystemComponent(JointHazard(uncertainMarginals: true)) { Name = "Joint Component" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var chainTransform = new TransformElement("Pool Chain")
        {
            Function = new LinearTransform
            {
                Name = "Pool Rating",
                SpecifiedHazard = "Pool Elevation",
                HazardUnit = "ft",
                TransformedHazard = "Pool Stage",
                TransformedHazardUnit = "ft",
                IsUncertain = true,
            },
            Input = new RiskConnection(hazard, 1),
        };
        var bivariate = new TransformElement("Stage Surface")
        {
            Function = JointTransform(),
            Input = new RiskConnection(hazard),
            SecondaryInput = new RiskConnection(chainTransform),
        };
        var response = new ResponseElement("Breach")
        {
            Function = Fragility("Stage Fragility", "Stage"),
            Input = new RiskConnection(bivariate),
        };
        component.Graph.AddElement(chainTransform);
        component.Graph.AddElement(bivariate);
        component.Graph.AddElement(response);
        AddTerminal(component, "Failure Damages", response);
        component.SetupSamplers(16, 13579, SamplingScheme.LatinHypercube);

        // Act
        var sink = new List<SensitivityInput>();
        component.CollectSensitivityInputs(sink);

        // Assert — marginal X then marginal Y lead the walk; the chain transform's column is
        // present; the bivariate hazard itself contributes none.
        Assert.AreEqual("Joint Component - Surge Marginal", sink[0].Label);
        Assert.AreEqual("Joint Component - Pool Marginal", sink[1].Label);
        Assert.IsTrue(sink.Any(input => input.Label == "Joint Component - Pool Rating"));
        Assert.IsFalse(sink.Any(input => input.Label.Contains("Joint Hazard")));
    }

    /// <summary>
    /// Verifies the component-level dependency set includes the linked marginals once.
    /// </summary>
    [TestMethod]
    public void Test_GetReferencedFunctions_IncludesMarginals()
    {
        // Arrange
        var fixture = CascadeComponent();
        var hazard = (BivariateHazard)fixture.Component.HazardFunction!;

        // Act
        var functions = fixture.Component.GetReferencedFunctions().ToList();

        // Assert
        Assert.IsTrue(functions.Contains(hazard.MarginalX!));
        Assert.IsTrue(functions.Contains(hazard.MarginalY!));
        Assert.AreEqual(functions.Count, functions.Distinct().Count());
    }

    #endregion

    #region Chain-authoring guard

    /// <summary>
    /// Verifies the chain-style expansion refuses bivariate topology loudly — a Secondary
    /// binding, a Secondary consequence dimension, or a secondary-hazard chain cannot be wired
    /// by a linear expansion, and dropping them silently would break the projection contract.
    /// </summary>
    [TestMethod]
    public void Test_AddFailureMode_BivariateShapes_Throw()
    {
        // Arrange
        var component = new SystemComponent(JointHazard());

        var secondaryBound = new FailureMode { HazardBinding = HazardDimension.Secondary };
        var secondaryDimension = new FailureMode
        {
            ConsequenceHazardDimension = HazardDimension.Secondary,
            ConsequenceHazardPosition = 0,
        };
        var chained = new FailureMode();
        chained.SecondaryHazardToResponse.Add(PoolTransform("Pool Rating"));

        // Act / Assert
        Assert.ThrowsException<NotSupportedException>(() => component.AddFailureMode(secondaryBound));
        Assert.ThrowsException<NotSupportedException>(() => component.AddFailureMode(secondaryDimension));
        Assert.ThrowsException<NotSupportedException>(() => component.AddFailureMode(chained));
    }

    #endregion

    #region Engine guardrails

    /// <summary>
    /// Builds a two-response bivariate component: a primary-bound surge path and, optionally, a
    /// second path bound to the hazard's secondary output — the competing-guard matrix fixture.
    /// </summary>
    private static SystemComponent CompetingFixture(bool secondPathSecondary, bool twoPaths = true)
    {
        var component = new SystemComponent(JointHazard()) { Name = "Competing Component" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var primaryBreach = new ResponseElement("Surge Breach")
        {
            Function = Fragility("Surge Fragility"),
            Input = new RiskConnection(hazard),
        };
        component.Graph.AddElement(primaryBreach);
        AddTerminal(component, "Surge Damages", primaryBreach);
        if (twoPaths)
        {
            var secondBreach = new ResponseElement("Second Breach")
            {
                Function = Fragility("Second Fragility", secondPathSecondary ? "Pool Elevation" : "Surge"),
                Input = new RiskConnection(hazard, secondPathSecondary ? 1 : 0),
            };
            component.Graph.AddElement(secondBreach);
            AddTerminal(component, "Second Damages", secondBreach);
        }
        AddTerminal(component, "Baseline Damages", hazard);
        component.FailureModeMethod = FailureModeMethod.CompetingFailures;
        return component;
    }

    /// <summary>
    /// Verifies the multi-unit competing guard: a Secondary-bound failure mode over a bivariate
    /// hazard is a validation error, while an all-primary configuration stays legal.
    /// </summary>
    [TestMethod]
    public void Test_CompetingGuard_MultiUnitSecondaryBound_Errors()
    {
        // Act
        var mixed = CompetingFixture(secondPathSecondary: true).Validate();
        var allPrimary = CompetingFixture(secondPathSecondary: false).Validate();

        // Assert
        Assert.IsTrue(mixed.ValidationMessages.Any(m =>
            m.StartsWith("Error:", StringComparison.Ordinal) && m.Contains("competing failure modes over a bivariate hazard")));
        Assert.IsFalse(allPrimary.ValidationMessages.Any(m => m.Contains("competing failure modes over a bivariate hazard")));
    }

    /// <summary>
    /// Verifies single-unit competing stays legal for any binding: one Secondary-bound failure
    /// mode needs no incidence pre-processing, so the guard does not fire.
    /// </summary>
    [TestMethod]
    public void Test_CompetingGuard_SingleUnitSecondaryBound_Legal()
    {
        // Arrange — one Secondary-bound failure path only.
        var component = new SystemComponent(JointHazard()) { Name = "Single Unit" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var breach = new ResponseElement("Pool Breach")
        {
            Function = Fragility("Pool Fragility", "Pool Elevation"),
            Input = new RiskConnection(hazard, 1),
        };
        component.Graph.AddElement(breach);
        AddTerminal(component, "Pool Damages", breach);
        AddTerminal(component, "Baseline Damages", hazard);
        component.FailureModeMethod = FailureModeMethod.CompetingFailures;

        // Act
        var result = component.Validate();

        // Assert
        Assert.IsFalse(result.ValidationMessages.Any(m => m.Contains("competing failure modes over a bivariate hazard")));
    }

    /// <summary>
    /// Verifies the recorded-entry estimate prices the conditional-bin cross product: a
    /// bivariate hazard multiplies the univariate-equivalent bound by (bins + 1).
    /// </summary>
    [TestMethod]
    public void Test_EstimateRecordedFailureEntries_ScalesByNodeCount()
    {
        // Arrange — the same single-mode shape under a univariate and a bivariate hazard.
        var univariate = new SystemComponent(UnivariateHazard()) { Name = "Univariate" };
        var uHazard = univariate.Graph.GetElements<HazardElement>().Single();
        var uBreach = new ResponseElement("Breach") { Function = Fragility(), Input = new RiskConnection(uHazard) };
        univariate.Graph.AddElement(uBreach);
        AddTerminal(univariate, "Damages", uBreach);
        AddTerminal(univariate, "Baseline", uHazard);

        var joint = JointHazard();
        joint.SecondaryIntegrationBins = 10;
        var bivariate = new SystemComponent(joint) { Name = "Bivariate" };
        var bHazard = bivariate.Graph.GetElements<HazardElement>().Single();
        var bBreach = new ResponseElement("Breach")
        {
            Function = Fragility("Pool Fragility", "Pool Elevation"),
            Input = new RiskConnection(bHazard, 1),
        };
        bivariate.Graph.AddElement(bBreach);
        AddTerminal(bivariate, "Damages", bBreach);
        AddTerminal(bivariate, "Baseline", bHazard);

        // Act / Assert — the bivariate estimate is the univariate bound times the adaptive
        // refinement budget's hard mesh bound (21·⌈(bins + 1)/2⌉ = 126 at ten bins; a
        // deliberately worst-case price — a converged sweep adopts far fewer nodes).
        long univariateEstimate = univariate.EstimateRecordedFailureEntries();
        Assert.AreEqual(126, SampledComponent.ConditionalMeshCapacity(10));
        Assert.AreEqual(univariateEstimate * 126L, bivariate.EstimateRecordedFailureEntries());
    }

    #endregion
}
