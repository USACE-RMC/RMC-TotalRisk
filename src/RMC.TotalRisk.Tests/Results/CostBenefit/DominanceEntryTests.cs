using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.CostBenefit;

namespace RMC.TotalRisk.Tests.Results.CostBenefit;

/// <summary>
/// Tests the dominance-screen entry: guards and the echoes.
/// </summary>
[TestClass]
public class DominanceEntryTests
{
    /// <summary>Verifies the guards and the echoes.</summary>
    [TestMethod]
    public void Test_Ctor_GuardsAndEcho()
    {
        // Act / Assert
        Assert.ThrowsException<ArgumentNullException>(() => new DominanceEntry(
            null!, "B", "Aleatory", "c", DominanceVerdict.None));
        Assert.ThrowsException<ArgumentNullException>(() => new DominanceEntry(
            "A", null!, "Aleatory", "c", DominanceVerdict.None));
        Assert.ThrowsException<ArgumentNullException>(() => new DominanceEntry(
            "A", "B", null!, "c", DominanceVerdict.None));
        Assert.ThrowsException<ArgumentNullException>(() => new DominanceEntry(
            "A", "B", "Aleatory", null!, DominanceVerdict.None));

        var entry = new DominanceEntry("Baseline", "Alt A", "Aleatory",
            "Total type 0 loss-exceedance", DominanceVerdict.SecondDominatesFirstOrder);
        Assert.AreEqual("Baseline", entry.FirstAlternative);
        Assert.AreEqual("Alt A", entry.SecondAlternative);
        Assert.AreEqual("Aleatory", entry.Layer);
        Assert.AreEqual("Total type 0 loss-exceedance", entry.CriterionLabel);
        Assert.AreEqual(DominanceVerdict.SecondDominatesFirstOrder, entry.Verdict);
    }
}
