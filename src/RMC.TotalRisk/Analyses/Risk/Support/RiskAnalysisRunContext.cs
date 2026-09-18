using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Analyses.Risk;

namespace RMC.TotalRisk.Analyses.Risk.Support
{
    /// <summary>
    /// Owns the isolated authoring-state snapshot consumed by one risk-analysis run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Component graphs, effective options, consequence declarations, labels, and pinned sampler
    /// seeds are copied together before background computation begins. The engine may mutate its
    /// sampled component copies without observing later edits to the authoring objects.
    /// </para>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal sealed class RiskAnalysisRunContext
    {
        /// <summary>Initializes an isolated run context.</summary>
        /// <param name="components">The cloned components in authoring order.</param>
        /// <param name="options">The cloned effective options.</param>
        /// <param name="additionalConsequenceTypes">The cloned additional consequence declarations.</param>
        /// <param name="specifiedConsequence">The captured primary consequence label.</param>
        /// <param name="consequenceUnit">The captured primary consequence unit.</param>
        /// <param name="pinnedSamplerSeeds">The cloned pinned seed map, or null.</param>
        private RiskAnalysisRunContext(
            List<SystemComponent> components,
            RiskAnalysisOptions options,
            ReadOnlyCollection<ConsequenceTypeDescriptor> additionalConsequenceTypes,
            string specifiedConsequence,
            string consequenceUnit,
            SamplerSeedMap? pinnedSamplerSeeds)
        {
            Components = components;
            Options = options;
            AdditionalConsequenceTypes = additionalConsequenceTypes;
            SpecifiedConsequence = specifiedConsequence;
            ConsequenceUnit = consequenceUnit;
            PinnedSamplerSeeds = pinnedSamplerSeeds;
        }

        /// <summary>Gets the isolated components in their original authoring order.</summary>
        internal List<SystemComponent> Components { get; }

        /// <summary>Gets the isolated effective options.</summary>
        internal RiskAnalysisOptions Options { get; }

        /// <summary>Gets the isolated additional consequence declarations.</summary>
        internal IReadOnlyList<ConsequenceTypeDescriptor> AdditionalConsequenceTypes { get; }

        /// <summary>Gets the captured primary consequence label.</summary>
        internal string SpecifiedConsequence { get; }

        /// <summary>Gets the captured primary consequence unit.</summary>
        internal string ConsequenceUnit { get; }

        /// <summary>Gets the isolated pinned sampler seed map, or null.</summary>
        internal SamplerSeedMap? PinnedSamplerSeeds { get; }

        /// <summary>
        /// Captures all authoring inputs used by a run without changing their declaration order.
        /// </summary>
        /// <param name="components">The authoring components.</param>
        /// <param name="options">The authoring options.</param>
        /// <param name="additionalConsequenceTypes">The authoring additional consequence declarations.</param>
        /// <param name="specifiedConsequence">The authoring primary consequence label.</param>
        /// <param name="consequenceUnit">The authoring primary consequence unit.</param>
        /// <param name="pinnedSamplerSeeds">The authoring pinned seed map, or null.</param>
        /// <returns>The isolated run context.</returns>
        /// <exception cref="ArgumentNullException">Thrown when a required authoring input is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a pinned seed map was captured from a different component count.
        /// </exception>
        internal static RiskAnalysisRunContext Capture(
            IReadOnlyList<SystemComponent> components,
            RiskAnalysisOptions options,
            IReadOnlyList<ConsequenceTypeDescriptor> additionalConsequenceTypes,
            string specifiedConsequence,
            string consequenceUnit,
            SamplerSeedMap? pinnedSamplerSeeds)
        {
            ArgumentNullException.ThrowIfNull(components);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(additionalConsequenceTypes);

            SamplerSeedMap? pinned = CloneSeedMap(pinnedSamplerSeeds);
            if (pinned != null && pinned.ComponentCount != components.Count)
            {
                throw new InvalidOperationException(
                    "The pinned sampler seed map was captured from a different component count. The seed-stable perturbation mode fits the model shape it was captured from — re-capture from a baseline run of the current structure.");
            }

            var componentSnapshot = new List<SystemComponent>(components.Count);
            for (int i = 0; i < components.Count; i++)
            {
                componentSnapshot.Add(components[i].Clone());
                TransferEvaluationAges(components[i], componentSnapshot[i]);
            }

            var optionsSnapshot = new RiskAnalysisOptions(options.ToXElement());
            optionsSnapshot.SetDefaultComponentCount(componentSnapshot.Count);

            var declarationSnapshot = new List<ConsequenceTypeDescriptor>(additionalConsequenceTypes.Count);
            for (int i = 0; i < additionalConsequenceTypes.Count; i++)
            {
                declarationSnapshot.Add(new ConsequenceTypeDescriptor(additionalConsequenceTypes[i].ToXElement()));
            }

            return new RiskAnalysisRunContext(
                componentSnapshot,
                optionsSnapshot,
                declarationSnapshot.AsReadOnly(),
                specifiedConsequence,
                consequenceUnit,
                pinned);
        }

        /// <summary>
        /// Carries deteriorating responses' external evaluation ages onto the run's cloned
        /// instances, matched by function id. The age is runtime-only state — never serialized —
        /// so the snapshot clone alone would reset it; copying it here makes the configured age
        /// govern the run it precedes. A component without deteriorating responses is untouched.
        /// </summary>
        /// <param name="author">The authoring component.</param>
        /// <param name="clone">The run-owned clone.</param>
        private static void TransferEvaluationAges(SystemComponent author, SystemComponent clone)
        {
            Dictionary<Guid, double>? ages = null;
            foreach (var function in author.GetReferencedFunctions())
            {
                if (function is DeterioratingResponse aging)
                {
                    ages ??= new Dictionary<Guid, double>();
                    ages[aging.Id] = aging.EvaluationAge;
                }
            }
            if (ages == null) return;

            foreach (var function in clone.GetReferencedFunctions())
            {
                if (function is DeterioratingResponse aging && ages.TryGetValue(aging.Id, out double age))
                {
                    aging.EvaluationAge = age;
                }
            }
        }

        /// <summary>Creates an isolated copy of a sampler seed map.</summary>
        /// <param name="source">The source seed map, or null.</param>
        /// <returns>The isolated seed map, or null.</returns>
        private static SamplerSeedMap? CloneSeedMap(SamplerSeedMap? source)
        {
            if (source == null)
            {
                return null;
            }

            var components = new List<int[]>(source.ComponentSeeds.Count);
            for (int i = 0; i < source.ComponentSeeds.Count; i++)
            {
                components.Add((int[])source.ComponentSeeds[i].Clone());
            }
            return new SamplerSeedMap(components, source.JointSeedBase);
        }
    }
}
