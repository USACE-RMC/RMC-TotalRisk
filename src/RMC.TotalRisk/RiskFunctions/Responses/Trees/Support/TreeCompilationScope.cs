using System;
using System.Collections.Generic;

namespace RMC.TotalRisk.RiskFunctions.Responses.Trees.Support
{
    /// <summary>
    /// The ambient per-thread function-plus-node recursion stack shared by every tree compiler.
    /// One stack serves all tree kinds, so a reference cycle that crosses kinds is detected with a
    /// complete path instead of overflowing the call stack. Frames are pushed and popped inside
    /// <c>try</c>/<c>finally</c> blocks, so the stack is empty between top-level compilations.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal static class TreeCompilationScope
    {
        /// <summary>The per-thread frame storage, created on first use and reused thereafter.</summary>
        [ThreadStatic]
        private static List<TreeCompilationFrame>? _frames;

        /// <summary>Gets the current thread's frame list.</summary>
        private static List<TreeCompilationFrame> Frames => _frames ??= new List<TreeCompilationFrame>();

        /// <summary>Finds the active frame matching one function instance and node id.</summary>
        /// <param name="function">The tree response owning the node.</param>
        /// <param name="nodeId">The authored node id.</param>
        /// <returns>The frame index, or -1 when the pair is not on the active stack.</returns>
        internal static int IndexOf(object function, Guid nodeId)
        {
            List<TreeCompilationFrame> frames = Frames;
            for (int i = 0; i < frames.Count; i++)
            {
                if (ReferenceEquals(frames[i].Function, function) && frames[i].NodeId == nodeId)
                    return i;
            }
            return -1;
        }

        /// <summary>Pushes one active frame; the caller must pop inside a <c>finally</c> block.</summary>
        /// <param name="function">The tree response owning the node.</param>
        /// <param name="node">The authored node.</param>
        /// <param name="nodeId">The authored node id.</param>
        /// <param name="describer">The frame's shared diagnostic renderer.</param>
        internal static void Push(object function, object node, Guid nodeId, TreeFrameDescriber describer)
        {
            Frames.Add(new TreeCompilationFrame(function, node, nodeId, describer));
        }

        /// <summary>Pops the most recent active frame.</summary>
        /// <exception cref="InvalidOperationException">Thrown when no frame is active.</exception>
        internal static void Pop()
        {
            List<TreeCompilationFrame> frames = Frames;
            if (frames.Count == 0)
                throw new InvalidOperationException("The tree compilation scope is unbalanced.");
            frames.RemoveAt(frames.Count - 1);
        }

        /// <summary>Builds a complete cycle diagnostic from the active stack plus the repeated entry.</summary>
        /// <param name="function">The repeated frame's tree response.</param>
        /// <param name="node">The repeated frame's authored node.</param>
        /// <param name="describer">The repeated frame's diagnostic renderer.</param>
        /// <returns>The exception carrying the full cycle path.</returns>
        internal static InvalidOperationException CreateCycleError(object function, object node,
            TreeFrameDescriber describer)
        {
            List<TreeCompilationFrame> frames = Frames;
            var labels = new List<string>(frames.Count + 1);
            bool singleKind = true;
            for (int i = 0; i < frames.Count; i++)
            {
                labels.Add(frames[i].Describer.Describe(frames[i].Function, frames[i].Node));
                if (!string.Equals(frames[i].Describer.CycleKindLabel, describer.CycleKindLabel,
                    StringComparison.Ordinal)) singleKind = false;
            }
            labels.Add(describer.Describe(function, node));
            string kind = singleKind ? describer.CycleKindLabel : "tree";
            return new InvalidOperationException(
                $"Cross-function {kind} cycle detected: {string.Join(" -> ", labels)}.");
        }

        /// <summary>One active recursion-stack entry.</summary>
        private readonly struct TreeCompilationFrame
        {
            /// <summary>Initializes one frame.</summary>
            /// <param name="function">The tree response owning the node.</param>
            /// <param name="node">The authored node.</param>
            /// <param name="nodeId">The authored node id.</param>
            /// <param name="describer">The frame's shared diagnostic renderer.</param>
            internal TreeCompilationFrame(object function, object node, Guid nodeId,
                TreeFrameDescriber describer)
            {
                Function = function;
                Node = node;
                NodeId = nodeId;
                Describer = describer;
            }

            /// <summary>The tree response owning the node.</summary>
            internal object Function { get; }

            /// <summary>The authored node.</summary>
            internal object Node { get; }

            /// <summary>The authored node id.</summary>
            internal Guid NodeId { get; }

            /// <summary>The frame's shared diagnostic renderer.</summary>
            internal TreeFrameDescriber Describer { get; }
        }
    }
}
