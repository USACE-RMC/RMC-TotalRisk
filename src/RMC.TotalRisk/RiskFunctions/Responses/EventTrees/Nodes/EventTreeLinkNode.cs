using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Support;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes
{
    /// <summary>
    /// A structural reference to an internal or external event-tree subtree. An
    /// <see cref="TreeLinkMode.IndependentClone"/> occurrence is evaluated and sampled as an
    /// independent logical clone, while a <see cref="TreeLinkMode.SharedLogicalEvent"/> occurrence
    /// unifies onto the referenced limb's sampling classes so the same draws produce the same
    /// probabilities at every occurrence. Both follow edits to the referenced authored subtree.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Sharing never changes the event-tree path algebra — expansion still multiplies each
    /// occurrence's conditional probability into its own path — it makes repeated occurrences of
    /// one limb draw the same sampled values within each realization, the state-of-knowledge
    /// correlation of a limb modeled once and referenced many times.
    /// </para>
    /// </remarks>
    public sealed class EventTreeLinkNode : EventNodeBase
    {
        /// <summary>Initializes an event-tree link.</summary>
        /// <param name="name">The display name of this authored occurrence.</param>
        /// <param name="target">The internal or external target reference.</param>
        /// <param name="targetFunction">
        /// The live external event-tree response, or null for an internal reference.
        /// </param>
        /// <param name="linkMode">The link semantics; independent clone is the event-tree default.</param>
        /// <exception cref="ArgumentNullException">Thrown when the target is null.</exception>
        /// <exception cref="ArgumentException">Thrown when internal/external addressing is inconsistent.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the link mode is undefined.</exception>
        public EventTreeLinkNode(string name, TreeNodeReference target,
            EventTreeResponse? targetFunction = null,
            TreeLinkMode linkMode = TreeLinkMode.IndependentClone)
            : base(name, true)
        {
            if (!Enum.IsDefined(linkMode)) throw new ArgumentOutOfRangeException(nameof(linkMode));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            bool external = IsExternalReference(target);
            if (external && targetFunction == null)
                throw new ArgumentException("An external event-tree link requires its live target function.", nameof(targetFunction));
            if (!external && targetFunction != null)
                throw new ArgumentException("An internal event-tree link cannot carry an external target function.", nameof(targetFunction));
            TargetFunction = targetFunction;
            LinkMode = linkMode;
        }

        /// <summary>Initializes a restored event-tree link.</summary>
        /// <param name="id">The persistent node id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="description">The display description.</param>
        /// <param name="isFailure">The terminal classification override.</param>
        /// <param name="outputPort">The persistent branch output port.</param>
        /// <param name="linkMode">The link semantics.</param>
        /// <param name="target">The serialized target reference.</param>
        /// <param name="targetFunction">The resolved or inline external target function.</param>
        /// <param name="unresolvedReferences">Any unresolved serialized function references.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the link mode is undefined.</exception>
        internal EventTreeLinkNode(Guid id, string name, string description, bool isFailure,
            int outputPort, TreeLinkMode linkMode, TreeNodeReference target,
            EventTreeResponse? targetFunction, IEnumerable<string> unresolvedReferences)
            : base(id, name, description, isFailure, outputPort)
        {
            if (!Enum.IsDefined(linkMode)) throw new ArgumentOutOfRangeException(nameof(linkMode));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            TargetFunction = targetFunction;
            LinkMode = linkMode;
            _unresolvedReferences.AddRange(unresolvedReferences ?? throw new ArgumentNullException(nameof(unresolvedReferences)));
        }

        /// <summary>The persisted target supplied at construction or read time.</summary>
        private readonly TreeNodeReference _target;

        /// <summary>Unresolved external-function references retained for validation.</summary>
        private readonly List<string> _unresolvedReferences = new List<string>();

        /// <summary>
        /// The current target address. Once attached, current node and external-function metadata
        /// are projected so lenient fallbacks, renames, and function-id regeneration are repaired
        /// on the next write.
        /// </summary>
        public TreeNodeReference Target
        {
            get
            {
                EventTree? targetTree = TargetFunction?.EventTree ?? Owner;
                if (targetTree == null) return _target;
                EventNodeBase? node = ResolveNode(targetTree, out _);
                return new TreeNodeReference(TargetFunction?.Id, node?.Id ?? _target.NodeId,
                    TargetFunction?.Name, node?.Name ?? _target.NodeName);
            }
        }

        /// <summary>The live external event-tree response, or null for an internal link.</summary>
        public EventTreeResponse? TargetFunction { get; }

        /// <summary>The link semantics; event trees support both modes.</summary>
        public TreeLinkMode LinkMode { get; }

        /// <inheritdoc/>
        internal override string SerializedName => nameof(EventTreeLinkNode);

        /// <summary>Whether the stored address identifies an external function.</summary>
        internal bool IsExternal => IsExternalReference(_target);

        /// <summary>Unresolved external references retained for validation diagnostics.</summary>
        internal IReadOnlyList<string> UnresolvedReferences => _unresolvedReferences;

        /// <summary>Resolves the target node within a selected target tree.</summary>
        /// <param name="targetTree">The internal or external target tree.</param>
        /// <param name="usedNameFallback">Whether the node-name fallback was used.</param>
        /// <returns>The resolved node, or null.</returns>
        internal EventNodeBase? ResolveNode(EventTree targetTree, out bool usedNameFallback)
        {
            usedNameFallback = false;
            EventNodeBase? byId = targetTree.FindById(_target.NodeId);
            if (byId != null) return byId;
            if (string.IsNullOrEmpty(_target.NodeName)) return null;

            EventNodeBase[] byName = targetTree.FindByName(_target.NodeName!).ToArray();
            if (byName.Length != 1) return null;
            usedNameFallback = true;
            return byName[0];
        }

        /// <summary>Writes the compute-relevant link mode and persistence target.</summary>
        /// <param name="element">The containing node element.</param>
        /// <param name="mode">The nested-function serialization mode.</param>
        internal void WriteContent(XElement element, RiskSerializationMode mode)
        {
            element.SetAttributeValue(nameof(LinkMode), LinkMode.ToString());
            TreeNodeReference target = Target;
            var targetElement = new XElement("Target");
            if (target.FunctionId.HasValue)
                targetElement.SetAttributeValue(nameof(TreeNodeReference.FunctionId), target.FunctionId.Value.ToString("D"));
            targetElement.SetAttributeValue(nameof(TreeNodeReference.NodeId), target.NodeId.ToString("D"));
            if (!string.IsNullOrEmpty(target.FunctionName))
                targetElement.SetAttributeValue(nameof(TreeNodeReference.FunctionName), target.FunctionName);
            if (!string.IsNullOrEmpty(target.NodeName))
                targetElement.SetAttributeValue(nameof(TreeNodeReference.NodeName), target.NodeName);
            element.Add(targetElement);

            if (!IsExternal) return;
            var function = new XElement("Function");
            if (TargetFunction != null)
            {
                function.Add(FunctionEntry.Write(TargetFunction, mode));
            }
            else
            {
                if (mode != RiskSerializationMode.ByReference)
                    throw new InvalidOperationException($"Event-tree link '{Name}' cannot be written self-contained because its external target is unresolved.");
                var reference = new XElement(FunctionEntry.ReferenceElementName);
                if (target.FunctionId.HasValue) reference.SetAttributeValue("Id", target.FunctionId.Value.ToString("D"));
                reference.SetAttributeValue("Name", target.FunctionName ?? string.Empty);
                function.Add(reference);
            }
            element.Add(function);
        }

        /// <summary>Reads the link-specific portion of a serialized node.</summary>
        /// <param name="element">The serialized node.</param>
        /// <param name="resolver">The optional external-function resolver.</param>
        /// <param name="ownerName">The owning response name for diagnostics.</param>
        /// <returns>The parsed link content.</returns>
        internal static (TreeLinkMode Mode, TreeNodeReference Target,
            EventTreeResponse? Function, IReadOnlyList<string> Unresolved) ReadContent(
            XElement element, IRiskFunctionResolver? resolver, string ownerName)
        {
            TreeLinkMode mode = SerializationUtilities.ReadEnum(element, nameof(LinkMode), TreeLinkMode.IndependentClone);
            if (!Enum.IsDefined(mode))
                throw new InvalidOperationException("A serialized event-tree link has an undefined link mode.");
            XElement targetElement = element.Element("Target")
                ?? throw new InvalidOperationException("A serialized event-tree link has no Target element.");
            Guid? functionId = Guid.TryParse(targetElement.Attribute(nameof(TreeNodeReference.FunctionId))?.Value,
                out Guid parsedFunctionId) && parsedFunctionId != Guid.Empty ? parsedFunctionId : null;
            if (!Guid.TryParse(targetElement.Attribute(nameof(TreeNodeReference.NodeId))?.Value,
                out Guid nodeId) || nodeId == Guid.Empty)
                throw new InvalidOperationException("A serialized event-tree link has no valid target node id.");
            string? functionName = targetElement.Attribute(nameof(TreeNodeReference.FunctionName))?.Value;
            string? nodeName = targetElement.Attribute(nameof(TreeNodeReference.NodeName))?.Value;
            var target = new TreeNodeReference(functionId, nodeId, functionName, nodeName);
            var unresolved = new List<string>();
            EventTreeResponse? function = null;
            if (IsExternalReference(target))
            {
                XElement child = element.Element("Function")?.Elements().FirstOrDefault()
                    ?? throw new InvalidOperationException("An external event-tree link has no serialized function entry.");
                // Repeated self-contained embeds of one external function must materialize as one
                // live instance, or shared-logical occurrences would stop unifying after a round
                // trip and both the sampled values and the canonical identity would move.
                bool embedded = child.Name.LocalName == nameof(EventTreeResponse)
                    && Guid.TryParse(child.Attribute("Id")?.Value, out Guid embeddedId)
                    && embeddedId != Guid.Empty;
                function = embedded
                    ? EventTreeReadScope.GetOrAdd(
                        Guid.Parse(child.Attribute("Id")!.Value), child,
                        () => FunctionEntry.Read<EventTreeResponse>(child, resolver,
                            c => RiskFunctionFactory.CreateFromXElement(c, resolver), ownerName,
                            $"The event-tree response '{ownerName}'", "event-tree link target", unresolved))
                    : FunctionEntry.Read<EventTreeResponse>(child, resolver,
                        c => RiskFunctionFactory.CreateFromXElement(c, resolver), ownerName,
                        $"The event-tree response '{ownerName}'", "event-tree link target", unresolved);
            }
            return (mode, target, function, unresolved);
        }

        /// <summary>Determines whether an address selects an external function.</summary>
        private static bool IsExternalReference(TreeNodeReference target)
        {
            return target.FunctionId.HasValue || !string.IsNullOrEmpty(target.FunctionName);
        }
    }
}
