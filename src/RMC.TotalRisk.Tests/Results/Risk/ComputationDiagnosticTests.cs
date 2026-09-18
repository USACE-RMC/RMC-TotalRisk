using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>Unit tests for <see cref="ComputationDiagnostic"/> compatibility formatting.</summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// </remarks>
[TestClass]
public class ComputationDiagnosticTests
{
    /// <summary>Verifies immutable fields, severity formatting, and code validation.</summary>
    [TestMethod]
    public void Test_ConstructionAndLegacyFormatting()
    {
        var diagnostic = new ComputationDiagnostic("TRC1001", DiagnosticSeverity.Warning,
            "Negative consequence was clamped.", "/Consequences/Failure");

        Assert.AreEqual("TRC1001", diagnostic.Code);
        Assert.AreEqual(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.AreEqual("Negative consequence was clamped.", diagnostic.Message);
        Assert.AreEqual("/Consequences/Failure", diagnostic.ObjectPath);
        Assert.AreEqual("Warning: Negative consequence was clamped.", diagnostic.ToLegacyMessage());
        Assert.ThrowsException<ArgumentException>(() =>
            new ComputationDiagnostic(" ", DiagnosticSeverity.Error, "x", ""));
    }
}
