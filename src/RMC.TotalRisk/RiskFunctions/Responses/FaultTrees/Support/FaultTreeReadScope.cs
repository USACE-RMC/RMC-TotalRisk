using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support
{
    /// <summary>
    /// The ambient per-thread deserialization scope that unifies repeated self-contained embeds
    /// of one external fault-tree function onto a single live instance. Without unification, two
    /// shared-logical transfers that embed the same function would deserialize into disjoint
    /// object graphs, silently splitting their unified Boolean variables and moving both the
    /// sampled values and the canonical identity away from the live and by-reference forms.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal static class FaultTreeReadScope
    {
        /// <summary>The materialized embedded functions of the active outermost read, by persistent id.</summary>
        [ThreadStatic]
        private static Dictionary<Guid, (XElement Content, FaultTreeResponse Instance)>? _embedded;

        /// <summary>The nested read depth of the current thread.</summary>
        [ThreadStatic]
        private static int _depth;

        /// <summary>Enters one fault-tree read, creating the unification table at the outermost read.</summary>
        internal static void Enter()
        {
            _depth++;
            _embedded ??= new Dictionary<Guid, (XElement, FaultTreeResponse)>();
        }

        /// <summary>Exits one fault-tree read, releasing the unification table at the outermost read.</summary>
        internal static void Exit()
        {
            if (--_depth <= 0)
            {
                _depth = 0;
                _embedded = null;
            }
        }

        /// <summary>Materializes one embedded function, unifying repeated embeds of the same id.</summary>
        /// <param name="id">The embedded function's persistent id.</param>
        /// <param name="content">The embedded serialized function.</param>
        /// <param name="factory">The materialization callback used on first encounter.</param>
        /// <returns>The unified instance, or null when the factory could not materialize one.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the same persistent id is embedded twice with divergent content.
        /// </exception>
        internal static FaultTreeResponse? GetOrAdd(Guid id, XElement content,
            Func<FaultTreeResponse?> factory)
        {
            if (_embedded == null) return factory();
            if (_embedded.TryGetValue(id, out (XElement Content, FaultTreeResponse Instance) existing))
            {
                if (!XNode.DeepEquals(existing.Content, content))
                {
                    throw new InvalidOperationException(
                        $"The serialized document embeds fault-tree function '{id:D}' more than once " +
                        "with divergent content. Repeated self-contained embeds must be identical " +
                        "snapshots of one function.");
                }
                return existing.Instance;
            }

            FaultTreeResponse? instance = factory();
            if (instance != null) _embedded.Add(id, (content, instance));
            return instance;
        }
    }
}
