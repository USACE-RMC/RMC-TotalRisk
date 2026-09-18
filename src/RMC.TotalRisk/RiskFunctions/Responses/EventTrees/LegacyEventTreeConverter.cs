using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees.Nodes;

namespace RMC.TotalRisk.RiskFunctions.Responses.EventTrees
{
    /// <summary>
    /// Converts the recursive event-node XML emitted by the v1.0 desktop product into the
    /// explicit v1.1 node-and-edge representation. The converter is deliberately an import-only
    /// boundary; no legacy discriminator or wrapper enters the runtime model or current writer.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal static class LegacyEventTreeConverter
    {
        /// <summary>The exact recursive element name used by the legacy reader and writer.</summary>
        private const string LegacyNodeName = "Node";

        /// <summary>Normalizes a current or legacy response element for the current constructor.</summary>
        /// <param name="source">A current response, a response containing a legacy root, or a direct legacy root.</param>
        /// <returns>The source unchanged when already current; otherwise canonical v1.1-shaped XML.</returns>
        internal static XElement Normalize(XElement source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (IsCurrentResponse(source)) return source;

            XElement? legacyRoot = FindLegacyRoot(source);
            if (legacyRoot == null) return source;
            return ConvertResponse(source, legacyRoot);
        }

        /// <summary>Tests whether an element is a direct legacy initiating-node root.</summary>
        /// <param name="element">The candidate element.</param>
        /// <returns>True for the direct legacy root shape accepted by the factory.</returns>
        internal static bool IsLegacyRoot(XElement element)
        {
            return element != null && element.Name.LocalName == LegacyNodeName;
        }

        /// <summary>Tests whether a response already carries the explicit current tree shape.</summary>
        private static bool IsCurrentResponse(XElement source)
        {
            return source.Name.LocalName == nameof(EventTreeResponse)
                && source.Element(nameof(EventTree))?.Element("Nodes") != null;
        }

        /// <summary>Finds the recursive legacy root in one of the accepted in-memory envelopes.</summary>
        private static XElement? FindLegacyRoot(XElement source)
        {
            if (IsLegacyRoot(source)) return source;
            if (source.Name.LocalName != nameof(EventTreeResponse)) return null;
            XElement[] roots = source.Elements(LegacyNodeName)
                .Concat(source.Element(nameof(EventTree))?.Elements(LegacyNodeName)
                    ?? Enumerable.Empty<XElement>()).ToArray();
            if (roots.Length > 1)
                throw MigrationError("root",
                    "the legacy event-tree response envelope contains more than one recursive Node root");
            return roots.SingleOrDefault();
        }

        /// <summary>Converts one legacy recursive tree into a current response envelope.</summary>
        private static XElement ConvertResponse(XElement source, XElement root)
        {
            string rootType = ReadRequiredType(root, "root");
            if (!string.Equals(rootType, nameof(InitiatingNode), StringComparison.Ordinal))
                throw MigrationError("root",
                    $"the legacy event-tree root must be an {nameof(InitiatingNode)}, not '{rootType}'");

            double[] hazards = ReadHazardLevels(root);
            var state = new ConversionState(hazards);
            Guid rootId = ConvertNode(root, null, 0, "root", "root", state);

            var response = new XElement(nameof(EventTreeResponse));
            bool directRoot = ReferenceEquals(source, root);
            response.SetAttributeValue("Id", ReadResponseId(source, root).ToString("D"));
            response.SetAttributeValue("Name", directRoot
                ? ReadMetadata(root, "TemplateName", ReadMetadata(root, "Name", string.Empty))
                : ReadMetadata(source, "Name",
                    ReadMetadata(root, "TemplateName", ReadMetadata(root, "Name", string.Empty))));
            response.SetAttributeValue("Description", directRoot
                ? ReadMetadata(root, "TemplateDescription", ReadMetadata(root, "Description", string.Empty))
                : ReadMetadata(source, "Description",
                    ReadMetadata(root, "TemplateDescription", ReadMetadata(root, "Description", string.Empty))));
            response.SetAttributeValue("SpecifiedHazard", directRoot
                ? ReadMetadata(root, "Name", string.Empty)
                : ReadMetadata(source, "SpecifiedHazard", ReadMetadata(root, "Name", string.Empty)));
            response.SetAttributeValue("HazardUnit", directRoot
                ? string.Empty
                : ReadMetadata(source, "HazardUnit", string.Empty));

            var hazardElement = new XElement("HazardLevels");
            foreach (double hazard in hazards)
            {
                var level = new XElement("Level");
                level.SetAttributeValue("Value", SerializationUtilities.FormatDouble(hazard));
                hazardElement.Add(level);
            }
            response.Add(hazardElement);

            var tree = new XElement(nameof(EventTree));
            tree.SetAttributeValue("RootNodeId", rootId.ToString("D"));
            tree.Add(new XElement("Nodes", state.Nodes));
            tree.Add(new XElement("Edges", state.Edges));
            response.Add(tree);
            return response;
        }

        /// <summary>Recursively flattens one legacy node and its child edges.</summary>
        private static Guid ConvertNode(XElement legacyNode, Guid? parentId, int childIndex,
            string persistencePath, string diagnosticPath, ConversionState state)
        {
            string type = ReadRequiredType(legacyNode, diagnosticPath);
            RejectExcludedType(type, diagnosticPath);
            if (type != nameof(InitiatingNode)
                && type != nameof(ChanceNode)
                && type != nameof(RemainderNode))
            {
                throw MigrationError(diagnosticPath,
                    $"unsupported legacy node type '{type}'");
            }
            if (parentId != null && type == nameof(InitiatingNode))
                throw MigrationError(diagnosticPath,
                    "an initiating node may appear only at the event-tree root");

            string name = ReadMetadata(legacyNode, "Name", string.Empty);
            string currentPath = diagnosticPath == "root"
                ? $"root/{type}('{name}')"
                : $"{diagnosticPath}/{type}('{name}')";
            Guid id = ReadNodeId(legacyNode, persistencePath, type, currentPath);
            if (!state.Ids.Add(id))
                throw MigrationError(currentPath, $"duplicate legacy node id '{id:D}'");

            var currentNode = new XElement(type);
            currentNode.SetAttributeValue("Id", id.ToString("D"));
            currentNode.SetAttributeValue("Name", name);
            currentNode.SetAttributeValue("Description", ReadMetadata(legacyNode, "Description", string.Empty));
            currentNode.SetAttributeValue("IsFailure", type == nameof(ChanceNode));
            currentNode.SetAttributeValue("OutputPort", type == nameof(InitiatingNode) ? "0" : "-1");
            if (type == nameof(ChanceNode))
                currentNode.Add(ConvertProbabilitySource(legacyNode, currentPath, state.Hazards));
            state.Nodes.Add(currentNode);

            if (parentId != null)
            {
                var edge = new XElement("Edge");
                edge.SetAttributeValue("ParentNodeId", parentId.Value.ToString("D"));
                edge.SetAttributeValue("ChildNodeId", id.ToString("D"));
                edge.SetAttributeValue("Order", childIndex.ToString(CultureInfo.InvariantCulture));
                state.Edges.Add(edge);
            }

            XElement[] children = legacyNode.Elements(LegacyNodeName).ToArray();
            if (type == nameof(RemainderNode) && children.Length != 0)
                throw MigrationError(currentPath, "a legacy remainder node cannot own child nodes");

            int remainderCount = children.Count(child =>
                string.Equals(child.Attribute("Type")?.Value, nameof(RemainderNode), StringComparison.Ordinal));
            if (remainderCount > 1)
                throw MigrationError(currentPath, "more than one legacy remainder child was serialized");
            if (remainderCount == 1
                && !string.Equals(children[^1].Attribute("Type")?.Value, nameof(RemainderNode), StringComparison.Ordinal))
            {
                throw MigrationError(currentPath, "the legacy remainder child must be serialized last");
            }

            for (int i = 0; i < children.Length; i++)
            {
                ConvertNode(children[i], id, i, $"{persistencePath}/{i}", currentPath, state);
            }

            if (children.Length != 0 && remainderCount == 0)
            {
                Guid remainderId = CreateDeterministicId($"{persistencePath}/implicit-remainder", nameof(RemainderNode));
                if (!state.Ids.Add(remainderId))
                    throw MigrationError(currentPath, "the generated legacy remainder id collided with another node id");
                var remainder = new XElement(nameof(RemainderNode));
                remainder.SetAttributeValue("Id", remainderId.ToString("D"));
                remainder.SetAttributeValue("Name", "Remainder");
                remainder.SetAttributeValue("Description", string.Empty);
                remainder.SetAttributeValue("IsFailure", false);
                remainder.SetAttributeValue("OutputPort", "-1");
                state.Nodes.Add(remainder);

                var edge = new XElement("Edge");
                edge.SetAttributeValue("ParentNodeId", id.ToString("D"));
                edge.SetAttributeValue("ChildNodeId", remainderId.ToString("D"));
                edge.SetAttributeValue("Order", children.Length.ToString(CultureInfo.InvariantCulture));
                state.Edges.Add(edge);
            }

            return id;
        }

        /// <summary>Converts the four legacy chance-source discriminators.</summary>
        private static XElement ConvertProbabilitySource(XElement node, string path,
            IReadOnlyList<double> hazards)
        {
            string sourceKind = ReadMetadata(node, "SourceProbability", "MultiValue");
            if (string.Equals(sourceKind, "MultiValue", StringComparison.Ordinal))
                return ConvertMultiValue(node, path);
            if (string.Equals(sourceKind, "SingleValue", StringComparison.Ordinal))
                return ConvertSingleValue(node, path, hazards);
            if (string.Equals(sourceKind, "ResponseFunction", StringComparison.Ordinal))
                return ConvertResponseReference(node, path);
            if (string.Equals(sourceKind, "EventNode", StringComparison.Ordinal))
            {
                string target = ReadMetadata(node, "ReferenceNode", string.Empty);
                if (string.IsNullOrWhiteSpace(target))
                    throw MigrationError(path, "a legacy EventNode probability source has no ReferenceNode id");
                if (!Guid.TryParse(target, out Guid targetId) || targetId == Guid.Empty)
                    throw MigrationError(path, $"legacy EventNode ReferenceNode id '{target}' is invalid");
                throw MigrationError(path,
                    $"legacy EventNode probability reference '{target}' is unsupported because its shared-probability semantics cannot be represented by an IndependentClone structural link without changing results");
            }
            throw MigrationError(path, $"unsupported legacy probability source '{sourceKind}'");
        }

        /// <summary>Converts a legacy hazard-aligned uncertain probability table.</summary>
        private static XElement ConvertMultiValue(XElement node, string path)
        {
            XElement tableElement = node.Element("IntervalDistributions")
                ?? throw MigrationError(path, "a MultiValue probability source has no IntervalDistributions element");
            try
            {
                var table = new UncertainOrderedPairedData(new XElement(tableElement))
                {
                    OrderX = SortOrder.Ascending,
                    OrderY = SortOrder.None,
                    StrictX = true,
                    StrictY = false,
                };
                table.Validate();
                if (!table.IsValid)
                    throw MigrationError(path, "the MultiValue IntervalDistributions content is invalid");
                return CreateTabularSource(table);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith(
                "Legacy event-tree conversion failed at '", StringComparison.Ordinal))
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException
                || ex is InvalidOperationException || ex is NotSupportedException
                || ex is NullReferenceException)
            {
                throw MigrationError(path, $"the MultiValue IntervalDistributions content is malformed: {ex.Message}", ex);
            }
        }

        /// <summary>Converts a legacy all-hazards distribution without losing its one-dimension uncertainty.</summary>
        private static XElement ConvertSingleValue(XElement node, string path,
            IReadOnlyList<double> hazards)
        {
            XElement? distributionElement = node.Element("AllHazardsDistribution");
            UnivariateDistributionBase distribution;
            try
            {
                distribution = distributionElement == null
                    ? new Deterministic(0.5d)
                    : UnivariateDistributionFactory.CreateDistribution(distributionElement);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException
                || ex is InvalidOperationException || ex is NotSupportedException || ex is NullReferenceException)
            {
                throw MigrationError(path, $"the SingleValue AllHazardsDistribution content is malformed: {ex.Message}", ex);
            }

            if (distribution.Type == UnivariateDistributionType.Deterministic)
            {
                var scalar = new XElement(nameof(ProbabilitySource));
                scalar.SetAttributeValue("Kind", ProbabilitySourceKind.DeterministicScalar.ToString());
                scalar.SetAttributeValue("ScalarProbability", SerializationUtilities.FormatDouble(distribution.Mean));
                return scalar;
            }

            var ordinates = hazards
                .Select(hazard => new UncertainOrdinate(hazard, distribution.Clone()))
                .ToArray();
            var table = new UncertainOrderedPairedData(ordinates, true, SortOrder.Ascending,
                false, SortOrder.None, distribution.Type);
            return CreateTabularSource(table);
        }

        /// <summary>Converts a legacy name-only response reference through the existing resolver surface.</summary>
        private static XElement ConvertResponseReference(XElement node, string path)
        {
            string name = ReadMetadata(node, "ResponseFunction", string.Empty);
            if (string.IsNullOrWhiteSpace(name))
                throw MigrationError(path, "a ResponseFunction probability source has no referenced function name");

            var marker = new XElement("FunctionReference");
            marker.SetAttributeValue("Name", name);
            var source = new XElement(nameof(ProbabilitySource));
            source.SetAttributeValue("Kind", ProbabilitySourceKind.ResponseFunctionReference.ToString());
            source.Add(new XElement("Function", marker));
            return source;
        }

        /// <summary>Creates a current uncertain-tabular probability-source element.</summary>
        private static XElement CreateTabularSource(UncertainOrderedPairedData table)
        {
            var source = new XElement(nameof(ProbabilitySource));
            source.SetAttributeValue("Kind", ProbabilitySourceKind.UncertainTabular.ToString());
            source.Add(table.SaveToXElement());
            return source;
        }

        /// <summary>Reads and validates the legacy root hazard-axis attribute.</summary>
        private static double[] ReadHazardLevels(XElement root)
        {
            XAttribute? attribute = root.Attribute("HazardLevels") ?? root.Attribute("HazardIntervals");
            if (attribute == null)
                throw MigrationError("root", "the legacy initiating node has no HazardLevels or HazardIntervals attribute");
            string[] tokens = attribute.Value.Split('|');
            if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace))
                throw MigrationError("root", $"the legacy hazard axis '{attribute.Value}' is malformed");

            var values = new double[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!double.TryParse(tokens[i], NumberStyles.Any, CultureInfo.InvariantCulture, out values[i])
                    || !double.IsFinite(values[i]))
                {
                    throw MigrationError("root",
                        $"legacy hazard level '{tokens[i]}' at index {i} is not a finite invariant-culture number");
                }
                if (i > 0 && values[i] <= values[i - 1])
                    throw MigrationError("root", "legacy hazard levels must be strictly ascending");
            }
            return values;
        }

        /// <summary>Reads a required legacy node discriminator.</summary>
        private static string ReadRequiredType(XElement node, string path)
        {
            if (node.Name.LocalName != LegacyNodeName)
                throw MigrationError(path, $"expected a legacy Node element, not '{node.Name.LocalName}'");
            string type = ReadMetadata(node, "Type", string.Empty);
            if (string.IsNullOrWhiteSpace(type))
                throw MigrationError(path, "a legacy node has no Type discriminator");
            return type;
        }

        /// <summary>Rejects explicitly out-of-scope legacy node types with stable migration guidance.</summary>
        private static void RejectExcludedType(string type, string path)
        {
            if (string.Equals(type, "SecondaryHazardNode", StringComparison.Ordinal))
                throw MigrationError(path,
                    "SecondaryHazardNode is excluded by the event-tree design");
            if (string.Equals(type, "WeightedHazardLevel", StringComparison.Ordinal))
                throw MigrationError(path,
                    "WeightedHazardLevel is a bivariate-response construct and cannot be loaded as an event-tree node");
        }

        /// <summary>Reads a persistent node id, accepting both legacy casing variants.</summary>
        private static Guid ReadNodeId(XElement node, string persistencePath, string type, string diagnosticPath)
        {
            XAttribute? attribute = node.Attribute("NodeGuid") ?? node.Attribute("NodeGUID");
            if (attribute == null || string.IsNullOrWhiteSpace(attribute.Value))
                return CreateDeterministicId(persistencePath, type);
            if (Guid.TryParse(attribute.Value, out Guid id) && id != Guid.Empty) return id;
            throw MigrationError(diagnosticPath, $"legacy node id '{attribute.Value}' is not a non-empty Guid");
        }

        /// <summary>Preserves a valid current response id or derives a deterministic import id.</summary>
        private static Guid ReadResponseId(XElement source, XElement root)
        {
            if (Guid.TryParse(source.Attribute("Id")?.Value, out Guid id) && id != Guid.Empty) return id;
            string rootId = root.Attribute("NodeGuid")?.Value
                ?? root.Attribute("NodeGUID")?.Value
                ?? "missing-root-id";
            return CreateDeterministicId(rootId, nameof(EventTreeResponse));
        }

        /// <summary>Creates a deterministic persistence-only id for missing legacy identities.</summary>
        private static Guid CreateDeterministicId(string path, string type)
        {
            var identity = new XElement("LegacyEventTreePersistenceId");
            identity.SetAttributeValue("Path", path);
            identity.SetAttributeValue("Type", type);
            byte[] hash = CanonicalContentHasher.Hash(identity, CanonicalizationRules.ModelRules);
            var bytes = new byte[16];
            Array.Copy(hash, bytes, bytes.Length);
            return new Guid(bytes);
        }

        /// <summary>Reads one optional metadata attribute without culture-sensitive conversion.</summary>
        private static string ReadMetadata(XElement element, string name, string defaultValue)
        {
            return element.Attribute(name)?.Value ?? defaultValue;
        }

        /// <summary>Creates a deterministic path-qualified migration exception.</summary>
        private static InvalidOperationException MigrationError(string path, string message,
            Exception? innerException = null)
        {
            return new InvalidOperationException(
                $"Legacy event-tree conversion failed at '{path}': {message}.", innerException);
        }

        /// <summary>Mutable output owned by one all-or-nothing conversion call.</summary>
        private sealed class ConversionState
        {
            /// <summary>Initializes conversion output over the legacy hazard axis.</summary>
            internal ConversionState(IReadOnlyList<double> hazards)
            {
                Hazards = hazards;
            }

            /// <summary>The legacy hazard axis used to expand SingleValue uncertainty.</summary>
            internal IReadOnlyList<double> Hazards { get; }

            /// <summary>The flattened current node elements.</summary>
            internal List<XElement> Nodes { get; } = new List<XElement>();

            /// <summary>The flattened current edge elements.</summary>
            internal List<XElement> Edges { get; } = new List<XElement>();

            /// <summary>All allocated persistent node identities.</summary>
            internal HashSet<Guid> Ids { get; } = new HashSet<Guid>();
        }
    }
}
