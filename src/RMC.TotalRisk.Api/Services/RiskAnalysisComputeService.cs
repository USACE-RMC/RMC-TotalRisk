using Microsoft.Extensions.Options;
using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Api.Configuration;
using RMC.TotalRisk.Api.DTOs;
using RMC.TotalRisk.Api.Mappers;
using RMC.TotalRisk.Api.Services.Exceptions;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Systems.Components;

namespace RMC.TotalRisk.Api.Services
{
    /// <summary>
    /// The stateless round-trip compute implementation: every request constructs a fresh
    /// <see cref="RiskAnalysis"/>, so no locks or resource state exist beyond a global run
    /// throttle protecting the host's CPU.
    /// </summary>
    public class RiskAnalysisComputeService : IRiskAnalysisComputeService
    {
        /// <summary>
        /// The configured host limits.
        /// </summary>
        private readonly ApiOptions _limits;

        /// <summary>
        /// The global run throttle (at most <see cref="ApiOptions.MaxConcurrentRuns"/> engine
        /// runs at once).
        /// </summary>
        private readonly SemaphoreSlim _runThrottle;

        /// <summary>
        /// Initializes the service.
        /// </summary>
        /// <param name="options">The host limits.</param>
        /// <exception cref="ArgumentNullException">Thrown when the options are null.</exception>
        public RiskAnalysisComputeService(IOptions<ApiOptions> options)
        {
            _limits = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _runThrottle = new SemaphoreSlim(Math.Max(1, _limits.MaxConcurrentRuns), Math.Max(1, _limits.MaxConcurrentRuns));
        }

        /// <inheritdoc/>
        public ValidateRiskAnalysisResponse Validate(ComputeRiskAnalysisRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            var (analysis, issues) = Build(request);
            bool isValid = analysis != null && !HasErrors(issues);
            var response = new ValidateRiskAnalysisResponse { IsValid = isValid };
            StampIssues(response, issues);
            return response;
        }

        /// <inheritdoc/>
        public async Task<ComputeRiskAnalysisResponse> ComputeAsync(ComputeRiskAnalysisRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            var (analysis, issues) = Build(request);
            if (analysis == null || HasErrors(issues))
            {
                throw new RequestValidationException(BuildValidationMessage(issues), issues);
            }

            await _runThrottle.WaitAsync(cancellationToken);
            try
            {
                await analysis.RunAsync(null, cancellationToken);
            }
            finally
            {
                _runThrottle.Release();
            }

            bool includeCurves = request.ResultOptions?.IncludeCurves ?? true;
            var response = ResultsMapper.ToResponse(analysis, includeCurves, ApiContractInfo.Version);
            StampIssues(response, issues);
            return response;
        }

        /// <summary>
        /// Maps the request to a validated model, collecting request-shape issues and the model's
        /// own structured validation issues.
        /// </summary>
        /// <param name="request">The compute request.</param>
        /// <returns>The analysis (null when construction failed) and every collected issue.</returns>
        private (RiskAnalysis? Analysis, List<ValidationIssueDto> Issues) Build(ComputeRiskAnalysisRequest request)
        {
            var issues = new List<ValidationIssueDto>();

            if (string.IsNullOrWhiteSpace(request.SpecifiedConsequence))
            {
                issues.Add(ValidationIssueDto.ApiError("API_LABEL_REQUIRED",
                    "The primary consequence type label (specifiedConsequence) is required.", "specifiedConsequence"));
            }
            if (string.IsNullOrWhiteSpace(request.ConsequenceUnit))
            {
                issues.Add(ValidationIssueDto.ApiError("API_LABEL_REQUIRED",
                    "The primary consequence unit label (consequenceUnit) is required.", "consequenceUnit"));
            }
            if (request.Components == null || request.Components.Count == 0)
            {
                issues.Add(ValidationIssueDto.ApiError("API_COMPONENTS_REQUIRED",
                    "At least one component is required.", "components"));
                return (null, issues);
            }
            if (request.Components.Count > _limits.MaxComponents)
            {
                issues.Add(ValidationIssueDto.ApiError("API_TOO_MANY_COMPONENTS",
                    $"The request exceeds the host limit of {_limits.MaxComponents} components (got {request.Components.Count}).",
                    "components"));
                return (null, issues);
            }

            var declaredTypes = new List<(string Label, string Unit)>
            {
                (request.SpecifiedConsequence ?? string.Empty, request.ConsequenceUnit ?? string.Empty),
            };
            if (request.AdditionalConsequenceTypes != null)
            {
                foreach (var type in request.AdditionalConsequenceTypes)
                {
                    declaredTypes.Add((type?.SpecifiedConsequence ?? string.Empty, type?.ConsequenceUnit ?? string.Empty));
                }
            }

            var options = OptionsMapper.ToOptions(request.Options, issues);

            var components = new List<SystemComponent>(request.Components.Count);
            for (int i = 0; i < request.Components.Count; i++)
            {
                if (request.Components[i] == null)
                {
                    issues.Add(ValidationIssueDto.ApiError("API_COMPONENT_REQUIRED",
                        "The component entry is null.", $"components[{i}]"));
                    continue;
                }
                var component = ComponentMapper.ToComponent(request.Components[i], i, _limits, declaredTypes, issues);
                if (component != null) components.Add(component);
            }
            if (HasErrors(issues)) return (null, issues);

            var analysis = new RiskAnalysis(components)
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? "Risk Analysis" : request.Name!,
                Description = request.Description ?? string.Empty,
                SpecifiedConsequence = request.SpecifiedConsequence ?? string.Empty,
                ConsequenceUnit = request.ConsequenceUnit ?? string.Empty,
            };
            if (request.AdditionalConsequenceTypes != null)
            {
                foreach (var type in request.AdditionalConsequenceTypes)
                {
                    analysis.AdditionalConsequenceTypes.Add(new ConsequenceTypeDescriptor(
                        type?.SpecifiedConsequence, type?.ConsequenceUnit, type?.ConsequenceThreshold ?? double.NaN));
                }
            }
            analysis.Options = options;

            foreach (var issue in analysis.ValidateIssues())
            {
                issues.Add(ValidationIssueDto.FromModel(issue));
            }
            return (analysis, issues);
        }

        /// <summary>
        /// Determines whether any collected issue is error severity.
        /// </summary>
        /// <param name="issues">The collected issues.</param>
        /// <returns>True when an error is present.</returns>
        private static bool HasErrors(List<ValidationIssueDto> issues)
        {
            return issues.Any(i => i.Severity == DiagnosticSeverity.Error);
        }

        /// <summary>
        /// Builds the summary message for a validation failure: the first few error messages,
        /// joined so MCP clients (whose only failure surface is the message text) can self-repair
        /// without a second call.
        /// </summary>
        /// <param name="issues">The collected issues.</param>
        /// <returns>The summary message.</returns>
        private static string BuildValidationMessage(List<ValidationIssueDto> issues)
        {
            var errors = issues.Where(i => i.Severity == DiagnosticSeverity.Error).ToList();
            const int maxListed = 10;
            string joined = string.Join(" | ", errors.Take(maxListed).Select(i => $"[{i.Code}] {i.Message}"));
            string suffix = errors.Count > maxListed ? $" (+{errors.Count - maxListed} more)" : string.Empty;
            return $"The request failed validation: {joined}{suffix}";
        }

        /// <summary>
        /// Stamps the collected issues onto a response: the structured list plus the plain-string
        /// error/warning mirrors.
        /// </summary>
        /// <param name="response">The response to stamp.</param>
        /// <param name="issues">The collected issues.</param>
        private static void StampIssues(ResponseBase response, List<ValidationIssueDto> issues)
        {
            if (issues.Count == 0) return;
            response.ValidationIssues = issues;
            var errors = issues.Where(i => i.Severity == DiagnosticSeverity.Error).Select(i => i.Message).ToList();
            var warnings = issues.Where(i => i.Severity == DiagnosticSeverity.Warning).Select(i => i.Message).ToList();
            response.ValidationErrors = errors.Count > 0 ? errors : null;
            response.ValidationWarnings = warnings.Count > 0 ? warnings : null;
        }
    }
}
