using System;
using System.Collections.Generic;
using Numerics.Data;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Analyses.CostBenefit.Support
{
    /// <summary>
    /// The pairwise stochastic-dominance screens: the aleatory comparison of two loss-exceedance
    /// curves at their union knots (first order exactly, second order through the stop-loss
    /// transform with interior crossing abscissae), and the epistemic comparison of two weighted
    /// ensemble samples through their empirical distributions.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The aleatory decision is exact for the curves as stored: between union knots both
    /// exceedance curves are single log-log segments, so their log-space difference is linear —
    /// knot signs decide first-order dominance, and the stop-loss difference is monotone
    /// between sign-constant stretches, so union knots plus the interior log-log crossing
    /// abscissae decide second order. Exceedance at zero consequence is the total probability
    /// by definition (never interpolated), and the stop-loss transform
    /// E[(C − c)⁺] = QuantileTailIntegral(0, G(c)) − G(c)·c reads the same quantile authority
    /// the conditional value-at-risk uses. Comparisons are exact floating-point comparisons —
    /// a tie broken by rounding noise degrades the verdict toward None, never fabricates
    /// dominance. The epistemic variant orients values as gains per the criterion direction and
    /// compares weighted step distributions at the union of sample points, where both the
    /// distributions and their running integrals are decided exactly by the union grid.
    /// </para>
    /// </remarks>
    internal static class StochasticDominanceEngine
    {
        /// <summary>
        /// Compares two loss-exceedance curves (losses — smaller is better; the first
        /// alternative dominating means the first is preferred).
        /// </summary>
        /// <param name="first">The first alternative's curve.</param>
        /// <param name="second">The second alternative's curve.</param>
        /// <returns>The strongest supported verdict.</returns>
        internal static DominanceVerdict CompareLossExceedanceCurves(Curve first, Curve second)
        {
            var probes = UnionConsequenceProbes(first, second);
            int count = probes.Count;
            var firstExceedance = new double[count];
            var secondExceedance = new double[count];
            bool firstNeverAbove = true;
            bool secondNeverAbove = true;
            bool anyFirstBelow = false;
            bool anySecondBelow = false;
            bool allEqual = true;
            for (int i = 0; i < count; i++)
            {
                double a = ExceedanceAt(first, probes[i]);
                double b = ExceedanceAt(second, probes[i]);
                if (double.IsNaN(a) || double.IsNaN(b)) return DominanceVerdict.None;
                firstExceedance[i] = a;
                secondExceedance[i] = b;
                if (a > b) { firstNeverAbove = false; anySecondBelow = true; }
                if (b > a) { secondNeverAbove = false; anyFirstBelow = true; }
                if (a != b) allEqual = false;
            }
            if (allEqual) return DominanceVerdict.Identical;
            if (firstNeverAbove && anyFirstBelow) return DominanceVerdict.FirstDominatesFirstOrder;
            if (secondNeverAbove && anySecondBelow) return DominanceVerdict.SecondDominatesFirstOrder;

            // Second order: the stop-loss difference is monotone wherever the exceedance
            // difference holds one sign, so probing the union knots plus each interval's
            // log-log crossing abscissa decides the global ordering exactly.
            var stopLossProbes = new List<double>(probes);
            for (int i = 0; i + 1 < count; i++)
            {
                bool firstAboveAtLeft = firstExceedance[i] > secondExceedance[i];
                bool firstAboveAtRight = firstExceedance[i + 1] > secondExceedance[i + 1];
                bool signChanges = (firstAboveAtLeft != firstAboveAtRight)
                    && firstExceedance[i] != secondExceedance[i]
                    && firstExceedance[i + 1] != secondExceedance[i + 1];
                if (!signChanges || probes[i] <= 0d) continue;
                double crossing = LogLogCrossing(probes[i], probes[i + 1],
                    firstExceedance[i], secondExceedance[i],
                    firstExceedance[i + 1], secondExceedance[i + 1]);
                if (crossing > probes[i] && crossing < probes[i + 1]) stopLossProbes.Add(crossing);
            }
            bool firstStopLossNeverAbove = true;
            bool secondStopLossNeverAbove = true;
            bool anyFirstStopLossBelow = false;
            bool anySecondStopLossBelow = false;
            for (int i = 0; i < stopLossProbes.Count; i++)
            {
                double c = stopLossProbes[i];
                double a = StopLoss(first, c);
                double b = StopLoss(second, c);
                if (double.IsNaN(a) || double.IsNaN(b)) return DominanceVerdict.None;
                if (a > b) { firstStopLossNeverAbove = false; anySecondStopLossBelow = true; }
                if (b > a) { secondStopLossNeverAbove = false; anyFirstStopLossBelow = true; }
            }
            if (firstStopLossNeverAbove && anyFirstStopLossBelow) return DominanceVerdict.FirstDominatesSecondOrder;
            if (secondStopLossNeverAbove && anySecondStopLossBelow) return DominanceVerdict.SecondDominatesSecondOrder;
            return DominanceVerdict.None;
        }

        /// <summary>
        /// Compares two weighted ensemble samples as gains-oriented empirical distributions
        /// (the criterion direction orients values so larger is better; the first alternative
        /// dominating means the first is preferred).
        /// </summary>
        /// <param name="firstValues">The first sample's values.</param>
        /// <param name="firstWeights">The first sample's weights, parallel to the values.</param>
        /// <param name="firstCount">The first sample's surviving count.</param>
        /// <param name="secondValues">The second sample's values.</param>
        /// <param name="secondWeights">The second sample's weights, parallel to the values.</param>
        /// <param name="secondCount">The second sample's surviving count.</param>
        /// <param name="direction">The criterion's optimization direction.</param>
        /// <returns>The strongest supported verdict.</returns>
        internal static DominanceVerdict CompareWeightedSamples(double[] firstValues,
            double[] firstWeights, int firstCount, double[] secondValues, double[] secondWeights,
            int secondCount, ObjectiveDirection direction)
        {
            if (firstCount == 0 || secondCount == 0) return DominanceVerdict.None;
            double firstTotal = 0d;
            for (int i = 0; i < firstCount; i++) firstTotal += firstWeights[i];
            double secondTotal = 0d;
            for (int i = 0; i < secondCount; i++) secondTotal += secondWeights[i];
            if (!(firstTotal > 0d) || !(secondTotal > 0d)) return DominanceVerdict.None;

            // Orient as gains: larger is better. Minimize-criterion values negate.
            double sign = direction == ObjectiveDirection.Maximize ? 1d : -1d;
            var union = new List<double>(firstCount + secondCount);
            for (int i = 0; i < firstCount; i++) union.Add(sign * firstValues[i]);
            for (int i = 0; i < secondCount; i++) union.Add(sign * secondValues[i]);
            union.Sort();
            int unique = 0;
            for (int i = 0; i < union.Count; i++)
            {
                if (unique == 0 || union[i] != union[unique - 1]) union[unique++] = union[i];
            }
            union.RemoveRange(unique, union.Count - unique);

            var firstCdf = new double[unique];
            var secondCdf = new double[unique];
            FillWeightedCdf(firstValues, firstWeights, firstCount, sign, firstTotal, union, firstCdf);
            FillWeightedCdf(secondValues, secondWeights, secondCount, sign, secondTotal, union, secondCdf);

            bool firstNeverAbove = true;
            bool secondNeverAbove = true;
            bool anyFirstBelow = false;
            bool anySecondBelow = false;
            bool allEqual = true;
            for (int i = 0; i < unique; i++)
            {
                double a = firstCdf[i];
                double b = secondCdf[i];
                if (a > b) { firstNeverAbove = false; anySecondBelow = true; }
                if (b > a) { secondNeverAbove = false; anyFirstBelow = true; }
                if (a != b) allEqual = false;
            }
            if (allEqual) return DominanceVerdict.Identical;
            if (firstNeverAbove && anyFirstBelow) return DominanceVerdict.FirstDominatesFirstOrder;
            if (secondNeverAbove && anySecondBelow) return DominanceVerdict.SecondDominatesFirstOrder;

            // Second order for gains: the running distribution integrals, piecewise linear
            // with knots at the union points, so the union grid decides exactly.
            bool firstIntegralNeverAbove = true;
            bool secondIntegralNeverAbove = true;
            bool anyFirstIntegralBelow = false;
            bool anySecondIntegralBelow = false;
            double firstIntegral = 0d;
            double secondIntegral = 0d;
            for (int i = 1; i < unique; i++)
            {
                double step = union[i] - union[i - 1];
                firstIntegral += firstCdf[i - 1] * step;
                secondIntegral += secondCdf[i - 1] * step;
                if (firstIntegral > secondIntegral) { firstIntegralNeverAbove = false; anySecondIntegralBelow = true; }
                if (secondIntegral > firstIntegral) { secondIntegralNeverAbove = false; anyFirstIntegralBelow = true; }
            }
            if (firstIntegralNeverAbove && anyFirstIntegralBelow) return DominanceVerdict.FirstDominatesSecondOrder;
            if (secondIntegralNeverAbove && anySecondIntegralBelow) return DominanceVerdict.SecondDominatesSecondOrder;
            return DominanceVerdict.None;
        }

        /// <summary>
        /// Builds the union consequence probes: zero plus every positive loss-exceedance knot
        /// of either curve, ascending and deduplicated.
        /// </summary>
        /// <param name="first">The first curve.</param>
        /// <param name="second">The second curve.</param>
        /// <returns>The probes.</returns>
        private static List<double> UnionConsequenceProbes(Curve first, Curve second)
        {
            var probes = new List<double>(first.LECConsequences.Length + second.LECConsequences.Length + 1)
            {
                0d,
            };
            for (int i = 0; i < first.LECConsequences.Length; i++)
            {
                if (first.LECConsequences[i] > 0d) probes.Add(first.LECConsequences[i]);
            }
            for (int i = 0; i < second.LECConsequences.Length; i++)
            {
                if (second.LECConsequences[i] > 0d) probes.Add(second.LECConsequences[i]);
            }
            probes.Sort();
            int unique = 0;
            for (int i = 0; i < probes.Count; i++)
            {
                if (unique == 0 || probes[i] != probes[unique - 1]) probes[unique++] = probes[i];
            }
            probes.RemoveRange(unique, probes.Count - unique);
            return probes;
        }

        /// <summary>
        /// Evaluates a curve's exceedance at a consequence: the total probability at zero (by
        /// definition — never interpolated), zero for a curve with no recorded losses, and the
        /// curve's own log-log interpolation with its raw clamps elsewhere.
        /// </summary>
        /// <param name="curve">The curve.</param>
        /// <param name="consequence">The consequence probe.</param>
        /// <returns>The exceedance probability.</returns>
        private static double ExceedanceAt(Curve curve, double consequence)
        {
            if (consequence <= 0d) return curve.TotalProbability;
            if (curve.LECConsequences.Length == 0) return 0d;
            return curve.LEC.GetYFromX(consequence, Transform.Logarithmic, Transform.Logarithmic);
        }

        /// <summary>
        /// The stop-loss transform E[(C − c)⁺] = QuantileTailIntegral(0, G(c)) − G(c)·c.
        /// </summary>
        /// <param name="curve">The curve.</param>
        /// <param name="consequence">The retention level.</param>
        /// <returns>The expected loss beyond the retention.</returns>
        private static double StopLoss(Curve curve, double consequence)
        {
            double exceedance = ExceedanceAt(curve, consequence);
            if (double.IsNaN(exceedance)) return double.NaN;
            return curve.QuantileTailIntegral(0d, exceedance) - exceedance * consequence;
        }

        /// <summary>
        /// Solves the log-log crossing abscissa of two exceedance segments over one probe
        /// interval: both differences are linear in log space, so the sign change locates one
        /// interior root.
        /// </summary>
        /// <param name="leftConsequence">The interval's left probe (positive).</param>
        /// <param name="rightConsequence">The interval's right probe.</param>
        /// <param name="firstLeft">The first curve's exceedance at the left probe.</param>
        /// <param name="secondLeft">The second curve's exceedance at the left probe.</param>
        /// <param name="firstRight">The first curve's exceedance at the right probe.</param>
        /// <param name="secondRight">The second curve's exceedance at the right probe.</param>
        /// <returns>The crossing consequence.</returns>
        private static double LogLogCrossing(double leftConsequence, double rightConsequence,
            double firstLeft, double secondLeft, double firstRight, double secondRight)
        {
            double u1 = Math.Log10(leftConsequence);
            double u2 = Math.Log10(rightConsequence);
            double d1 = Math.Log10(firstLeft) - Math.Log10(secondLeft);
            double d2 = Math.Log10(firstRight) - Math.Log10(secondRight);
            double t = d1 / (d1 - d2);
            return Math.Pow(10d, u1 + t * (u2 - u1));
        }

        /// <summary>
        /// Fills a weighted right-continuous empirical distribution at the union grid.
        /// </summary>
        /// <param name="values">The sample values.</param>
        /// <param name="weights">The sample weights, parallel to the values.</param>
        /// <param name="count">The surviving count.</param>
        /// <param name="sign">The gains orientation sign.</param>
        /// <param name="totalWeight">The sample's total weight (the normalizer).</param>
        /// <param name="union">The ascending union grid.</param>
        /// <param name="cdf">The output distribution values, parallel to the grid.</param>
        private static void FillWeightedCdf(double[] values, double[] weights, int count,
            double sign, double totalWeight, List<double> union, double[] cdf)
        {
            var oriented = new double[count];
            var orientedWeights = new double[count];
            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                oriented[i] = sign * values[i];
                orientedWeights[i] = weights[i];
                order[i] = i;
            }
            Array.Sort(order, (a, b) => oriented[a].CompareTo(oriented[b]));
            double cumulative = 0d;
            int next = 0;
            for (int g = 0; g < union.Count; g++)
            {
                while (next < count && oriented[order[next]] <= union[g])
                {
                    cumulative += orientedWeights[order[next]];
                    next++;
                }
                cdf[g] = cumulative / totalWeight;
            }
        }
    }
}
