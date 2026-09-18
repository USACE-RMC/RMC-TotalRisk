using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.CostBenefit;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.CostBenefit;

/// <summary>
/// Tests the utility declaration: echoes, the validation matrix, and the XML round trip.
/// </summary>
[TestClass]
public class UtilityDeclarationTests
{
    /// <summary>Verifies the properties echo and the validation matrix.</summary>
    [TestMethod]
    public void Test_Ctor_EchoAndValidateMatrix()
    {
        // Act
        var declaration = new UtilityDeclaration(UtilityFunctionForm.ExponentialCara, 0.002d);
        var invalid = new UtilityDeclaration(UtilityFunctionForm.PowerCrra, -1d);

        // Assert
        Assert.AreEqual(UtilityFunctionForm.ExponentialCara, declaration.Form);
        Assert.AreEqual(0.002d, declaration.RiskAversion);
        Assert.IsTrue(declaration.Validate().IsValid);
        (bool isValid, var messages) = invalid.Validate();
        Assert.IsFalse(isValid);
        StringAssert.StartsWith(messages[0], "Error: The risk-aversion parameter must be finite and positive.");
    }

    /// <summary>Verifies the XML round trip is bit-exact.</summary>
    [TestMethod]
    public void Test_Serialization_RoundTrip()
    {
        // Arrange
        var declaration = new UtilityDeclaration(UtilityFunctionForm.PowerCrra, 1.2345678901234567d);

        // Act
        var restored = new UtilityDeclaration(declaration.ToXElement());

        // Assert
        Assert.AreEqual(declaration.Form, restored.Form);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(declaration.RiskAversion),
            BitConverter.DoubleToInt64Bits(restored.RiskAversion));
        Assert.ThrowsException<ArgumentNullException>(() => new UtilityDeclaration(null!));
    }
}
