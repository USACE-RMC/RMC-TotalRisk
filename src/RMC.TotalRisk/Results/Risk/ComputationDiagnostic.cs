using System;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Results.Risk
{
    /// <summary>A machine-readable condition observed while computing an analysis result.</summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// </remarks>
    public sealed record ComputationDiagnostic
    {
        /// <summary>Initializes a structured computation diagnostic.</summary>
        /// <param name="code">The stable diagnostic code.</param>
        /// <param name="severity">The diagnostic severity.</param>
        /// <param name="message">The caller-facing message without a severity prefix.</param>
        /// <param name="objectPath">The result or model object path; empty for the whole run.</param>
        /// <exception cref="ArgumentException">Thrown when the code is blank.</exception>
        public ComputationDiagnostic(string code, DiagnosticSeverity severity, string? message, string? objectPath)
        {
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("A computation diagnostic code is required.", nameof(code));
            Code = code;
            Severity = severity;
            Message = message ?? string.Empty;
            ObjectPath = objectPath ?? string.Empty;
        }

        /// <summary>The stable machine-readable code.</summary>
        public string Code { get; }

        /// <summary>The diagnostic severity.</summary>
        public DiagnosticSeverity Severity { get; }

        /// <summary>The caller-facing message without a severity prefix.</summary>
        public string Message { get; }

        /// <summary>The result or model object path; empty for the whole run.</summary>
        public string ObjectPath { get; }

        /// <summary>Formats the compatibility string used by <c>ComputationWarnings</c>.</summary>
        /// <returns>The severity-prefixed message.</returns>
        public string ToLegacyMessage()
        {
            return Severity switch
            {
                DiagnosticSeverity.Error => $"Error: {Message}",
                DiagnosticSeverity.Warning => $"Warning: {Message}",
                _ => Message,
            };
        }
    }
}
