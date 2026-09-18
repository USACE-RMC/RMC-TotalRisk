using System;
using System.Collections.Generic;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using Numerics.Mathematics.LinearAlgebra;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;

namespace RMC.TotalRisk.RiskFunctions.Support
{
    /// <summary>
    /// The guards and mappings shared by the composite input functions — the correlation-matrix
    /// gate, the Numerics dependence mapping, the posterior-capacity check, and the default summary
    /// probability grid.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Extracted so the hazard and response composites cannot drift apart on the rules that must
    /// agree between them — the same reason <c>FunctionEntry</c> owns the serialized reference
    /// shape for every container.
    /// </para>
    /// </remarks>
    internal static class CompositeSupport
    {
        /// <summary>
        /// The non-exceedance probability grid contributed by a child whose own grid lives on a
        /// hazard axis rather than a probability axis (tabular and nonparametric children), so it
        /// cannot be unioned into a probability grid directly. The v1.0 parametric default
        /// ordinates, inverted to non-exceedance and in ascending order.
        /// </summary>
        internal static readonly IReadOnlyList<double> DefaultSummaryProbabilities = new[]
        {
            0.01d, 0.02d, 0.05d, 0.1d, 0.2d, 0.3d, 0.5d, 0.7d, 0.8d, 0.9d, 0.95d, 0.98d, 0.99d,
            0.995d, 0.998d, 0.999d, 0.9995d, 0.9998d, 0.9999d, 0.99995d, 0.99998d, 0.99999d,
            0.999995d, 0.999998d, 0.999999d,
        };

        /// <summary>
        /// Determines whether a composite is in the one configuration that reads a user-specified
        /// correlation matrix: competing risks under the correlation-matrix dependence.
        /// </summary>
        /// <param name="combinationType">The composite's combination mode.</param>
        /// <param name="dependency">The composite's configured dependence.</param>
        /// <returns>True when the correlation matrix is live content.</returns>
        internal static bool IsMatrixMode(CompositeCombinationType combinationType, DependencyType dependency)
        {
            return combinationType == CompositeCombinationType.CompetingRisks
                && dependency == DependencyType.CorrelationMatrix;
        }

        /// <summary>
        /// Validates a composite's correlation matrix: one row and column per child entry, and
        /// positive definite (Cholesky). Always true outside <see cref="IsMatrixMode"/>, whose
        /// combinations construct their own valid matrices.
        /// </summary>
        /// <param name="combinationType">The composite's combination mode.</param>
        /// <param name="dependency">The composite's configured dependence.</param>
        /// <param name="matrix">The candidate matrix; may be null.</param>
        /// <param name="dimension">The child entry count.</param>
        /// <returns>True when the matrix is usable (or not required).</returns>
        internal static bool IsValid(CompositeCombinationType combinationType, DependencyType dependency,
            double[,]? matrix, int dimension)
        {
            if (!IsMatrixMode(combinationType, dependency)) return true;
            if (matrix == null || matrix.GetLength(0) != dimension || matrix.GetLength(1) != dimension) return false;

            try
            {
                var cholesky = new CholeskyDecomposition(new Matrix(matrix));
                return cholesky.IsPositiveDefinite;
            }
            catch (Exception)
            {
                // A failed decomposition means the matrix is not positive definite — the exact v1.0
                // treatment of the numerical failure path, mirrored from SystemComponent.
                return false;
            }
        }

        /// <summary>
        /// Maps the model library's dependence option onto the Numerics option consumed by
        /// <see cref="CompetingRisks"/>.
        /// </summary>
        /// <param name="dependency">The model dependence option.</param>
        /// <returns>The Numerics dependence option.</returns>
        /// <exception cref="NotSupportedException">Thrown for an unrecognized option.</exception>
        /// <remarks>
        /// Deliberately an explicit name-based switch rather than a cast, even though the two enums
        /// are member- and order-identical today: an upstream reorder would otherwise silently
        /// change dependence semantics with no compiler error and no test failure outside the
        /// families that exercise every mode.
        /// </remarks>
        internal static Probability.DependencyType MapDependency(DependencyType dependency)
        {
            return dependency switch
            {
                DependencyType.Independent => Probability.DependencyType.Independent,
                DependencyType.PerfectlyPositive => Probability.DependencyType.PerfectlyPositive,
                DependencyType.PerfectlyNegative => Probability.DependencyType.PerfectlyNegative,
                DependencyType.CorrelationMatrix => Probability.DependencyType.CorrelationMatrix,
                _ => throw new NotSupportedException($"The dependency type '{dependency}' is not supported."),
            };
        }

        /// <summary>
        /// Rejects a posterior-indexed child whose realization capacity is below the sample size
        /// the composite is being set up for, naming the child and both counts.
        /// </summary>
        /// <param name="child">The child function.</param>
        /// <param name="sampleSize">The requested sample size.</param>
        /// <param name="ownerName">The owning composite's name, for the message.</param>
        /// <exception cref="InvalidOperationException">Thrown when the child cannot serve the sample size.</exception>
        /// <remarks>
        /// A parametric child's <c>Realizations</c> (its posterior depth, default 10,000) is
        /// independent of the analysis sample size, and its indexed sampling throws
        /// <see cref="ArgumentOutOfRangeException"/> once the index passes it. Composites make that
        /// mismatch far easier to hit — one 10,000-realization parametric child alongside a tabular
        /// child that samples happily at a million. Failing at setup with both counts named beats
        /// failing deep inside a realization loop. The check is scoped to composites, where the
        /// mismatch is easiest to construct; <c>SystemComponent.SetupSamplers</c> performs no
        /// equivalent check.
        /// </remarks>
        internal static void ThrowIfPosteriorCapacityTooSmall(IRiskFunction child, int sampleSize, string ownerName)
        {
            int capacity = child switch
            {
                ParametricUnivariateHazard hazard when hazard.IsUncertain => hazard.Realizations,
                ParametricResponse response when response.IsUncertain => response.Realizations,
                _ => int.MaxValue,
            };

            if (capacity >= sampleSize) return;

            throw new InvalidOperationException(
                $"The composite '{ownerName}' was set up for {sampleSize} realizations, but its child " +
                $"'{child.Name}' carries a posterior of only {capacity}. Increase the child's Realizations " +
                "to at least the sample size, or reduce the sample size.");
        }

        /// <summary>
        /// Rejects sampled child distributions the Numerics combination kernels cannot accept.
        /// </summary>
        /// <param name="distributions">The sampled child distributions.</param>
        /// <param name="ownerType">The owning composite's type name, for the message.</param>
        /// <exception cref="InvalidOperationException">Thrown when a child sampled to an unusable distribution.</exception>
        /// <remarks>
        /// <see cref="Mixture"/> and <see cref="CompetingRisks"/> both hard-cast their
        /// <see cref="IUnivariateDistribution"/> arguments to <see cref="UnivariateDistributionBase"/>,
        /// so a null (the <c>NonFailResponse</c> sentinel) or a non-Numerics implementation would
        /// surface as a <see cref="NullReferenceException"/> or an
        /// <see cref="InvalidCastException"/> from inside the kernel. Checking here keeps the
        /// cluster's standard invalid-configuration message.
        /// </remarks>
        internal static void ThrowIfNotNumericsDistributions(IUnivariateDistribution[] distributions, string ownerType)
        {
            for (int i = 0; i < distributions.Length; i++)
            {
                if (distributions[i] is UnivariateDistributionBase) continue;

                throw new InvalidOperationException(
                    $"A child of the {ownerType} sampled to " +
                    (distributions[i] == null ? "no distribution" : $"a {distributions[i].GetType().Name}") +
                    ", which the combination kernels cannot accept. Call Validate() and correct the " +
                    "reported errors before sampling.");
            }
        }
    }
}
