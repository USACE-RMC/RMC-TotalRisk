using System;
using System.Collections.Generic;
using System.Linq;
using Numerics.Data;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Interfaces;

namespace RMC.TotalRisk.RiskFunctions.Responses.Trees.Support
{
    /// <summary>
    /// Immutable dependency fingerprints and subscriptions for one compiled tree plan. Controlled
    /// tree-response edits invalidate by revision/event; mutable Numerics table content and
    /// ordinary live response content also retain a defensive canonical fingerprint so silent
    /// in-place edits cannot reuse stale instructions.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal sealed class TreePlanDependencies
    {
        /// <summary>The empty dependency set used only by transient nested compiler products.</summary>
        internal static TreePlanDependencies Empty { get; } =
            new TreePlanDependencies(Array.Empty<ITreeComputeSource>(),
                Array.Empty<UncertainOrderedPairedData>(), Array.Empty<IResponseFunction>(),
                Array.Empty<ITransformFunction>());

        /// <summary>Initializes and fingerprints one complete dependency set.</summary>
        /// <param name="treeSources">The controlled tree responses tracked by compute revision.</param>
        /// <param name="tables">The mutable local uncertain tables tracked by canonical fingerprint.</param>
        /// <param name="ordinaryResponses">The ordinary live responses tracked by canonical fingerprint.</param>
        /// <param name="transformFunctions">The live probability-source hazard transforms tracked by canonical fingerprint.</param>
        internal TreePlanDependencies(IReadOnlyList<ITreeComputeSource> treeSources,
            IReadOnlyList<UncertainOrderedPairedData> tables,
            IReadOnlyList<IResponseFunction> ordinaryResponses,
            IReadOnlyList<ITransformFunction> transformFunctions)
        {
            _treeSources = treeSources
                .Select(source => new TreeSourceVersion(source, source.ComputeRevision))
                .ToArray();
            _tables = tables
                .Select(table => new TableVersion(table, HashTable(table)))
                .ToArray();
            _ordinaryResponses = ordinaryResponses
                .Select(function => new FunctionVersion(function, function.CanonicalHash()))
                .ToArray();
            _transformFunctions = transformFunctions
                .Select(function => new FunctionVersion(function, function.CanonicalHash()))
                .ToArray();
        }

        /// <summary>The controlled tree-response revision snapshots.</summary>
        private readonly TreeSourceVersion[] _treeSources;

        /// <summary>The mutable local table snapshots.</summary>
        private readonly TableVersion[] _tables;

        /// <summary>The ordinary live response snapshots.</summary>
        private readonly FunctionVersion[] _ordinaryResponses;

        /// <summary>The live probability-source hazard-transform snapshots.</summary>
        private readonly FunctionVersion[] _transformFunctions;

        /// <summary>Checks whether every compute dependency still matches this plan.</summary>
        /// <returns>True when the plan may be reused.</returns>
        internal bool IsCurrent()
        {
            for (int i = 0; i < _treeSources.Length; i++)
            {
                if (_treeSources[i].Revision
                    != _treeSources[i].Source.ComputeRevision) return false;
            }
            try
            {
                for (int i = 0; i < _tables.Length; i++)
                {
                    if (!_tables[i].Hash.AsSpan().SequenceEqual(
                        HashTable(_tables[i].Table))) return false;
                }
                for (int i = 0; i < _ordinaryResponses.Length; i++)
                {
                    if (!_ordinaryResponses[i].Hash.AsSpan().SequenceEqual(
                        _ordinaryResponses[i].Function.CanonicalHash())) return false;
                }
                for (int i = 0; i < _transformFunctions.Length; i++)
                {
                    if (!_transformFunctions[i].Hash.AsSpan().SequenceEqual(
                        _transformFunctions[i].Function.CanonicalHash())) return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException
                || ex is InvalidOperationException || ex is NotSupportedException)
            {
                return false;
            }
            return true;
        }

        /// <summary>Subscribes one cache owner to every external compute dependency.</summary>
        /// <param name="owner">The plan owner receiving staleness signals.</param>
        internal void Attach(ITreeComputeSource owner)
        {
            for (int i = 0; i < _treeSources.Length; i++)
            {
                if (!ReferenceEquals(_treeSources[i].Source, owner))
                    _treeSources[i].Source.ComputeStateChanged += owner.DependencyTreeComputeChanged;
            }
            for (int i = 0; i < _tables.Length; i++)
                _tables[i].Table.CollectionChanged += owner.DependencyTableCollectionChanged;
            for (int i = 0; i < _ordinaryResponses.Length; i++)
                _ordinaryResponses[i].Function.PropertyChanged += owner.DependencyFunctionPropertyChanged;
            for (int i = 0; i < _transformFunctions.Length; i++)
                _transformFunctions[i].Function.PropertyChanged += owner.DependencyFunctionPropertyChanged;
        }

        /// <summary>Removes every dependency subscription held for one cache owner.</summary>
        /// <param name="owner">The plan owner that subscribed through <see cref="Attach"/>.</param>
        internal void Detach(ITreeComputeSource owner)
        {
            for (int i = 0; i < _treeSources.Length; i++)
            {
                if (!ReferenceEquals(_treeSources[i].Source, owner))
                    _treeSources[i].Source.ComputeStateChanged -= owner.DependencyTreeComputeChanged;
            }
            for (int i = 0; i < _tables.Length; i++)
                _tables[i].Table.CollectionChanged -= owner.DependencyTableCollectionChanged;
            for (int i = 0; i < _ordinaryResponses.Length; i++)
                _ordinaryResponses[i].Function.PropertyChanged -= owner.DependencyFunctionPropertyChanged;
            for (int i = 0; i < _transformFunctions.Length; i++)
                _transformFunctions[i].Function.PropertyChanged -= owner.DependencyFunctionPropertyChanged;
        }

        /// <summary>Hashes the complete mutable table definition.</summary>
        /// <param name="table">The uncertain table.</param>
        /// <returns>The canonical fingerprint.</returns>
        private static byte[] HashTable(UncertainOrderedPairedData table)
        {
            return CanonicalContentHasher.Hash(table.SaveToXElement(),
                CanonicalizationRules.ModelRules);
        }

        /// <summary>One controlled tree-response revision snapshot.</summary>
        private readonly record struct TreeSourceVersion(ITreeComputeSource Source, long Revision);

        /// <summary>One mutable uncertain-table fingerprint.</summary>
        private readonly record struct TableVersion(UncertainOrderedPairedData Table, byte[] Hash);

        /// <summary>One live risk-function fingerprint (an ordinary response or a chain transform).</summary>
        private readonly record struct FunctionVersion(IRiskFunction Function, byte[] Hash);
    }
}
