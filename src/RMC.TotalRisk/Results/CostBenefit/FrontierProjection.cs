using System;
using System.Collections.Generic;
using System.Linq;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.CostBenefit
{
    /// <summary>
    /// One two-dimensional frontier projection: labeled axes with their directions, the
    /// per-alternative points, and the projection's own non-dominance screen.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A projection is its own two-objective weak-dominance screen over fixed axes, emitted
    /// beside the declared-vector frontier so the standard planning charts are always
    /// available; an alternative with a NaN coordinate is excluded and flagged.
    /// </para>
    /// </remarks>
    public sealed class FrontierProjection
    {
        /// <summary>
        /// Initializes a projection.
        /// </summary>
        /// <param name="label">The projection's display label.</param>
        /// <param name="xLabel">The x-axis label.</param>
        /// <param name="xDirection">The x-axis direction.</param>
        /// <param name="yLabel">The y-axis label.</param>
        /// <param name="yDirection">The y-axis direction.</param>
        /// <param name="alternativeNames">The alternative names, in results row order.</param>
        /// <param name="xValues">The x coordinates, parallel to the names.</param>
        /// <param name="yValues">The y coordinates, parallel to the names.</param>
        /// <param name="isNonDominated">The projection's non-dominance flags, parallel to the names.</param>
        /// <param name="isExcludedForNaN">The NaN-exclusion flags, parallel to the names.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a list does not parallel the names.</exception>
        public FrontierProjection(string label, string xLabel, ObjectiveDirection xDirection,
            string yLabel, ObjectiveDirection yDirection, IReadOnlyList<string> alternativeNames,
            IReadOnlyList<double> xValues, IReadOnlyList<double> yValues,
            IReadOnlyList<bool> isNonDominated, IReadOnlyList<bool> isExcludedForNaN)
        {
            Label = label ?? throw new ArgumentNullException(nameof(label));
            XLabel = xLabel ?? throw new ArgumentNullException(nameof(xLabel));
            XDirection = xDirection;
            YLabel = yLabel ?? throw new ArgumentNullException(nameof(yLabel));
            YDirection = yDirection;
            if (alternativeNames == null) throw new ArgumentNullException(nameof(alternativeNames));
            if (xValues == null) throw new ArgumentNullException(nameof(xValues));
            if (yValues == null) throw new ArgumentNullException(nameof(yValues));
            if (isNonDominated == null) throw new ArgumentNullException(nameof(isNonDominated));
            if (isExcludedForNaN == null) throw new ArgumentNullException(nameof(isExcludedForNaN));
            if (xValues.Count != alternativeNames.Count || yValues.Count != alternativeNames.Count
                || isNonDominated.Count != alternativeNames.Count
                || isExcludedForNaN.Count != alternativeNames.Count)
            {
                throw new ArgumentException("Every projection list must parallel the alternative names.",
                    nameof(alternativeNames));
            }
            AlternativeNames = Array.AsReadOnly(alternativeNames.ToArray());
            XValues = Array.AsReadOnly(xValues.ToArray());
            YValues = Array.AsReadOnly(yValues.ToArray());
            IsNonDominated = Array.AsReadOnly(isNonDominated.ToArray());
            IsExcludedForNaN = Array.AsReadOnly(isExcludedForNaN.ToArray());
        }

        /// <summary>The projection's display label.</summary>
        public string Label { get; }

        /// <summary>The x-axis label.</summary>
        public string XLabel { get; }

        /// <summary>The x-axis direction.</summary>
        public ObjectiveDirection XDirection { get; }

        /// <summary>The y-axis label.</summary>
        public string YLabel { get; }

        /// <summary>The y-axis direction.</summary>
        public ObjectiveDirection YDirection { get; }

        /// <summary>The alternative names, in results row order.</summary>
        public IReadOnlyList<string> AlternativeNames { get; }

        /// <summary>The x coordinates, parallel to the names.</summary>
        public IReadOnlyList<double> XValues { get; }

        /// <summary>The y coordinates, parallel to the names.</summary>
        public IReadOnlyList<double> YValues { get; }

        /// <summary>The projection's non-dominance flags, parallel to the names.</summary>
        public IReadOnlyList<bool> IsNonDominated { get; }

        /// <summary>The NaN-exclusion flags, parallel to the names.</summary>
        public IReadOnlyList<bool> IsExcludedForNaN { get; }
    }
}
