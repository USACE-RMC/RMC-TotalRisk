using System.Text.Json.Serialization;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Api.DTOs
{
    /// <summary>
    /// A structured diagnostic raised during the computation itself (as opposed to validation):
    /// a stable code, a severity, the message, and the result or model object path.
    /// </summary>
    public class ComputationDiagnosticDto
    {
        /// <summary>
        /// The stable machine-readable code.
        /// </summary>
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// The diagnostic severity, serialized as a camelCase string.
        /// </summary>
        [JsonPropertyName("severity")]
        public DiagnosticSeverity Severity { get; set; }

        /// <summary>
        /// The caller-facing message without a severity prefix.
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// The result or model object path; empty for the whole run.
        /// </summary>
        [JsonPropertyName("objectPath")]
        public string ObjectPath { get; set; } = string.Empty;

        /// <summary>
        /// Maps an engine computation diagnostic to its API DTO.
        /// </summary>
        /// <param name="diagnostic">The engine diagnostic.</param>
        /// <returns>The DTO carrying the same code, severity, message, and path.</returns>
        public static ComputationDiagnosticDto FromModel(ComputationDiagnostic diagnostic)
        {
            ArgumentNullException.ThrowIfNull(diagnostic);
            return new ComputationDiagnosticDto
            {
                Code = diagnostic.Code,
                Severity = diagnostic.Severity,
                Message = diagnostic.Message,
                ObjectPath = diagnostic.ObjectPath,
            };
        }
    }
}
