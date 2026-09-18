using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Tests.Results.Support;

/// <summary>
/// Tests deterministic clipping of ordered exclusive probability partitions.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// </remarks>
[TestClass]
public sealed class ProbabilityPartitionBoundaryTests
{
    /// <summary>
    /// Verifies that only trailing overshoot is discarded and earlier cells remain unchanged.
    /// </summary>
    [TestMethod]
    public void Test_ClipInPlace_DiscardsOnlyTrailingOvershoot()
    {
        var probabilities = new List<double> { 0.6d, 0.3d, 0.10001d, 0.25d };

        ProbabilityPartitionBoundary.ClipInPlace(probabilities);

        CollectionAssert.AreEqual(
            new[] { 0.6d, 0.3d, 0.10000000000000003d, 0d },
            probabilities);
        Assert.AreEqual(1d, probabilities.Sum());
    }

    /// <summary>
    /// Verifies that negative cells are clipped without redistributing their deficit.
    /// </summary>
    [TestMethod]
    public void Test_ClipInPlace_ClipsNegativeWithoutRenormalizing()
    {
        var probabilities = new List<double> { 0.2d, -0.1d, 0.3d };

        ProbabilityPartitionBoundary.ClipInPlace(probabilities);

        CollectionAssert.AreEqual(new[] { 0.2d, 0d, 0.3d }, probabilities);
        Assert.AreEqual(0.5d, probabilities.Sum());
    }
}
