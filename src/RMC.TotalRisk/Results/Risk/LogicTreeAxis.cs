using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>
    /// One axis of a logic-tree enumeration: a shared epistemic variable (every composite bound
    /// to the name selects together) or a single unbound epistemic-mixture composite, with the
    /// branch set the enumerator crosses.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Axes order deterministically — shared variables first, by ordinal name, then unbound
    /// composites by function id — so the enumeration's combination indexing is reproducible
    /// from the model alone.
    /// </para>
    /// </remarks>
    public sealed class LogicTreeAxis
    {
        /// <summary>
        /// Initializes one enumeration axis.
        /// </summary>
        /// <param name="name">The axis label.</param>
        /// <param name="isSharedVariable">True for a shared-variable axis.</param>
        /// <param name="functionId">The unbound composite's id, or null for a shared-variable axis.</param>
        /// <param name="branches">The selectable branches, in child-entry order.</param>
        /// <exception cref="ArgumentNullException">Thrown when the branch list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the branch list is empty.</exception>
        internal LogicTreeAxis(string name, bool isSharedVariable, Guid? functionId, IList<LogicTreeBranch> branches)
        {
            if (branches == null) throw new ArgumentNullException(nameof(branches));
            if (branches.Count == 0) throw new ArgumentException("An enumeration axis needs at least one selectable branch.", nameof(branches));
            Name = name ?? string.Empty;
            IsSharedVariable = isSharedVariable;
            FunctionId = functionId;
            Branches = new ReadOnlyCollection<LogicTreeBranch>(branches);
        }

        /// <summary>
        /// The axis label: the shared variable's name, or the unbound composite's function name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// True when the axis is a shared epistemic variable — one branch draw applied to every
        /// composite bound to the name; false when it is a single unbound composite.
        /// </summary>
        public bool IsSharedVariable { get; }

        /// <summary>
        /// The unbound composite's function id (the enumerator's forcing target), or null for a
        /// shared-variable axis, which forces by name across however many binders exist.
        /// </summary>
        public Guid? FunctionId { get; }

        /// <summary>
        /// The selectable branches — the positively weighted children — in child-entry order.
        /// </summary>
        public IReadOnlyList<LogicTreeBranch> Branches { get; }
    }
}
