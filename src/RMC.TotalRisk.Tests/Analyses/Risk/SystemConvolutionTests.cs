using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RMC.TotalRisk.Analyses.Risk;

namespace RMC.TotalRisk.Tests.Analyses.Risk;

/// <summary>
/// Unit tests for <see cref="SystemConvolution"/> — the exact lattice convolution behind the
/// additive system-risk aggregation: exact combination enumeration on atom fixtures, exact mean
/// preservation off-lattice, mass conservation, variance additivity, the degenerate and
/// single-component paths, and the argument contract.
/// </summary>
[TestClass]
public class SystemConvolutionTests
{
    /// <summary>
    /// Verifies the convolution enumerates all failure/non-failure combinations exactly when the
    /// atoms sit on the lattice: two zero-inflated single-atom components produce the four
    /// combination masses of the 2×2 enumeration.
    /// </summary>
    [TestMethod]
    public void Test_TwoAtomComponents_ExactCombinationEnumeration()
    {
        // Arrange — A fails with 0.3 at 10; B fails with 0.2 at 20. Lattice step 10.
        var components = new List<IReadOnlyList<(double Mass, double Consequence)>>
        {
            new[] { (0.3d, 10d) },
            new[] { (0.2d, 20d) },
        };

        // Act
        var (pmf, step) = SystemConvolution.Convolve(components, convolutionPoints: 4);

        // Assert — P(0) = 0.7·0.8, P(10) = 0.3·0.8, P(20) = 0.7·0.2, P(30) = 0.3·0.2.
        Assert.AreEqual(10d, step, 1e-12);
        Assert.AreEqual(0.56d, pmf[0], 1e-12, "The joint zero atom is the no-failure combination.");
        Assert.AreEqual(0.24d, pmf[1], 1e-12, "Only A fails.");
        Assert.AreEqual(0.14d, pmf[2], 1e-12, "Only B fails.");
        Assert.AreEqual(0.06d, pmf[3], 1e-12, "Both fail — the summed tail the conditional-mean collapse loses.");
    }

    /// <summary>
    /// Verifies exact mean preservation with off-lattice consequences: the moment-preserving
    /// two-node split keeps the convolved mean equal to the sum of the component means to
    /// floating-point roundoff — the additive method's v1.0 mean-parity gate.
    /// </summary>
    [TestMethod]
    public void Test_OffLatticePairs_MeanPreservedExactly()
    {
        // Arrange — irrational-ish consequences that never land on lattice nodes.
        var componentA = new[] { (0.13d, 17.777d), (0.07d, 123.456789d), (0.11d, 3.14159d) };
        var componentB = new[] { (0.21d, 55.5551d), (0.02d, 250.999d) };
        double meanA = 0.13d * 17.777d + 0.07d * 123.456789d + 0.11d * 3.14159d;
        double meanB = 0.21d * 55.5551d + 0.02d * 250.999d;

        // Act
        var (pmf, step) = SystemConvolution.Convolve(
            new List<IReadOnlyList<(double Mass, double Consequence)>> { componentA, componentB }, 4096);

        // Assert
        double mass = 0d;
        double mean = 0d;
        for (int k = 0; k < pmf.Length; k++)
        {
            mass += pmf[k];
            mean += pmf[k] * (k * step);
        }
        Assert.AreEqual(1d, mass, 1e-9, "Proper zero-inflated inputs convolve to a proper distribution.");
        Assert.AreEqual(meanA + meanB, mean, 1e-9 * (meanA + meanB),
            "The convolved mean must equal the sum of the component means to roundoff.");
    }

    /// <summary>
    /// Verifies independent variances add through the convolution on an on-lattice fixture
    /// (exact — no quantization).
    /// </summary>
    [TestMethod]
    public void Test_OnLatticePairs_VariancesAdd()
    {
        // Arrange — supports {0, 30, 60} and {0, 90}; points chosen so step divides both.
        var componentA = new[] { (0.25d, 30d), (0.05d, 60d) };
        var componentB = new[] { (0.1d, 90d) };

        // Act — total maximum 150; 6 points → step 30 keeps every value on the lattice.
        var (pmf, step) = SystemConvolution.Convolve(
            new List<IReadOnlyList<(double Mass, double Consequence)>> { componentA, componentB }, 6);

        // Assert
        (double Mean, double Variance) Moments(IReadOnlyList<(double Mass, double Consequence)> pairs)
        {
            double mean = 0d;
            double second = 0d;
            double recorded = 0d;
            for (int i = 0; i < pairs.Count; i++)
            {
                recorded += pairs[i].Mass;
                mean += pairs[i].Mass * pairs[i].Consequence;
                second += pairs[i].Mass * pairs[i].Consequence * pairs[i].Consequence;
            }
            _ = recorded;
            return (mean, second - mean * mean);
        }
        var momentsA = Moments(componentA);
        var momentsB = Moments(componentB);

        double systemMean = 0d;
        double systemSecond = 0d;
        for (int k = 0; k < pmf.Length; k++)
        {
            systemMean += pmf[k] * (k * step);
            systemSecond += pmf[k] * (k * step) * (k * step);
        }
        double systemVariance = systemSecond - systemMean * systemMean;
        Assert.AreEqual(momentsA.Mean + momentsB.Mean, systemMean, 1e-9);
        Assert.AreEqual(momentsA.Variance + momentsB.Variance, systemVariance, 1e-6,
            "Independent variances must add exactly on an on-lattice fixture.");
    }

    /// <summary>
    /// Verifies the single-component and degenerate paths: one component round-trips its binned
    /// distribution, and an all-zero system collapses to the unit atom with a zero step.
    /// </summary>
    [TestMethod]
    public void Test_SingleComponent_And_DegenerateZero()
    {
        // Single component: the binned lattice plus its zero atom.
        var (pmf, step) = SystemConvolution.Convolve(
            new List<IReadOnlyList<(double Mass, double Consequence)>> { new[] { (0.4d, 100d) } }, 5);
        Assert.AreEqual(25d, step, 1e-12);
        Assert.AreEqual(0.6d, pmf[0], 1e-12);
        Assert.AreEqual(0.4d, pmf[4], 1e-12);

        // Every component a pure zero atom (a consequence-free reliability system).
        var (degeneratePmf, degenerateStep) = SystemConvolution.Convolve(
            new List<IReadOnlyList<(double Mass, double Consequence)>>
            {
                Array.Empty<(double, double)>(),
                new[] { (0.5d, 0d) },
            }, 4096);
        Assert.AreEqual(0d, degenerateStep);
        Assert.AreEqual(1, degeneratePmf.Length);
        Assert.AreEqual(1d, degeneratePmf[0], 0d);
    }

    /// <summary>
    /// Verifies the argument contract: null and empty component lists, a sub-two lattice
    /// resolution, and negative masses or consequences all throw.
    /// </summary>
    [TestMethod]
    public void Test_Validation_Throws()
    {
        var valid = new List<IReadOnlyList<(double Mass, double Consequence)>> { new[] { (0.5d, 10d) } };

        Assert.ThrowsException<ArgumentNullException>(() => SystemConvolution.Convolve(null!, 4096));
        Assert.ThrowsException<ArgumentException>(() =>
            SystemConvolution.Convolve(new List<IReadOnlyList<(double Mass, double Consequence)>>(), 4096));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => SystemConvolution.Convolve(valid, 1));
        Assert.ThrowsException<ArgumentNullException>(() =>
            SystemConvolution.Convolve(new List<IReadOnlyList<(double Mass, double Consequence)>> { null! }, 4096));
        Assert.ThrowsException<ArgumentException>(() =>
            SystemConvolution.Convolve(new List<IReadOnlyList<(double Mass, double Consequence)>> { new[] { (-0.1d, 10d) } }, 4096));
        Assert.ThrowsException<ArgumentException>(() =>
            SystemConvolution.Convolve(new List<IReadOnlyList<(double Mass, double Consequence)>> { new[] { (0.1d, -10d) } }, 4096));
    }

    /// <summary>
    /// Verifies a three-component convolution conserves mass and mean — the sequential pairwise
    /// chain does not degrade with depth.
    /// </summary>
    [TestMethod]
    public void Test_ThreeComponents_MassAndMeanConserved()
    {
        // Arrange
        var components = new List<IReadOnlyList<(double Mass, double Consequence)>>
        {
            new[] { (0.3d, 12.7d), (0.1d, 44.1d) },
            new[] { (0.05d, 200.3d) },
            new[] { (0.5d, 7.77d), (0.2d, 31.9d), (0.05d, 99.9d) },
        };
        double expectedMean = 0.3d * 12.7d + 0.1d * 44.1d + 0.05d * 200.3d
            + 0.5d * 7.77d + 0.2d * 31.9d + 0.05d * 99.9d;

        // Act
        var (pmf, step) = SystemConvolution.Convolve(components, 8192);

        // Assert
        double mass = 0d;
        double mean = 0d;
        for (int k = 0; k < pmf.Length; k++)
        {
            mass += pmf[k];
            mean += pmf[k] * (k * step);
        }
        Assert.AreEqual(1d, mass, 1e-9);
        Assert.AreEqual(expectedMean, mean, 1e-9 * expectedMean);
    }
}
