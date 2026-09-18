using RMC.TotalRisk.Analyses.Risk;
using RMC.TotalRisk.Api.DTOs;
using RMC.TotalRisk.Results.Risk;

namespace RMC.TotalRisk.Api.Mappers
{
    /// <summary>
    /// Maps the engine's mean-results realization tree into the API's camelCase result DTOs.
    /// </summary>
    /// <remarks>
    /// The realization tree is the single mapped source: it carries the curves, the scalar
    /// measures, the contributions, and the adjusted failure-mode curves. Measures the engine
    /// stores as NaN (disabled measure flags, undefined conditionals) map to null so clients
    /// never have to reason about NaN semantics; curve arrays the engine did not record map to
    /// absent blocks.
    /// </remarks>
    public static class ResultsMapper
    {
        /// <summary>
        /// Builds the compute response body from an estimated analysis.
        /// </summary>
        /// <param name="analysis">The analysis after a successful run.</param>
        /// <param name="includeCurves">Whether to include the curve arrays.</param>
        /// <param name="apiContractVersion">The wire-contract version stamped into the provenance block.</param>
        /// <returns>The response body.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the analysis carries no mean results.</exception>
        public static ComputeRiskAnalysisResponse ToResponse(RiskAnalysis analysis, bool includeCurves, string apiContractVersion)
        {
            ArgumentNullException.ThrowIfNull(analysis);
            var realization = analysis.MeanRiskResults
                ?? throw new InvalidOperationException("The analysis carries no mean results; run it before mapping.");

            var response = new ComputeRiskAnalysisResponse
            {
                Provenance = ToProvenance(realization.Manifest, apiContractVersion),
                EffectiveOptions = OptionsMapper.ToDto(analysis.Options),
                ComputationWarnings = analysis.ComputationWarnings.Count > 0 ? analysis.ComputationWarnings.ToList() : null,
                ComputationDiagnostics = analysis.ComputationDiagnostics.Count > 0
                    ? analysis.ComputationDiagnostics.Select(ComputationDiagnosticDto.FromModel).ToList()
                    : null,
                Results = ToSystemResults(realization, includeCurves),
            };
            return response;
        }

        /// <summary>
        /// Maps the run manifest to the provenance block; null stays null.
        /// </summary>
        /// <param name="manifest">The run manifest.</param>
        /// <param name="apiContractVersion">The wire-contract version.</param>
        /// <returns>The provenance DTO, or null.</returns>
        public static ProvenanceDto? ToProvenance(AnalysisRunManifest? manifest, string apiContractVersion)
        {
            if (manifest == null) return null;
            return new ProvenanceDto
            {
                ResultsSchemaVersion = manifest.ResultsSchemaVersion,
                TotalRiskVersion = manifest.TotalRiskAssemblyVersion,
                NumericsVersion = manifest.NumericsAssemblyVersion,
                ApiContractVersion = apiContractVersion,
                AnalysisContentHash = manifest.AnalysisContentHash,
                EffectiveOptionsHash = manifest.EffectiveOptionsHash,
                ComponentContentHashes = manifest.ComponentContentHashes.ToList(),
                ComponentOccurrenceIndices = manifest.ComponentOccurrenceIndices.ToList(),
                PrngSeed = manifest.PRNGSeed,
            };
        }

        /// <summary>
        /// Maps a system realization to the system results DTO.
        /// </summary>
        /// <param name="realization">The realization tree.</param>
        /// <param name="includeCurves">Whether to include the curve arrays.</param>
        /// <returns>The system results DTO.</returns>
        public static SystemResultsDto ToSystemResults(SystemRealization realization, bool includeCurves)
        {
            ArgumentNullException.ThrowIfNull(realization);
            var dto = new SystemResultsDto
            {
                Name = realization.Name,
                ConsequenceLabels = realization.ConsequenceLabels.ToList(),
                ConsequenceUnits = realization.ConsequenceUnits.ToList(),
                Curves = ToCurveSet(realization.Curves, includeCurves),
                AdditionalCurves = ToCurveSets(realization.AdditionalCurves, includeCurves),
                FunctionEvaluations = realization.FunctionEvaluations,
                StandardError = realization.StandardError,
            };
            for (int i = 0; i < realization.Components.Count; i++)
            {
                dto.Components.Add(ToComponentResults(realization.Components[i], includeCurves));
            }
            return dto;
        }

        /// <summary>
        /// Maps a component realization to its results DTO.
        /// </summary>
        /// <param name="component">The component realization.</param>
        /// <param name="includeCurves">Whether to include the curve arrays.</param>
        /// <returns>The component results DTO.</returns>
        private static ComponentResultsDto ToComponentResults(ComponentRealization component, bool includeCurves)
        {
            var dto = new ComponentResultsDto
            {
                Name = component.Name,
                Curves = ToCurveSet(component.Curves, includeCurves),
                AdditionalCurves = ToCurveSets(component.AdditionalCurves, includeCurves),
                SystemContribution = ToContribution(component.SystemContribution),
                AdditionalSystemContributions = ToContributions(component.AdditionalSystemContributions),
                MinHazard = component.MinH,
                MaxHazard = component.MaxH,
            };
            for (int i = 0; i < component.FailureModes.Count; i++)
            {
                dto.FailureModes.Add(ToFailureModeResults(component.FailureModes[i], includeCurves));
            }
            return dto;
        }

        /// <summary>
        /// Maps a failure-mode realization — unadjusted curves, adjusted curves, contribution —
        /// to its results DTO.
        /// </summary>
        /// <param name="mode">The failure-mode realization.</param>
        /// <param name="includeCurves">Whether to include the curve arrays.</param>
        /// <returns>The failure-mode results DTO.</returns>
        private static FailureModeResultsDto ToFailureModeResults(FailureModeRealization mode, bool includeCurves)
        {
            return new FailureModeResultsDto
            {
                Name = mode.Name,
                PathLabel = mode.PathLabel,
                Curves = ToCurveSet(mode.Curves, includeCurves),
                AdditionalCurves = ToCurveSets(mode.AdditionalCurves, includeCurves),
                AdjustedCurves = mode.AdjustedCurves != null ? ToCurveSet(mode.AdjustedCurves, includeCurves) : null,
                AdditionalAdjustedCurves = ToCurveSets(mode.AdditionalAdjustedCurves, includeCurves),
                Contribution = ToContribution(mode.Contribution),
                AdditionalContributions = ToContributions(mode.AdditionalContributions),
            };
        }

        /// <summary>
        /// Maps a five-stream curve set.
        /// </summary>
        /// <param name="curves">The curve set.</param>
        /// <param name="includeCurves">Whether to include the curve arrays.</param>
        /// <returns>The curve set DTO.</returns>
        private static CurveSetDto ToCurveSet(Curves curves, bool includeCurves)
        {
            return new CurveSetDto
            {
                Excess = ToCurve(curves.Excess, includeCurves),
                Background = ToCurve(curves.Background, includeCurves),
                Total = ToCurve(curves.Total, includeCurves),
                Fail = ToCurve(curves.Fail, includeCurves),
                NonFail = ToCurve(curves.NonFail, includeCurves),
            };
        }

        /// <summary>
        /// Maps a list of curve sets; an empty list maps to null.
        /// </summary>
        /// <param name="curveSets">The curve sets.</param>
        /// <param name="includeCurves">Whether to include the curve arrays.</param>
        /// <returns>The DTO list, or null.</returns>
        private static List<CurveSetDto>? ToCurveSets(List<Curves> curveSets, bool includeCurves)
        {
            if (curveSets == null || curveSets.Count == 0) return null;
            var result = new List<CurveSetDto>(curveSets.Count);
            for (int i = 0; i < curveSets.Count; i++)
            {
                result.Add(ToCurveSet(curveSets[i], includeCurves));
            }
            return result;
        }

        /// <summary>
        /// Maps one curve stream: the scalar measures (NaN → null) plus the recorded arrays.
        /// </summary>
        /// <param name="curve">The curve stream.</param>
        /// <param name="includeCurves">Whether to include the curve arrays.</param>
        /// <returns>The curve DTO.</returns>
        private static CurveDto ToCurve(Curve curve, bool includeCurves)
        {
            var dto = new CurveDto
            {
                Stats = new CurveStatsDto
                {
                    TotalProbability = curve.TotalProbability,
                    MassBalance = curve.MassBalance,
                    IsExhaustive = curve.IsExhaustive,
                    Mean = curve.Mean,
                    ConditionalMean = NanToNull(curve.ConditionalMean),
                    StandardDeviation = curve.StandardDeviation,
                    Skewness = NanToNull(curve.Skewness),
                    Kurtosis = NanToNull(curve.Kurtosis),
                    ValueAtRisk = NanToNull(curve.ValueAtRisk),
                    ConditionalValueAtRisk = NanToNull(curve.ConditionalValueAtRisk),
                    ConsequenceThresholdProbability = NanToNull(curve.ConsequenceThresholdProbability),
                    HazardThresholdProbability = NanToNull(curve.HazardThresholdProbability),
                },
            };
            if (!includeCurves) return dto;

            if (curve.LECConsequences.Length > 0)
            {
                dto.Lec = new LossExceedanceCurveDto
                {
                    Consequences = curve.LECConsequences.ToList(),
                    Probabilities = curve.LECProbabilities.ToList(),
                };
            }
            if (curve.HazardFrequencyHazards.Length > 0)
            {
                dto.HazardFrequency = new HazardFrequencyCurveDto
                {
                    Hazards = curve.HazardFrequencyHazards.ToList(),
                    Probabilities = curve.HazardFrequencyProbabilities.ToList(),
                };
            }
            if (curve.HazardVsCenHazards.Length > 0)
            {
                dto.HazardVsConditionalMean = new HazardConsequenceCurveDto
                {
                    Hazards = curve.HazardVsCenHazards.ToList(),
                    Consequences = curve.HazardVsCenConsequences.ToList(),
                };
            }
            if (curve.CumulativeFailureProbabilities.Length > 0 || curve.CumulativeExpectedConsequences.Length > 0
                || curve.SystemResponseExceedanceProbabilities.Length > 0 || curve.SystemResponseProbabilities.Length > 0)
            {
                dto.Profiles = new RiskProfilesDto
                {
                    CumulativeFailureProbabilities = ToNullableList(curve.CumulativeFailureProbabilities),
                    CumulativeExpectedConsequences = ToNullableList(curve.CumulativeExpectedConsequences),
                    SystemResponseExceedanceProbabilities = ToNullableList(curve.SystemResponseExceedanceProbabilities),
                    SystemResponseProbabilities = ToNullableList(curve.SystemResponseProbabilities),
                };
            }
            return dto;
        }

        /// <summary>
        /// Maps a contribution; null stays null.
        /// </summary>
        /// <param name="contribution">The contribution.</param>
        /// <returns>The contribution DTO, or null.</returns>
        private static ContributionDto? ToContribution(RiskContribution? contribution)
        {
            if (contribution == null) return null;
            return new ContributionDto
            {
                FailureProbability = contribution.FailureProbability,
                FailureMean = contribution.FailureMean,
                ExcessMean = contribution.ExcessMean,
            };
        }

        /// <summary>
        /// Maps a per-type contribution list; an empty list maps to null.
        /// </summary>
        /// <param name="contributions">The contributions.</param>
        /// <returns>The DTO list, or null.</returns>
        private static List<ContributionDto?>? ToContributions(List<RiskContribution?> contributions)
        {
            if (contributions == null || contributions.Count == 0) return null;
            var result = new List<ContributionDto?>(contributions.Count);
            for (int i = 0; i < contributions.Count; i++)
            {
                result.Add(ToContribution(contributions[i]));
            }
            return result;
        }

        /// <summary>
        /// Converts an engine NaN to null (the wire's "not computed" marker); finite values pass
        /// through.
        /// </summary>
        /// <param name="value">The engine value.</param>
        /// <returns>The value, or null for NaN.</returns>
        private static double? NanToNull(double value)
        {
            return double.IsNaN(value) ? null : value;
        }

        /// <summary>
        /// Converts an array to a list, mapping an empty array to null.
        /// </summary>
        /// <param name="values">The array.</param>
        /// <returns>The list, or null.</returns>
        private static List<double>? ToNullableList(double[] values)
        {
            return values.Length > 0 ? values.ToList() : null;
        }
    }
}
