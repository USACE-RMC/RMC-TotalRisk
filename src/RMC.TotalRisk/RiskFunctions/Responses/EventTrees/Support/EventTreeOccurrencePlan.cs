using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Numerics.Data;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees.Support;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Support
{
    /// <summary>
    /// Immutable expanded event-tree occurrence plan. Authored link nodes are retained in the
    /// projected identity and occurrence paths but resolved to their target subtrees for compute.
    /// Chance occurrences unify onto sampling classes keyed by authored node and independent
    /// context: shared-logical links inherit the caller's context so repeated occurrences sample
    /// once, while independent-clone links fork a fresh context whose interior sources become
    /// distinct classes.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal sealed class EventTreeOccurrencePlan
    {
        /// <summary>Initializes a completed occurrence plan.</summary>
        private EventTreeOccurrencePlan(EventTreeOccurrenceNode root,
            IReadOnlyList<string> warnings, TreePlanDependencies dependencies)
        {
            Root = root;
            Warnings = Array.AsReadOnly(warnings.ToArray());
            Dependencies = dependencies;
            var preOrder = new List<EventTreeOccurrenceNode>();
            var stack = new Stack<EventTreeOccurrenceNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                EventTreeOccurrenceNode node = stack.Pop();
                preOrder.Add(node);
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
            }
            CanonicalPreOrder = Array.AsReadOnly(preOrder.ToArray());
            Leaves = Array.AsReadOnly(preOrder
                .Where(node => node.Children.Count == 0 && node.SourceNode is not InitiatingNode)
                .ToArray());

            var classes = new List<EventTreeSamplingClass>();
            var classCounts = new Dictionary<EventTreeSamplingClass, int>(
                ReferenceEqualityComparer.Instance);
            foreach (EventTreeOccurrenceNode node in preOrder)
            {
                if (node.SamplingClass == null) continue;
                if (classCounts.TryGetValue(node.SamplingClass, out int count))
                {
                    classCounts[node.SamplingClass] = count + 1;
                    continue;
                }
                node.SamplingClass.AssignOrdinal(classes.Count);
                classes.Add(node.SamplingClass);
                classCounts.Add(node.SamplingClass, 1);
            }
            bool anyShared = false;
            foreach (EventTreeSamplingClass samplingClass in classes)
            {
                if (classCounts[samplingClass] < 2) continue;
                samplingClass.MarkShared();
                anyShared = true;
            }
            SamplingClasses = Array.AsReadOnly(classes.ToArray());
            SamplingDimensions = classes.Sum(samplingClass => samplingClass.SamplingDimensions);
            IsDeterministic = classes.All(samplingClass => samplingClass.IsDeterministic);
            _identity = new XElement(nameof(EventTree),
                anyShared ? BuildSharedIdentity(root) : root.Identity);
            IdentityToken = CanonicalContentHasher.ToTokenHex(
                CanonicalContentHasher.Hash(_identity, CanonicalizationRules.ModelRules));

            var indexes = new Dictionary<EventTreeOccurrenceNode, int>(
                ReferenceEqualityComparer.Instance);
            for (int i = 0; i < preOrder.Count; i++) indexes.Add(preOrder[i], i);
            var instructions = new EventTreeEvaluationInstruction[preOrder.Count];
            int edgeCount = 0;
            for (int i = 0; i < preOrder.Count; i++)
            {
                int[] childIndexes = preOrder[i].Children.Select(child => indexes[child]).ToArray();
                edgeCount += childIndexes.Length;
                instructions[i] = new EventTreeEvaluationInstruction(preOrder[i], childIndexes);
            }
            EvaluationInstructions = Array.AsReadOnly(instructions);
            ExpandedEdgeCount = edgeCount;
        }

        /// <summary>The expanded initiating root or selected subtree root.</summary>
        internal EventTreeOccurrenceNode Root { get; }

        /// <summary>All expanded occurrences in metadata-inert canonical pre-order.</summary>
        internal IReadOnlyList<EventTreeOccurrenceNode> CanonicalPreOrder { get; }

        /// <summary>Expanded terminal occurrences in canonical pre-order.</summary>
        internal IReadOnlyList<EventTreeOccurrenceNode> Leaves { get; }

        /// <summary>Primitive-index evaluation instructions in parent-before-child order.</summary>
        internal IReadOnlyList<EventTreeEvaluationInstruction> EvaluationInstructions { get; }

        /// <summary>The number of expanded parent-child edges.</summary>
        internal int ExpandedEdgeCount { get; }

        /// <summary>The privately owned projected identity.</summary>
        private readonly XElement _identity;

        /// <summary>Returns an owned copy of the projected identity.</summary>
        internal XElement Identity => new XElement(_identity);

        /// <summary>The stable token of <see cref="Identity"/>.</summary>
        internal string IdentityToken { get; }

        /// <summary>The unified sampling classes in first-occurrence canonical pre-order.</summary>
        internal IReadOnlyList<EventTreeSamplingClass> SamplingClasses { get; }

        /// <summary>The per-unified-class local and recursively referenced sampling-dimension count.</summary>
        internal int SamplingDimensions { get; }

        /// <summary>Whether every expanded local and nested probability source is deterministic.</summary>
        internal bool IsDeterministic { get; }

        /// <summary>Lenient node-name fallback diagnostics discovered during expansion.</summary>
        internal IReadOnlyList<string> Warnings { get; }

        /// <summary>The complete live-content snapshot governing safe plan reuse.</summary>
        internal TreePlanDependencies Dependencies { get; }

        /// <summary>Compiles the full response tree.</summary>
        /// <param name="owner">The owning response.</param>
        /// <returns>The immutable expanded plan.</returns>
        internal static EventTreeOccurrencePlan Compile(EventTreeResponse owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            return Compile(owner, owner.EventTree.Root);
        }

        /// <summary>Compiles one authored subtree in the context of its owning response.</summary>
        /// <param name="owner">The owning response.</param>
        /// <param name="subtreeRoot">The selected authored subtree root.</param>
        /// <returns>The immutable expanded plan.</returns>
        internal static EventTreeOccurrencePlan Compile(EventTreeResponse owner, EventNodeBase subtreeRoot)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (subtreeRoot == null) throw new ArgumentNullException(nameof(subtreeRoot));
            if (!ReferenceEquals(subtreeRoot.Owner, owner.EventTree))
                throw new InvalidOperationException("The selected event-tree subtree does not belong to the response.");

            var compiler = new Compiler(new TreeDependencyCollector());
            var persistence = new List<Guid> { subtreeRoot.Id };
            EventTreeOccurrenceNode root = compiler.Expand(owner, subtreeRoot,
                compiler.RootContext, false, Array.Empty<EventTreeLinkNode>(), persistence);
            AssignCanonicalPaths(root);
            return new EventTreeOccurrencePlan(root, compiler.Warnings.ToArray(),
                compiler.CreateDependencies());
        }

        /// <summary>
        /// Compiles one nested event-tree response on the shared ambient recursion stack while
        /// accumulating its live dependencies into the caller's collector. Cross-kind callers use
        /// this seam so a reference cycle through both tree kinds produces one complete diagnostic
        /// and every nested content edit invalidates the outermost plan.
        /// </summary>
        /// <param name="function">The nested event-tree response.</param>
        /// <param name="collector">The caller's dependency collector.</param>
        /// <returns>The nested plan carrying identity and dimensions.</returns>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        internal static EventTreeOccurrencePlan CompileNested(EventTreeResponse function,
            TreeDependencyCollector collector)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (collector == null) throw new ArgumentNullException(nameof(collector));
            var compiler = new Compiler(collector);
            EventNodeBase rootNode = function.EventTree.Root;
            var persistence = new List<Guid> { rootNode.Id };
            EventTreeOccurrenceNode root = compiler.Expand(function, rootNode,
                compiler.RootContext, false, Array.Empty<EventTreeLinkNode>(), persistence);
            AssignCanonicalPaths(root);
            return new EventTreeOccurrencePlan(root, Array.Empty<string>(),
                TreePlanDependencies.Empty);
        }

        /// <summary>Assigns stable paths after each sibling set has been sorted by projected identity.</summary>
        private static void AssignCanonicalPaths(EventTreeOccurrenceNode root)
        {
            TreeCanonicalPaths.Assign(root, node => node.Children, node => node.IdentityToken,
                (node, path) => node.AssignCanonicalPath(path));
        }

        /// <summary>
        /// Builds the projected identity with shared-class ordinals applied. Chance occurrences
        /// whose class unifies more than one expanded occurrence carry a <c>SharedVariable</c>
        /// ordinal numbered by first canonical occurrence, so shared links to one authored node
        /// hash differently from links to equal-content distinct nodes. Plans without shared
        /// classes never take this path, keeping every unshared identity byte-identical.
        /// </summary>
        /// <param name="root">The expanded root occurrence.</param>
        /// <returns>The stamped identity element.</returns>
        private XElement BuildSharedIdentity(EventTreeOccurrenceNode root)
        {
            var sharedNumbers = new Dictionary<EventTreeSamplingClass, int>(
                ReferenceEqualityComparer.Instance);
            for (int i = 0; i < SamplingClasses.Count; i++)
            {
                if (SamplingClasses[i].IsShared)
                    sharedNumbers.Add(SamplingClasses[i], sharedNumbers.Count);
            }
            return WrapSharedIdentity(BuildSharedIdentityCore(root, sharedNumbers), root);
        }

        /// <summary>Builds one occurrence's stamped underlying identity.</summary>
        /// <param name="node">The expanded occurrence.</param>
        /// <param name="sharedNumbers">The shared-class ordinal numbering.</param>
        /// <returns>The identity element without this occurrence's link wrappers.</returns>
        private static XElement BuildSharedIdentityCore(EventTreeOccurrenceNode node,
            IReadOnlyDictionary<EventTreeSamplingClass, int> sharedNumbers)
        {
            var identity = new XElement("Node");
            identity.SetAttributeValue("Type", node.SourceNode.SerializedName);
            if (node.Children.Count == 0)
                identity.SetAttributeValue(nameof(EventNodeBase.IsFailure), node.IsFailure);
            if (node.SourceNode is ChanceNode chance)
            {
                if (node.SamplingClass!.IsShared)
                {
                    identity.SetAttributeValue("SharedVariable",
                        sharedNumbers[node.SamplingClass].ToString(CultureInfo.InvariantCulture));
                }
                identity.Add(chance.ProbabilitySource.ToIdentityXElement(
                    node.SamplingClass.RecursiveResponseHash));
            }
            for (int i = 0; i < node.Children.Count; i++)
            {
                identity.Add(WrapSharedIdentity(
                    BuildSharedIdentityCore(node.Children[i], sharedNumbers), node.Children[i]));
            }
            return identity;
        }

        /// <summary>Applies one occurrence's link wrappers around its stamped underlying identity.</summary>
        /// <param name="identity">The stamped underlying identity.</param>
        /// <param name="node">The expanded occurrence owning the wrappers.</param>
        /// <returns>The wrapped identity element.</returns>
        private static XElement WrapSharedIdentity(XElement identity, EventTreeOccurrenceNode node)
        {
            bool terminal = node.Children.Count == 0;
            for (int i = node.WrapperModes.Count - 1; i >= 0; i--)
            {
                var wrapper = new XElement("Node");
                wrapper.SetAttributeValue("Type", nameof(EventTreeLinkNode));
                wrapper.SetAttributeValue(nameof(EventTreeLinkNode.LinkMode), node.WrapperModes[i].ToString());
                if (terminal && i == 0) wrapper.SetAttributeValue(nameof(EventNodeBase.IsFailure), node.IsFailure);
                wrapper.Add(new XElement("Target", identity));
                identity = wrapper;
            }
            return identity;
        }

        /// <summary>Stateful depth-first compiler used for cycle paths and lenient warnings.</summary>
        private sealed class Compiler
        {
            /// <summary>Initializes a compiler over one dependency collector.</summary>
            /// <param name="collector">The shared dependency collector.</param>
            internal Compiler(TreeDependencyCollector collector)
            {
                _collector = collector;
            }

            /// <summary>The shared dependency collector.</summary>
            private readonly TreeDependencyCollector _collector;

            /// <summary>The root independent-context object of the outermost compile.</summary>
            internal object RootContext { get; } = new object();

            /// <summary>The unified sampling classes keyed by authored chance node and context.</summary>
            private readonly Dictionary<(ChanceNode Node, object Context), EventTreeSamplingClass> _classes =
                new Dictionary<(ChanceNode, object), EventTreeSamplingClass>();

            /// <summary>
            /// Memoized independent-link fork contexts keyed by authored link and caller context.
            /// A shared limb containing an independent link therefore evaluates as one deep
            /// object: every shared occurrence of the limb reuses the same interior fork, so the
            /// limb samples and computes identically wherever it appears. Absent shared links each
            /// key is reached once and the memo is inert.
            /// </summary>
            private readonly Dictionary<(EventTreeLinkNode Link, object Context), object> _linkContexts =
                new Dictionary<(EventTreeLinkNode, object), object>();

            /// <summary>Lenient resolution warnings.</summary>
            internal List<string> Warnings { get; } = new List<string>();

            /// <summary>Captures the complete dependency state after expansion succeeds.</summary>
            internal TreePlanDependencies CreateDependencies()
            {
                return _collector.CreateDependencies();
            }

            /// <summary>Expands one authored node, following any link wrapper.</summary>
            internal EventTreeOccurrenceNode Expand(EventTreeResponse function, EventNodeBase node,
                object context, bool linkedAncestor, IReadOnlyList<EventTreeLinkNode> pendingLinks,
                IReadOnlyList<Guid> persistencePath)
            {
                if (TreeCompilationScope.IndexOf(function, node.Id) >= 0)
                    throw TreeCompilationScope.CreateCycleError(function, node, EventTreeFrameDescriber.Instance);
                _collector.TreeSources.Add(function);

                TreeCompilationScope.Push(function, node, node.Id, EventTreeFrameDescriber.Instance);
                try
                {
                    if (node is EventTreeLinkNode link)
                    {
                        if (link.Children.Count != 0)
                            throw new InvalidOperationException($"Event-tree link '{link.Name}' in function '{DisplayFunction(function)}' cannot own authored children.");
                        if (link.UnresolvedReferences.Count > 0)
                            throw new InvalidOperationException($"Event-tree link '{link.Name}' in function '{DisplayFunction(function)}' references {string.Join(", ", link.UnresolvedReferences)}, which was not found.");

                        EventTreeResponse targetFunction = link.IsExternal
                            ? link.TargetFunction ?? throw new InvalidOperationException(
                                $"External event-tree link '{link.Name}' in function '{DisplayFunction(function)}' has no resolved target function.")
                            : function;
                        EventTree targetTree = targetFunction.EventTree;
                        EventNodeBase? targetNode = link.ResolveNode(targetTree, out bool usedNameFallback);
                        if (targetNode == null)
                        {
                            TreeNodeReference target = link.Target;
                            throw new InvalidOperationException(
                                $"Event-tree link '{link.Name}' in function '{DisplayFunction(function)}' cannot resolve target node " +
                                $"'{target.NodeName ?? target.NodeId.ToString("D")}' in function '{DisplayFunction(targetFunction)}'.");
                        }
                        if (targetNode is InitiatingNode)
                            throw new InvalidOperationException(
                                $"Event-tree link '{link.Name}' in function '{DisplayFunction(function)}' targets initiating node " +
                                $"'{targetNode.Name}' in function '{DisplayFunction(targetFunction)}'; links must target a chance or remainder subtree.");
                        if (usedNameFallback)
                        {
                            Warnings.Add(
                                $"Warning: Event-tree link '{link.Name}' in function '{DisplayFunction(function)}' resolved target node " +
                                $"'{targetNode.Name}' by its name fallback; save the model to repair the persistent node id.");
                        }

                        var wrappers = new List<EventTreeLinkNode>(pendingLinks.Count + 1);
                        wrappers.AddRange(pendingLinks);
                        wrappers.Add(link);
                        var targetPersistence = new List<Guid>(persistencePath.Count + 1);
                        targetPersistence.AddRange(persistencePath);
                        targetPersistence.Add(targetNode.Id);
                        object targetContext;
                        if (link.LinkMode == TreeLinkMode.SharedLogicalEvent)
                        {
                            targetContext = context;
                        }
                        else if (!_linkContexts.TryGetValue((link, context), out targetContext!))
                        {
                            targetContext = new object();
                            _linkContexts.Add((link, context), targetContext);
                        }
                        return Expand(targetFunction, targetNode, targetContext, true, wrappers,
                            targetPersistence);
                    }

                    EventTreeSamplingClass? samplingClass = null;
                    XElement? probabilityIdentity = null;
                    if (node is ChanceNode chance)
                    {
                        samplingClass = GetOrCreateClass(chance, context);
                        probabilityIdentity = chance.ProbabilitySource.ToIdentityXElement(
                            samplingClass.RecursiveResponseHash);
                    }

                    var children = new List<EventTreeOccurrenceNode>(node.Children.Count);
                    for (int i = 0; i < node.Children.Count; i++)
                    {
                        EventNodeBase child = node.Children[i];
                        var childPersistence = new List<Guid>(persistencePath.Count + 1);
                        childPersistence.AddRange(persistencePath);
                        childPersistence.Add(child.Id);
                        EventTreeOccurrenceNode occurrence = Expand(function, child, context,
                            linkedAncestor || pendingLinks.Count > 0,
                            Array.Empty<EventTreeLinkNode>(), childPersistence);
                        occurrence.AssignAuthoredSiblingOrder(i);
                        children.Add(occurrence);
                    }
                    children = children.Select((child, index) => (Child: child, Index: index))
                        .OrderBy(item => item.Child.IdentityToken, StringComparer.Ordinal)
                        .ThenBy(item => item.Index)
                        .Select(item => item.Child)
                        .ToList();

                    bool terminal = children.Count == 0;
                    bool isFailure = terminal && pendingLinks.Count > 0 ? pendingLinks[0].IsFailure : node.IsFailure;
                    XElement identity = BuildUnderlyingIdentity(node, isFailure, terminal, children,
                        probabilityIdentity);
                    if (pendingLinks.Count > 0)
                    {
                        for (int i = pendingLinks.Count - 1; i >= 0; i--)
                        {
                            EventTreeLinkNode wrapper = pendingLinks[i];
                            var linkIdentity = new XElement("Node");
                            linkIdentity.SetAttributeValue("Type", nameof(EventTreeLinkNode));
                            linkIdentity.SetAttributeValue(nameof(EventTreeLinkNode.LinkMode), wrapper.LinkMode.ToString());
                            if (terminal && i == 0) linkIdentity.SetAttributeValue(nameof(EventNodeBase.IsFailure), isFailure);
                            linkIdentity.Add(new XElement("Target", identity));
                            identity = linkIdentity;
                        }
                    }

                    EventNodeBase displayNode = pendingLinks.Count > 0 && terminal
                        ? pendingLinks[0]
                        : node;
                    var wrapperModes = new TreeLinkMode[pendingLinks.Count];
                    for (int i = 0; i < pendingLinks.Count; i++) wrapperModes[i] = pendingLinks[i].LinkMode;
                    return new EventTreeOccurrenceNode(function, node, children, identity,
                        linkedAncestor || pendingLinks.Count > 0, isFailure, displayNode,
                        string.Join("/", persistencePath.Select(id => id.ToString("N"))),
                        wrapperModes, samplingClass, probabilityIdentity);
                }
                finally
                {
                    TreeCompilationScope.Pop();
                }
            }

            /// <summary>
            /// Compiles one nested response while retaining the caller's active cycle stack. The
            /// nested function samples through its own isolated setup clone, so its expansion
            /// receives a fresh root context and never unifies with the caller's sampling classes.
            /// </summary>
            private EventTreeOccurrencePlan CompileNestedResponse(EventTreeResponse function)
            {
                EventNodeBase rootNode = function.EventTree.Root;
                var persistence = new List<Guid> { rootNode.Id };
                EventTreeOccurrenceNode root = Expand(function, rootNode, new object(), false,
                    Array.Empty<EventTreeLinkNode>(), persistence);
                AssignCanonicalPaths(root);
                return new EventTreeOccurrencePlan(root, Array.Empty<string>(),
                    TreePlanDependencies.Empty);
            }

            /// <summary>Gets or creates the unified sampling class for one chance node in one context.</summary>
            /// <param name="chance">The authored chance node.</param>
            /// <param name="context">The independent-variable context.</param>
            /// <returns>The unified sampling class.</returns>
            private EventTreeSamplingClass GetOrCreateClass(ChanceNode chance, object context)
            {
                if (_classes.TryGetValue((chance, context), out EventTreeSamplingClass? existing))
                    return existing;

                ProbabilitySource source = chance.ProbabilitySource;
                if (source.Table is not null) _collector.Tables.Add(source.Table);
                foreach (var transform in source.HazardTransforms)
                {
                    if (transform != null) _collector.TransformFunctions.Add(transform);
                }
                int samplingDimensions;
                bool isDeterministic;
                byte[]? recursiveResponseHash = null;
                if (source.ResponseFunction is EventTreeResponse nestedResponse)
                {
                    EventTreeOccurrencePlan nestedPlan = CompileNestedResponse(nestedResponse);
                    samplingDimensions = nestedPlan.SamplingDimensions + source.HazardTransformDimensions;
                    isDeterministic = nestedPlan.IsDeterministic && source.HazardTransformsAreDeterministic;
                    recursiveResponseHash = nestedResponse.CanonicalHash(nestedPlan);
                }
                else if (source.ResponseFunction is FaultTrees.FaultTreeResponse nestedFault)
                {
                    FaultTrees.Support.FaultTreeOccurrencePlan nestedPlan =
                        FaultTrees.Support.FaultTreeOccurrencePlan.CompileNested(nestedFault, _collector);
                    samplingDimensions = nestedPlan.SamplingDimensions + source.HazardTransformDimensions;
                    isDeterministic = nestedPlan.IsDeterministic && source.HazardTransformsAreDeterministic;
                    recursiveResponseHash = nestedFault.CanonicalHash(nestedPlan);
                }
                else
                {
                    if (source.ResponseFunction != null)
                        _collector.OrdinaryResponses.Add(source.ResponseFunction);
                    samplingDimensions = source.SamplingDimensions;
                    isDeterministic = source.IsDeterministic;
                }
                var samplingClass = new EventTreeSamplingClass(samplingDimensions, isDeterministic,
                    recursiveResponseHash);
                _classes.Add((chance, context), samplingClass);
                return samplingClass;
            }

            /// <summary>Builds the projected identity of one effective non-link node.</summary>
            private static XElement BuildUnderlyingIdentity(EventNodeBase node, bool isFailure,
                bool terminal, IReadOnlyList<EventTreeOccurrenceNode> children,
                XElement? probabilityIdentity)
            {
                var identity = new XElement("Node");
                identity.SetAttributeValue("Type", node.SerializedName);
                if (terminal) identity.SetAttributeValue(nameof(EventNodeBase.IsFailure), isFailure);
                if (node is ChanceNode) identity.Add(probabilityIdentity);
                for (int i = 0; i < children.Count; i++) identity.Add(children[i].Identity);
                return identity;
            }

            /// <summary>Describes one cycle-path entry without using it as compute identity.</summary>
            private static string Describe(EventTreeResponse function, EventNodeBase node)
            {
                string nodeName = string.IsNullOrEmpty(node.Name) ? node.SerializedName : node.Name;
                return $"'{DisplayFunction(function)}'::'{nodeName}' ({node.SerializedName}, {node.Id:D})";
            }

            /// <summary>Returns a useful function label for diagnostics.</summary>
            private static string DisplayFunction(EventTreeResponse function)
            {
                return string.IsNullOrEmpty(function.Name) ? "<unnamed event tree>" : function.Name;
            }

            /// <summary>The shared event-tree frame renderer for the ambient compilation scope.</summary>
            private sealed class EventTreeFrameDescriber : TreeFrameDescriber
            {
                /// <summary>The shared instance.</summary>
                internal static EventTreeFrameDescriber Instance { get; } = new EventTreeFrameDescriber();

                /// <inheritdoc/>
                internal override string CycleKindLabel => "event-tree";

                /// <inheritdoc/>
                internal override string Describe(object function, object node)
                {
                    return Compiler.Describe((EventTreeResponse)function, (EventNodeBase)node);
                }
            }
        }
    }

    /// <summary>One immutable parent-before-child evaluation instruction.</summary>
    internal sealed class EventTreeEvaluationInstruction
    {
        /// <summary>Initializes one instruction and seals its child-index array.</summary>
        internal EventTreeEvaluationInstruction(EventTreeOccurrenceNode occurrence,
            IReadOnlyList<int> childIndexes)
        {
            Occurrence = occurrence;
            _childIndexes = childIndexes.ToArray();
        }

        /// <summary>The effective occurrence evaluated by this instruction.</summary>
        internal EventTreeOccurrenceNode Occurrence { get; }

        /// <summary>The immutable child-index storage.</summary>
        private readonly int[] _childIndexes;

        /// <summary>The number of outgoing expanded edges.</summary>
        internal int ChildCount => _childIndexes.Length;

        /// <summary>Returns one child instruction index.</summary>
        internal int ChildIndex(int index)
        {
            return _childIndexes[index];
        }
    }

    /// <summary>One effective node occurrence in an expanded event-tree plan.</summary>
    internal sealed class EventTreeOccurrenceNode
    {
        /// <summary>Initializes an expanded occurrence node.</summary>
        internal EventTreeOccurrenceNode(EventTreeResponse sourceFunction, EventNodeBase sourceNode,
            IReadOnlyList<EventTreeOccurrenceNode> children, XElement identity, bool isLinkedOccurrence,
            bool isFailure, EventNodeBase displayNode, string persistencePath,
            IReadOnlyList<TreeLinkMode> wrapperModes, EventTreeSamplingClass? samplingClass,
            XElement? probabilityIdentity)
        {
            SourceFunction = sourceFunction;
            SourceNode = sourceNode;
            Children = Array.AsReadOnly(children.ToArray());
            _identity = new XElement(identity);
            IsLinkedOccurrence = isLinkedOccurrence;
            IsFailure = isFailure;
            DisplayNode = displayNode;
            PersistencePath = persistencePath;
            WrapperModes = Array.AsReadOnly(wrapperModes.ToArray());
            SamplingClass = samplingClass;
            IdentityToken = CanonicalContentHasher.ToTokenHex(CanonicalContentHasher.Hash(
                _identity, CanonicalizationRules.ModelRules));
            ProbabilityIdentityToken = probabilityIdentity == null
                ? string.Empty
                : CanonicalContentHasher.ToTokenHex(
                    CanonicalContentHasher.Hash(probabilityIdentity, CanonicalizationRules.ModelRules));
        }

        /// <summary>The response owning the effective authored source node.</summary>
        internal EventTreeResponse SourceFunction { get; }

        /// <summary>The effective authored chance or remainder node.</summary>
        internal EventNodeBase SourceNode { get; }

        /// <summary>Canonical-order expanded children.</summary>
        internal IReadOnlyList<EventTreeOccurrenceNode> Children { get; }

        /// <summary>The privately owned projected occurrence identity.</summary>
        private readonly XElement _identity;

        /// <summary>Returns an owned copy of the projected occurrence identity.</summary>
        internal XElement Identity => new XElement(_identity);

        /// <summary>The stable token of <see cref="Identity"/>.</summary>
        internal string IdentityToken { get; }

        /// <summary>Whether this occurrence is inside at least one independent link.</summary>
        internal bool IsLinkedOccurrence { get; }

        /// <summary>The effective terminal classification.</summary>
        internal bool IsFailure { get; }

        /// <summary>The authored metadata node that labels this occurrence.</summary>
        private EventNodeBase DisplayNode { get; }

        /// <summary>The occurrence display label.</summary>
        internal string DisplayName => DisplayNode.Name;

        /// <summary>The authored display node's persistent id.</summary>
        internal Guid DisplayNodeId => DisplayNode.Id;

        /// <summary>The persistent-id occurrence path used only for stable branch addressing.</summary>
        internal string PersistencePath { get; }

        /// <summary>The outermost-first link wrapper modes crossed to reach this occurrence.</summary>
        internal IReadOnlyList<TreeLinkMode> WrapperModes { get; }

        /// <summary>The unified sampling class, for chance occurrences.</summary>
        internal EventTreeSamplingClass? SamplingClass { get; }

        /// <summary>The exact recursive sampling dimensions contributed by this chance occurrence's class.</summary>
        internal int ProbabilitySamplingDimensions => SamplingClass?.SamplingDimensions ?? 0;

        /// <summary>Whether this chance occurrence's complete probability-source graph is deterministic.</summary>
        internal bool ProbabilityIsDeterministic => SamplingClass?.IsDeterministic ?? true;

        /// <summary>The stable projected-identity token used for this source occurrence's seed.</summary>
        internal string ProbabilityIdentityToken { get; }

        /// <summary>The source parent's persistent child order used only for topology inspection.</summary>
        internal int AuthoredSiblingOrder { get; private set; }

        /// <summary>The metadata-free occurrence path assigned after canonical sibling sorting.</summary>
        internal string CanonicalPath { get; private set; } = string.Empty;

        /// <summary>Assigns the source presentation order during compilation.</summary>
        internal void AssignAuthoredSiblingOrder(int order)
        {
            AuthoredSiblingOrder = order;
        }

        /// <summary>Assigns the canonical occurrence path during compilation.</summary>
        internal void AssignCanonicalPath(string path)
        {
            CanonicalPath = path;
        }
    }

    /// <summary>
    /// One unified sampling class of an expanded event-tree plan. Every chance occurrence of one
    /// authored node reached in one independent context shares this class, so shared-logical link
    /// occurrences bind one dimension set, one referenced-response clone, and one draw per
    /// realization, while independent-clone occurrences fork distinct classes.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal sealed class EventTreeSamplingClass
    {
        /// <summary>Initializes one unified sampling class before ordinal assignment.</summary>
        /// <param name="samplingDimensions">The recursive sampler-dimension count of the source.</param>
        /// <param name="isDeterministic">Whether the source carries no knowledge uncertainty.</param>
        /// <param name="recursiveResponseHash">The nested tree-response identity hash, when applicable.</param>
        internal EventTreeSamplingClass(int samplingDimensions, bool isDeterministic,
            byte[]? recursiveResponseHash)
        {
            SamplingDimensions = samplingDimensions;
            IsDeterministic = isDeterministic;
            RecursiveResponseHash = recursiveResponseHash;
        }

        /// <summary>The recursive sampler-dimension count of the source.</summary>
        internal int SamplingDimensions { get; }

        /// <summary>Whether the source carries no knowledge uncertainty.</summary>
        internal bool IsDeterministic { get; }

        /// <summary>The nested tree-response identity hash used to rebuild the source identity.</summary>
        internal byte[]? RecursiveResponseHash { get; }

        /// <summary>The class ordinal by first canonical occurrence, or -1 before assignment.</summary>
        internal int Ordinal { get; private set; } = -1;

        /// <summary>Whether more than one expanded occurrence unifies onto this class.</summary>
        internal bool IsShared { get; private set; }

        /// <summary>Assigns the canonical first-occurrence ordinal during plan construction.</summary>
        /// <param name="ordinal">The class ordinal.</param>
        internal void AssignOrdinal(int ordinal)
        {
            Ordinal = ordinal;
        }

        /// <summary>Marks the class as unifying repeated occurrences during plan construction.</summary>
        internal void MarkShared()
        {
            IsShared = true;
        }
    }
}
