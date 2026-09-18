using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Tests.Results.Risk;

/// <summary>
/// Unit tests for <see cref="EnsembleResults"/> — the persisted summary ensemble with the v1.0
/// null-tolerant indexer and the JSON results contract.
/// </summary>
[TestClass]
public class EnsembleResultsTests
{
    /// <summary>Verifies the v1.0 null-tolerant indexer semantics.</summary>
    [TestMethod]
    public void Test_Indexer_NullTolerant()
    {
        // Arrange
        var ensemble = new EnsembleResults(2);
        var summary = new SystemRiskResults();

        // Act — out-of-range writes are ignored; out-of-range reads return null.
        ensemble[0] = summary;

        // Assert
        Assert.AreEqual(2, ensemble.Count);
        Assert.AreSame(summary, ensemble[0]);
        Assert.IsNull(ensemble[1]);
        Assert.IsNull(ensemble[5]);
        Assert.IsNull(ensemble[-1]);
        Assert.IsNull(new EnsembleResults()[0]);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ensemble[5] = summary);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ensemble[-1] = summary);
        Assert.IsTrue(ensemble.TryGetRealization(0, out var present));
        Assert.AreSame(summary, present);
        Assert.IsTrue(ensemble.TryGetRealization(1, out var unwritten));
        Assert.IsNull(unwritten);
        Assert.IsFalse(ensemble.TryGetRealization(5, out var missing));
        Assert.IsNull(missing);
    }

    /// <summary>Verifies the JSON and compressed round trips, including unwritten null slots.</summary>
    [TestMethod]
    public void Test_Json_RoundTrip_WithNullSlots()
    {
        // Arrange
        var ensemble = new EnsembleResults(3);
        ensemble[0] = new SystemRiskResults { FunctionEvaluations = 42d, StandardError = 0.5d };
        ensemble[2] = new SystemRiskResults { ChiSquared = 1.25d };

        // Act
        var fromJson = EnsembleResults.FromJson(ensemble.ToJson());
        var fromBytes = EnsembleResults.FromCompressedBytes(ensemble.ToCompressedBytes());

        // Assert
        Assert.AreEqual(3, fromJson.Count);
        Assert.AreEqual(42d, fromJson[0]!.FunctionEvaluations, 0d);
        Assert.IsNull(fromJson[1]);
        Assert.AreEqual(1.25d, fromJson[2]!.ChiSquared, 0d);
        Assert.AreEqual(ensemble.ToJson(), fromBytes.ToJson());
    }

    /// <summary>
    /// Verifies the validating weight setter: a valid vector is stored by copy, null clears,
    /// and each invalid shape throws its documented exception type before any mutation.
    /// </summary>
    [TestMethod]
    public void Test_SetRealizationWeights_ValidationMatrix()
    {
        // Arrange
        var ensemble = new EnsembleResults(3);

        // A valid vector stores a defensive copy.
        var source = new[] { 1d, 2d, 3d };
        ensemble.SetRealizationWeights(source);
        source[0] = 99d;
        Assert.AreEqual(1d, ensemble.RealizationWeights![0], 0d, "The stored weights must be a copy of the caller's vector.");

        // Null clears.
        ensemble.SetRealizationWeights(null);
        Assert.IsNull(ensemble.RealizationWeights);

        // Structural failures throw ArgumentException; value failures throw
        // ArgumentOutOfRangeException (the upstream guard convention).
        Assert.ThrowsException<ArgumentException>(() => ensemble.SetRealizationWeights(new[] { 1d, 2d }));
        Assert.ThrowsException<ArgumentException>(() => ensemble.SetRealizationWeights(new[] { 0d, 0d, 0d }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ensemble.SetRealizationWeights(new[] { 1d, -0.5d, 1d }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ensemble.SetRealizationWeights(new[] { 1d, double.NaN, 1d }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ensemble.SetRealizationWeights(new[] { 1d, double.PositiveInfinity, 1d }));

        // A failed assignment leaves the previous state intact.
        Assert.IsNull(ensemble.RealizationWeights);
    }

    /// <summary>
    /// Pins the unweighted payload byte-for-byte against the pre-weight library: the fixture's
    /// serialized JSON must hash to the SHA-256 captured from the library before the weight
    /// members existed, and the payload must not mention any of the new optional fields. This
    /// is the append-only guarantee — an unweighted result set is byte-identical across the
    /// change.
    /// </summary>
    [TestMethod]
    public void Test_UnweightedJson_ByteIdenticalToPreWeightLibrary()
    {
        // Arrange — the exact fixture whose payload was captured from the build without the
        // weight fields (three realizations with distinct scalars and a computed summary at 0.9).
        var ensemble = new EnsembleResults(3);
        for (int i = 0; i < 3; i++)
        {
            var summary = new SystemRiskResults();
            summary.Total.TotalProbability = 0.01 * (i + 1);
            summary.Total.Mean = 1.5 + i;
            summary.Fail.TotalProbability = 0.001 * (i + 1);
            summary.Fail.Mean = 0.25 * (i + 1);
            summary.Excess.Mean = 0.125 * (i + 1);
            summary.FunctionEvaluations = 1000 + i;
            summary.StandardError = 1e-6 * (i + 1);
            ensemble[i] = summary;
        }
        ensemble.Summary = ensemble.ComputeSummary(0.9);

        // Act
        string json = ensemble.ToJson();
        string digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));

        // Assert — captured from the pre-weight build of 2026-08-27 (9,955 UTF-8 bytes).
        Assert.AreEqual("D901ACF7A4FBBC6053D99FD6913B17931D3B432EE46D414C8B7A5F780BF2ABBA", digest,
            "The unweighted payload moved — the append-only guarantee is broken.");
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("RealizationWeights"));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("EffectiveRealizationCount"));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("RealizationWeightsHash"));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("LoadDiagnostics"));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("TolerableRiskConfidence"));
    }

    /// <summary>
    /// Verifies a weighted payload round-trips bit-faithfully through JSON and the compressed
    /// bytes, with an empty load-diagnostic list on a clean load.
    /// </summary>
    [TestMethod]
    public void Test_WeightedJson_RoundTrip()
    {
        // Arrange
        var ensemble = new EnsembleResults(3);
        for (int i = 0; i < 3; i++)
        {
            ensemble[i] = new SystemRiskResults { FunctionEvaluations = i + 1d };
        }
        var weights = new[] { 0.1d, Math.PI, 2.5e-7d };
        ensemble.SetRealizationWeights(weights);

        // Act
        var fromJson = EnsembleResults.FromJson(ensemble.ToJson());
        var fromBytes = EnsembleResults.FromCompressedBytes(ensemble.ToCompressedBytes());

        // Assert — bit-faithful weights on both paths and clean load diagnostics.
        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(weights[i]), BitConverter.DoubleToInt64Bits(fromJson.RealizationWeights![i]));
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(weights[i]), BitConverter.DoubleToInt64Bits(fromBytes.RealizationWeights![i]));
        }
        Assert.AreEqual(0, fromJson.LoadDiagnostics.Count);
        Assert.AreEqual(0, fromBytes.LoadDiagnostics.Count);
    }

    /// <summary>
    /// Verifies the load integrity ruling: a stored payload whose weight vector is invalid
    /// loads without throwing, arrives with its results cleared (realizations, summary, and
    /// weights) and its manifest preserved, and records a load diagnostic telling the caller
    /// to rerun the analysis and save its results again.
    /// </summary>
    [TestMethod]
    public void Test_CorruptStoredWeights_LoadClearsResultsAndDiagnoses()
    {
        // Arrange — a valid weighted payload, then two corruptions of the stored vector.
        var ensemble = new EnsembleResults(3);
        for (int i = 0; i < 3; i++)
        {
            ensemble[i] = new SystemRiskResults { FunctionEvaluations = i + 1d };
        }
        ensemble.SetRealizationWeights(new[] { 1d, 2d, 3d });
        ensemble.Summary = ensemble.ComputeSummary(0.9d);
        ensemble.Manifest = new AnalysisRunManifest(1, "1.0", "2.0", "AA", "BB",
            new[] { "CC" }, new[] { 0 }, 12345, "DD");
        string json = ensemble.ToJson();

        // Act / Assert — a length mismatch clears the results without throwing.
        string lengthMismatch = json.Replace("\"RealizationWeights\":[1,2,3]", "\"RealizationWeights\":[1,2]");
        Assert.AreNotEqual(json, lengthMismatch, "The corruption must actually edit the payload.");
        var cleared = EnsembleResults.FromJson(lengthMismatch);
        Assert.AreEqual(0, cleared.Count, "The stored realizations must be cleared.");
        Assert.IsNull(cleared.Summary, "The stored summary must be cleared.");
        Assert.IsNull(cleared.RealizationWeights, "The invalid weights must be cleared.");
        Assert.IsNotNull(cleared.Manifest, "The provenance manifest is kept for identification.");
        Assert.AreEqual(1, cleared.LoadDiagnostics.Count);
        StringAssert.StartsWith(cleared.LoadDiagnostics[0], "Error:");
        StringAssert.Contains(cleared.LoadDiagnostics[0], "rerun the analysis");

        // A negative weight clears identically, through the compressed reader too.
        string negative = json.Replace("\"RealizationWeights\":[1,2,3]", "\"RealizationWeights\":[1,-2,3]");
        var clearedNegative = EnsembleResults.FromJson(negative);
        Assert.AreEqual(0, clearedNegative.Count);
        Assert.AreEqual(1, clearedNegative.LoadDiagnostics.Count);
    }
}
