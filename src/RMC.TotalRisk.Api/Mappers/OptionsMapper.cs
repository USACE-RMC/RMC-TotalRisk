using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Api.DTOs;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Api.Mappers
{
    /// <summary>
    /// Maps the options DTO onto <see cref="RiskAnalysisOptions"/> and back (the effective-echo
    /// direction).
    /// </summary>
    /// <remarks>
    /// The engine re-applies its component-count-scaled integration defaults at run start
    /// whenever <c>UseDefaults</c> is true, so the mapper explicitly switches
    /// <c>UseDefaults</c> off whenever the request supplies ANY of the eight integration knobs —
    /// a supplied knob must survive to the run. The property setters flip the flag on their own,
    /// but the mapper does it first so the rule never depends on setter order.
    /// </remarks>
    public static class OptionsMapper
    {
        /// <summary>
        /// Applies a request's option values onto fresh engine options.
        /// </summary>
        /// <param name="dto">The options DTO; null applies the defaults.</param>
        /// <param name="issues">The issue collector (the mean-only gate reports here).</param>
        /// <returns>The engine options.</returns>
        public static RiskAnalysisOptions ToOptions(RiskAnalysisOptionsDto? dto, List<ValidationIssueDto> issues)
        {
            ArgumentNullException.ThrowIfNull(issues);

            var options = new RiskAnalysisOptions();

            // The API defaults adjusted failure-mode curves ON (the engine default is off): the
            // round-trip contract promises both adjusted and unadjusted marginal results.
            options.OutputAdjustedFailureModeCurves = dto?.OutputAdjustedFailureModeCurves ?? true;

            if (dto == null)
            {
                options.EstimateMeanRiskOnly = true;
                return options;
            }

            if (dto.EstimateMeanRiskOnly == false)
            {
                issues.Add(ValidationIssueDto.ApiError("API_MEAN_ONLY_REQUIRED",
                    "This contract version computes mean risk only; set estimateMeanRiskOnly to true or omit it. Full-uncertainty compute arrives in a later contract increment.",
                    "options.estimateMeanRiskOnly"));
            }
            options.EstimateMeanRiskOnly = true;

            bool anyIntegrationKnob = dto.MaxEvaluations.HasValue || dto.MaxDepth.HasValue || dto.Tolerance.HasValue
                || dto.EnsembleTolerance.HasValue || dto.EnsembleMinDepth.HasValue || dto.WarmupEvaluations.HasValue
                || dto.WarmupCycles.HasValue || dto.FinalEvaluations.HasValue;
            if (anyIntegrationKnob)
            {
                options.UseDefaults = false;
            }

            if (dto.Realizations.HasValue) options.Realizations = dto.Realizations.Value;
            if (dto.ConfidenceIntervalWidth.HasValue) options.ConfidenceIntervalWidth = dto.ConfidenceIntervalWidth.Value;
            if (dto.PrngSeed.HasValue) options.PRNGSeed = dto.PrngSeed.Value;
            if (dto.LecOutputLength.HasValue) options.LECOutputLength = dto.LecOutputLength.Value;
            if (dto.SamplingScheme.HasValue) options.SamplingScheme = dto.SamplingScheme.Value;
            if (dto.Mode.HasValue) options.Mode = dto.Mode.Value;
            if (dto.RiskIntegrand.HasValue) options.RiskIntegrand = dto.RiskIntegrand.Value;
            if (dto.SystemRiskMethod.HasValue) options.SystemRiskMethod = dto.SystemRiskMethod.Value;
            if (dto.JointConsequences.HasValue) options.JointConsequences = dto.JointConsequences.Value;
            if (dto.ComponentHazardDependency.HasValue) options.ComponentHazardDependency = dto.ComponentHazardDependency.Value;
            if (dto.HazardCorrelationMatrix != null)
            {
                var matrix = ComponentMapper.ToMatrix(dto.HazardCorrelationMatrix, "options.hazardCorrelationMatrix", issues);
                if (matrix != null) options.HazardCorrelationMatrix = matrix;
            }
            if (dto.ConsequenceThreshold.HasValue) options.ConsequenceThreshold = dto.ConsequenceThreshold.Value;
            if (dto.Alpha.HasValue) options.Alpha = dto.Alpha.Value;

            if (dto.MaxEvaluations.HasValue) options.MaxEvaluations = dto.MaxEvaluations.Value;
            if (dto.MaxDepth.HasValue) options.MaxDepth = dto.MaxDepth.Value;
            if (dto.Tolerance.HasValue) options.Tolerance = dto.Tolerance.Value;
            if (dto.EnsembleTolerance.HasValue) options.EnsembleTolerance = dto.EnsembleTolerance.Value;
            if (dto.EnsembleMinDepth.HasValue) options.EnsembleMinDepth = dto.EnsembleMinDepth.Value;
            if (dto.WarmupEvaluations.HasValue) options.WarmupEvaluations = dto.WarmupEvaluations.Value;
            if (dto.WarmupCycles.HasValue) options.WarmupCycles = dto.WarmupCycles.Value;
            if (dto.FinalEvaluations.HasValue) options.FinalEvaluations = dto.FinalEvaluations.Value;

            if (dto.VegasTailFocusMode.HasValue) options.VegasTailFocusMode = dto.VegasTailFocusMode.Value;
            if (dto.VegasTailFocusParameter.HasValue) options.VegasTailFocusParameter = dto.VegasTailFocusParameter.Value;
            if (dto.SystemConvolutionPoints.HasValue) options.SystemConvolutionPoints = dto.SystemConvolutionPoints.Value;
            if (dto.MaxSystemCombinations.HasValue) options.MaxSystemCombinations = dto.MaxSystemCombinations.Value;
            if (dto.MaxPathwayCombinations.HasValue) options.MaxPathwayCombinations = dto.MaxPathwayCombinations.Value;

            if (dto.RiskMeasures != null)
            {
                var measures = RiskMeasureOptions.None;
                foreach (var flag in dto.RiskMeasures)
                {
                    measures |= flag;
                }
                options.RiskMeasures = measures;
            }

            if (dto.UseSobolJointSampling.HasValue) options.UseSobolJointSampling = dto.UseSobolJointSampling.Value;

            return options;
        }

        /// <summary>
        /// Builds the effective-options echo from the engine options a run used: every field
        /// populated with its resolved value, so a client can replay the run exactly.
        /// </summary>
        /// <param name="options">The engine options.</param>
        /// <returns>The fully populated options DTO.</returns>
        public static RiskAnalysisOptionsDto ToDto(RiskAnalysisOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            return new RiskAnalysisOptionsDto
            {
                EstimateMeanRiskOnly = options.EstimateMeanRiskOnly,
                Realizations = options.Realizations,
                ConfidenceIntervalWidth = options.ConfidenceIntervalWidth,
                PrngSeed = options.PRNGSeed,
                LecOutputLength = options.LECOutputLength,
                SamplingScheme = options.SamplingScheme,
                Mode = options.Mode,
                RiskIntegrand = options.RiskIntegrand,
                SystemRiskMethod = options.SystemRiskMethod,
                JointConsequences = options.JointConsequences,
                ComponentHazardDependency = options.ComponentHazardDependency,
                HazardCorrelationMatrix = ToRows(options.HazardCorrelationMatrix),
                ConsequenceThreshold = options.ConsequenceThreshold,
                Alpha = options.Alpha,
                MaxEvaluations = options.MaxEvaluations,
                MaxDepth = options.MaxDepth,
                Tolerance = options.Tolerance,
                EnsembleTolerance = options.EnsembleTolerance,
                EnsembleMinDepth = options.EnsembleMinDepth,
                WarmupEvaluations = options.WarmupEvaluations,
                WarmupCycles = options.WarmupCycles,
                FinalEvaluations = options.FinalEvaluations,
                VegasTailFocusMode = options.VegasTailFocusMode,
                VegasTailFocusParameter = options.VegasTailFocusParameter,
                SystemConvolutionPoints = options.SystemConvolutionPoints,
                MaxSystemCombinations = options.MaxSystemCombinations,
                MaxPathwayCombinations = options.MaxPathwayCombinations,
                RiskMeasures = ToMeasureList(options.RiskMeasures),
                OutputAdjustedFailureModeCurves = options.OutputAdjustedFailureModeCurves,
                UseSobolJointSampling = options.UseSobolJointSampling,
            };
        }

        /// <summary>
        /// Converts a rectangular matrix to the wire's row-list form; null stays null.
        /// </summary>
        /// <param name="matrix">The matrix.</param>
        /// <returns>The rows, or null.</returns>
        private static List<List<double>>? ToRows(double[,]? matrix)
        {
            if (matrix == null) return null;
            int rows = matrix.GetLength(0);
            int columns = matrix.GetLength(1);
            var result = new List<List<double>>(rows);
            for (int r = 0; r < rows; r++)
            {
                var row = new List<double>(columns);
                for (int c = 0; c < columns; c++)
                {
                    row.Add(matrix[r, c]);
                }
                result.Add(row);
            }
            return result;
        }

        /// <summary>
        /// Decomposes a measure-flags value into the wire's flag-name list ("all" when every
        /// flag is set, "none" when none is).
        /// </summary>
        /// <param name="measures">The flags value.</param>
        /// <returns>The flag list.</returns>
        private static List<RiskMeasureOptions> ToMeasureList(RiskMeasureOptions measures)
        {
            if (measures == RiskMeasureOptions.All) return new List<RiskMeasureOptions> { RiskMeasureOptions.All };
            if (measures == RiskMeasureOptions.None) return new List<RiskMeasureOptions> { RiskMeasureOptions.None };
            var flags = new List<RiskMeasureOptions>();
            foreach (var flag in new[]
            {
                RiskMeasureOptions.HigherMoments,
                RiskMeasureOptions.ValueAtRisk,
                RiskMeasureOptions.ThresholdProbabilities,
                RiskMeasureOptions.RiskProfiles,
                RiskMeasureOptions.Contributions,
            })
            {
                if (measures.HasFlag(flag)) flags.Add(flag);
            }
            return flags;
        }
    }
}
