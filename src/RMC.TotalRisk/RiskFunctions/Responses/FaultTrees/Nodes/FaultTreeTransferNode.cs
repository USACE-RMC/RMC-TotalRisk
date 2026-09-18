using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Support;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes
{
    /// <summary>
    /// A structural reference to an internal or external fault-tree subtree. A
    /// <see cref="TreeLinkMode.SharedLogicalEvent"/> transfer resolves every repeated occurrence
    /// to the same Boolean variables, so <c>AND(A, A)</c> reduces to <c>A</c>; an
    /// <see cref="TreeLinkMode.IndependentClone"/> transfer forks a fresh independent variable
    /// context for deliberate repeated-but-independent equipment.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed class FaultTreeTransferNode : FaultTreeNodeBase
    {
        /// <summary>Initializes a fault-tree transfer.</summary>
        /// <param name="name">The display name of this authored occurrence.</param>
        /// <param name="target">The internal or external target reference.</param>
        /// <param name="targetFunction">
        /// The live external fault-tree response, or null for an internal reference.
        /// </param>
        /// <param name="linkMode">The link semantics; shared logical event is the fault default.</param>
        /// <exception cref="ArgumentNullException">Thrown when the target is null.</exception>
        /// <exception cref="ArgumentException">Thrown when internal/external addressing is inconsistent.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the link mode is undefined.</exception>
        public FaultTreeTransferNode(string name, TreeNodeReference target,
            FaultTreeResponse? targetFunction = null,
            TreeLinkMode linkMode = TreeLinkMode.SharedLogicalEvent)
            : base(name)
        {
            if (!Enum.IsDefined(linkMode)) throw new ArgumentOutOfRangeException(nameof(linkMode));
            _target = target ?? throw new ArgumentNullException(nameof(target));
            bool external = IsExternalReference(target);
            if (external && targetFunction == null)
                throw new ArgumentException("An external fault-tree transfer requires its live target function.", nameof(targetFunction));
            if (!external && targetFunction != null)
                throw new ArgumentException("An internal fault-tree transfer cannot carry an external target function.", nameof(targetFunction));
            TargetFunction = targetFunction;
            LinkMode = linkMode;
        }

        /// <summary>Initializes a restored fault-tree transfer.</summary>
        /// <param name="id">The persistent node id.</param>
        /// <param name="name">The display name.</param>
        /// <param name="description">The display description.</param>
        /// <param name="linkMode">The link semantics.</param>
        /// <param name="target">The serialized target reference.</param>
        /// <param name="targetFunction">The resolved or inline external target function.</param>
        /// <param name="unresolvedReferences">Any unresolved serialized function references.</param>
        /// <exception cref="ArgumentNullException">Thrown when the target or unresolved list is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the link mode is undefined.</exception>
        internal FaultTreeTransferNode(Guid id, string name, string description,
            TreeLinkMode linkMode, TreeNodeReference target, FaultTreeResponse? targetFunction,
            IEnumerable<string> unresolvedReferences)
            : base(id, name, description)
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
                FaultTree? targetTree = TargetFunction?.FaultTree ?? Owner;
                if (targetTree == null) return _target;
                FaultTreeNodeBase? node = ResolveNode(targetTree, out _);
                return new TreeNodeReference(TargetFunction?.Id, node?.Id ?? _target.NodeId,
                    TargetFunction?.Name, node?.Name ?? _target.NodeName);
            }
        }

        /// <summary>The live external fault-tree response, or null for an internal transfer.</summary>
        public FaultTreeResponse? TargetFunction { get; }

        /// <summary>The link semantics; fault trees support both modes.</summary>
        public TreeLinkMode LinkMode { get; }

        /// <inheritdoc/>
        internal override string SerializedName => nameof(FaultTreeTransferNode);

        /// <summary>Whether the stored address identifies an external function.</summary>
        internal bool IsExternal => IsExternalReference(_target);

        /// <summary>Unresolved external references retained for validation diagnostics.</summary>
        internal IReadOnlyList<string> UnresolvedReferences => _unresolvedReferences;

        /// <summary>Resolves the target node within a selected target tree.</summary>
        /// <param name="targetTree">The internal or external target tree.</param>
        /// <param name="usedNameFallback">Whether the node-name fallback was used.</param>
        /// <returns>The resolved node, or null.</returns>
        internal FaultTreeNodeBase? ResolveNode(FaultTree targetTree, out bool usedNameFallback)
        {
            usedNameFallback = false;
            FaultTreeNodeBase? byId = targetTree.FindById(_target.NodeId);
            if (byId != null) return byId;
            if (string.IsNullOrEmpty(_target.NodeName)) return null;

            FaultTreeNodeBase[] byName = targetTree.FindByName(_target.NodeName!).ToArray();
            if (byName.Length != 1) return null;
            usedNameFallback = true;
            return byName[0];
        }

        /// <summary>Writes the compute-relevant link mode and persistence target.</summary>
        /// <param name="element">The containing node element.</param>
        /// <param name="mode">The nested-function serialization mode.</param>
        /// <exception cref="InvalidOperationException">Thrown when an unresolved external target is written self-contained.</exception>
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
                    throw new InvalidOperationException($"Fault-tree transfer '{Name}' cannot be written self-contained because its external target is unresolved.");
                var reference = new XElement(FunctionEntry.ReferenceElementName);
                if (target.FunctionId.HasValue) reference.SetAttributeValue("Id", target.FunctionId.Value.ToString("D"));
                reference.SetAttributeValue("Name", target.FunctionName ?? string.Empty);
                function.Add(reference);
            }
            element.Add(function);
        }

        /// <summary>Reads the transfer-specific portion of a serialized node.</summary>
        /// <param name="element">The serialized node.</param>
        /// <param name="resolver">The optional external-function resolver.</param>
        /// <param name="ownerName">The owning response name for diagnostics.</param>
        /// <returns>The parsed transfer content.</returns>
        /// <exception cref="InvalidOperationException">Thrown when required content is absent or malformed.</exception>
        internal static (TreeLinkMode Mode, TreeNodeReference Target,
            FaultTreeResponse? Function, IReadOnlyList<string> Unresolved) ReadContent(
            XElement element, IRiskFunctionResolver? resolver, string ownerName)
        {
            TreeLinkMode mode = SerializationUtilities.ReadEnum(element, nameof(LinkMode), TreeLinkMode.SharedLogicalEvent);
            if (!Enum.IsDefined(mode))
                throw new InvalidOperationException("A serialized fault-tree transfer has an undefined link mode.");
            XElement targetElement = element.Element("Target")
                ?? throw new InvalidOperationException("A serialized fault-tree transfer has no Target element.");
            Guid? functionId = Guid.TryParse(targetElement.Attribute(nameof(TreeNodeReference.FunctionId))?.Value,
                out Guid parsedFunctionId) && parsedFunctionId != Guid.Empty ? parsedFunctionId : null;
            if (!Guid.TryParse(targetElement.Attribute(nameof(TreeNodeReference.NodeId))?.Value,
                out Guid nodeId) || nodeId == Guid.Empty)
                throw new InvalidOperationException("A serialized fault-tree transfer has no valid target node id.");
            string? functionName = targetElement.Attribute(nameof(TreeNodeReference.FunctionName))?.Value;
            string? nodeName = targetElement.Attribute(nameof(TreeNodeReference.NodeName))?.Value;
            var target = new TreeNodeReference(functionId, nodeId, functionName, nodeName);
            var unresolved = new List<string>();
            FaultTreeResponse? function = null;
            if (IsExternalReference(target))
            {
                XElement child = element.Element("Function")?.Elements().FirstOrDefault()
                    ?? throw new InvalidOperationException("An external fault-tree transfer has no serialized function entry.");
                // Repeated self-contained embeds of one external function must materialize as one
                // live instance, or shared-logical occurrences would stop unifying after a round
                // trip and both the sampled values and the canonical identity would move.
                bool embedded = child.Name.LocalName == nameof(FaultTreeResponse)
                    && Guid.TryParse(child.Attribute("Id")?.Value, out Guid embeddedId)
                    && embeddedId != Guid.Empty;
                function = embedded
                    ? FaultTreeReadScope.GetOrAdd(
                        Guid.Parse(child.Attribute("Id")!.Value), child,
                        () => FunctionEntry.Read<FaultTreeResponse>(child, resolver,
                            c => RiskFunctionFactory.CreateFromXElement(c, resolver), ownerName,
                            $"The fault-tree response '{ownerName}'", "fault-tree transfer target", unresolved))
                    : FunctionEntry.Read<FaultTreeResponse>(child, resolver,
                        c => RiskFunctionFactory.CreateFromXElement(c, resolver), ownerName,
                        $"The fault-tree response '{ownerName}'", "fault-tree transfer target", unresolved);
            }
            return (mode, target, function, unresolved);
        }

        /// <summary>Determines whether an address selects an external function.</summary>
        /// <param name="target">The target reference.</param>
        /// <returns>True when the address carries external-function identity.</returns>
        private static bool IsExternalReference(TreeNodeReference target)
        {
            return target.FunctionId.HasValue || !string.IsNullOrEmpty(target.FunctionName);
        }
    }
}
