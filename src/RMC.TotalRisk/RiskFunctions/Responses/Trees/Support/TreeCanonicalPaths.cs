using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.RiskFunctions.Responses.Trees.Support
{
    /// <summary>
    /// Assigns metadata-free canonical occurrence paths over an expanded tree whose sibling sets
    /// have already been sorted by projected identity. Content-identical siblings receive
    /// consecutive occurrence ordinals, so identical occurrences stay independent but reproducible.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal static class TreeCanonicalPaths
    {
        /// <summary>Assigns stable paths from the root using the shared <c>R</c> origin.</summary>
        /// <typeparam name="T">The expanded occurrence-node type.</typeparam>
        /// <param name="root">The expanded root occurrence.</param>
        /// <param name="children">Reads one occurrence's canonical-order children.</param>
        /// <param name="identityToken">Reads one occurrence's projected identity token.</param>
        /// <param name="assignPath">Stores one occurrence's assigned canonical path.</param>
        internal static void Assign<T>(T root, Func<T, IReadOnlyList<T>> children,
            Func<T, string> identityToken, Action<T, string> assignPath) where T : class
        {
            AssignCore(root, "R", children, identityToken, assignPath);
        }

        /// <summary>Assigns one occurrence's path and recurses over its sorted children.</summary>
        /// <typeparam name="T">The expanded occurrence-node type.</typeparam>
        /// <param name="node">The occurrence receiving the path.</param>
        /// <param name="path">The assigned canonical path.</param>
        /// <param name="children">Reads one occurrence's canonical-order children.</param>
        /// <param name="identityToken">Reads one occurrence's projected identity token.</param>
        /// <param name="assignPath">Stores one occurrence's assigned canonical path.</param>
        private static void AssignCore<T>(T node, string path, Func<T, IReadOnlyList<T>> children,
            Func<T, string> identityToken, Action<T, string> assignPath) where T : class
        {
            assignPath(node, path);
            string? previousToken = null;
            int occurrence = -1;
            IReadOnlyList<T> nodeChildren = children(node);
            for (int i = 0; i < nodeChildren.Count; i++)
            {
                T child = nodeChildren[i];
                string token = identityToken(child);
                if (!string.Equals(previousToken, token, StringComparison.Ordinal))
                {
                    previousToken = token;
                    occurrence = 0;
                }
                else
                {
                    occurrence++;
                }
                AssignCore(child, $"{path}/{token}:{occurrence}", children, identityToken, assignPath);
            }
        }
    }
}
