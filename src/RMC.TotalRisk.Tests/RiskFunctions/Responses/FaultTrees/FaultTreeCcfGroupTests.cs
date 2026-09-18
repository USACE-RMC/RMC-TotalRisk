using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses.FaultTrees;

/// <summary>Tests the common-cause group contract: construction, notification, serialization, the factor kernel, and the configuration matrix.</summary>
[TestClass]
public class FaultTreeCcfGroupTests
{
    /// <summary>Verifies construction, defensive copies, and change notification.</summary>
    [TestMethod]
    public void Test_Construction_StoresCopiesAndNotifies()
    {
        // Arrange
        var parameters = new List<double> { 0.1d };
        var members = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var group = new FaultTreeCcfGroup("Pumps", FaultTreeCcfModel.BetaFactor, parameters, members);
        parameters[0] = 0.9d;
        members[0] = Guid.Empty;
        string? lastProperty = null;
        group.PropertyChanged += (_, e) => lastProperty = e.PropertyName;

        // Act / Assert
        Assert.AreEqual(0.1d, group.Parameters[0]);
        Assert.AreNotEqual(Guid.Empty, group.MemberNodeIds[0]);
        group.Model = FaultTreeCcfModel.AlphaFactor;
        Assert.AreEqual(nameof(FaultTreeCcfGroup.Model), lastProperty);
        group.Parameters = new[] { 0.9d, 0.1d };
        Assert.AreEqual(nameof(FaultTreeCcfGroup.Parameters), lastProperty);
        group.MemberNodeIds = new[] { Guid.NewGuid() };
        Assert.AreEqual(nameof(FaultTreeCcfGroup.MemberNodeIds), lastProperty);
        group.Name = "Renamed";
        Assert.AreEqual(nameof(FaultTreeCcfGroup.Name), lastProperty);
    }

    /// <summary>Verifies the serialization round trip preserves every stored value.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrips()
    {
        // Arrange
        var members = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var group = new FaultTreeCcfGroup("Gates", FaultTreeCcfModel.AlphaFactor,
            new[] { 0.9d, 0.08d, 0.02d }, members)
        {
            Description = "Spillway gates",
        };

        // Act
        var restored = new FaultTreeCcfGroup(group.ToXElement());

        // Assert
        Assert.AreEqual("Gates", restored.Name);
        Assert.AreEqual("Spillway gates", restored.Description);
        Assert.AreEqual(FaultTreeCcfModel.AlphaFactor, restored.Model);
        CollectionAssert.AreEqual(group.Parameters.ToArray(), restored.Parameters.ToArray());
        CollectionAssert.AreEqual(members, restored.MemberNodeIds.ToArray());
    }

    /// <summary>
    /// Verifies the factor kernel's closed forms and the exact per-member identity
    /// Σ C(n−1, k−1)·f_k = 1 across all three models.
    /// </summary>
    [TestMethod]
    public void Test_ComputeFactors_ClosedFormsAndIdentity()
    {
        // Arrange — beta-factor, n = 2: f = {1 − β, β}.
        var beta = new FaultTreeCcfGroup("B", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { Guid.NewGuid(), Guid.NewGuid() });
        // The multiple Greek letter model, n = 3, β = 0.1, γ = 0.5:
        // f_1 = 0.9, f_2 = 0.1·0.5/2 = 0.025, f_3 = 0.1·0.5 = 0.05.
        var mgl = new FaultTreeCcfGroup("M", FaultTreeCcfModel.MultipleGreekLetter, new[] { 0.1d, 0.5d },
            new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() });
        // Alpha-factor, n = 3, α = {0.9, 0.08, 0.02}: α_t = 0.9 + 0.16 + 0.06 = 1.12;
        // f_1 = 0.9/1.12, f_2 = 2·0.08/(2·1.12), f_3 = 3·0.02/1.12.
        var alpha = new FaultTreeCcfGroup("A", FaultTreeCcfModel.AlphaFactor, new[] { 0.9d, 0.08d, 0.02d },
            new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() });

        // Act
        double[] betaFactors = beta.ComputeFactors();
        double[] mglFactors = mgl.ComputeFactors();
        double[] alphaFactors = alpha.ComputeFactors();

        // Assert — closed forms.
        Assert.AreEqual(0.9d, betaFactors[0], 1e-15d);
        Assert.AreEqual(0.1d, betaFactors[1], 1e-15d);
        Assert.AreEqual(0.9d, mglFactors[0], 1e-15d);
        Assert.AreEqual(0.025d, mglFactors[1], 1e-15d);
        Assert.AreEqual(0.05d, mglFactors[2], 1e-15d);
        Assert.AreEqual(0.9d / 1.12d, alphaFactors[0], 1e-15d);
        Assert.AreEqual(0.08d / 1.12d, alphaFactors[1], 1e-15d);
        Assert.AreEqual(0.06d / 1.12d, alphaFactors[2], 1e-15d);

        // Assert — the exact per-member identity for every model.
        Assert.AreEqual(1d, betaFactors[0] + betaFactors[1], 1e-15d);
        Assert.AreEqual(1d, mglFactors[0] + 2d * mglFactors[1] + mglFactors[2], 1e-15d);
        Assert.AreEqual(1d, alphaFactors[0] + 2d * alphaFactors[1] + alphaFactors[2], 1e-15d);
    }

    /// <summary>Verifies the factor kernel refuses invalid configurations loudly.</summary>
    [TestMethod]
    public void Test_ComputeFactors_InvalidConfigurations_Throw()
    {
        // Arrange
        var single = new FaultTreeCcfGroup("S", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { Guid.NewGuid() });
        var badAlpha = new FaultTreeCcfGroup("A", FaultTreeCcfModel.AlphaFactor, new[] { 0.5d, 0.2d },
            new[] { Guid.NewGuid(), Guid.NewGuid() });

        // Act / Assert
        try
        {
            _ = single.ComputeFactors();
            Assert.Fail("A single-member group must throw.");
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            _ = badAlpha.ComputeFactors();
            Assert.Fail("Alpha fractions not summing to one must throw.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Verifies the configuration-error matrix against an owning tree.</summary>
    [TestMethod]
    public void Test_GetConfigurationErrors_Matrix()
    {
        // Arrange
        var tree = new FaultTree();
        var pump = new FaultTreeBasicEventNode("Pump", new ProbabilitySource(0.2d));
        var valve = new FaultTreeBasicEventNode("Valve", new ProbabilitySource(0.2d));
        var different = new FaultTreeBasicEventNode("Different", new ProbabilitySource(0.5d));
        var house = new FaultTreeHouseEventNode("Bypass", false);
        tree.Add(tree.Root.Id, pump);
        tree.Add(tree.Root.Id, valve);
        tree.Add(tree.Root.Id, different);
        tree.Add(tree.Root.Id, house);

        // Act / Assert — a valid exchangeable pair reports nothing.
        var valid = new FaultTreeCcfGroup("Valid", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { pump.Id, valve.Id });
        Assert.AreEqual(0, valid.GetConfigurationErrors(tree).Count);

        var missing = new FaultTreeCcfGroup("Missing", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { pump.Id, Guid.NewGuid() });
        Assert.IsTrue(missing.GetConfigurationErrors(tree).Any(m => m.Contains("was not found")));

        var wrongKind = new FaultTreeCcfGroup("Kind", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { pump.Id, house.Id });
        Assert.IsTrue(wrongKind.GetConfigurationErrors(tree).Any(m => m.Contains("is not a basic event")));

        var duplicate = new FaultTreeCcfGroup("Duplicate", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { pump.Id, pump.Id });
        Assert.IsTrue(duplicate.GetConfigurationErrors(tree).Any(m => m.Contains("more than once")));

        var unequal = new FaultTreeCcfGroup("Unequal", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            new[] { pump.Id, different.Id });
        Assert.IsTrue(unequal.GetConfigurationErrors(tree).Any(m => m.Contains("same probability-source content")));

        var badParameters = new FaultTreeCcfGroup("Params", FaultTreeCcfModel.AlphaFactor, new[] { 0.5d },
            new[] { pump.Id, valve.Id });
        Assert.IsTrue(badParameters.GetConfigurationErrors(tree).Any(m => m.Contains("alpha fraction")));

        var oversized = new FaultTreeCcfGroup("Big", FaultTreeCcfModel.BetaFactor, new[] { 0.1d },
            Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray());
        Assert.IsTrue(oversized.GetConfigurationErrors(tree).Any(m => m.Contains("supported group size")));
    }
}
