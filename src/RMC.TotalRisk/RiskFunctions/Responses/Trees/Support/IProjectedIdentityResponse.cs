using System.Xml.Linq;

namespace RMC.TotalRisk.RiskFunctions.Responses.Trees.Support
{
    /// <summary>
    /// A response function whose canonical identity is a projected form rather than its persisted
    /// XML. Component and failure-mode identity hashing substitutes the projection so persistence
    /// ids, display names, and reference wrappers can never move a seed.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    internal interface IProjectedIdentityResponse
    {
        /// <summary>Builds the metadata-free projected identity form.</summary>
        /// <returns>The identity element.</returns>
        XElement ToIdentityXElement();
    }
}
