using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The per-realization results of one failure mode: its five loss exceedance curve streams
    /// per consequence type.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The multi-consequence axis: <see cref="Curves"/> carries the
    /// primary consequence type (position 0 of the analysis's declared axis) and
    /// <see cref="AdditionalCurves"/> entry k − 1 carries type k, in declared order — the same
    /// append-only convention every results container uses, so single-type results are
    /// shape-identical to single-type payloads. The failure-mode scope is the
    /// per-consequence-terminal
    /// scope: under the cascade end-state design each terminal is one end state, so
    /// this container is already the end-state results scope.
    /// </para>
    /// </remarks>
    public class FailureModeRealization
    {
        /// <summary>
        /// Initializes an empty failure-mode realization.
        /// </summary>
        public FailureModeRealization()
        {
            Curves = new Curves();
            AdditionalCurves = new List<Curves>();
        }

        /// <summary>
        /// The failure mode's display name, carried for results labeling. Stamped by the engine
        /// from the projected end state (the consequence terminal's element name when the mode
        /// came from a graph).
        /// </summary>
        public string Name { get; set; } = "Failure Mode Risk";

        /// <summary>
        /// The end state's branch path descriptor — each stage's response name with its branch
        /// polarity, e.g. <c>"Initiation[Fail] → Progression[NonFail]"</c> (append-only; null
        /// on earlier payloads and unstamped realizations).
        /// </summary>
        public string? PathLabel { get; set; }

        /// <summary>
        /// The five loss exceedance curve streams of the primary consequence type.
        /// </summary>
        public Curves Curves { get; set; }

        /// <summary>
        /// The five-stream curve sets of the additional consequence types, in declared order
        /// (entry k − 1 is type k). Empty on a single-type analysis.
        /// </summary>
        public List<Curves> AdditionalCurves { get; set; }

        /// <summary>
        /// The mode's combination-adjusted Fail and Excess curves for the primary consequence type —
        /// its share after the component's combination method has resolved the modes against one
        /// another, so the modes sum to the component total. Null unless
        /// <c>RiskAnalysisOptions.OutputAdjustedFailureModeCurves</c> is set.
        /// </summary>
        public Curves? AdjustedCurves { get; set; }

        /// <summary>
        /// The combination-adjusted curves of the additional consequence types, in declared order
        /// (entry k − 1 is type k). Empty unless adjusted output is requested.
        /// </summary>
        public List<Curves> AdditionalAdjustedCurves { get; set; } = new List<Curves>();

        /// <summary>
        /// Creates the adjusted curve sets for the primary and additional consequence types.
        /// </summary>
        /// <param name="additionalTypes">The number of additional consequence types.</param>
        public void EnableAdjustedCurves(int additionalTypes)
        {
            AdjustedCurves ??= new Curves();
            while (AdditionalAdjustedCurves.Count < additionalTypes)
            {
                AdditionalAdjustedCurves.Add(new Curves());
            }
        }

        /// <summary>
        /// The adjusted curve set for a consequence type, or null when adjusted output is off.
        /// </summary>
        /// <param name="typeIndex">The consequence type index; zero is the primary type.</param>
        /// <returns>The adjusted curve set, or null.</returns>
        public Curves? AdjustedCurvesFor(int typeIndex)
        {
            if (typeIndex == 0) return AdjustedCurves;
            int index = typeIndex - 1;
            return index < AdditionalAdjustedCurves.Count ? AdditionalAdjustedCurves[index] : null;
        }

        /// <summary>
        /// This mode's attributed contribution to the component's risk on the primary
        /// consequence type (the % contribution diagnostic; see
        /// <see cref="RiskContribution"/> for the attribution scheme). Null until finalized —
        /// the "not computed" state older payloads and band realizations carry.
        /// </summary>
        public RiskContribution? Contribution { get; set; }

        /// <summary>
        /// The attributed contributions for the additional consequence types, in declared order
        /// (entry k − 1 is type k; entries may be null when not computed). Empty on a
        /// single-type analysis.
        /// </summary>
        public List<RiskContribution?> AdditionalContributions { get; set; } = new List<RiskContribution?>();

        /// <summary>
        /// The runtime per-type contribution accumulators (entry 0 = the primary type), created
        /// lazily by the first recorded sample. Never serialized; cleared by
        /// <see cref="DumpMemory"/>.
        /// </summary>
        [JsonIgnore]
        internal ContributionAccumulator?[]? ContributionAccumulators { get; private set; }

        /// <summary>
        /// Appends one recording evaluation's attributed contribution sample for a consequence
        /// type (the sampled-component kernels' sink).
        /// </summary>
        /// <param name="typeIndex">The consequence-type position (0 is the primary).</param>
        /// <param name="probability">The evaluation's probability coordinate (non-exceedance on the 1D path; the VEGAS weight on the joint path).</param>
        /// <param name="probabilityShare">The attributed probability.</param>
        /// <param name="failureShare">The attributed probability × failure consequence.</param>
        /// <param name="excessShare">The attributed probability × excess consequence.</param>
        internal void AddContributionSample(int typeIndex, double probability, double probabilityShare, double failureShare, double excessShare)
        {
            var accumulators = ContributionAccumulators ??= new ContributionAccumulator?[1 + AdditionalCurves.Count];
            (accumulators[typeIndex] ??= new ContributionAccumulator()).Add(probability, probabilityShare, failureShare, excessShare);
        }

        /// <summary>
        /// Finalizes the accumulated contribution samples into the stored per-type
        /// contributions under the caller's mass regime. No-ops when nothing was accumulated.
        /// </summary>
        /// <param name="ledger">
        /// The sealed quadrature mass ledger on the one-dimensional path (each accumulated
        /// abscissa's mass is read from it); null on the VEGAS path, where the accumulated
        /// probability coordinates are recorded weights scaled by <paramref name="scale"/>.
        /// </param>
        /// <param name="scale">The VEGAS self-normalization scale (ignored when a ledger is supplied).</param>
        internal void FinalizeContributions(QuadratureMassLedger? ledger, double scale = 1d)
        {
            var accumulators = ContributionAccumulators;
            if (accumulators == null) return;
            for (int k = 0; k < accumulators.Length; k++)
            {
                var accumulator = accumulators[k];
                if (accumulator == null) continue;
                var contribution = ledger != null ? accumulator.FinalizeFromLedger(ledger) : accumulator.FinalizeDirect(scale);
                if (k == 0)
                {
                    Contribution = contribution;
                }
                else
                {
                    while (AdditionalContributions.Count < k)
                    {
                        AdditionalContributions.Add(null);
                    }
                    AdditionalContributions[k - 1] = contribution;
                }
            }
        }

        /// <summary>
        /// Ensures the additional consequence-type slots exist (one curve set per type beyond
        /// the primary), creating any missing entries.
        /// </summary>
        /// <param name="count">The number of additional consequence types. Must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the count is negative.</exception>
        public void EnsureAdditionalCurves(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "The additional consequence-type count must not be negative.");
            while (AdditionalCurves.Count < count)
            {
                AdditionalCurves.Add(new Curves());
            }
        }

        /// <summary>
        /// Applies the run's optional-measure selection to every curve set this mode owns.
        /// </summary>
        /// <param name="measures">The measures to compute.</param>
        public void SetMeasureOptions(RiskMeasureOptions measures)
        {
            Curves.SetMeasureOptions(measures);
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].SetMeasureOptions(measures);
            }
            AdjustedCurves?.SetMeasureOptions(measures);
            for (int k = 0; k < AdditionalAdjustedCurves.Count; k++)
            {
                AdditionalAdjustedCurves[k].SetMeasureOptions(measures);
            }
        }

        /// <summary>
        /// Applies the quadrature ledger's masses to every consequence type.
        /// </summary>
        /// <param name="ledger">The pass's quadrature ledger, sealed.</param>
        internal void ApplyRecordedMass(QuadratureMassLedger ledger)
        {
            Curves.ApplyRecordedMass(ledger);
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].ApplyRecordedMass(ledger);
            }
            AdjustedCurves?.ApplyRecordedMass(ledger);
            for (int k = 0; k < AdditionalAdjustedCurves.Count; k++)
            {
                AdditionalAdjustedCurves[k].ApplyRecordedMass(ledger);
            }
        }

        /// <summary>
        /// Builds the exact curves and moments on every consequence type.
        /// </summary>
        /// <param name="outputLength">The output resolution of the stored curves.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the output length is less than two.</exception>
        public void CreateCurves(int outputLength)
        {
            Curves.CreateCurves(outputLength);
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].CreateCurves(outputLength);
            }
            AdjustedCurves?.CreateCurves(outputLength);
            for (int k = 0; k < AdditionalAdjustedCurves.Count; k++)
            {
                AdditionalAdjustedCurves[k].CreateCurves(outputLength);
            }
        }

        /// <summary>
        /// Builds the risk profiles on every consequence type. The failure-stream profiles build
        /// on the primary type's Fail stream only; at mode scope they carry the mode's raw
        /// sampled response probability — the marginal semantics the mode-level results already
        /// use, NOT the combination-adjusted share (the % contribution diagnostic covers
        /// adjusted attribution at scalar level).
        /// </summary>
        public void CreateProfiles()
        {
            Curves.CreateProfiles(primaryType: true);
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].CreateProfiles();
            }
        }

        /// <summary>
        /// Computes the risk-measure catalog on every consequence type. The primary consequence
        /// threshold is declared in the primary type's units; each additional type reads its own
        /// declared threshold or NaN when none was declared.
        /// </summary>
        /// <param name="consequenceThreshold">The consequence threshold for the primary type's assurance measure.</param>
        /// <param name="alpha">The exceedance level for value-at-risk and conditional value-at-risk.</param>
        /// <param name="hazardThreshold">The hazard threshold, or NaN when none applies.</param>
        /// <param name="additionalThresholds">
        /// The declared per-type thresholds for the additional consequence types (entry k for
        /// <see cref="AdditionalCurves"/> position k), or null for NaN throughout.
        /// </param>
        public void ComputeRiskMeasures(double consequenceThreshold, double alpha, double hazardThreshold = double.NaN,
            IReadOnlyList<double>? additionalThresholds = null)
        {
            Curves.ComputeRiskMeasures(consequenceThreshold, alpha, hazardThreshold);
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                double typeThreshold = additionalThresholds != null && k < additionalThresholds.Count ? additionalThresholds[k] : double.NaN;
                AdditionalCurves[k].ComputeRiskMeasures(typeThreshold, alpha, hazardThreshold);
            }
        }

        /// <summary>
        /// Clears the recorded risk points and the contribution accumulators on every
        /// consequence type — call only after post-processing.
        /// </summary>
        public void DumpMemory()
        {
            Curves.DumpMemory();
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].DumpMemory();
            }
            AdjustedCurves?.DumpMemory();
            for (int k = 0; k < AdditionalAdjustedCurves.Count; k++)
            {
                AdditionalAdjustedCurves[k].DumpMemory();
            }
            var accumulators = ContributionAccumulators;
            if (accumulators != null)
            {
                for (int k = 0; k < accumulators.Length; k++)
                {
                    accumulators[k]?.Clear();
                }
                ContributionAccumulators = null;
            }
        }

        /// <summary>
        /// Scales the recorded risk-point masses on every consequence type (the joint system
        /// path's VEGAS weight self-normalization).
        /// </summary>
        /// <param name="factor">The positive scale factor.</param>
        internal void ScaleRecordedMass(double factor)
        {
            Curves.ScaleRecordedMass(factor);
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].ScaleRecordedMass(factor);
            }
            AdjustedCurves?.ScaleRecordedMass(factor);
            for (int k = 0; k < AdditionalAdjustedCurves.Count; k++)
            {
                AdditionalAdjustedCurves[k].ScaleRecordedMass(factor);
            }
        }
    }
}
