using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>Unit tests for <see cref="SensitivityEntry"/> immutable values.</summary>
/// <remarks>
/// <para><b>Authors:</b> Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil</para>
/// </remarks>
[TestClass]
public class SensitivityEntryTests
{
    /// <summary>Verifies construction and null-label coercion.</summary>
    [TestMethod]
    public void Test_Constructor_PreservesValue()
    {
        var entry = new SensitivityEntry(null, -0.75d);
        Assert.AreEqual(string.Empty, entry.Label);
        Assert.AreEqual(-0.75d, entry.Value);
    }
}
