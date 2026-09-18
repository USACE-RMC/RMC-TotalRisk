using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees.Support;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Support;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support
{
    /// <summary>
    /// Immutable expanded fault-tree occurrence plan. Authored transfers are resolved to their
    /// target subtrees; shared-logical occurrences unify onto one Boolean variable per independent
    /// context, while independent-clone transfers fork a fresh context whose interior events
    /// become distinct variables. The plan carries the projected identity, the unified variable
    /// table, and — when every gate is well formed and the decision-node budget holds — the exact
    /// frozen decision diagram.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal sealed class FaultTreeOccurrencePlan
    {
        /// <summary>Initializes a completed occurrence plan.</summary>
        /// <param name="root">The expanded root occurrence.</param>
        /// <param name="variables">The unified variable table in ordinal order.</param>
        /// <param name="frozenBdd">The frozen diagram, or null when compile diagnostics exist.</param>
        /// <param name="compileDiagnostics">Gate-shape and budget diagnostics without severity prefixes.</param>
        /// <param name="warnings">Lenient resolution warnings.</param>
        /// <param name="dependencies">The live-content snapshot governing safe plan reuse.</param>
        private FaultTreeOccurrencePlan(FaultTreeOccurrenceNode root,
            IReadOnlyList<FaultTreeVariableSlot> variables, FrozenFaultTreeBdd? frozenBdd,
            IReadOnlyList<string> compileDiagnostics, IReadOnlyList<string> warnings,
            TreePlanDependencies dependencies)
        {
            Root = root;
            Variables = Array.AsReadOnly(variables.ToArray());
            FrozenBdd = frozenBdd;
            CompileDiagnostics = Array.AsReadOnly(compileDiagnostics.ToArray());
            Warnings = Array.AsReadOnly(warnings.ToArray());
            Dependencies = dependencies;

            var preOrder = new List<FaultTreeOccurrenceNode>();
            var stack = new Stack<FaultTreeOccurrenceNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                FaultTreeOccurrenceNode node = stack.Pop();
                preOrder.Add(node);
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
            }
            CanonicalPreOrder = Array.AsReadOnly(preOrder.ToArray());
            SamplingDimensions = variables.Sum(variable => variable.SamplingDimensions);
            IsDeterministic = variables.All(variable => variable.IsDeterministic);
            IsCoherent = preOrder.All(node =>
                node.SourceNode is not FaultTreeGateNode gate || gate.GateType != FaultTreeGateType.Xor);
            _identity = new XElement(nameof(FaultTree), BuildFinalIdentity(root));
            IdentityToken = CanonicalContentHasher.ToTokenHex(
                CanonicalContentHasher.Hash(_identity, CanonicalizationRules.ModelRules));
        }

        /// <summary>The expanded root occurrence.</summary>
        internal FaultTreeOccurrenceNode Root { get; }

        /// <summary>All expanded occurrences in metadata-inert canonical pre-order.</summary>
        internal IReadOnlyList<FaultTreeOccurrenceNode> CanonicalPreOrder { get; }

        /// <summary>The unified variable table in ordinal order.</summary>
        internal IReadOnlyList<FaultTreeVariableSlot> Variables { get; }

        /// <summary>The frozen exact diagram, or null when <see cref="CompileDiagnostics"/> is non-empty.</summary>
        internal FrozenFaultTreeBdd? FrozenBdd { get; }

        /// <summary>Gate-shape and budget diagnostics without severity prefixes.</summary>
        internal IReadOnlyList<string> CompileDiagnostics { get; }

        /// <summary>The privately owned projected identity.</summary>
        private readonly XElement _identity;

        /// <summary>Returns an owned copy of the projected identity.</summary>
        internal XElement Identity => new XElement(_identity);

        /// <summary>The stable token of <see cref="Identity"/>.</summary>
        internal string IdentityToken { get; }

        /// <summary>The per-unified-variable sampling-dimension total.</summary>
        internal int SamplingDimensions { get; }

        /// <summary>Whether every unified variable source is deterministic.</summary>
        internal bool IsDeterministic { get; }

        /// <summary>Whether the expanded logic contains no exclusive-disjunction gate.</summary>
        internal bool IsCoherent { get; }

        /// <summary>Lenient node-name fallback diagnostics discovered during expansion.</summary>
        internal IReadOnlyList<string> Warnings { get; }

        /// <summary>The complete live-content snapshot governing safe plan reuse.</summary>
        internal TreePlanDependencies Dependencies { get; }

        /// <summary>Compiles the full response tree.</summary>
        /// <param name="owner">The owning response.</param>
        /// <returns>The immutable expanded plan.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the owner is null.</exception>
        internal static FaultTreeOccurrencePlan Compile(FaultTreeResponse owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            return Compile(owner, owner.FaultTree.Root);
        }

        /// <summary>Compiles one authored subtree in the context of its owning response.</summary>
        /// <param name="owner">The owning response.</param>
        /// <param name="subtreeRoot">The selected authored subtree root.</param>
        /// <returns>The immutable expanded plan.</returns>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the subtree does not belong to the response.</exception>
        internal static FaultTreeOccurrencePlan Compile(FaultTreeResponse owner, FaultTreeNodeBase subtreeRoot)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (subtreeRoot == null) throw new ArgumentNullException(nameof(subtreeRoot));
            if (!ReferenceEquals(subtreeRoot.Owner, owner.FaultTree))
                throw new InvalidOperationException("The selected fault-tree subtree does not belong to the response.");

            var compiler = new Compiler(new TreeDependencyCollector());
            FaultTreeOccurrenceNode root = compiler.Expand(owner, subtreeRoot, compiler.RootContext,
                Array.Empty<FaultTreeTransferNode>());
            return compiler.Finish(owner, root, buildDiagram: true, compiler.Collector.CreateDependencies());
        }

        /// <summary>
        /// Compiles one nested fault-tree response on the shared ambient recursion stack while
        /// accumulating its live dependencies into the caller's collector. The diagram is not
        /// built: nested sources are evaluated through independently prepared setup clones, and
        /// the nested function's own validation reports its gate diagnostics.
        /// </summary>
        /// <param name="function">The nested fault-tree response.</param>
        /// <param name="collector">The caller's dependency collector.</param>
        /// <returns>The nested plan carrying identity, variables, and dimensions.</returns>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        internal static FaultTreeOccurrencePlan CompileNested(FaultTreeResponse function,
            TreeDependencyCollector collector)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (collector == null) throw new ArgumentNullException(nameof(collector));
            var compiler = new Compiler(collector);
            FaultTreeOccurrenceNode root = compiler.Expand(function, function.FaultTree.Root,
                compiler.RootContext, Array.Empty<FaultTreeTransferNode>());
            return compiler.Finish(function, root, buildDiagram: false, TreePlanDependencies.Empty);
        }

        /// <summary>Builds the projected identity of one unowned transfer-free subtree.</summary>
        /// <param name="root">The authored subtree root.</param>
        /// <returns>The metadata-free identity whose basic events carry first-occurrence ordinals.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the subtree contains a transfer.</exception>
        internal static XElement BuildUnownedIdentity(FaultTreeNodeBase root)
        {
            UnownedIdentityNode shape = BuildUnownedShape(root);
            int nextOrdinal = 0;
            AssignUnownedOrdinals(shape, ref nextOrdinal);
            return EmitUnownedIdentity(shape);
        }

        /// <summary>Builds the final projected identity with unified-variable ordinals applied.</summary>
        /// <param name="node">The expanded occurrence.</param>
        /// <returns>The identity element.</returns>
        private static XElement BuildFinalIdentity(FaultTreeOccurrenceNode node)
        {
            XElement identity = node.BuildUnderlyingFinalIdentity();
            for (int i = node.TransferWrapperModes.Count - 1; i >= 0; i--)
            {
                var wrapper = new XElement("Node");
                wrapper.SetAttributeValue("Type", nameof(FaultTreeTransferNode));
                wrapper.SetAttributeValue(nameof(FaultTreeTransferNode.LinkMode),
                    node.TransferWrapperModes[i].ToString());
                wrapper.Add(new XElement("Target", identity));
                identity = wrapper;
            }
            return identity;
        }

        /// <summary>Builds the sorted content-only shape of one unowned subtree.</summary>
        /// <param name="node">The authored node.</param>
        /// <returns>The sorted shape.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a transfer is encountered.</exception>
        private static UnownedIdentityNode BuildUnownedShape(FaultTreeNodeBase node)
        {
            if (node is FaultTreeTransferNode)
                throw new InvalidOperationException(
                    "A fault tree containing transfers must be owned by a FaultTreeResponse before its identity can be computed.");
            var children = new List<UnownedIdentityNode>(node.Children.Count);
            for (int i = 0; i < node.Children.Count; i++)
                children.Add(BuildUnownedShape(node.Children[i]));
            List<UnownedIdentityNode> sorted = children
                .Select((child, index) => (Child: child, Index: index))
                .OrderBy(item => item.Child.Token, StringComparer.Ordinal)
                .ThenBy(item => item.Index)
                .Select(item => item.Child)
                .ToList();
            XElement contentIdentity = BuildContentIdentity(node,
                node is FaultTreeBasicEventNode basic ? basic.ProbabilitySource.ToIdentityXElement() : null,
                sorted.Select(child => child.ContentIdentity).ToList());
            return new UnownedIdentityNode(node, sorted, contentIdentity,
                CanonicalContentHasher.ToTokenHex(
                    CanonicalContentHasher.Hash(contentIdentity, CanonicalizationRules.ModelRules)));
        }

        /// <summary>Assigns first-occurrence ordinals over one sorted unowned shape.</summary>
        /// <param name="shape">The sorted shape.</param>
        /// <param name="nextOrdinal">The running ordinal counter.</param>
        private static void AssignUnownedOrdinals(UnownedIdentityNode shape, ref int nextOrdinal)
        {
            if (shape.Source is FaultTreeBasicEventNode) shape.Ordinal = nextOrdinal++;
            for (int i = 0; i < shape.Children.Count; i++)
                AssignUnownedOrdinals(shape.Children[i], ref nextOrdinal);
        }

        /// <summary>Emits the final unowned identity with ordinals applied.</summary>
        /// <param name="shape">The sorted shape.</param>
        /// <returns>The identity element.</returns>
        private static XElement EmitUnownedIdentity(UnownedIdentityNode shape)
        {
            XElement identity = BuildContentIdentity(shape.Source,
                shape.Source is FaultTreeBasicEventNode basic ? basic.ProbabilitySource.ToIdentityXElement() : null,
                shape.Children.Select(EmitUnownedIdentity).ToList());
            if (shape.Source is FaultTreeBasicEventNode)
                identity.SetAttributeValue("SharedVariable", shape.Ordinal.ToString(CultureInfo.InvariantCulture));
            return identity;
        }

        /// <summary>Builds one node's content identity from its kind and pre-built children.</summary>
        /// <param name="node">The authored node.</param>
        /// <param name="probabilityIdentity">The basic-event source identity, when applicable.</param>
        /// <param name="children">The sorted child identities.</param>
        /// <returns>The identity element.</returns>
        private static XElement BuildContentIdentity(FaultTreeNodeBase node,
            XElement? probabilityIdentity, IReadOnlyList<XElement> children)
        {
            var identity = new XElement("Node");
            identity.SetAttributeValue("Type", node.SerializedName);
            if (node is FaultTreeGateNode gate)
            {
                identity.SetAttributeValue(nameof(FaultTreeGateNode.GateType), gate.GateType.ToString());
                if (gate.GateType == FaultTreeGateType.KOfN)
                    identity.SetAttributeValue(nameof(FaultTreeGateNode.K), gate.K.ToString(CultureInfo.InvariantCulture));
            }
            if (node is FaultTreeHouseEventNode house)
                identity.SetAttributeValue(nameof(FaultTreeHouseEventNode.State), house.State);
            if (probabilityIdentity != null) identity.Add(probabilityIdentity);
            for (int i = 0; i < children.Count; i++) identity.Add(children[i]);
            return identity;
        }

        /// <summary>One sorted unowned-identity shape node.</summary>
        private sealed class UnownedIdentityNode
        {
            /// <summary>Initializes one shape node.</summary>
            /// <param name="source">The authored node.</param>
            /// <param name="children">The sorted children.</param>
            /// <param name="contentIdentity">The content-only identity.</param>
            /// <param name="token">The content-only identity token.</param>
            internal UnownedIdentityNode(FaultTreeNodeBase source,
                IReadOnlyList<UnownedIdentityNode> children, XElement contentIdentity, string token)
            {
                Source = source;
                Children = children;
                ContentIdentity = contentIdentity;
                Token = token;
            }

            /// <summary>The authored node.</summary>
            internal FaultTreeNodeBase Source { get; }

            /// <summary>The sorted children.</summary>
            internal IReadOnlyList<UnownedIdentityNode> Children { get; }

            /// <summary>The content-only identity.</summary>
            internal XElement ContentIdentity { get; }

            /// <summary>The content-only identity token.</summary>
            internal string Token { get; }

            /// <summary>The assigned first-occurrence variable ordinal, for basic events.</summary>
            internal int Ordinal { get; set; }
        }

        /// <summary>Stateful depth-first compiler used for expansion, unification, and warnings.</summary>
        private sealed class Compiler
        {
            /// <summary>Initializes a compiler over one dependency collector.</summary>
            /// <param name="collector">The shared dependency collector.</param>
            internal Compiler(TreeDependencyCollector collector)
            {
                Collector = collector;
            }

            /// <summary>The shared dependency collector.</summary>
            internal TreeDependencyCollector Collector { get; }

            /// <summary>The root independent-variable context.</summary>
            internal object RootContext { get; } = new object();

            /// <summary>The unified variable classes keyed by authored node and independent context.</summary>
            private readonly Dictionary<(FaultTreeBasicEventNode Node, object Context), FaultTreeVariableSlot> _variables =
                new Dictionary<(FaultTreeBasicEventNode, object), FaultTreeVariableSlot>();

            /// <summary>The expanded common-cause group slot sets keyed by group and independent context.</summary>
            private readonly Dictionary<(FaultTreeCcfGroup Group, object Context), CcfGroupSlots> _ccfSlots =
                new Dictionary<(FaultTreeCcfGroup, object), CcfGroupSlots>();

            /// <summary>The group slot sets in first-creation order, for deterministic basis ordinals.</summary>
            private readonly List<CcfGroupSlots> _ccfSlotsInOrder = new List<CcfGroupSlots>();

            /// <summary>The groups already found invalid this compile, diagnosed once.</summary>
            private readonly HashSet<FaultTreeCcfGroup> _ccfInvalidGroups =
                new HashSet<FaultTreeCcfGroup>(ReferenceEqualityComparer.Instance);

            /// <summary>Gate-shape diagnostics collected leniently during expansion.</summary>
            internal List<string> GateDiagnostics { get; } = new List<string>();

            /// <summary>Lenient resolution warnings.</summary>
            internal List<string> Warnings { get; } = new List<string>();

            /// <summary>Expands one authored node, following any transfer chain.</summary>
            /// <param name="function">The function owning the node.</param>
            /// <param name="node">The authored node.</param>
            /// <param name="context">The current independent-variable context.</param>
            /// <param name="pendingTransfers">Transfers followed to reach this node.</param>
            /// <returns>The expanded occurrence.</returns>
            /// <exception cref="InvalidOperationException">Thrown on cycles or unresolved transfers.</exception>
            internal FaultTreeOccurrenceNode Expand(FaultTreeResponse function, FaultTreeNodeBase node,
                object context, IReadOnlyList<FaultTreeTransferNode> pendingTransfers)
            {
                if (TreeCompilationScope.IndexOf(function, node.Id) >= 0)
                    throw TreeCompilationScope.CreateCycleError(function, node, FaultTreeFrameDescriber.Instance);
                Collector.TreeSources.Add(function);

                TreeCompilationScope.Push(function, node, node.Id, FaultTreeFrameDescriber.Instance);
                try
                {
                    if (node is FaultTreeTransferNode transfer)
                    {
                        if (transfer.Children.Count != 0)
                            throw new InvalidOperationException($"Fault-tree transfer '{transfer.Name}' in function '{DisplayFunction(function)}' cannot own authored inputs.");
                        if (transfer.UnresolvedReferences.Count > 0)
                            throw new InvalidOperationException($"Fault-tree transfer '{transfer.Name}' in function '{DisplayFunction(function)}' references {string.Join(", ", transfer.UnresolvedReferences)}, which was not found.");

                        FaultTreeResponse targetFunction = transfer.IsExternal
                            ? transfer.TargetFunction ?? throw new InvalidOperationException(
                                $"External fault-tree transfer '{transfer.Name}' in function '{DisplayFunction(function)}' has no resolved target function.")
                            : function;
                        FaultTree targetTree = targetFunction.FaultTree;
                        FaultTreeNodeBase? targetNode = transfer.ResolveNode(targetTree, out bool usedNameFallback);
                        if (targetNode == null)
                        {
                            TreeNodeReference target = transfer.Target;
                            throw new InvalidOperationException(
                                $"Fault-tree transfer '{transfer.Name}' in function '{DisplayFunction(function)}' cannot resolve target node " +
                                $"'{target.NodeName ?? target.NodeId.ToString("D")}' in function '{DisplayFunction(targetFunction)}'.");
                        }
                        if (usedNameFallback)
                        {
                            Warnings.Add(
                                $"Warning: Fault-tree transfer '{transfer.Name}' in function '{DisplayFunction(function)}' resolved target node " +
                                $"'{targetNode.Name}' by its name fallback; save the model to repair the persistent node id.");
                        }

                        var wrappers = new List<FaultTreeTransferNode>(pendingTransfers.Count + 1);
                        wrappers.AddRange(pendingTransfers);
                        wrappers.Add(transfer);
                        object targetContext = transfer.LinkMode == TreeLinkMode.IndependentClone
                            ? new object()
                            : context;
                        return Expand(targetFunction, targetNode, targetContext, wrappers);
                    }

                    FaultTreeVariableSlot? variable = null;
                    XElement? probabilityIdentity = null;
                    if (node is FaultTreeBasicEventNode basic)
                    {
                        if (basic.Owner is FaultTree owningTree && owningTree.CcfGroups.Count > 0
                            && owningTree.FindCcfGroup(basic, out int memberIndex) is FaultTreeCcfGroup group)
                        {
                            FaultTreeOccurrenceNode? expanded = TryExpandCcfMember(function, basic,
                                owningTree, group, memberIndex, context, pendingTransfers);
                            if (expanded != null) return expanded;
                        }
                        variable = GetOrCreateVariable(function, basic, context);
                        probabilityIdentity = variable.ProbabilityIdentity;
                    }

                    var children = new List<FaultTreeOccurrenceNode>(node.Children.Count);
                    for (int i = 0; i < node.Children.Count; i++)
                    {
                        FaultTreeOccurrenceNode occurrence = Expand(function, node.Children[i], context,
                            Array.Empty<FaultTreeTransferNode>());
                        occurrence.AssignAuthoredSiblingOrder(i);
                        children.Add(occurrence);
                    }
                    children = children.Select((child, index) => (Child: child, Index: index))
                        .OrderBy(item => item.Child.IdentityToken, StringComparer.Ordinal)
                        .ThenBy(item => item.Index)
                        .Select(item => item.Child)
                        .ToList();

                    if (node is FaultTreeGateNode gate)
                    {
                        if (children.Count == 0)
                            GateDiagnostics.Add($"Fault-tree gate '{gate.Name}' in function '{DisplayFunction(function)}' has no inputs.");
                        else if (gate.GateType == FaultTreeGateType.Xor && children.Count != 2)
                            GateDiagnostics.Add($"Fault-tree Xor gate '{gate.Name}' in function '{DisplayFunction(function)}' requires exactly two inputs but has {children.Count}.");
                        else if (gate.GateType == FaultTreeGateType.KOfN
                            && (gate.K < 1 || gate.K > children.Count))
                            GateDiagnostics.Add($"Fault-tree gate '{gate.Name}' in function '{DisplayFunction(function)}' requires K between 1 and its input count; K is {gate.K} with {children.Count} inputs.");
                    }

                    XElement contentIdentity = BuildContentIdentity(node, probabilityIdentity,
                        children.Select(child => child.WrappedContentIdentity).ToList());
                    XElement wrapped = contentIdentity;
                    var wrapperModes = new TreeLinkMode[pendingTransfers.Count];
                    for (int i = pendingTransfers.Count - 1; i >= 0; i--)
                    {
                        wrapperModes[i] = pendingTransfers[i].LinkMode;
                        var wrapper = new XElement("Node");
                        wrapper.SetAttributeValue("Type", nameof(FaultTreeTransferNode));
                        wrapper.SetAttributeValue(nameof(FaultTreeTransferNode.LinkMode), pendingTransfers[i].LinkMode.ToString());
                        wrapper.Add(new XElement("Target", wrapped));
                        wrapped = wrapper;
                    }

                    FaultTreeNodeBase displayNode = pendingTransfers.Count > 0 ? pendingTransfers[0] : node;
                    return new FaultTreeOccurrenceNode(function, node, children, wrapped, wrapperModes,
                        probabilityIdentity, variable, displayNode);
                }
                finally
                {
                    TreeCompilationScope.Pop();
                }
            }

            /// <summary>Completes one compile: paths, ordinals, optional diagram, and the plan.</summary>
            /// <param name="owner">The response whose budget governs the diagram build.</param>
            /// <param name="root">The expanded root occurrence.</param>
            /// <param name="buildDiagram">Whether to build and freeze the exact diagram.</param>
            /// <param name="dependencies">The dependency snapshot to attach to the plan.</param>
            /// <returns>The immutable plan.</returns>
            internal FaultTreeOccurrencePlan Finish(FaultTreeResponse owner, FaultTreeOccurrenceNode root,
                bool buildDiagram, TreePlanDependencies dependencies)
            {
                TreeCanonicalPaths.Assign(root, node => node.Children, node => node.IdentityToken,
                    (node, path) => node.AssignCanonicalPath(path));

                var ordered = new List<FaultTreeVariableSlot>(_variables.Count);
                AssignVariableOrdinals(root, ordered);

                // Common-cause basis variables carry the group's shared draw but never appear as
                // diagram leaves, so they receive trailing ordinals here; a group whose members
                // were only partially reachable in one context cannot carry its coupling and is
                // diagnosed loudly.
                foreach (CcfGroupSlots slots in _ccfSlotsInOrder)
                {
                    int size = slots.Group.MemberNodeIds.Count;
                    if (slots.ExpandedMembers.Count != size)
                    {
                        GateDiagnostics.Add(
                            $"Fault-tree CCF group '{slots.Group.Name}' in function '{DisplayFunction(owner)}' " +
                            $"expanded {slots.ExpandedMembers.Count} of its {size} member events in one independent " +
                            "context; every member must be reachable in the same context for the common-cause " +
                            "coupling to hold.");
                    }
                    if (slots.Basis.Ordinal < 0 && slots.FirstOccurrence != null)
                    {
                        slots.Basis.AssignOrdinal(ordered.Count, slots.FirstOccurrence);
                        ordered.Add(slots.Basis);
                    }
                }

                FrozenFaultTreeBdd? frozen = null;
                if (buildDiagram && GateDiagnostics.Count == 0)
                {
                    try
                    {
                        var bdd = new FaultTreeBdd(ordered.Count, owner.BddNodeLimit);
                        int top = BuildDiagram(bdd, root);
                        frozen = bdd.Freeze(top);
                    }
                    catch (FaultTreeBddBudgetException ex)
                    {
                        GateDiagnostics.Add(
                            $"Fault-tree response '{owner.Name}' exceeded the configured decision-diagram resource budget: " +
                            $"{ex.ObservedNodeCount} nodes were created and BddNodeLimit is {ex.ConfiguredLimit}. " +
                            "Reduce repeated shared events or wide K-of-N gates, split subtrees into referenced " +
                            "fault-tree responses, or raise BddNodeLimit if the model genuinely requires a larger " +
                            "exact diagram. Exact evaluation is never approximated.");
                    }
                }

                return new FaultTreeOccurrencePlan(root, ordered, frozen, GateDiagnostics, Warnings,
                    dependencies);
            }

            /// <summary>Assigns unified-variable ordinals by first occurrence in canonical pre-order.</summary>
            /// <param name="node">The expanded occurrence.</param>
            /// <param name="ordered">The accumulating ordinal-ordered variable table.</param>
            private static void AssignVariableOrdinals(FaultTreeOccurrenceNode node,
                List<FaultTreeVariableSlot> ordered)
            {
                if (node.Variable != null && node.Variable.Ordinal < 0)
                {
                    node.Variable.AssignOrdinal(ordered.Count, node);
                    ordered.Add(node.Variable);
                }
                for (int i = 0; i < node.Children.Count; i++) AssignVariableOrdinals(node.Children[i], ordered);
            }

            /// <summary>Builds the exact diagram bottom-up over one expanded subtree.</summary>
            /// <param name="bdd">The diagram builder.</param>
            /// <param name="node">The expanded occurrence.</param>
            /// <returns>The node's diagram index.</returns>
            private static int BuildDiagram(FaultTreeBdd bdd, FaultTreeOccurrenceNode node)
            {
                if (node.SourceNode is FaultTreeBasicEventNode)
                    return bdd.Variable(node.Variable!.Ordinal);
                if (node.SourceNode is FaultTreeHouseEventNode house)
                    return bdd.Constant(house.State);

                var gate = (FaultTreeGateNode)node.SourceNode;
                if (gate.GateType == FaultTreeGateType.Xor)
                {
                    return bdd.Xor(BuildDiagram(bdd, node.Children[0]),
                        BuildDiagram(bdd, node.Children[1]));
                }
                if (gate.GateType == FaultTreeGateType.KOfN)
                {
                    var inputs = new int[node.Children.Count];
                    for (int i = 0; i < inputs.Length; i++) inputs[i] = BuildDiagram(bdd, node.Children[i]);
                    return bdd.KOfN(inputs, gate.K);
                }

                int result = BuildDiagram(bdd, node.Children[0]);
                for (int i = 1; i < node.Children.Count; i++)
                {
                    int child = BuildDiagram(bdd, node.Children[i]);
                    result = gate.GateType == FaultTreeGateType.And
                        ? bdd.And(result, child)
                        : bdd.Or(result, child);
                }
                return result;
            }

            /// <summary>
            /// Expands one common-cause group member into its derived-event disjunction: the
            /// member's independent event plus every group combination containing it, all shared
            /// per (group, context) so co-membered events reference the same derived variables
            /// and the exact diagram carries the coupling natively. Returns null — falling back
            /// to the plain expansion with the configuration diagnosed once — when the group is
            /// invalid.
            /// </summary>
            /// <param name="function">The function owning the member.</param>
            /// <param name="basic">The authored member basic event.</param>
            /// <param name="owningTree">The tree owning the member and the group.</param>
            /// <param name="group">The claiming group.</param>
            /// <param name="memberIndex">The member's position in the group order.</param>
            /// <param name="context">The independent-variable context.</param>
            /// <param name="pendingTransfers">Transfers followed to reach the member.</param>
            /// <returns>The synthesized disjunction occurrence, or null for an invalid group.</returns>
            private FaultTreeOccurrenceNode? TryExpandCcfMember(FaultTreeResponse function,
                FaultTreeBasicEventNode basic, FaultTree owningTree, FaultTreeCcfGroup group,
                int memberIndex, object context, IReadOnlyList<FaultTreeTransferNode> pendingTransfers)
            {
                CcfGroupSlots? slots = GetOrCreateCcfSlots(function, owningTree, group, context);
                if (slots == null) return null;
                slots.ExpandedMembers.Add(memberIndex);

                var leaves = new List<FaultTreeOccurrenceNode>(1 + slots.CombinationMasks.Count);
                leaves.Add(CreateCcfLeaf(function, basic, slots.IndependentSlots[memberIndex]));
                for (int c = 0; c < slots.CombinationMasks.Count; c++)
                {
                    if ((slots.CombinationMasks[c] & (1 << memberIndex)) == 0) continue;
                    leaves.Add(CreateCcfLeaf(function, basic, slots.CombinationSlots[c]));
                }
                for (int i = 0; i < leaves.Count; i++) leaves[i].AssignAuthoredSiblingOrder(i);
                List<FaultTreeOccurrenceNode> sorted = leaves
                    .Select((child, index) => (Child: child, Index: index))
                    .OrderBy(item => item.Child.IdentityToken, StringComparer.Ordinal)
                    .ThenBy(item => item.Index)
                    .Select(item => item.Child)
                    .ToList();

                XElement contentIdentity = BuildContentIdentity(slots.MemberGates[memberIndex], null,
                    sorted.Select(child => child.WrappedContentIdentity).ToList());
                XElement wrapped = contentIdentity;
                var wrapperModes = new TreeLinkMode[pendingTransfers.Count];
                for (int i = pendingTransfers.Count - 1; i >= 0; i--)
                {
                    wrapperModes[i] = pendingTransfers[i].LinkMode;
                    var wrapper = new XElement("Node");
                    wrapper.SetAttributeValue("Type", nameof(FaultTreeTransferNode));
                    wrapper.SetAttributeValue(nameof(FaultTreeTransferNode.LinkMode), pendingTransfers[i].LinkMode.ToString());
                    wrapper.Add(new XElement("Target", wrapped));
                    wrapped = wrapper;
                }
                FaultTreeNodeBase displayNode = pendingTransfers.Count > 0 ? pendingTransfers[0] : basic;
                var occurrence = new FaultTreeOccurrenceNode(function, slots.MemberGates[memberIndex],
                    sorted, wrapped, wrapperModes, null, null, displayNode);
                slots.FirstOccurrence ??= occurrence;
                return occurrence;
            }

            /// <summary>Gets or creates the shared derived-slot set for one group in one context.</summary>
            /// <param name="function">The function owning the group's members.</param>
            /// <param name="owningTree">The tree owning the group.</param>
            /// <param name="group">The group.</param>
            /// <param name="context">The independent-variable context.</param>
            /// <returns>The slot set, or null for an invalid group.</returns>
            private CcfGroupSlots? GetOrCreateCcfSlots(FaultTreeResponse function, FaultTree owningTree,
                FaultTreeCcfGroup group, object context)
            {
                if (_ccfSlots.TryGetValue((group, context), out CcfGroupSlots? existing)) return existing;
                if (_ccfInvalidGroups.Contains(group)) return null;
                List<string> errors = group.GetConfigurationErrors(owningTree);
                if (errors.Count > 0)
                {
                    _ccfInvalidGroups.Add(group);
                    foreach (string error in errors)
                    {
                        GateDiagnostics.Add(error.StartsWith("Error: ", StringComparison.Ordinal)
                            ? error.Substring("Error: ".Length)
                            : error);
                    }
                    return null;
                }

                double[] factors = group.ComputeFactors();
                int size = group.MemberNodeIds.Count;
                var members = new FaultTreeBasicEventNode[size];
                for (int i = 0; i < size; i++)
                    members[i] = (FaultTreeBasicEventNode)owningTree.FindNode(group.MemberNodeIds[i])!;

                // The exchangeable basis rides the ordinary variable machinery on the first
                // member (member sources are content-identical by validation), so its dimensions,
                // clone streams, and hazard-transform chains follow the established rules while
                // the group draws once per realization per context.
                FaultTreeVariableSlot basis = GetOrCreateVariable(function, members[0], context);
                basis.AssignCcfBasis($"{group.Name} (CCF basis)");
                XElement basisIdentity = basis.ProbabilityIdentity;
                var slots = new CcfGroupSlots(group, basis, size);
                for (int m = 0; m < size; m++)
                {
                    slots.MemberGates[m] = new FaultTreeGateNode($"{members[m].Name} (CCF)", FaultTreeGateType.Or);
                    slots.IndependentSlots[m] = CreateDerivedSlot(function, members[m], group, basis,
                        basisIdentity, factors[0], multiplicity: 1, membersMask: 1 << m,
                        $"{members[m].Name} (independent)");
                }
                for (int multiplicity = 2; multiplicity <= size; multiplicity++)
                {
                    for (int mask = 0; mask < 1 << size; mask++)
                    {
                        if (CountBits(mask) != multiplicity) continue;
                        var names = new List<string>(multiplicity);
                        int lowest = -1;
                        for (int m = 0; m < size; m++)
                        {
                            if ((mask & (1 << m)) == 0) continue;
                            if (lowest < 0) lowest = m;
                            names.Add(members[m].Name);
                        }
                        slots.CombinationMasks.Add(mask);
                        slots.CombinationSlots.Add(CreateDerivedSlot(function, members[lowest], group,
                            basis, basisIdentity, factors[multiplicity - 1], multiplicity, mask,
                            $"{string.Join(" · ", names)} (common cause)"));
                    }
                }
                _ccfSlots.Add((group, context), slots);
                _ccfSlotsInOrder.Add(slots);
                return slots;
            }

            /// <summary>Creates one derived common-cause variable.</summary>
            /// <param name="function">The owning function.</param>
            /// <param name="sourceNode">The member the derived event is anchored to for reporting.</param>
            /// <param name="group">The group.</param>
            /// <param name="basis">The group's shared basis variable.</param>
            /// <param name="basisIdentity">The basis source identity embedded into the derived identity.</param>
            /// <param name="factor">The multiplicity factor.</param>
            /// <param name="multiplicity">The event multiplicity.</param>
            /// <param name="membersMask">The member-combination bit mask over group positions.</param>
            /// <param name="displayName">The derived-event display label.</param>
            /// <returns>The derived variable.</returns>
            private static FaultTreeVariableSlot CreateDerivedSlot(FaultTreeResponse function,
                FaultTreeBasicEventNode sourceNode, FaultTreeCcfGroup group, FaultTreeVariableSlot basis,
                XElement basisIdentity, double factor, int multiplicity, int membersMask, string displayName)
            {
                var identity = new XElement("CcfEvent");
                identity.SetAttributeValue("Model", group.Model.ToString());
                identity.SetAttributeValue("GroupSize",
                    group.MemberNodeIds.Count.ToString(CultureInfo.InvariantCulture));
                identity.SetAttributeValue("Parameters",
                    string.Join("|", group.Parameters.Select(SerializationUtilities.FormatDouble)));
                identity.SetAttributeValue("Multiplicity", multiplicity.ToString(CultureInfo.InvariantCulture));
                var positions = new List<string>(multiplicity);
                for (int m = 0; m < group.MemberNodeIds.Count; m++)
                {
                    if ((membersMask & (1 << m)) != 0) positions.Add(m.ToString(CultureInfo.InvariantCulture));
                }
                identity.SetAttributeValue("Members", string.Join("|", positions));
                identity.Add(new XElement(basisIdentity));
                var slot = new FaultTreeVariableSlot(function, sourceNode, identity,
                    samplingDimensions: 0, isDeterministic: true);
                slot.AssignCcfDerivation(factor, basis, displayName);
                return slot;
            }

            /// <summary>Creates one derived-event leaf occurrence anchored to a member node.</summary>
            /// <param name="function">The owning function.</param>
            /// <param name="member">The authored member the leaf displays as.</param>
            /// <param name="slot">The derived variable.</param>
            /// <returns>The leaf occurrence.</returns>
            private static FaultTreeOccurrenceNode CreateCcfLeaf(FaultTreeResponse function,
                FaultTreeBasicEventNode member, FaultTreeVariableSlot slot)
            {
                XElement probabilityIdentity = slot.ProbabilityIdentity;
                XElement identity = BuildContentIdentity(member, probabilityIdentity,
                    Array.Empty<XElement>());
                return new FaultTreeOccurrenceNode(function, member,
                    Array.Empty<FaultTreeOccurrenceNode>(), identity, Array.Empty<TreeLinkMode>(),
                    probabilityIdentity, slot, member);
            }

            /// <summary>Counts the set bits of one combination mask.</summary>
            /// <param name="mask">The mask.</param>
            /// <returns>The bit count.</returns>
            private static int CountBits(int mask)
            {
                int count = 0;
                while (mask != 0)
                {
                    count += mask & 1;
                    mask >>= 1;
                }
                return count;
            }

            /// <summary>One group's shared derived-slot set within one independent context.</summary>
            private sealed class CcfGroupSlots
            {
                /// <summary>Initializes one slot set.</summary>
                /// <param name="group">The group.</param>
                /// <param name="basis">The shared basis variable.</param>
                /// <param name="memberCount">The group size.</param>
                internal CcfGroupSlots(FaultTreeCcfGroup group, FaultTreeVariableSlot basis, int memberCount)
                {
                    Group = group;
                    Basis = basis;
                    MemberGates = new FaultTreeGateNode[memberCount];
                    IndependentSlots = new FaultTreeVariableSlot[memberCount];
                }

                /// <summary>The group.</summary>
                internal FaultTreeCcfGroup Group { get; }

                /// <summary>The shared total-probability basis variable.</summary>
                internal FaultTreeVariableSlot Basis { get; }

                /// <summary>The synthesized per-member disjunction gates.</summary>
                internal FaultTreeGateNode[] MemberGates { get; }

                /// <summary>The per-member independent derived variables.</summary>
                internal FaultTreeVariableSlot[] IndependentSlots { get; }

                /// <summary>The combination bit masks, ordered by multiplicity then mask.</summary>
                internal List<int> CombinationMasks { get; } = new List<int>();

                /// <summary>The combination derived variables, parallel to <see cref="CombinationMasks"/>.</summary>
                internal List<FaultTreeVariableSlot> CombinationSlots { get; } = new List<FaultTreeVariableSlot>();

                /// <summary>The member positions expanded in this context.</summary>
                internal HashSet<int> ExpandedMembers { get; } = new HashSet<int>();

                /// <summary>The first synthesized member occurrence, anchoring the basis ordinal.</summary>
                internal FaultTreeOccurrenceNode? FirstOccurrence { get; set; }
            }

            /// <summary>Gets or creates the unified variable for one basic event in one context.</summary>
            /// <param name="function">The function owning the basic event.</param>
            /// <param name="basic">The authored basic event.</param>
            /// <param name="context">The independent-variable context.</param>
            /// <returns>The unified variable.</returns>
            private FaultTreeVariableSlot GetOrCreateVariable(FaultTreeResponse function,
                FaultTreeBasicEventNode basic, object context)
            {
                if (_variables.TryGetValue((basic, context), out FaultTreeVariableSlot? existing))
                    return existing;

                ProbabilitySource source = basic.ProbabilitySource;
                if (source.Table is not null) Collector.Tables.Add(source.Table);
                foreach (var transform in source.HazardTransforms)
                {
                    if (transform != null) Collector.TransformFunctions.Add(transform);
                }
                int samplingDimensions;
                bool isDeterministic;
                byte[]? recursiveResponseHash = null;
                if (source.ResponseFunction is EventTreeResponse nestedEvent)
                {
                    EventTreeOccurrencePlan nestedPlan = EventTreeOccurrencePlan.CompileNested(nestedEvent, Collector);
                    samplingDimensions = nestedPlan.SamplingDimensions + source.HazardTransformDimensions;
                    isDeterministic = nestedPlan.IsDeterministic && source.HazardTransformsAreDeterministic;
                    recursiveResponseHash = nestedEvent.CanonicalHash(nestedPlan);
                }
                else if (source.ResponseFunction is FaultTreeResponse nestedFault)
                {
                    FaultTreeOccurrencePlan nestedPlan = CompileNested(nestedFault, Collector);
                    samplingDimensions = nestedPlan.SamplingDimensions + source.HazardTransformDimensions;
                    isDeterministic = nestedPlan.IsDeterministic && source.HazardTransformsAreDeterministic;
                    recursiveResponseHash = nestedFault.CanonicalHash(nestedPlan);
                }
                else
                {
                    if (source.ResponseFunction != null) Collector.OrdinaryResponses.Add(source.ResponseFunction);
                    samplingDimensions = source.SamplingDimensions;
                    isDeterministic = source.IsDeterministic;
                }

                XElement probabilityIdentity = source.ToIdentityXElement(recursiveResponseHash);
                var variable = new FaultTreeVariableSlot(function, basic, probabilityIdentity,
                    samplingDimensions, isDeterministic);
                _variables.Add((basic, context), variable);
                return variable;
            }

            /// <summary>Returns a useful function label for diagnostics.</summary>
            /// <param name="function">The fault-tree response.</param>
            /// <returns>The display label.</returns>
            private static string DisplayFunction(FaultTreeResponse function)
            {
                return string.IsNullOrEmpty(function.Name) ? "<unnamed fault tree>" : function.Name;
            }

            /// <summary>The shared fault-tree frame renderer for the ambient compilation scope.</summary>
            private sealed class FaultTreeFrameDescriber : TreeFrameDescriber
            {
                /// <summary>The shared instance.</summary>
                internal static FaultTreeFrameDescriber Instance { get; } = new FaultTreeFrameDescriber();

                /// <inheritdoc/>
                internal override string CycleKindLabel => "fault-tree";

                /// <inheritdoc/>
                internal override string Describe(object function, object node)
                {
                    var typedFunction = (FaultTreeResponse)function;
                    var typedNode = (FaultTreeNodeBase)node;
                    string nodeName = string.IsNullOrEmpty(typedNode.Name) ? typedNode.SerializedName : typedNode.Name;
                    return $"'{DisplayFunction(typedFunction)}'::'{nodeName}' ({typedNode.SerializedName}, {typedNode.Id:D})";
                }
            }
        }
    }

    /// <summary>One unified Boolean variable of an expanded fault-tree plan.</summary>
    internal sealed class FaultTreeVariableSlot
    {
        /// <summary>Initializes one unified variable before ordinal assignment.</summary>
        /// <param name="sourceFunction">The function owning the authored basic event.</param>
        /// <param name="sourceNode">The authored basic event.</param>
        /// <param name="probabilityIdentity">The metadata-free source identity.</param>
        /// <param name="samplingDimensions">The recursive sampler-dimension count.</param>
        /// <param name="isDeterministic">Whether the source carries no knowledge uncertainty.</param>
        internal FaultTreeVariableSlot(FaultTreeResponse sourceFunction, FaultTreeBasicEventNode sourceNode,
            XElement probabilityIdentity, int samplingDimensions, bool isDeterministic)
        {
            SourceFunction = sourceFunction;
            SourceNode = sourceNode;
            _probabilityIdentity = probabilityIdentity;
            SamplingDimensions = samplingDimensions;
            IsDeterministic = isDeterministic;
            ContentToken = CanonicalContentHasher.ToTokenHex(
                CanonicalContentHasher.Hash(probabilityIdentity, CanonicalizationRules.ModelRules));
        }

        /// <summary>The function owning the authored basic event.</summary>
        internal FaultTreeResponse SourceFunction { get; }

        /// <summary>The authored basic event.</summary>
        internal FaultTreeBasicEventNode SourceNode { get; }

        /// <summary>
        /// The common-cause multiplicity factor for a derived CCF event, or null for an ordinary
        /// or basis variable. A derived variable's probability is this factor times its group's
        /// evaluated basis probability; it owns no sampler dimensions of its own.
        /// </summary>
        internal double? CcfFactor { get; private set; }

        /// <summary>The derived CCF event's shared group basis variable, or null.</summary>
        internal FaultTreeVariableSlot? CcfBasisSlot { get; private set; }

        /// <summary>The display-name override for CCF basis and derived variables, or null.</summary>
        private string? _displayName;

        /// <summary>The variable's diagnostic and reporting label.</summary>
        internal string DisplayName => _displayName ?? SourceNode.Name;

        /// <summary>Marks this variable as one group's shared common-cause basis.</summary>
        /// <param name="displayName">The basis display label.</param>
        internal void AssignCcfBasis(string displayName)
        {
            _displayName = displayName;
        }

        /// <summary>Marks this variable as a derived common-cause event.</summary>
        /// <param name="factor">The multiplicity factor applied to the basis probability.</param>
        /// <param name="basisSlot">The group's shared basis variable.</param>
        /// <param name="displayName">The derived-event display label.</param>
        internal void AssignCcfDerivation(double factor, FaultTreeVariableSlot basisSlot, string displayName)
        {
            CcfFactor = factor;
            CcfBasisSlot = basisSlot;
            _displayName = displayName;
        }

        /// <summary>The privately owned metadata-free source identity.</summary>
        private readonly XElement _probabilityIdentity;

        /// <summary>Returns an owned copy of the metadata-free source identity.</summary>
        internal XElement ProbabilityIdentity => new XElement(_probabilityIdentity);

        /// <summary>The recursive sampler-dimension count of the source.</summary>
        internal int SamplingDimensions { get; }

        /// <summary>Whether the source carries no knowledge uncertainty.</summary>
        internal bool IsDeterministic { get; }

        /// <summary>The stable source-content token used for occurrence seeding.</summary>
        internal string ContentToken { get; }

        /// <summary>The assigned variable ordinal, or -1 before assignment.</summary>
        internal int Ordinal { get; private set; } = -1;

        /// <summary>The first expanded occurrence in canonical pre-order.</summary>
        internal FaultTreeOccurrenceNode? FirstOccurrence { get; private set; }

        /// <summary>Assigns the canonical first-occurrence ordinal during compilation.</summary>
        /// <param name="ordinal">The variable ordinal.</param>
        /// <param name="firstOccurrence">The first expanded occurrence.</param>
        internal void AssignOrdinal(int ordinal, FaultTreeOccurrenceNode firstOccurrence)
        {
            Ordinal = ordinal;
            FirstOccurrence = firstOccurrence;
        }
    }

    /// <summary>One effective node occurrence in an expanded fault-tree plan.</summary>
    internal sealed class FaultTreeOccurrenceNode
    {
        /// <summary>Initializes one expanded occurrence.</summary>
        /// <param name="sourceFunction">The function owning the effective authored node.</param>
        /// <param name="sourceNode">The effective authored node.</param>
        /// <param name="children">Canonical-order expanded children.</param>
        /// <param name="wrappedContentIdentity">The transfer-wrapped content-only identity.</param>
        /// <param name="transferWrapperModes">The outermost-first transfer wrapper modes.</param>
        /// <param name="probabilityIdentity">The content-only source identity, for basic events.</param>
        /// <param name="variable">The unified variable, for basic events.</param>
        /// <param name="displayNode">The authored metadata node that labels this occurrence.</param>
        internal FaultTreeOccurrenceNode(FaultTreeResponse sourceFunction, FaultTreeNodeBase sourceNode,
            IReadOnlyList<FaultTreeOccurrenceNode> children, XElement wrappedContentIdentity,
            IReadOnlyList<TreeLinkMode> transferWrapperModes, XElement? probabilityIdentity,
            FaultTreeVariableSlot? variable, FaultTreeNodeBase displayNode)
        {
            SourceFunction = sourceFunction;
            SourceNode = sourceNode;
            Children = Array.AsReadOnly(children.ToArray());
            _wrappedContentIdentity = new XElement(wrappedContentIdentity);
            TransferWrapperModes = Array.AsReadOnly(transferWrapperModes.ToArray());
            _probabilityIdentity = probabilityIdentity == null ? null : new XElement(probabilityIdentity);
            Variable = variable;
            DisplayNode = displayNode;
            IdentityToken = CanonicalContentHasher.ToTokenHex(CanonicalContentHasher.Hash(
                _wrappedContentIdentity, CanonicalizationRules.ModelRules));
        }

        /// <summary>The function owning the effective authored node.</summary>
        internal FaultTreeResponse SourceFunction { get; }

        /// <summary>The effective authored gate, basic, or house node.</summary>
        internal FaultTreeNodeBase SourceNode { get; }

        /// <summary>Canonical-order expanded children.</summary>
        internal IReadOnlyList<FaultTreeOccurrenceNode> Children { get; }

        /// <summary>The privately owned transfer-wrapped content-only identity.</summary>
        private readonly XElement _wrappedContentIdentity;

        /// <summary>Returns an owned copy of the transfer-wrapped content-only identity.</summary>
        internal XElement WrappedContentIdentity => new XElement(_wrappedContentIdentity);

        /// <summary>The outermost-first transfer wrapper modes crossed to reach this occurrence.</summary>
        internal IReadOnlyList<TreeLinkMode> TransferWrapperModes { get; }

        /// <summary>The privately owned content-only source identity, for basic events.</summary>
        private readonly XElement? _probabilityIdentity;

        /// <summary>The stable token of the content-only wrapped identity.</summary>
        internal string IdentityToken { get; }

        /// <summary>The unified variable, for basic events.</summary>
        internal FaultTreeVariableSlot? Variable { get; }

        /// <summary>The authored metadata node that labels this occurrence.</summary>
        private FaultTreeNodeBase DisplayNode { get; }

        /// <summary>The occurrence display label.</summary>
        internal string DisplayName => DisplayNode.Name;

        /// <summary>The source parent's persistent input order used only for topology inspection.</summary>
        internal int AuthoredSiblingOrder { get; private set; }

        /// <summary>The metadata-free occurrence path assigned after canonical sibling sorting.</summary>
        internal string CanonicalPath { get; private set; } = string.Empty;

        /// <summary>Assigns the source presentation order during compilation.</summary>
        /// <param name="order">The persistent input order.</param>
        internal void AssignAuthoredSiblingOrder(int order)
        {
            AuthoredSiblingOrder = order;
        }

        /// <summary>Assigns the canonical occurrence path during compilation.</summary>
        /// <param name="path">The canonical path.</param>
        internal void AssignCanonicalPath(string path)
        {
            CanonicalPath = path;
        }

        /// <summary>Builds this occurrence's final unwrapped identity with variable ordinals applied.</summary>
        /// <returns>The identity element.</returns>
        internal XElement BuildUnderlyingFinalIdentity()
        {
            var identity = new XElement("Node");
            identity.SetAttributeValue("Type", SourceNode.SerializedName);
            if (SourceNode is FaultTreeGateNode gate)
            {
                identity.SetAttributeValue(nameof(FaultTreeGateNode.GateType), gate.GateType.ToString());
                if (gate.GateType == FaultTreeGateType.KOfN)
                    identity.SetAttributeValue(nameof(FaultTreeGateNode.K), gate.K.ToString(CultureInfo.InvariantCulture));
            }
            if (SourceNode is FaultTreeHouseEventNode house)
                identity.SetAttributeValue(nameof(FaultTreeHouseEventNode.State), house.State);
            if (_probabilityIdentity != null)
            {
                identity.SetAttributeValue("SharedVariable",
                    Variable!.Ordinal.ToString(CultureInfo.InvariantCulture));
                identity.Add(new XElement(_probabilityIdentity));
            }
            for (int i = 0; i < Children.Count; i++)
            {
                XElement child = Children[i].BuildUnderlyingFinalIdentity();
                for (int wrapper = Children[i].TransferWrapperModes.Count - 1; wrapper >= 0; wrapper--)
                {
                    var wrapped = new XElement("Node");
                    wrapped.SetAttributeValue("Type", nameof(FaultTreeTransferNode));
                    wrapped.SetAttributeValue(nameof(FaultTreeTransferNode.LinkMode),
                        Children[i].TransferWrapperModes[wrapper].ToString());
                    wrapped.Add(new XElement("Target", child));
                    child = wrapped;
                }
                identity.Add(child);
            }
            return identity;
        }
    }
}
