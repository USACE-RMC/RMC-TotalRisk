using System;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.LifeCycle;
using RMC.TotalRisk.RiskFunctions.Hazards;

namespace RMC.TotalRisk.Tests.Analyses.LifeCycle;

/// <summary>
/// Tests the hazard-replacement input record: constructor guards and the referenced-not-owned
/// property contract.
/// </summary>
[TestClass]
public class HazardReplacementTests
{
    /// <summary>Verifies an empty target id is refused.</summary>
    [TestMethod]
    public void Test_Ctor_EmptyTargetId_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentException>(
            () => new HazardReplacement(Guid.Empty, new TabularHazard()));
    }

    /// <summary>Verifies a null replacement is refused.</summary>
    [TestMethod]
    public void Test_Ctor_NullReplacement_Throws()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(
            () => new HazardReplacement(Guid.NewGuid(), null!));
    }

    /// <summary>Verifies the properties echo the arguments and hold the live reference.</summary>
    [TestMethod]
    public void Test_Ctor_ValidArgs_PropertiesHeld()
    {
        // Arrange
        var target = Guid.NewGuid();
        var replacement = new TabularHazard { Name = "Future stage frequency" };

        // Act
        var action = new HazardReplacement(target, replacement);

        // Assert — referenced, not owned.
        Assert.AreEqual(target, action.TargetFunctionId);
        Assert.IsTrue(ReferenceEquals(replacement, action.Replacement));
    }

    /// <summary>
    /// Verifies the XML round trip carries the self-contained replacement payload, and an
    /// unknown child refuses loudly.
    /// </summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var replacement = new TabularHazard { Name = "Future stage frequency" };
        var action = new HazardReplacement(replacement.Id, replacement);

        // Act
        var restored = new HazardReplacement(action.ToXElement());

        // Assert — id, payload identity, and payload content all survive.
        Assert.AreEqual(action.TargetFunctionId, restored.TargetFunctionId);
        Assert.AreEqual(replacement.Id, restored.Replacement.Id);
        Assert.AreEqual(Convert.ToHexString(replacement.CanonicalHash()),
            Convert.ToHexString(restored.Replacement.CanonicalHash()));
        Assert.ThrowsException<ArgumentNullException>(() => new HazardReplacement(null!));

        // A child naming no known hazard function refuses loudly.
        var unknown = new XElement(nameof(HazardReplacement));
        unknown.SetAttributeValue(nameof(HazardReplacement.TargetFunctionId), replacement.Id.ToString("D"));
        unknown.Add(new XElement("NotAHazard"));
        Assert.ThrowsException<ArgumentException>(() => new HazardReplacement(unknown));
    }
}
