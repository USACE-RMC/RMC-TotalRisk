using System;
using System.Collections.Generic;
using System.Linq;

namespace RMC.TotalRisk.Results.LifeCycle
{
    /// <summary>
    /// A life-cycle risk trajectory: the per-epoch rows plus the horizon aggregates — the
    /// probability of at least one failure by the horizon, cumulative and discounted expected
    /// consequences per type under both the non-absorbing (annual-renewal) and the absorbing
    /// (first-failure-terminates) conventions, and the equivalent-annual consequences — on the
    /// Total stream, with the Excess- and Fail-stream aggregate twins alongside so a benefit
    /// stream can be selected without re-deriving the trajectory.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// A plain query result — never serialized. Every quantification is mean-only on throwaway
    /// clones, so the numbers are the deterministic trajectory answers and the authored model is
    /// never touched. The non-absorbing convention treats every year as exposed (the
    /// exposure-period conversions' reading, so a stationary trajectory reproduces them exactly);
    /// the absorbing convention weights each year by the probability the system has survived every
    /// earlier year — the reading a first failure that ends the story calls for. The absorbing
    /// probability of failure by the horizon is algebraically identical to the non-absorbing one,
    /// so it is not duplicated here.
    /// </para>
    /// </remarks>
    public sealed class LifeCycleRiskResults
    {
        /// <summary>
        /// Initializes a life-cycle trajectory result.
        /// </summary>
        /// <param name="periodYears">The planning horizon in years.</param>
        /// <param name="discountRate">The annual discount rate.</param>
        /// <param name="consequenceLabels">The declared consequence-type labels (position 0 = primary).</param>
        /// <param name="consequenceUnits">The declared consequence-type units, parallel to the labels.</param>
        /// <param name="epochs">The per-epoch rows in ascending start-year order.</param>
        /// <param name="failureProbabilityByHorizon">P(at least one failure by the horizon).</param>
        /// <param name="cumulativeExpectedConsequences">Σ span·mean per type (non-absorbing).</param>
        /// <param name="presentValueOfExpectedConsequences">The discounted expected consequences per type (non-absorbing).</param>
        /// <param name="equivalentAnnualConsequences">The equivalent-annual consequences per type.</param>
        /// <param name="absorbingCumulativeExpectedConsequences">The survival-weighted cumulative expected consequences per type.</param>
        /// <param name="absorbingPresentValueOfExpectedConsequences">The survival-weighted discounted expected consequences per type.</param>
        /// <param name="appliedInterventions">One label per intervention entry, in ascending year order.</param>
        /// <param name="excessCumulativeExpectedConsequences">The Excess-stream cumulative expected consequences per type; null for none.</param>
        /// <param name="excessPresentValueOfExpectedConsequences">The Excess-stream discounted expected consequences per type; null for none.</param>
        /// <param name="excessEquivalentAnnualConsequences">The Excess-stream equivalent-annual consequences per type; null for none.</param>
        /// <param name="absorbingExcessCumulativeExpectedConsequences">The Excess-stream survival-weighted cumulative expected consequences per type; null for none.</param>
        /// <param name="absorbingExcessPresentValueOfExpectedConsequences">The Excess-stream survival-weighted discounted expected consequences per type; null for none.</param>
        /// <param name="failCumulativeExpectedConsequences">The Fail-stream cumulative expected consequences per type; null for none.</param>
        /// <param name="failPresentValueOfExpectedConsequences">The Fail-stream discounted expected consequences per type; null for none.</param>
        /// <param name="failEquivalentAnnualConsequences">The Fail-stream equivalent-annual consequences per type; null for none.</param>
        /// <param name="absorbingFailCumulativeExpectedConsequences">The Fail-stream survival-weighted cumulative expected consequences per type; null for none.</param>
        /// <param name="absorbingFailPresentValueOfExpectedConsequences">The Fail-stream survival-weighted discounted expected consequences per type; null for none.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the labels, units, or any per-type list misalign.</exception>
        public LifeCycleRiskResults(int periodYears, double discountRate,
            IReadOnlyList<string> consequenceLabels, IReadOnlyList<string> consequenceUnits,
            IReadOnlyList<LifeCycleEpochRisk> epochs, double failureProbabilityByHorizon,
            IReadOnlyList<double> cumulativeExpectedConsequences,
            IReadOnlyList<double> presentValueOfExpectedConsequences,
            IReadOnlyList<double> equivalentAnnualConsequences,
            IReadOnlyList<double> absorbingCumulativeExpectedConsequences,
            IReadOnlyList<double> absorbingPresentValueOfExpectedConsequences,
            IReadOnlyList<string> appliedInterventions,
            IReadOnlyList<double>? excessCumulativeExpectedConsequences = null,
            IReadOnlyList<double>? excessPresentValueOfExpectedConsequences = null,
            IReadOnlyList<double>? excessEquivalentAnnualConsequences = null,
            IReadOnlyList<double>? absorbingExcessCumulativeExpectedConsequences = null,
            IReadOnlyList<double>? absorbingExcessPresentValueOfExpectedConsequences = null,
            IReadOnlyList<double>? failCumulativeExpectedConsequences = null,
            IReadOnlyList<double>? failPresentValueOfExpectedConsequences = null,
            IReadOnlyList<double>? failEquivalentAnnualConsequences = null,
            IReadOnlyList<double>? absorbingFailCumulativeExpectedConsequences = null,
            IReadOnlyList<double>? absorbingFailPresentValueOfExpectedConsequences = null)
        {
            PeriodYears = periodYears;
            DiscountRate = discountRate;
            if (consequenceLabels == null) throw new ArgumentNullException(nameof(consequenceLabels));
            if (consequenceUnits == null) throw new ArgumentNullException(nameof(consequenceUnits));
            if (epochs == null) throw new ArgumentNullException(nameof(epochs));
            if (cumulativeExpectedConsequences == null) throw new ArgumentNullException(nameof(cumulativeExpectedConsequences));
            if (presentValueOfExpectedConsequences == null) throw new ArgumentNullException(nameof(presentValueOfExpectedConsequences));
            if (equivalentAnnualConsequences == null) throw new ArgumentNullException(nameof(equivalentAnnualConsequences));
            if (absorbingCumulativeExpectedConsequences == null) throw new ArgumentNullException(nameof(absorbingCumulativeExpectedConsequences));
            if (absorbingPresentValueOfExpectedConsequences == null) throw new ArgumentNullException(nameof(absorbingPresentValueOfExpectedConsequences));
            if (appliedInterventions == null) throw new ArgumentNullException(nameof(appliedInterventions));

            int typeCount = consequenceLabels.Count;
            if (consequenceUnits.Count != typeCount
                || cumulativeExpectedConsequences.Count != typeCount
                || presentValueOfExpectedConsequences.Count != typeCount
                || equivalentAnnualConsequences.Count != typeCount
                || absorbingCumulativeExpectedConsequences.Count != typeCount
                || absorbingPresentValueOfExpectedConsequences.Count != typeCount)
            {
                throw new ArgumentException("The consequence labels, units, and per-type aggregate lists must align.",
                    nameof(consequenceLabels));
            }

            ConsequenceLabels = Array.AsReadOnly(consequenceLabels.ToArray());
            ConsequenceUnits = Array.AsReadOnly(consequenceUnits.ToArray());
            Epochs = Array.AsReadOnly(epochs.ToArray());
            FailureProbabilityByHorizon = failureProbabilityByHorizon;
            CumulativeExpectedConsequences = Array.AsReadOnly(cumulativeExpectedConsequences.ToArray());
            PresentValueOfExpectedConsequences = Array.AsReadOnly(presentValueOfExpectedConsequences.ToArray());
            EquivalentAnnualConsequences = Array.AsReadOnly(equivalentAnnualConsequences.ToArray());
            AbsorbingCumulativeExpectedConsequences = Array.AsReadOnly(absorbingCumulativeExpectedConsequences.ToArray());
            AbsorbingPresentValueOfExpectedConsequences = Array.AsReadOnly(absorbingPresentValueOfExpectedConsequences.ToArray());
            AppliedInterventions = Array.AsReadOnly(appliedInterventions.ToArray());
            ExcessCumulativeExpectedConsequences = SnapshotStreamList(excessCumulativeExpectedConsequences, typeCount, nameof(excessCumulativeExpectedConsequences));
            ExcessPresentValueOfExpectedConsequences = SnapshotStreamList(excessPresentValueOfExpectedConsequences, typeCount, nameof(excessPresentValueOfExpectedConsequences));
            ExcessEquivalentAnnualConsequences = SnapshotStreamList(excessEquivalentAnnualConsequences, typeCount, nameof(excessEquivalentAnnualConsequences));
            AbsorbingExcessCumulativeExpectedConsequences = SnapshotStreamList(absorbingExcessCumulativeExpectedConsequences, typeCount, nameof(absorbingExcessCumulativeExpectedConsequences));
            AbsorbingExcessPresentValueOfExpectedConsequences = SnapshotStreamList(absorbingExcessPresentValueOfExpectedConsequences, typeCount, nameof(absorbingExcessPresentValueOfExpectedConsequences));
            FailCumulativeExpectedConsequences = SnapshotStreamList(failCumulativeExpectedConsequences, typeCount, nameof(failCumulativeExpectedConsequences));
            FailPresentValueOfExpectedConsequences = SnapshotStreamList(failPresentValueOfExpectedConsequences, typeCount, nameof(failPresentValueOfExpectedConsequences));
            FailEquivalentAnnualConsequences = SnapshotStreamList(failEquivalentAnnualConsequences, typeCount, nameof(failEquivalentAnnualConsequences));
            AbsorbingFailCumulativeExpectedConsequences = SnapshotStreamList(absorbingFailCumulativeExpectedConsequences, typeCount, nameof(absorbingFailCumulativeExpectedConsequences));
            AbsorbingFailPresentValueOfExpectedConsequences = SnapshotStreamList(absorbingFailPresentValueOfExpectedConsequences, typeCount, nameof(absorbingFailPresentValueOfExpectedConsequences));
        }

        /// <summary>
        /// Snapshots one optional per-type stream aggregate list — empty when the trajectory does
        /// not carry the stream axis, aligned with the declared types when it does.
        /// </summary>
        /// <param name="values">The supplied list, or null for none.</param>
        /// <param name="typeCount">The declared consequence-type count.</param>
        /// <param name="parameterName">The parameter name for the misalignment diagnostic.</param>
        /// <returns>The read-only snapshot (empty when null was supplied).</returns>
        /// <exception cref="ArgumentException">Thrown when a supplied list misaligns with the declared types.</exception>
        private static IReadOnlyList<double> SnapshotStreamList(IReadOnlyList<double>? values,
            int typeCount, string parameterName)
        {
            if (values == null) return Array.AsReadOnly(Array.Empty<double>());
            if (values.Count != typeCount)
                throw new ArgumentException("The per-type stream aggregate lists must align with the consequence labels.",
                    parameterName);
            return Array.AsReadOnly(values.ToArray());
        }

        /// <summary>
        /// The planning horizon in years; exposure years run 1 through this value.
        /// </summary>
        public int PeriodYears { get; }

        /// <summary>
        /// The annual discount rate (0 = undiscounted).
        /// </summary>
        public double DiscountRate { get; }

        /// <summary>
        /// The declared consequence-type labels (position 0 = primary).
        /// </summary>
        public IReadOnlyList<string> ConsequenceLabels { get; }

        /// <summary>
        /// The declared consequence-type units, parallel to the labels.
        /// </summary>
        public IReadOnlyList<string> ConsequenceUnits { get; }

        /// <summary>
        /// The per-epoch rows, in ascending start-year order.
        /// </summary>
        public IReadOnlyList<LifeCycleEpochRisk> Epochs { get; }

        /// <summary>
        /// The probability of at least one system failure by the horizon, accumulated in log
        /// space over the trajectory's annualized failure probabilities.
        /// </summary>
        public double FailureProbabilityByHorizon { get; }

        /// <summary>
        /// The cumulative expected consequences per type over the horizon (non-absorbing:
        /// every year exposed).
        /// </summary>
        public IReadOnlyList<double> CumulativeExpectedConsequences { get; }

        /// <summary>
        /// The discounted (present-value) expected consequences per type over the horizon
        /// (non-absorbing), evaluated by exact per-epoch annuity segments.
        /// </summary>
        public IReadOnlyList<double> PresentValueOfExpectedConsequences { get; }

        /// <summary>
        /// The equivalent-annual consequences per type — the level annual amount whose present
        /// value over the horizon equals <see cref="PresentValueOfExpectedConsequences"/>.
        /// </summary>
        public IReadOnlyList<double> EquivalentAnnualConsequences { get; }

        /// <summary>
        /// The survival-weighted cumulative expected consequences per type (absorbing: each year
        /// weighted by the probability every earlier year survived).
        /// </summary>
        public IReadOnlyList<double> AbsorbingCumulativeExpectedConsequences { get; }

        /// <summary>
        /// The survival-weighted discounted expected consequences per type (absorbing).
        /// </summary>
        public IReadOnlyList<double> AbsorbingPresentValueOfExpectedConsequences { get; }

        /// <summary>
        /// One label per intervention entry, in ascending year order, echoing the applied
        /// house-event states and hazard replacements.
        /// </summary>
        public IReadOnlyList<string> AppliedInterventions { get; }

        /// <summary>
        /// The Excess-stream cumulative expected consequences per type (non-absorbing); empty
        /// when the trajectory does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> ExcessCumulativeExpectedConsequences { get; }

        /// <summary>
        /// The Excess-stream discounted (present-value) expected consequences per type
        /// (non-absorbing); empty when the trajectory does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> ExcessPresentValueOfExpectedConsequences { get; }

        /// <summary>
        /// The Excess-stream equivalent-annual consequences per type; empty when the trajectory
        /// does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> ExcessEquivalentAnnualConsequences { get; }

        /// <summary>
        /// The Excess-stream survival-weighted cumulative expected consequences per type
        /// (absorbing); empty when the trajectory does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> AbsorbingExcessCumulativeExpectedConsequences { get; }

        /// <summary>
        /// The Excess-stream survival-weighted discounted expected consequences per type
        /// (absorbing); empty when the trajectory does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> AbsorbingExcessPresentValueOfExpectedConsequences { get; }

        /// <summary>
        /// The Fail-stream cumulative expected consequences per type (non-absorbing); empty when
        /// the trajectory does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> FailCumulativeExpectedConsequences { get; }

        /// <summary>
        /// The Fail-stream discounted (present-value) expected consequences per type
        /// (non-absorbing); empty when the trajectory does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> FailPresentValueOfExpectedConsequences { get; }

        /// <summary>
        /// The Fail-stream equivalent-annual consequences per type; empty when the trajectory
        /// does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> FailEquivalentAnnualConsequences { get; }

        /// <summary>
        /// The Fail-stream survival-weighted cumulative expected consequences per type
        /// (absorbing); empty when the trajectory does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> AbsorbingFailCumulativeExpectedConsequences { get; }

        /// <summary>
        /// The Fail-stream survival-weighted discounted expected consequences per type
        /// (absorbing); empty when the trajectory does not carry the stream axis.
        /// </summary>
        public IReadOnlyList<double> AbsorbingFailPresentValueOfExpectedConsequences { get; }
    }
}
