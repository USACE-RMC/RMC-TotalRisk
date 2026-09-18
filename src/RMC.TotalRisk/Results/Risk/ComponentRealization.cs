using System;
using System.Collections.Generic;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Support;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// The per-realization results of one system component: its five loss exceedance curve
    /// streams per consequence type, the per-failure-mode realizations, and the
    /// hazard/consequence extent tracking.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The multi-consequence axis: <see cref="Curves"/> and the
    /// <see cref="MinN"/>/<see cref="MaxN"/> extents carry the primary consequence type;
    /// <see cref="AdditionalCurves"/> and the parallel <see cref="AdditionalMinN"/>/
    /// <see cref="AdditionalMaxN"/> extents carry type k at entry k − 1, in declared order.
    /// Per-type extents exist because consequence types live on different magnitude scales
    /// (lives versus dollars) — every type gets its own percentile grid.
    /// </para>
    /// </remarks>
    public class ComponentRealization
    {
        /// <summary>
        /// Initializes an empty component realization.
        /// </summary>
        public ComponentRealization()
        {
            Curves = new Curves();
            FailureModes = new List<FailureModeRealization>();
            AdditionalCurves = new List<Curves>();
            AdditionalMinN = new List<double>();
            AdditionalMaxN = new List<double>();
        }

        /// <summary>
        /// Initializes a component realization with the given number of empty failure-mode
        /// realizations.
        /// </summary>
        /// <param name="failureModes">The failure-mode count. Must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the count is negative.</exception>
        public ComponentRealization(int failureModes)
        {
            if (failureModes < 0) throw new ArgumentOutOfRangeException(nameof(failureModes), "The failure-mode count must not be negative.");
            Curves = new Curves();
            FailureModes = new List<FailureModeRealization>(failureModes);
            for (int i = 0; i < failureModes; i++)
            {
                FailureModes.Add(new FailureModeRealization());
            }
            AdditionalCurves = new List<Curves>();
            AdditionalMinN = new List<double>();
            AdditionalMaxN = new List<double>();
        }

        /// <summary>
        /// The component's display name, carried for results labeling.
        /// </summary>
        public string Name { get; set; } = "Component Risk";

        /// <summary>
        /// The per-failure-mode realizations, in the component's projected failure-mode order.
        /// </summary>
        public List<FailureModeRealization> FailureModes { get; set; }

        /// <summary>
        /// The component-level five loss exceedance curve streams of the primary consequence
        /// type.
        /// </summary>
        public Curves Curves { get; set; }

        /// <summary>
        /// The component-level five-stream curve sets of the additional consequence types, in
        /// declared order (entry k − 1 is type k). Empty on a single-type analysis.
        /// </summary>
        public List<Curves> AdditionalCurves { get; set; }

        /// <summary>
        /// This component's attributed contribution to the system's risk on the primary
        /// consequence type (the % contribution diagnostic; see
        /// <see cref="RiskContribution"/>): under the additive method the exact Shapley split of
        /// the independent failure union with the component's own means (means add exactly under
        /// the convolution); under the joint method the per-combination attribution accumulated
        /// through the VEGAS passes; on a single-component system the component's own totals
        /// (a 100% share). Null until finalized — the "not computed" state older payloads and
        /// band realizations carry.
        /// </summary>
        public RiskContribution? SystemContribution { get; set; }

        /// <summary>
        /// The attributed system contributions for the additional consequence types, in declared
        /// order (entry k − 1 is type k; entries may be null when not computed). Empty on a
        /// single-type analysis.
        /// </summary>
        public List<RiskContribution?> AdditionalSystemContributions { get; set; } = new List<RiskContribution?>();

        /// <summary>
        /// The smallest consequence observed for this component in this realization (primary
        /// consequence type).
        /// </summary>
        public double MinN { get; set; } = double.MaxValue;

        /// <summary>
        /// The largest consequence observed for this component in this realization (primary
        /// consequence type).
        /// </summary>
        public double MaxN { get; set; } = double.MinValue;

        /// <summary>
        /// The smallest consequence observed per additional consequence type, parallel to
        /// <see cref="AdditionalCurves"/>.
        /// </summary>
        public List<double> AdditionalMinN { get; set; }

        /// <summary>
        /// The largest consequence observed per additional consequence type, parallel to
        /// <see cref="AdditionalCurves"/>.
        /// </summary>
        public List<double> AdditionalMaxN { get; set; }

        /// <summary>
        /// Ensures the additional consequence-type slots exist on this component and every
        /// failure-mode realization (curve sets and extent slots), creating any missing entries.
        /// </summary>
        /// <param name="count">The number of additional consequence types. Must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the count is negative.</exception>
        public void EnsureAdditionalCurves(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "The additional consequence-type count must not be negative.");
            while (AdditionalCurves.Count < count)
            {
                AdditionalCurves.Add(new Curves());
                AdditionalMinN.Add(double.MaxValue);
                AdditionalMaxN.Add(double.MinValue);
            }
            for (int i = 0; i < FailureModes.Count; i++)
            {
                FailureModes[i].EnsureAdditionalCurves(count);
            }
        }

        /// <summary>
        /// The smallest hazard level observed for this component in this realization.
        /// </summary>
        public double MinH { get; set; } = double.MaxValue;

        /// <summary>
        /// The largest hazard level observed for this component in this realization.
        /// </summary>
        public double MaxH { get; set; } = double.MinValue;

        /// <summary>
        /// Applies the run's optional-measure selection to every curve set this realization owns.
        /// </summary>
        /// <param name="measures">The measures to compute.</param>
        public void SetMeasureOptions(RiskMeasureOptions measures)
        {
            Curves.SetMeasureOptions(measures);
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].SetMeasureOptions(measures);
            }
            for (int i = 0; i < FailureModes.Count; i++)
            {
                FailureModes[i].SetMeasureOptions(measures);
            }
        }

        /// <summary>
        /// Applies the quadrature ledger's masses to the component and every failure mode, across
        /// every consequence type.
        /// </summary>
        /// <param name="ledger">The pass's quadrature ledger, sealed.</param>
        internal void ApplyRecordedMass(QuadratureMassLedger ledger)
        {
            Curves.ApplyRecordedMass(ledger);
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].ApplyRecordedMass(ledger);
            }
            for (int i = 0; i < FailureModes.Count; i++)
            {
                FailureModes[i].ApplyRecordedMass(ledger);
            }
        }

        /// <summary>
        /// Builds the exact curves and moments on the component and every failure mode, across
        /// every consequence type.
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
            for (int i = 0; i < FailureModes.Count; i++)
            {
                FailureModes[i].CreateCurves(outputLength);
            }
        }

        /// <summary>
        /// Builds the component-level risk profiles across every consequence type, and — when
        /// requested — the failure-mode profiles. Ensemble realizations skip the mode profiles
        /// (v1.0 banded component profiles only); the engine builds mode profiles on the mean
        /// pass, where they cost one pass over already-recorded points.
        /// </summary>
        /// <param name="includeFailureModes">
        /// True to also build every failure mode's profiles (the engine's mean-pass behavior).
        /// </param>
        public void CreateProfiles(bool includeFailureModes = false)
        {
            Curves.CreateProfiles(primaryType: true);
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].CreateProfiles();
            }
            if (includeFailureModes)
            {
                for (int i = 0; i < FailureModes.Count; i++)
                {
                    FailureModes[i].CreateProfiles();
                }
            }
        }

        /// <summary>
        /// Computes the risk-measure catalog on the component (with the hazard threshold) and on
        /// every failure mode (without — v1.0 behavior), across every consequence type. The
        /// primary consequence threshold is declared in the primary type's units; each
        /// additional type reads its own declared threshold or NaN when none was
        /// declared.
        /// </summary>
        /// <param name="consequenceThreshold">The consequence threshold for the primary type's assurance measure.</param>
        /// <param name="alpha">The exceedance level for value-at-risk and conditional value-at-risk.</param>
        /// <param name="hazardThreshold">The component hazard threshold, or NaN when none applies.</param>
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
            for (int i = 0; i < FailureModes.Count; i++)
            {
                FailureModes[i].ComputeRiskMeasures(consequenceThreshold, alpha, additionalThresholds: additionalThresholds);
            }
        }

        /// <summary>
        /// Finalizes every failure mode's accumulated contribution samples into their stored
        /// per-type contributions. No-ops for modes that accumulated nothing.
        /// </summary>
        /// <param name="ledger">
        /// The sealed quadrature mass ledger on the one-dimensional path (each accumulated
        /// abscissa's mass is read from it); null on the VEGAS path, where the accumulated
        /// probability coordinates are recorded weights scaled by <paramref name="scale"/>.
        /// </param>
        /// <param name="scale">The VEGAS self-normalization scale (ignored when a ledger is supplied).</param>
        internal void FinalizeContributions(QuadratureMassLedger? ledger, double scale = 1d)
        {
            for (int i = 0; i < FailureModes.Count; i++)
            {
                FailureModes[i].FinalizeContributions(ledger, scale);
            }
        }

        /// <summary>
        /// Scales the recorded risk-point masses on the component and every failure mode, across
        /// every consequence type (the joint system path's VEGAS weight self-normalization).
        /// </summary>
        /// <param name="factor">The positive scale factor.</param>
        internal void ScaleRecordedMass(double factor)
        {
            Curves.ScaleRecordedMass(factor);
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].ScaleRecordedMass(factor);
            }
            for (int i = 0; i < FailureModes.Count; i++)
            {
                FailureModes[i].ScaleRecordedMass(factor);
            }
        }

        /// <summary>
        /// Clears the recorded risk points on the component and every failure mode, across every
        /// consequence type — call only after post-processing.
        /// </summary>
        public void DumpMemory()
        {
            Curves.DumpMemory();
            for (int k = 0; k < AdditionalCurves.Count; k++)
            {
                AdditionalCurves[k].DumpMemory();
            }
            for (int i = 0; i < FailureModes.Count; i++)
            {
                FailureModes[i].DumpMemory();
            }
        }
    }
}
