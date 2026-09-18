using System;
using System.Collections.Generic;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results;

namespace RMC.TotalRisk.Analyses.CostBenefit.Support
{
    /// <summary>
    /// The aleatory loss-exceedance partition arithmetic: partitioned conditional means over
    /// declared exceedance regions and the expected-utility certainty equivalent over the
    /// curve's mass pairs — both exact reads of the log-log quantile tail integral.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Both computations are pure reads of the curve's stored loss-exceedance ordinates —
    /// nothing is cloned because nothing is mutated; the clone rule applies to the mutating
    /// measure computation only. Region means are quantile tail-integral differences over the
    /// region widths, so the low-probability high-consequence region reproduces the curve's
    /// conditional value-at-risk at its boundary exactly. The certainty equivalent evaluates
    /// the design's discrete convention — expected utility over the curve's mass pairs, the
    /// no-exceedance atom at zero consequence, the clamp slab at the largest consequence, and
    /// interior segment masses at their exact conditional means — with the exponential family
    /// evaluated in shifted log space so large risk-aversion parameters cannot overflow.
    /// </para>
    /// </remarks>
    internal static class LecPartitionEngine
    {
        /// <summary>
        /// Computes the partitioned conditional means over declared strictly descending
        /// exceedance boundaries: k boundaries divide the exceedance axis into k + 1 regions
        /// from the high-probability range down to the low-probability high-consequence tail.
        /// </summary>
        /// <param name="curve">The loss-exceedance curve.</param>
        /// <param name="descendingBoundaries">The exceedance boundaries, strictly descending, each in (0, 1).</param>
        /// <returns>
        /// The per-region conditional means, high-probability region first; a region whose
        /// lower exceedance bound is at or above the curve's total probability is NaN (no
        /// losses attain it), and an empty curve reports NaN throughout.
        /// </returns>
        internal static double[] RegionConditionalMeans(Curve curve,
            IReadOnlyList<double> descendingBoundaries)
        {
            int regionCount = descendingBoundaries.Count + 1;
            var means = new double[regionCount];
            if (curve.LECProbabilities.Length == 0)
            {
                for (int i = 0; i < regionCount; i++) means[i] = double.NaN;
                return means;
            }
            double totalProbability = curve.TotalProbability;
            for (int i = 0; i < regionCount; i++)
            {
                double upper = i == 0 ? 1d : descendingBoundaries[i - 1];
                double lower = i == regionCount - 1 ? 0d : descendingBoundaries[i];
                if (!(lower < totalProbability))
                {
                    means[i] = double.NaN;
                    continue;
                }
                means[i] = curve.QuantileTailIntegral(lower, upper) / (upper - lower);
            }
            return means;
        }

        /// <summary>
        /// Computes the expected-utility certainty equivalent over the curve's mass pairs: the
        /// no-exceedance atom (one minus the total probability) at zero consequence, the clamp
        /// slab below the first ordinate at the largest consequence, and interior segment
        /// masses at their exact quantile conditional means.
        /// </summary>
        /// <param name="curve">The loss-exceedance curve.</param>
        /// <param name="form">The utility-function family.</param>
        /// <param name="riskAversion">The family's risk-aversion parameter (positive).</param>
        /// <returns>
        /// The certainty equivalent in consequence units; zero for a curve with no losses, and
        /// NaN for an unmeasurable curve.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined utility family.</exception>
        internal static double CertaintyEquivalent(Curve curve, UtilityFunctionForm form,
            double riskAversion)
        {
            var probabilities = curve.LECProbabilities;
            var consequences = curve.LECConsequences;
            int count = probabilities.Length;
            double totalProbability = curve.TotalProbability;
            if (count == 0)
            {
                return totalProbability == 0d ? 0d : double.NaN;
            }
            if (double.IsNaN(totalProbability)) return double.NaN;

            // The mass pairs: the atom, the clamp slab, then the interior segments. Masses
            // telescope to one because the last ordinate's exceedance is the total probability.
            var masses = new List<double>(count + 1);
            var values = new List<double>(count + 1);
            double atom = Math.Max(0d, 1d - totalProbability);
            if (atom > 0d)
            {
                masses.Add(atom);
                values.Add(0d);
            }
            if (probabilities[0] > 0d)
            {
                masses.Add(probabilities[0]);
                values.Add(consequences[0]);
            }
            for (int i = 0; i + 1 < count; i++)
            {
                double width = probabilities[i + 1] - probabilities[i];
                if (!(width > 0d)) continue;
                masses.Add(width);
                values.Add(curve.QuantileTailIntegral(probabilities[i], probabilities[i + 1]) / width);
            }
            if (masses.Count == 0) return double.NaN;

            switch (form)
            {
                case UtilityFunctionForm.ExponentialCara:
                {
                    // CE = (1/θ)·ln Σ mᵢ·e^{θcᵢ}, evaluated log-sum-exp about the largest
                    // consequence so θ·c cannot overflow.
                    double largest = values[0];
                    for (int i = 1; i < values.Count; i++)
                    {
                        if (values[i] > largest) largest = values[i];
                    }
                    double sum = 0d;
                    for (int i = 0; i < values.Count; i++)
                    {
                        sum += masses[i] * Math.Exp(riskAversion * (values[i] - largest));
                    }
                    return largest + Math.Log(sum) / riskAversion;
                }
                case UtilityFunctionForm.PowerCrra:
                {
                    // CE = (Σ mᵢ·cᵢ^{1+γ})^{1/(1+γ)}; zero-consequence masses contribute zero.
                    double exponent = 1d + riskAversion;
                    double sum = 0d;
                    for (int i = 0; i < values.Count; i++)
                    {
                        sum += masses[i] * Math.Pow(values[i], exponent);
                    }
                    return Math.Pow(sum, 1d / exponent);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(form), form,
                        "The utility-function form is not a defined member.");
            }
        }
    }
}
