using System.Collections.Generic;
using Numerics.Data;
using RMC.TotalRisk.Core.Interfaces;

namespace RMC.TotalRisk.RiskFunctions.Responses.Trees.Support
{
    /// <summary>
    /// Accumulates the live compute dependencies discovered while compiling one tree plan:
    /// controlled tree responses tracked by revision, mutable uncertain tables and ordinary
    /// referenced responses tracked by canonical fingerprint. One collector is shared across
    /// nested and cross-kind compilations so the outermost plan tracks every content edit that
    /// can change its results.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal sealed class TreeDependencyCollector
    {
        /// <summary>Every controlled tree response whose live compute state feeds the plan.</summary>
        internal HashSet<ITreeComputeSource> TreeSources { get; } =
            new HashSet<ITreeComputeSource>(ReferenceEqualityComparer.Instance);

        /// <summary>Every mutable local uncertain table read by the plan.</summary>
        internal HashSet<UncertainOrderedPairedData> Tables { get; } =
            new HashSet<UncertainOrderedPairedData>(ReferenceEqualityComparer.Instance);

        /// <summary>Every ordinary live response referenced by the plan.</summary>
        internal HashSet<IResponseFunction> OrdinaryResponses { get; } =
            new HashSet<IResponseFunction>(ReferenceEqualityComparer.Instance);

        /// <summary>Every live hazard transform carried by a probability-source chain in the plan.</summary>
        internal HashSet<ITransformFunction> TransformFunctions { get; } =
            new HashSet<ITransformFunction>(ReferenceEqualityComparer.Instance);

        /// <summary>Captures the complete dependency state after expansion succeeds.</summary>
        /// <returns>The immutable fingerprinted dependency set.</returns>
        internal TreePlanDependencies CreateDependencies()
        {
            var sources = new ITreeComputeSource[TreeSources.Count];
            TreeSources.CopyTo(sources);
            var tables = new UncertainOrderedPairedData[Tables.Count];
            Tables.CopyTo(tables);
            var ordinary = new IResponseFunction[OrdinaryResponses.Count];
            OrdinaryResponses.CopyTo(ordinary);
            var transforms = new ITransformFunction[TransformFunctions.Count];
            TransformFunctions.CopyTo(transforms);
            return new TreePlanDependencies(sources, tables, ordinary, transforms);
        }
    }
}
