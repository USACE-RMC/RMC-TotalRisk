using System;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The five loss exceedance curves of one realization scope — one <see cref="Curve"/> per
    /// <see cref="Core.Enums.RiskType"/> stream: Excess (incremental), Background (irreducible),
    /// Total, Fail, and NonFail.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Fail, Excess, and NonFail are defective (total probability below one); Background and Total
    /// are exhaustive — the v1.0 assignment, preserved. The fan-out methods mirror the per-curve
    /// pipeline: mass post-processing, exact curve construction at the output resolution, the
    /// hazard profiles, the risk measures, and the post-aggregation memory dump.
    /// </para>
    /// </remarks>
    public class Curves
    {
        /// <summary>
        /// Initializes the five streams with their v1.0 exhaustiveness assignments.
        /// </summary>
        public Curves()
        {
            Excess = new Curve { IsExhaustive = false };
            Background = new Curve();
            Total = new Curve();
            Fail = new Curve { IsExhaustive = false };
            NonFail = new Curve { IsExhaustive = false };
        }

        /// <summary>
        /// The incremental (excess) risk curve — the reducible risk. Defective.
        /// </summary>
        public Curve Excess { get; set; }

        /// <summary>
        /// The background (irreducible, non-breach) risk curve. Exhaustive.
        /// </summary>
        public Curve Background { get; set; }

        /// <summary>
        /// The total risk curve. Exhaustive.
        /// </summary>
        public Curve Total { get; set; }

        /// <summary>
        /// The failure risk curve — its total probability is the annualized failure probability.
        /// Defective.
        /// </summary>
        public Curve Fail { get; set; }

        /// <summary>
        /// The non-failure risk curve. Defective.
        /// </summary>
        public Curve NonFail { get; set; }

        /// <summary>
        /// Gets the stream curve for a risk type — the enum-driven accessor over the five named
        /// streams for callers that iterate the decomposition.
        /// </summary>
        /// <param name="riskType">The risk-type stream.</param>
        /// <returns>The stream's curve.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined risk type.</exception>
        public Curve GetCurve(RiskType riskType)
        {
            switch (riskType)
            {
                case RiskType.Excess: return Excess;
                case RiskType.Background: return Background;
                case RiskType.Total: return Total;
                case RiskType.Fail: return Fail;
                case RiskType.NonFail: return NonFail;
                default: throw new ArgumentOutOfRangeException(nameof(riskType), riskType, "The risk type is not a defined stream.");
            }
        }

        /// <summary>
        /// Applies the quadrature ledger's masses to every stream.
        /// </summary>
        /// <param name="ledger">The pass's quadrature ledger, sealed.</param>
        internal void ApplyRecordedMass(QuadratureMassLedger ledger)
        {
            Excess.ApplyRecordedMass(ledger);
            Background.ApplyRecordedMass(ledger);
            Total.ApplyRecordedMass(ledger);
            Fail.ApplyRecordedMass(ledger);
            NonFail.ApplyRecordedMass(ledger);
        }

        /// <summary>
        /// Builds the exact curve and moments on every stream.
        /// </summary>
        /// <param name="outputLength">The output resolution of the stored curves.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the output length is less than two.</exception>
        public void CreateCurves(int outputLength)
        {
            Excess.CreateCurve(outputLength);
            Background.CreateCurve(outputLength);
            Total.CreateCurve(outputLength);
            Fail.CreateCurve(outputLength);
            NonFail.CreateCurve(outputLength);
        }

        /// <summary>
        /// Applies the run's optional-measure selection to every stream.
        /// </summary>
        /// <param name="measures">The measures to compute.</param>
        public void SetMeasureOptions(RiskMeasureOptions measures)
        {
            Excess.MeasureOptions = measures;
            Background.MeasureOptions = measures;
            Total.MeasureOptions = measures;
            Fail.MeasureOptions = measures;
            NonFail.MeasureOptions = measures;
        }

        /// <summary>
        /// Builds the risk profiles on every stream. The failure-stream profiles (the cumulative
        /// failure probability by hazard and the system response profile) build on the Fail
        /// stream only, and only for the primary consequence type — probabilities are
        /// type-independent, so per-type copies would duplicate byte-identical data.
        /// </summary>
        /// <param name="primaryType">
        /// True when this curve set is the primary consequence type's (enables the Fail stream's
        /// failure profiles — the risk-profile catalog).
        /// </param>
        public void CreateProfiles(bool primaryType = false)
        {
            Excess.CreateProfiles();
            Background.CreateProfiles();
            Total.CreateProfiles();
            Fail.CreateProfiles(primaryType);
            NonFail.CreateProfiles();
        }

        /// <summary>
        /// Computes the risk-measure catalog on every stream.
        /// </summary>
        /// <param name="consequenceThreshold">The consequence threshold for the assurance measure.</param>
        /// <param name="alpha">The exceedance level for value-at-risk and conditional value-at-risk.</param>
        /// <param name="hazardThreshold">The hazard threshold, or NaN when none applies.</param>
        public void ComputeRiskMeasures(double consequenceThreshold, double alpha, double hazardThreshold = double.NaN)
        {
            Excess.ComputeRiskMeasures(consequenceThreshold, alpha, hazardThreshold);
            Background.ComputeRiskMeasures(consequenceThreshold, alpha, hazardThreshold);
            Total.ComputeRiskMeasures(consequenceThreshold, alpha, hazardThreshold);
            Fail.ComputeRiskMeasures(consequenceThreshold, alpha, hazardThreshold);
            NonFail.ComputeRiskMeasures(consequenceThreshold, alpha, hazardThreshold);
        }

        /// <summary>
        /// Scales every stream's recorded risk-point masses by the given factor (the joint
        /// system path's VEGAS weight self-normalization).
        /// </summary>
        /// <param name="factor">The positive scale factor.</param>
        internal void ScaleRecordedMass(double factor)
        {
            Excess.ScaleRecordedMass(factor);
            Background.ScaleRecordedMass(factor);
            Total.ScaleRecordedMass(factor);
            Fail.ScaleRecordedMass(factor);
            NonFail.ScaleRecordedMass(factor);
        }

        /// <summary>
        /// Clears every stream's recorded risk points — call only after post-processing.
        /// </summary>
        public void DumpMemory()
        {
            Excess.DumpMemory();
            Background.DumpMemory();
            Total.DumpMemory();
            Fail.DumpMemory();
            NonFail.DumpMemory();
        }

        /// <summary>
        /// Creates a deep copy of the five streams.
        /// </summary>
        /// <returns>The copy.</returns>
        public Curves Clone()
        {
            return new Curves
            {
                Excess = Excess.Clone(),
                Background = Background.Clone(),
                Total = Total.Clone(),
                Fail = Fail.Clone(),
                NonFail = NonFail.Clone(),
            };
        }
    }
}
