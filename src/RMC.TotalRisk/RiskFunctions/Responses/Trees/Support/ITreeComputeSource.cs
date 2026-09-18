using System;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RMC.TotalRisk.RiskFunctions.Responses.Trees.Support
{
    /// <summary>
    /// The internal compute-state contract shared by controlled tree responses. A tree response is
    /// both a dependency <b>source</b> (its compute revision and staleness event let dependent
    /// plans track it without fingerprint hashing) and a plan <b>owner</b> (its dependency-changed
    /// handlers receive staleness signals from the trees, tables, and ordinary functions its
    /// compiled plan reads).
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal interface ITreeComputeSource
    {
        /// <summary>The monotonic compute revision observed by dependent tree plans.</summary>
        long ComputeRevision { get; }

        /// <summary>Raised after this source's compute state becomes stale.</summary>
        event EventHandler? ComputeStateChanged;

        /// <summary>Invalidates the owner when a tracked tree response's compute state changes.</summary>
        /// <param name="sender">The originating tree response.</param>
        /// <param name="e">The event payload.</param>
        void DependencyTreeComputeChanged(object? sender, EventArgs e);

        /// <summary>Invalidates the owner when a tracked uncertain table changes membership.</summary>
        /// <param name="sender">The originating table.</param>
        /// <param name="e">The collection-change payload.</param>
        void DependencyTableCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e);

        /// <summary>Invalidates the owner for compute-relevant edits to an ordinary referenced function.</summary>
        /// <param name="sender">The originating function.</param>
        /// <param name="e">The property-change payload.</param>
        void DependencyFunctionPropertyChanged(object? sender, PropertyChangedEventArgs e);
    }
}
