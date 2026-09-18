using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for <see cref="TolerableRiskCriterion"/> — the guideline default construction,
/// the validation matrix, the serialization round trip with name-based enums, and the
/// forward-load defaults.
/// </summary>
[TestClass]
public class TolerableRiskCriterionTests
{
    /// <summary>
    /// Verifies the default construction is the annualized incremental-risk guideline shape,
    /// and the full construction stores every field.
    /// </summary>
    [TestMethod]
    public void Test_Construction_GuidelineDefaultAndFullForm()
    {
        // Act
        var guideline = new TolerableRiskCriterion();
        var custom = new TolerableRiskCriterion(RiskMeasure.ValueAtRisk, RiskType.Total, 2, 750d);

        // Assert
        Assert.AreEqual(RiskMeasure.Mean, guideline.Measure);
        Assert.AreEqual(RiskType.Excess, guideline.RiskType);
        Assert.AreEqual(0, guideline.ConsequenceTypeIndex);
        Assert.AreEqual(1e-3, guideline.Threshold, 0d);

        Assert.AreEqual(RiskMeasure.ValueAtRisk, custom.Measure);
        Assert.AreEqual(RiskType.Total, custom.RiskType);
        Assert.AreEqual(2, custom.ConsequenceTypeIndex);
        Assert.AreEqual(750d, custom.Threshold, 0d);
    }

    /// <summary>
    /// Verifies the validation matrix: the default is valid; unrecognized enum members, a
    /// negative type position, and a non-finite threshold are Errors.
    /// </summary>
    [TestMethod]
    public void Test_Validate_Matrix()
    {
        // The guideline default is valid.
        var (isValid, messages) = new TolerableRiskCriterion().Validate();
        Assert.IsTrue(isValid, string.Join(Environment.NewLine, messages));
        Assert.AreEqual(0, messages.Count);

        // Each invalid shape is its own Error.
        Assert.IsFalse(new TolerableRiskCriterion((RiskMeasure)99, RiskType.Excess, 0, 1e-3).Validate().IsValid);
        Assert.IsFalse(new TolerableRiskCriterion(RiskMeasure.Mean, (RiskType)99, 0, 1e-3).Validate().IsValid);
        Assert.IsFalse(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, -1, 1e-3).Validate().IsValid);
        Assert.IsFalse(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, double.NaN).Validate().IsValid);
        Assert.IsFalse(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, double.PositiveInfinity).Validate().IsValid);

        // A zero or negative finite threshold is legal — P(x > c) is well defined.
        Assert.IsTrue(new TolerableRiskCriterion(RiskMeasure.Mean, RiskType.Excess, 0, 0d).Validate().IsValid);
        Assert.IsTrue(new TolerableRiskCriterion(RiskMeasure.Skewness, RiskType.Total, 0, -2d).Validate().IsValid);
    }

    /// <summary>
    /// Verifies the serialization round trip: name-based enum attributes, G17 threshold, and
    /// forward-loading of an attribute-free element as the guideline default.
    /// </summary>
    [TestMethod]
    public void Test_Serialization_RoundTripAndForwardLoad()
    {
        // Arrange
        var criterion = new TolerableRiskCriterion(RiskMeasure.ConditionalValueAtRisk, RiskType.Fail, 1, 12.75d);

        // Act
        var element = criterion.ToXElement();
        var restored = new TolerableRiskCriterion(element);

        // Assert — name-based enums and bit-exact threshold.
        Assert.AreEqual(nameof(TolerableRiskCriterion), element.Name.LocalName);
        Assert.AreEqual("ConditionalValueAtRisk", element.Attribute(nameof(TolerableRiskCriterion.Measure))!.Value);
        Assert.AreEqual("Fail", element.Attribute(nameof(TolerableRiskCriterion.RiskType))!.Value);
        Assert.AreEqual(criterion.Measure, restored.Measure);
        Assert.AreEqual(criterion.RiskType, restored.RiskType);
        Assert.AreEqual(criterion.ConsequenceTypeIndex, restored.ConsequenceTypeIndex);
        Assert.AreEqual(BitConverter.DoubleToInt64Bits(criterion.Threshold), BitConverter.DoubleToInt64Bits(restored.Threshold));

        // A bare element loads forward as the guideline default; null throws.
        var defaulted = new TolerableRiskCriterion(new System.Xml.Linq.XElement(nameof(TolerableRiskCriterion)));
        Assert.AreEqual(RiskMeasure.Mean, defaulted.Measure);
        Assert.AreEqual(RiskType.Excess, defaulted.RiskType);
        Assert.AreEqual(0, defaulted.ConsequenceTypeIndex);
        Assert.AreEqual(1e-3, defaulted.Threshold, 0d);
        Assert.ThrowsException<ArgumentNullException>(() => new TolerableRiskCriterion(null!));
    }
}
