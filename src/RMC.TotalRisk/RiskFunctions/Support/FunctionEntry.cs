using System;
using System.Collections.Generic;
using System.Xml.Linq;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;

namespace RMC.TotalRisk.RiskFunctions.Support
{
    /// <summary>
    /// The single implementation of the serialized function-entry contract shared by every
    /// container that wraps stored functions (graph elements, composite functions): inline
    /// function content under <see cref="RiskSerializationMode.SelfContained"/>, or a
    /// <c>FunctionReference</c> marker carrying the function's id and name under
    /// <see cref="RiskSerializationMode.ByReference"/>, resolved back to the live stored instance
    /// on read.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Extracted from <c>RiskElementBase</c> so the marker shape, the resolver policy (id
    /// authoritative and loud, name a lenient fallback), and the failure messages exist in exactly
    /// one place — a second container implementing its own variant is how reference formats drift.
    /// </para>
    /// </remarks>
    internal static class FunctionEntry
    {
        /// <summary>
        /// The element name marking a serialized function reference, as opposed to inline function
        /// content. Serialized contract.
        /// </summary>
        internal const string ReferenceElementName = "FunctionReference";

        /// <summary>
        /// Serializes one wrapped function as a container child: its inline content under
        /// <see cref="RiskSerializationMode.SelfContained"/>, or a
        /// <see cref="ReferenceElementName"/> marker carrying its id and name under
        /// <see cref="RiskSerializationMode.ByReference"/>.
        /// </summary>
        /// <param name="function">The wrapped function.</param>
        /// <param name="mode">The serialization mode.</param>
        /// <returns>The child to place inside the container's function element.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
        internal static XElement Write(IRiskFunction function, RiskSerializationMode mode)
        {
            if (function == null) throw new ArgumentNullException(nameof(function));
            if (mode != RiskSerializationMode.ByReference) return function.ToXElement();

            var reference = new XElement(ReferenceElementName);
            reference.SetAttributeValue("Id", function.Id.ToString("D"));
            reference.SetAttributeValue("Name", function.Name);
            return reference;
        }

        /// <summary>
        /// Reads one serialized function container child: resolves a
        /// <see cref="ReferenceElementName"/> marker through the resolver, or reconstructs inline
        /// content through the cluster factory. Inline content always wins when present, so a
        /// self-contained form loads identically whether or not a resolver was supplied.
        /// </summary>
        /// <typeparam name="T">The cluster interface the wrapped function must satisfy.</typeparam>
        /// <param name="child">The container child.</param>
        /// <param name="resolver">The function resolver; null when reading a self-contained form.</param>
        /// <param name="inlineFactory">The cluster factory reconstructing inline content.</param>
        /// <param name="ownerName">The owning container's name, used in the inline-content failure message.</param>
        /// <param name="ownerDescription">
        /// The owning container described for reference messages, e.g. "The ConsequenceElement
        /// 'Damages'" — the resolver's stale-id message and the wrong-cluster message both lead
        /// with it.
        /// </param>
        /// <param name="linkDescription">
        /// A short description of the reference used in the unresolved-reference record, e.g.
        /// "consequence function".
        /// </param>
        /// <param name="unresolvedSink">Receives a description of each reference that could not be resolved.</param>
        /// <returns>
        /// The wrapped function, or null when a reference could not be resolved (recorded in
        /// <paramref name="unresolvedSink"/> for the owner's <c>Validate()</c>) — never null for
        /// inline content, which throws instead.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when the child, the factory, or the sink is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when inline content cannot be reconstructed (dropping it would lose model content
        /// on the next save), when a serialized reference id is stale, or when the resolved
        /// function is of the wrong cluster.
        /// </exception>
        internal static T? Read<T>(XElement child, IRiskFunctionResolver? resolver,
            Func<XElement, IRiskFunction?> inlineFactory, string ownerName, string ownerDescription,
            string linkDescription, ICollection<string> unresolvedSink)
            where T : class, IRiskFunction
        {
            if (child == null) throw new ArgumentNullException(nameof(child));
            if (inlineFactory == null) throw new ArgumentNullException(nameof(inlineFactory));
            if (unresolvedSink == null) throw new ArgumentNullException(nameof(unresolvedSink));

            if (child.Name.LocalName != ReferenceElementName)
            {
                return inlineFactory(child) as T
                    ?? throw new InvalidOperationException(
                        $"Unrecognized function element '{child.Name.LocalName}' in the serialized element '{ownerName}'. " +
                        "The element cannot be reconstructed faithfully; the serialized form may come from a newer version.");
            }

            Guid? pendingId = Guid.TryParse(child.Attribute("Id")?.Value, out var id) ? id : null;
            string? pendingName = child.Attribute("Name")?.Value;
            string reference = string.IsNullOrEmpty(pendingName)
                ? $"{linkDescription} Id '{pendingId:D}'"
                : $"{linkDescription} '{pendingName}'";

            if (resolver == null)
            {
                unresolvedSink.Add(reference);
                return null;
            }

            var resolved = resolver.Resolve(pendingId, pendingName, ownerDescription);
            if (resolved == null)
            {
                unresolvedSink.Add(reference);
                return null;
            }

            return resolved as T
                ?? throw new InvalidOperationException(
                    $"{ownerDescription} references {reference}, which resolved to a " +
                    $"{resolved.GetType().Name} — the wrong kind of risk function for this element.");
        }
    }
}
