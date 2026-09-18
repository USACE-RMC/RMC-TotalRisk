using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Tests.Core.Enums;

/// <summary>
/// Unit tests for <see cref="ExtrapolationPolicy"/> — pins the members and their order. The enum
/// is serialized by name (only when non-default) on the tabular and nonparametric function types
/// and is hashed canonical content, so the member set is append-only contract.
/// </summary>
[TestClass]
public class ExtrapolationPolicyTests
{
    /// <summary>Pins the declared members and order.</summary>
    [TestMethod]
    public void Test_Members_Pinned()
    {
        // Assert
        CollectionAssert.AreEqual(
            new[] { "None", "Below", "Above", "Both", "Error" },
            Enum.GetNames<ExtrapolationPolicy>());
        Assert.AreEqual(0, (int)ExtrapolationPolicy.None);
        Assert.AreEqual(1, (int)ExtrapolationPolicy.Below);
        Assert.AreEqual(2, (int)ExtrapolationPolicy.Above);
        Assert.AreEqual(3, (int)ExtrapolationPolicy.Both);
        Assert.AreEqual(4, (int)ExtrapolationPolicy.Error);
    }

    /// <summary>The default is the historical endpoint hold.</summary>
    [TestMethod]
    public void Test_Default_IsNone()
    {
        // Assert
        Assert.AreEqual(ExtrapolationPolicy.None, default(ExtrapolationPolicy));
    }

    /// <summary>
    /// Pins the mapping onto the Numerics lookup surface: the extending members map to their
    /// numerically equal sides, and Error maps to None — the sampled wrapper holds while the
    /// model-side guards throw, so a bypassed guard degrades to the historical hold.
    /// </summary>
    [TestMethod]
    public void Test_Map_PinnedToNumericsSides()
    {
        // Assert
        Assert.AreEqual(ExtrapolationSides.None, ExtrapolationSupport.Map(ExtrapolationPolicy.None));
        Assert.AreEqual(ExtrapolationSides.Below, ExtrapolationSupport.Map(ExtrapolationPolicy.Below));
        Assert.AreEqual(ExtrapolationSides.Above, ExtrapolationSupport.Map(ExtrapolationPolicy.Above));
        Assert.AreEqual(ExtrapolationSides.Both, ExtrapolationSupport.Map(ExtrapolationPolicy.Both));
        Assert.AreEqual(ExtrapolationSides.None, ExtrapolationSupport.Map(ExtrapolationPolicy.Error));
    }
}
