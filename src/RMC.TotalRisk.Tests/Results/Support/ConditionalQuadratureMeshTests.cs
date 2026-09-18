using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Distributions;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Tests.Results.Support;

/// <summary>
/// Unit tests for <see cref="ConditionalQuadratureMesh"/> — the recorder append, the probit
/// adoption's sort/coalesce/Jacobian fold, the mass-sanity gate, the largest-mass residual
/// convention, the capacity tripwire, and reuse across resets.
/// </summary>
[TestClass]
public class ConditionalQuadratureMeshTests
{
    /// <summary>
    /// Builds the probit-space quadrature weight that folds to the target probability mass at
    /// the given standard-normal abscissa.
    /// </summary>
    /// <param name="z">The standard-normal abscissa.</param>
    /// <param name="mass">The target probability mass.</param>
    private static double WeightFor(double z, double mass) => mass / Normal.StandardPDF(z);

    /// <summary>
    /// Verifies probit adoption sorts, folds the φ(z) Jacobian, and renormalizes to an exact
    /// unit sum.
    /// </summary>
    [TestMethod]
    public void Test_AdoptProbit_SortsFoldsAndRestoresExactUnitSum()
    {
        // Arrange — three unsorted z-nodes whose folded masses target {0.5, 0.25, 0.25}.
        var mesh = new ConditionalQuadratureMesh(8);
        mesh.Record(1.5d, WeightFor(1.5d, 0.25d), 0d);
        mesh.Record(0d, WeightFor(0d, 0.5d), 0d);
        mesh.Record(-1d, WeightFor(-1d, 0.25d), 0d);

        // Act
        mesh.AdoptProbitExhaustive("test");

        // Assert — sorted abscissas; masses at the targets; Σ exactly one.
        Assert.AreEqual(3, mesh.Count);
        Assert.AreEqual(-1d, mesh.Nodes[0], 0d);
        Assert.AreEqual(0d, mesh.Nodes[1], 0d);
        Assert.AreEqual(1.5d, mesh.Nodes[2], 0d);
        Assert.AreEqual(0.25d, mesh.Weights[0], 1e-15);
        Assert.AreEqual(0.5d, mesh.Weights[1], 1e-15);
        Assert.AreEqual(0.25d, mesh.Weights[2], 1e-15);
        double sum = mesh.Weights[0] + mesh.Weights[1] + mesh.Weights[2];
        Assert.AreEqual(1d, sum, 0d, "The adopted masses must sum to exactly one.");
    }

    /// <summary>Verifies duplicate abscissas coalesce with their weights summed before the fold.</summary>
    [TestMethod]
    public void Test_AdoptProbit_CoalescesDuplicates()
    {
        // Arrange — the z = 0.5 node recorded twice at half its target weight.
        var mesh = new ConditionalQuadratureMesh(8);
        mesh.Record(0.5d, WeightFor(0.5d, 0.2d), 0d);
        mesh.Record(0.5d, WeightFor(0.5d, 0.2d), 0d);
        mesh.Record(-0.5d, WeightFor(-0.5d, 0.6d), 0d);

        // Act
        mesh.AdoptProbitExhaustive("test");

        // Assert
        Assert.AreEqual(2, mesh.Count);
        Assert.AreEqual(-0.5d, mesh.Nodes[0], 0d);
        Assert.AreEqual(0.5d, mesh.Nodes[1], 0d);
        Assert.AreEqual(0.4d, mesh.Weights[1], 1e-15);
        Assert.AreEqual(1d, mesh.Weights[0] + mesh.Weights[1], 0d);
    }

    /// <summary>
    /// The rounding-residual regression: a φ-tail node whose folded mass sits far below the
    /// normalization's rounding noise must adopt without throwing and stay non-negative — the
    /// residual rides the largest-mass node, never an edge node.
    /// </summary>
    [TestMethod]
    public void Test_AdoptProbit_TailNodeStaysNonNegative()
    {
        // Arrange — a dominant central node, a secondary node, and a deep-tail node at
        // z = 8 whose mass (1e-18) is orders below one ulp of the unit total.
        var mesh = new ConditionalQuadratureMesh(8);
        mesh.Record(0d, WeightFor(0d, 0.7d), 0d);
        mesh.Record(-1d, WeightFor(-1d, 0.3d), 0d);
        mesh.Record(8d, WeightFor(8d, 1e-18d), 0d);

        // Act
        mesh.AdoptProbitExhaustive("test");

        // Assert — every mass non-negative, the tail mass at its scale, Σ exactly one.
        Assert.AreEqual(3, mesh.Count);
        Assert.IsTrue(mesh.Weights[0] >= 0d && mesh.Weights[1] >= 0d && mesh.Weights[2] >= 0d,
            "No adopted mass may be negative.");
        Assert.IsTrue(mesh.Weights[2] < 1e-15, "The deep-tail node keeps its tiny folded mass.");
        Assert.AreEqual(0.7d, mesh.Weights[1], 1e-12, "The central node carries the residual.");
        Assert.AreEqual(1d, mesh.Weights[0] + mesh.Weights[1] + mesh.Weights[2], 1e-15);
    }

    /// <summary>Verifies the mass-sanity gate throws on materially incomplete folded mass.</summary>
    [TestMethod]
    public void Test_AdoptProbit_GatesMaterialMassLoss()
    {
        // Arrange — half the unit mass is missing.
        var mesh = new ConditionalQuadratureMesh(4);
        mesh.Record(0d, WeightFor(0d, 0.5d), 0d);

        // Act / Assert
        var failure = Assert.ThrowsException<InvalidOperationException>(() => mesh.AdoptProbitExhaustive("test owner"));
        Assert.IsTrue(failure.Message.Contains("test owner"));

        // An empty mesh is refused too.
        var empty = new ConditionalQuadratureMesh(4);
        Assert.ThrowsException<InvalidOperationException>(() => empty.AdoptProbitExhaustive("test"));
    }

    /// <summary>Verifies the guards: capacity overflow, invalid records, and record-after-adopt.</summary>
    [TestMethod]
    public void Test_Guards()
    {
        var mesh = new ConditionalQuadratureMesh(2);
        mesh.Record(-0.3d, WeightFor(-0.3d, 0.5d), 0d);
        mesh.Record(0.3d, WeightFor(0.3d, 0.5d), 0d);
        Assert.ThrowsException<InvalidOperationException>(() => mesh.Record(0d, 0.1d, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ConditionalQuadratureMesh(2).Record(double.NaN, 0.1d, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ConditionalQuadratureMesh(2).Record(0.5d, -0.1d, 0d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new ConditionalQuadratureMesh(0));

        mesh.AdoptProbitExhaustive("test");
        Assert.ThrowsException<InvalidOperationException>(() => mesh.Record(0d, 0.1d, 0d));
    }

    /// <summary>Verifies a reset returns the mesh to a recordable, re-adoptable state.</summary>
    [TestMethod]
    public void Test_Reset_Reuses()
    {
        // Arrange — adopt once, reset, adopt a different fill.
        var mesh = new ConditionalQuadratureMesh(4);
        mesh.Record(0d, WeightFor(0d, 1d), 0d);
        mesh.AdoptProbitExhaustive("test");

        // Act
        mesh.Reset();
        mesh.Record(-0.7d, WeightFor(-0.7d, 0.5d), 0d);
        mesh.Record(0.7d, WeightFor(0.7d, 0.5d), 0d);
        mesh.AdoptProbitExhaustive("test");

        // Assert
        Assert.AreEqual(2, mesh.Count);
        Assert.AreEqual(-0.7d, mesh.Nodes[0], 0d);
        Assert.AreEqual(1d, mesh.Weights[0] + mesh.Weights[1], 0d);
    }
}
