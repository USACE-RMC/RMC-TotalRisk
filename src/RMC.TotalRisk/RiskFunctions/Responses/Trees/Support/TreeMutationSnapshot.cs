using System;
using System.Collections.Generic;
using System.Linq;

namespace RMC.TotalRisk.RiskFunctions.Responses.Trees.Support
{
    /// <summary>
    /// One complete pre-mutation structural capture used for transactional authoring rollback:
    /// the node list, per-node parentage and child order, one opaque tree-kind-specific state
    /// slot, and the owning response's compiled-plan checkpoint. Restore semantics stay with the
    /// controlled tree that captured the snapshot.
    /// </summary>
    /// <typeparam name="TNode">The controlled tree's node type.</typeparam>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal sealed class TreeMutationSnapshot<TNode> where TNode : class
    {
        /// <summary>Captures the current nodes, parentage, and child order.</summary>
        /// <param name="nodes">The tree's current node list.</param>
        /// <param name="parent">Reads one node's current parent.</param>
        /// <param name="children">Reads one node's current child order.</param>
        /// <param name="treeState">The opaque tree-kind-specific rollback state.</param>
        /// <param name="ownerCheckpoint">The owning response's compiled-plan checkpoint, when attached.</param>
        internal TreeMutationSnapshot(IReadOnlyList<TNode> nodes, Func<TNode, TNode?> parent,
            Func<TNode, IReadOnlyList<TNode>> children, object? treeState, object? ownerCheckpoint)
        {
            Nodes = nodes.ToArray();
            Parents = Nodes.ToDictionary(node => node, parent);
            Children = Nodes.ToDictionary(node => node, node => children(node).ToArray());
            TreeState = treeState;
            OwnerCheckpoint = ownerCheckpoint;
        }

        /// <summary>The captured node list in tree order.</summary>
        internal IReadOnlyList<TNode> Nodes { get; }

        /// <summary>The captured per-node parentage.</summary>
        internal IReadOnlyDictionary<TNode, TNode?> Parents { get; }

        /// <summary>The captured per-node child order.</summary>
        internal IReadOnlyDictionary<TNode, TNode[]> Children { get; }

        /// <summary>The opaque tree-kind-specific rollback state.</summary>
        internal object? TreeState { get; }

        /// <summary>The owning response's exact pre-mutation cache state, when attached.</summary>
        internal object? OwnerCheckpoint { get; }
    }
}
