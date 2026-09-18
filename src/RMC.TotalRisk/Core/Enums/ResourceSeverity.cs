namespace RMC.TotalRisk.Core.Enums
{
    /// <summary>
    /// The severity of a resource-estimate finding.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public enum ResourceSeverity
    {
        /// <summary>A reported quantity that needs no action.</summary>
        Informational,

        /// <summary>A quantity large enough to be worth the caller's attention.</summary>
        Warning,

        /// <summary>A quantity that will not run on a normal machine.</summary>
        Error,
    }
}
