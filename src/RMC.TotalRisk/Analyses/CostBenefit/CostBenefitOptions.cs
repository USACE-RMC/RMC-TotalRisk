using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Numerics;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// The declarations of a cost-benefit study: the horizon and discount rate every
    /// alternative shares, the benefit stream and accounting convention the headline
    /// economics read, the exceedance levels, monetization, willingness-to-pay and ALARP
    /// declarations, the individual-risk seats, the do-no-harm policy, and the objective,
    /// constraint, ε-study, MCDA, utility, and partition declarations of the decision
    /// framework.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The options are immutable — replace the study's options to edit them. The study owns
    /// time and money: plans carry no rate and no horizon, so a mismatch is unrepresentable,
    /// and a different horizon is a different study. Declarations are post-processing
    /// selections, never compute content: nothing here enters a canonical-hash or seed
    /// surface. No willingness-to-pay or price ships as a default — absent values skip their
    /// blocks rather than asserting a number. Element and attribute names are append-only
    /// serialized contract.
    /// </para>
    /// </remarks>
    public sealed class CostBenefitOptions
    {
        #region Construction

        /// <summary>
        /// Initializes the study declarations.
        /// </summary>
        /// <param name="periodYears">The planning horizon in years (at least one).</param>
        /// <param name="discountRate">The annual discount rate (0 = undiscounted; finite and non-negative).</param>
        /// <param name="evaluationYears">Study-wide epoch start years refining every trajectory; null or empty for none.</param>
        /// <param name="benefitRiskType">The stream the headline economics difference (Total by default).</param>
        /// <param name="accounting">The accounting convention the headline metrics read (non-absorbing by default).</param>
        /// <param name="alphaLevels">The declared exceedance levels for the tail measures; null reads as the 1% level.</param>
        /// <param name="monetization">The monetization map, or null for none.</param>
        /// <param name="lifeSafetyConsequenceType">The life-safety consequence-type position; −1 skips the life-saved family.</param>
        /// <param name="willingnessToPay">The willingness to pay per statistical life; NaN skips the disproportionality block.</param>
        /// <param name="willingnessToPayVintage">The willingness-to-pay vintage echo; null reads as empty.</param>
        /// <param name="alarpProximity">The tolerable-risk proximity selecting the ALARP band table.</param>
        /// <param name="alarpBandThresholds">The three ascending band thresholds, or null for the regulation defaults.</param>
        /// <param name="individualRiskLimit">The individual-risk floor of the equity weighting.</param>
        /// <param name="equityExponent">The equity-versus-efficiency exponent of the equity weighting.</param>
        /// <param name="baselineIndividualRisk">The baseline individual risk; NaN reads the survival-equivalent proxy.</param>
        /// <param name="alternativeIndividualRisk">The per-alternative individual risk; NaN reads the survival-equivalent proxy.</param>
        /// <param name="doNoHarm">The do-no-harm screen policy.</param>
        /// <param name="objectives">The declared objectives; null reads as the default vector (cost, monetized benefit, dispersion).</param>
        /// <param name="constraints">The fixed constraint set; null or empty for none.</param>
        /// <param name="epsilonStudy">The ε-constraint study declaration, or null for none.</param>
        /// <param name="mcdaWeights">The weighted-sum weights, parallel to the objectives; null skips the weighted-sum block.</param>
        /// <param name="utility">The expected-utility declaration, or null to skip that ranking.</param>
        /// <param name="pmrmPartition">The partitioned-risk declaration, or null to skip that table.</param>
        /// <param name="hurwiczAlpha">The Hurwicz optimism blend in [0, 1].</param>
        /// <param name="dispersionK">The mean-plus-k-standard-deviations multiplier.</param>
        /// <param name="chanceConstraintConfidenceLevels">The chance-constraint confidence levels; null reads as 0.9.</param>
        /// <param name="epistemicTailAlpha">The epistemic tail level for knowledge-ensemble tail averages.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown for a non-positive horizon, an invalid discount rate, or an evaluation year
        /// outside [0, periodYears − 1].
        /// </exception>
        /// <exception cref="ArgumentException">Thrown when a supplied declaration list contains a null entry.</exception>
        public CostBenefitOptions(int periodYears, double discountRate = 0d,
            IReadOnlyList<int>? evaluationYears = null,
            RiskType benefitRiskType = RiskType.Total,
            LifeCycleAccounting accounting = LifeCycleAccounting.NonAbsorbing,
            IReadOnlyList<double>? alphaLevels = null,
            ConsequenceMonetization? monetization = null,
            int lifeSafetyConsequenceType = -1,
            double willingnessToPay = double.NaN,
            string? willingnessToPayVintage = null,
            AlarpProximity alarpProximity = AlarpProximity.JustBelowTolerableLimit,
            IReadOnlyList<double>? alarpBandThresholds = null,
            double individualRiskLimit = 1e-4,
            double equityExponent = 1d,
            double baselineIndividualRisk = double.NaN,
            double alternativeIndividualRisk = double.NaN,
            DoNoHarmPolicy doNoHarm = DoNoHarmPolicy.Enforce,
            IReadOnlyList<ObjectiveDeclaration>? objectives = null,
            IReadOnlyList<CostBenefitConstraint>? constraints = null,
            EpsilonConstraintStudy? epsilonStudy = null,
            IReadOnlyList<double>? mcdaWeights = null,
            UtilityDeclaration? utility = null,
            PmrmPartition? pmrmPartition = null,
            double hurwiczAlpha = 0.5d,
            double dispersionK = 1d,
            IReadOnlyList<double>? chanceConstraintConfidenceLevels = null,
            double epistemicTailAlpha = 0.1d)
        {
            if (periodYears < 1)
                throw new ArgumentOutOfRangeException(nameof(periodYears), "The planning horizon must be at least one year.");
            if (!Tools.IsFinite(discountRate) || discountRate < 0d)
                throw new ArgumentOutOfRangeException(nameof(discountRate), "The discount rate must be finite and non-negative.");
            PeriodYears = periodYears;
            DiscountRate = discountRate;

            var yearSnapshot = evaluationYears == null ? Array.Empty<int>() : evaluationYears.ToArray();
            for (int i = 0; i < yearSnapshot.Length; i++)
            {
                if (yearSnapshot[i] < 0 || yearSnapshot[i] >= periodYears)
                    throw new ArgumentOutOfRangeException(nameof(evaluationYears),
                        $"The evaluation year {yearSnapshot[i]} lies outside [0, {periodYears - 1}].");
            }
            EvaluationYears = Array.AsReadOnly(yearSnapshot);

            BenefitRiskType = benefitRiskType;
            Accounting = accounting;
            AlphaLevels = alphaLevels == null
                ? Array.AsReadOnly(new[] { 0.01d })
                : Array.AsReadOnly(alphaLevels.ToArray());
            Monetization = monetization;
            LifeSafetyConsequenceType = lifeSafetyConsequenceType;
            WillingnessToPay = willingnessToPay;
            WillingnessToPayVintage = willingnessToPayVintage ?? string.Empty;
            AlarpProximity = alarpProximity;
            AlarpBandThresholds = alarpBandThresholds == null
                ? null
                : Array.AsReadOnly(alarpBandThresholds.ToArray());
            IndividualRiskLimit = individualRiskLimit;
            EquityExponent = equityExponent;
            BaselineIndividualRisk = baselineIndividualRisk;
            AlternativeIndividualRisk = alternativeIndividualRisk;
            DoNoHarm = doNoHarm;
            Objectives = SnapshotObjectives(objectives);
            Constraints = SnapshotConstraints(constraints);
            EpsilonStudy = epsilonStudy;
            McdaWeights = mcdaWeights == null ? null : Array.AsReadOnly(mcdaWeights.ToArray());
            Utility = utility;
            PmrmPartition = pmrmPartition;
            HurwiczAlpha = hurwiczAlpha;
            DispersionK = dispersionK;
            ChanceConstraintConfidenceLevels = chanceConstraintConfidenceLevels == null
                ? Array.AsReadOnly(new[] { 0.9d })
                : Array.AsReadOnly(chanceConstraintConfidenceLevels.ToArray());
            EpistemicTailAlpha = epistemicTailAlpha;
        }

        /// <summary>
        /// Restores the declarations from their serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the stored values violate the construction guards.</exception>
        public CostBenefitOptions(XElement xElement)
            : this(SerializationUtilities.ReadInt32(SerializationUtilities.RequireElement(xElement, nameof(xElement)), nameof(PeriodYears), 1),
                SerializationUtilities.ReadDouble(xElement, nameof(DiscountRate)),
                ReadIntList(xElement, nameof(EvaluationYears)),
                SerializationUtilities.ReadEnum(xElement, nameof(BenefitRiskType), RiskType.Total),
                SerializationUtilities.ReadEnum(xElement, nameof(Accounting), LifeCycleAccounting.NonAbsorbing),
                ReadDoubleList(xElement, nameof(AlphaLevels)) ?? new[] { 0.01d },
                ReadMonetization(xElement),
                SerializationUtilities.ReadInt32(xElement, nameof(LifeSafetyConsequenceType), -1),
                SerializationUtilities.ReadDouble(xElement, nameof(WillingnessToPay), double.NaN),
                SerializationUtilities.ReadString(xElement, nameof(WillingnessToPayVintage)),
                SerializationUtilities.ReadEnum(xElement, nameof(AlarpProximity), AlarpProximity.JustBelowTolerableLimit),
                ReadDoubleList(xElement, nameof(AlarpBandThresholds)),
                SerializationUtilities.ReadDouble(xElement, nameof(IndividualRiskLimit), 1e-4),
                SerializationUtilities.ReadDouble(xElement, nameof(EquityExponent), 1d),
                SerializationUtilities.ReadDouble(xElement, nameof(BaselineIndividualRisk), double.NaN),
                SerializationUtilities.ReadDouble(xElement, nameof(AlternativeIndividualRisk), double.NaN),
                SerializationUtilities.ReadEnum(xElement, nameof(DoNoHarm), DoNoHarmPolicy.Enforce),
                ReadObjectives(xElement),
                ReadConstraints(xElement),
                ReadEpsilonStudy(xElement),
                ReadDoubleList(xElement, nameof(McdaWeights)),
                ReadUtility(xElement),
                ReadPartition(xElement),
                SerializationUtilities.ReadDouble(xElement, nameof(HurwiczAlpha), 0.5d),
                SerializationUtilities.ReadDouble(xElement, nameof(DispersionK), 1d),
                ReadDoubleList(xElement, nameof(ChanceConstraintConfidenceLevels)) ?? new[] { 0.9d },
                SerializationUtilities.ReadDouble(xElement, nameof(EpistemicTailAlpha), 0.1d))
        {
        }

        /// <summary>
        /// Snapshots the objectives, refusing null entries; null reads as the default vector —
        /// minimize the present value of total cost, maximize the monetized present-value
        /// benefit, and minimize the standard deviation of annual risk (the declared
        /// secondary dispersion objective).
        /// </summary>
        /// <param name="objectives">The supplied list, or null for the defaults.</param>
        /// <returns>The read-only snapshot.</returns>
        /// <exception cref="ArgumentException">Thrown when the list contains a null entry.</exception>
        private static IReadOnlyList<ObjectiveDeclaration> SnapshotObjectives(
            IReadOnlyList<ObjectiveDeclaration>? objectives)
        {
            if (objectives == null)
            {
                return Array.AsReadOnly(new[]
                {
                    new ObjectiveDeclaration("Present value of total cost",
                        CostBenefitMetric.ForEconomic(EconomicMetric.PresentValueOfTotalCost),
                        ObjectiveDirection.Minimize),
                    new ObjectiveDeclaration("Monetized present-value benefit",
                        CostBenefitMetric.ForEconomic(EconomicMetric.MonetizedPresentValueBenefit),
                        ObjectiveDirection.Maximize),
                    new ObjectiveDeclaration("Standard deviation of annual risk",
                        CostBenefitMetric.ForRiskMeasure(RiskMeasure.StandardDeviation, RiskType.Total),
                        ObjectiveDirection.Minimize),
                });
            }
            var snapshot = objectives.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] == null)
                    throw new ArgumentException("The objective list contains a null entry.", nameof(objectives));
            }
            return Array.AsReadOnly(snapshot);
        }

        /// <summary>
        /// Snapshots the fixed constraints, refusing null entries.
        /// </summary>
        /// <param name="constraints">The supplied list, or null for none.</param>
        /// <returns>The read-only snapshot.</returns>
        /// <exception cref="ArgumentException">Thrown when the list contains a null entry.</exception>
        private static IReadOnlyList<CostBenefitConstraint> SnapshotConstraints(
            IReadOnlyList<CostBenefitConstraint>? constraints)
        {
            var snapshot = constraints == null ? Array.Empty<CostBenefitConstraint>() : constraints.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] == null)
                    throw new ArgumentException("The constraint list contains a null entry.", nameof(constraints));
            }
            return Array.AsReadOnly(snapshot);
        }

        /// <summary>
        /// Reads a pipe-delimited integer list attribute; a missing attribute reads as null.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <param name="attributeName">The attribute name.</param>
        /// <returns>The list, or null.</returns>
        private static IReadOnlyList<int>? ReadIntList(XElement xElement, string attributeName)
        {
            XAttribute? attribute = xElement.Attribute(attributeName);
            if (attribute == null) return null;
            string[] parts = attribute.Value.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var values = new List<int>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    values.Add(value);
                }
            }
            return values;
        }

        /// <summary>
        /// Reads a pipe-delimited double list attribute; a missing attribute reads as null.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <param name="attributeName">The attribute name.</param>
        /// <returns>The list, or null.</returns>
        private static IReadOnlyList<double>? ReadDoubleList(XElement xElement, string attributeName)
        {
            XAttribute? attribute = xElement.Attribute(attributeName);
            if (attribute == null) return null;
            string[] parts = attribute.Value.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var values = new List<double>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                values.Add(SerializationUtilities.ParseDouble(parts[i]));
            }
            return values;
        }

        /// <summary>
        /// Reads the monetization child, or null when absent.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The monetization map, or null.</returns>
        private static ConsequenceMonetization? ReadMonetization(XElement xElement)
        {
            XElement? child = xElement.Element(nameof(ConsequenceMonetization));
            return child == null ? null : new ConsequenceMonetization(child);
        }

        /// <summary>
        /// Reads the objective declarations; a missing wrapper reads as null (the defaults).
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The objectives, or null.</returns>
        private static IReadOnlyList<ObjectiveDeclaration>? ReadObjectives(XElement xElement)
        {
            XElement? wrapper = xElement.Element(nameof(Objectives));
            if (wrapper == null) return null;
            var objectives = new List<ObjectiveDeclaration>();
            foreach (XElement child in wrapper.Elements(nameof(ObjectiveDeclaration)))
            {
                objectives.Add(new ObjectiveDeclaration(child));
            }
            return objectives;
        }

        /// <summary>
        /// Reads the fixed constraints; a missing wrapper reads as none.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The constraints, or null.</returns>
        private static IReadOnlyList<CostBenefitConstraint>? ReadConstraints(XElement xElement)
        {
            XElement? wrapper = xElement.Element(nameof(Constraints));
            if (wrapper == null) return null;
            var constraints = new List<CostBenefitConstraint>();
            foreach (XElement child in wrapper.Elements(nameof(CostBenefitConstraint)))
            {
                constraints.Add(new CostBenefitConstraint(child));
            }
            return constraints;
        }

        /// <summary>
        /// Reads the ε-study child, or null when absent.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The declaration, or null.</returns>
        private static EpsilonConstraintStudy? ReadEpsilonStudy(XElement xElement)
        {
            XElement? child = xElement.Element(nameof(EpsilonConstraintStudy));
            return child == null ? null : new EpsilonConstraintStudy(child);
        }

        /// <summary>
        /// Reads the utility child, or null when absent.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The declaration, or null.</returns>
        private static UtilityDeclaration? ReadUtility(XElement xElement)
        {
            XElement? child = xElement.Element(nameof(UtilityDeclaration));
            return child == null ? null : new UtilityDeclaration(child);
        }

        /// <summary>
        /// Reads the partition child, or null when absent.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The declaration, or null.</returns>
        private static PmrmPartition? ReadPartition(XElement xElement)
        {
            XElement? child = xElement.Element(nameof(PmrmPartition));
            return child == null ? null : new PmrmPartition(child);
        }

        #endregion

        #region Members

        /// <summary>
        /// The planning horizon in years; exposure years run 1 through this value.
        /// </summary>
        public int PeriodYears { get; }

        /// <summary>
        /// The annual discount rate (0 = undiscounted).
        /// </summary>
        public double DiscountRate { get; }

        /// <summary>
        /// Study-wide epoch start years refining every alternative's trajectory.
        /// </summary>
        public IReadOnlyList<int> EvaluationYears { get; }

        /// <summary>
        /// The stream the headline economics difference (Total by default — it carries the
        /// trade-offs between failure and non-failure consequences that the incremental
        /// stream alone can miss). The life-saved family always reads Excess.
        /// </summary>
        public RiskType BenefitRiskType { get; }

        /// <summary>
        /// The accounting convention the headline metrics read; both conventions are always
        /// computed.
        /// </summary>
        public LifeCycleAccounting Accounting { get; }

        /// <summary>
        /// The declared exceedance levels for the tail measures.
        /// </summary>
        public IReadOnlyList<double> AlphaLevels { get; }

        /// <summary>
        /// The monetization map, or null for none (the monetary aggregates are then NaN).
        /// </summary>
        public ConsequenceMonetization? Monetization { get; }

        /// <summary>
        /// The life-safety consequence-type position; −1 skips the life-saved family.
        /// Nothing is inferred from labels — the study says which position is life safety.
        /// </summary>
        public int LifeSafetyConsequenceType { get; }

        /// <summary>
        /// The willingness to pay per statistical life; NaN skips the disproportionality and
        /// ALARP block. No number ships as a default.
        /// </summary>
        public double WillingnessToPay { get; }

        /// <summary>
        /// The willingness-to-pay vintage echoed into the results (empty when none was given).
        /// </summary>
        public string WillingnessToPayVintage { get; }

        /// <summary>
        /// The tolerable-risk proximity selecting the ALARP band table.
        /// </summary>
        public AlarpProximity AlarpProximity { get; }

        /// <summary>
        /// The three ascending band thresholds, or null for the regulation defaults selected
        /// by the proximity.
        /// </summary>
        public IReadOnlyList<double>? AlarpBandThresholds { get; }

        /// <summary>
        /// The individual-risk floor of the equity weighting.
        /// </summary>
        public double IndividualRiskLimit { get; }

        /// <summary>
        /// The equity-versus-efficiency exponent of the equity weighting (1 = equilibrium).
        /// </summary>
        public double EquityExponent { get; }

        /// <summary>
        /// The baseline individual risk; NaN reads the survival-equivalent annualized
        /// failure-probability proxy (echoed as a proxy in the results).
        /// </summary>
        public double BaselineIndividualRisk { get; }

        /// <summary>
        /// The per-alternative individual risk; NaN reads each alternative's
        /// survival-equivalent proxy.
        /// </summary>
        public double AlternativeIndividualRisk { get; }

        /// <summary>
        /// The do-no-harm screen policy.
        /// </summary>
        public DoNoHarmPolicy DoNoHarm { get; }

        /// <summary>
        /// The declared objectives (the default vector minimizes the present value of total
        /// cost, maximizes the monetized present-value benefit, and minimizes the standard
        /// deviation of annual risk).
        /// </summary>
        public IReadOnlyList<ObjectiveDeclaration> Objectives { get; }

        /// <summary>
        /// The fixed constraint set.
        /// </summary>
        public IReadOnlyList<CostBenefitConstraint> Constraints { get; }

        /// <summary>
        /// The ε-constraint study declaration, or null for none.
        /// </summary>
        public EpsilonConstraintStudy? EpsilonStudy { get; }

        /// <summary>
        /// The weighted-sum weights, parallel to the objectives; null skips the weighted-sum
        /// block.
        /// </summary>
        public IReadOnlyList<double>? McdaWeights { get; }

        /// <summary>
        /// The expected-utility declaration, or null to skip that ranking.
        /// </summary>
        public UtilityDeclaration? Utility { get; }

        /// <summary>
        /// The partitioned-risk declaration, or null to skip that table.
        /// </summary>
        public PmrmPartition? PmrmPartition { get; }

        /// <summary>
        /// The Hurwicz optimism blend in [0, 1] (0 = worst case, 1 = best case).
        /// </summary>
        public double HurwiczAlpha { get; }

        /// <summary>
        /// The mean-plus-k-standard-deviations multiplier.
        /// </summary>
        public double DispersionK { get; }

        /// <summary>
        /// The chance-constraint confidence levels.
        /// </summary>
        public IReadOnlyList<double> ChanceConstraintConfidenceLevels { get; }

        /// <summary>
        /// The epistemic tail level for knowledge-ensemble tail averages.
        /// </summary>
        public double EpistemicTailAlpha { get; }

        #endregion

        #region IModel Methods

        /// <summary>
        /// Validates the option-local rules: recognized enum members, level and parameter
        /// ranges, band-threshold shape, weighted-sum weight alignment, and every child
        /// declaration's own rules. Cross-alternative rules (axis alignment, horizon bounds
        /// on plan and cost years, monetization against the declared types) validate at the
        /// study level, where the alternatives live.
        /// </summary>
        /// <returns>The validity flag and messages.</returns>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();
            if (!Enum.IsDefined(BenefitRiskType))
                messages.Add("Error: The benefit stream is not a recognized member.");
            if (!Enum.IsDefined(Accounting))
                messages.Add("Error: The accounting convention is not a recognized member.");
            if (!Enum.IsDefined(AlarpProximity))
                messages.Add("Error: The ALARP proximity is not a recognized member.");
            if (!Enum.IsDefined(DoNoHarm))
                messages.Add("Error: The do-no-harm policy is not a recognized member.");

            if (AlphaLevels.Count == 0)
                messages.Add("Error: At least one exceedance level must be declared.");
            for (int i = 0; i < AlphaLevels.Count; i++)
            {
                if (!Tools.IsFinite(AlphaLevels[i]) || AlphaLevels[i] <= 0d || AlphaLevels[i] >= 1d)
                {
                    messages.Add("Error: Every declared exceedance level must lie in (0, 1).");
                    break;
                }
            }

            if (LifeSafetyConsequenceType < -1)
                messages.Add("Error: The life-safety consequence-type position must be −1 (none) or a declared position.");
            if (!double.IsNaN(WillingnessToPay) && (!Tools.IsFinite(WillingnessToPay) || WillingnessToPay <= 0d))
                messages.Add("Error: The willingness to pay must be positive and finite, or NaN to skip the block.");
            if (AlarpBandThresholds != null)
            {
                if (AlarpBandThresholds.Count != 3)
                {
                    messages.Add("Error: The ALARP band thresholds must supply exactly three ascending values.");
                }
                else
                {
                    bool valid = true;
                    for (int i = 0; i < 3; i++)
                    {
                        if (!Tools.IsFinite(AlarpBandThresholds[i]) || AlarpBandThresholds[i] <= 0d) valid = false;
                    }
                    if (valid && (AlarpBandThresholds[0] >= AlarpBandThresholds[1]
                        || AlarpBandThresholds[1] >= AlarpBandThresholds[2]))
                    {
                        valid = false;
                    }
                    if (!valid)
                        messages.Add("Error: The ALARP band thresholds must be positive, finite, and strictly ascending.");
                }
            }
            if (!Tools.IsFinite(IndividualRiskLimit) || IndividualRiskLimit <= 0d || IndividualRiskLimit >= 1d)
                messages.Add("Error: The individual-risk limit must lie in (0, 1).");
            if (!Tools.IsFinite(EquityExponent) || EquityExponent <= 0d)
                messages.Add("Error: The equity exponent must be finite and positive.");
            if (!double.IsNaN(BaselineIndividualRisk)
                && (!Tools.IsFinite(BaselineIndividualRisk) || BaselineIndividualRisk <= 0d || BaselineIndividualRisk >= 1d))
            {
                messages.Add("Error: The baseline individual risk must lie in (0, 1), or be NaN for the proxy.");
            }
            if (!double.IsNaN(AlternativeIndividualRisk)
                && (!Tools.IsFinite(AlternativeIndividualRisk) || AlternativeIndividualRisk <= 0d || AlternativeIndividualRisk >= 1d))
            {
                messages.Add("Error: The alternative individual risk must lie in (0, 1), or be NaN for the proxy.");
            }
            if (!Tools.IsFinite(HurwiczAlpha) || HurwiczAlpha < 0d || HurwiczAlpha > 1d)
                messages.Add("Error: The Hurwicz blend must lie in [0, 1].");
            if (!Tools.IsFinite(DispersionK) || DispersionK < 0d)
                messages.Add("Error: The dispersion multiplier must be finite and non-negative.");
            if (!Tools.IsFinite(EpistemicTailAlpha) || EpistemicTailAlpha <= 0d || EpistemicTailAlpha >= 1d)
                messages.Add("Error: The epistemic tail level must lie in (0, 1).");
            for (int i = 0; i < ChanceConstraintConfidenceLevels.Count; i++)
            {
                double level = ChanceConstraintConfidenceLevels[i];
                if (!Tools.IsFinite(level) || level <= 0d || level >= 1d)
                {
                    messages.Add("Error: Every chance-constraint confidence level must lie in (0, 1).");
                    break;
                }
            }

            if (McdaWeights != null)
            {
                if (McdaWeights.Count != Objectives.Count)
                {
                    messages.Add("Error: The weighted-sum weight count must equal the objective count.");
                }
                for (int i = 0; i < McdaWeights.Count; i++)
                {
                    if (!Tools.IsFinite(McdaWeights[i]) || McdaWeights[i] <= 0d)
                    {
                        messages.Add("Error: Every weighted-sum weight must be finite and positive.");
                        break;
                    }
                }
            }

            for (int i = 0; i < Objectives.Count; i++)
            {
                (_, List<string> objectiveMessages) = Objectives[i].Validate();
                messages.AddRange(objectiveMessages);
            }
            for (int i = 0; i < Constraints.Count; i++)
            {
                (_, List<string> constraintMessages) = Constraints[i].Validate();
                messages.AddRange(constraintMessages);
            }
            if (EpsilonStudy != null)
            {
                (_, List<string> studyMessages) = EpsilonStudy.Validate();
                messages.AddRange(studyMessages);
            }
            if (Utility != null)
            {
                (_, List<string> utilityMessages) = Utility.Validate();
                messages.AddRange(utilityMessages);
            }
            if (PmrmPartition != null)
            {
                (_, List<string> partitionMessages) = PmrmPartition.Validate();
                messages.AddRange(partitionMessages);
            }
            return (messages.Count == 0, messages);
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the declarations. Element and attribute names are append-only contract;
        /// nullable declarations write only when present, so their absence reads back as
        /// absence.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(CostBenefitOptions));
            element.SetAttributeValue(nameof(PeriodYears), PeriodYears);
            element.SetAttributeValue(nameof(DiscountRate), SerializationUtilities.FormatDouble(DiscountRate));
            element.SetAttributeValue(nameof(EvaluationYears), JoinInts(EvaluationYears));
            element.SetAttributeValue(nameof(BenefitRiskType), BenefitRiskType.ToString());
            element.SetAttributeValue(nameof(Accounting), Accounting.ToString());
            element.SetAttributeValue(nameof(AlphaLevels), JoinDoubles(AlphaLevels));
            element.SetAttributeValue(nameof(LifeSafetyConsequenceType), LifeSafetyConsequenceType);
            element.SetAttributeValue(nameof(WillingnessToPay), SerializationUtilities.FormatDouble(WillingnessToPay));
            element.SetAttributeValue(nameof(WillingnessToPayVintage), WillingnessToPayVintage);
            element.SetAttributeValue(nameof(AlarpProximity), AlarpProximity.ToString());
            if (AlarpBandThresholds != null)
            {
                element.SetAttributeValue(nameof(AlarpBandThresholds), JoinDoubles(AlarpBandThresholds));
            }
            element.SetAttributeValue(nameof(IndividualRiskLimit), SerializationUtilities.FormatDouble(IndividualRiskLimit));
            element.SetAttributeValue(nameof(EquityExponent), SerializationUtilities.FormatDouble(EquityExponent));
            element.SetAttributeValue(nameof(BaselineIndividualRisk), SerializationUtilities.FormatDouble(BaselineIndividualRisk));
            element.SetAttributeValue(nameof(AlternativeIndividualRisk), SerializationUtilities.FormatDouble(AlternativeIndividualRisk));
            element.SetAttributeValue(nameof(DoNoHarm), DoNoHarm.ToString());
            element.SetAttributeValue(nameof(HurwiczAlpha), SerializationUtilities.FormatDouble(HurwiczAlpha));
            element.SetAttributeValue(nameof(DispersionK), SerializationUtilities.FormatDouble(DispersionK));
            element.SetAttributeValue(nameof(ChanceConstraintConfidenceLevels), JoinDoubles(ChanceConstraintConfidenceLevels));
            element.SetAttributeValue(nameof(EpistemicTailAlpha), SerializationUtilities.FormatDouble(EpistemicTailAlpha));
            if (McdaWeights != null)
            {
                element.SetAttributeValue(nameof(McdaWeights), JoinDoubles(McdaWeights));
            }
            if (Monetization != null)
            {
                element.Add(Monetization.ToXElement());
            }
            var objectives = new XElement(nameof(Objectives));
            for (int i = 0; i < Objectives.Count; i++)
            {
                objectives.Add(Objectives[i].ToXElement());
            }
            element.Add(objectives);
            var constraints = new XElement(nameof(Constraints));
            for (int i = 0; i < Constraints.Count; i++)
            {
                constraints.Add(Constraints[i].ToXElement());
            }
            element.Add(constraints);
            if (EpsilonStudy != null)
            {
                element.Add(EpsilonStudy.ToXElement());
            }
            if (Utility != null)
            {
                element.Add(Utility.ToXElement());
            }
            if (PmrmPartition != null)
            {
                element.Add(PmrmPartition.ToXElement());
            }
            return element;
        }

        /// <summary>
        /// Joins an integer list into the pipe-delimited attribute text.
        /// </summary>
        /// <param name="values">The values.</param>
        /// <returns>The attribute text.</returns>
        private static string JoinInts(IReadOnlyList<int> values)
        {
            string[] parts = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                parts[i] = values[i].ToString(CultureInfo.InvariantCulture);
            }
            return string.Join("|", parts);
        }

        /// <summary>
        /// Joins a double list into the pipe-delimited round-trip-exact attribute text.
        /// </summary>
        /// <param name="values">The values.</param>
        /// <returns>The attribute text.</returns>
        private static string JoinDoubles(IReadOnlyList<double> values)
        {
            string[] parts = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                parts[i] = SerializationUtilities.FormatDouble(values[i]);
            }
            return string.Join("|", parts);
        }

        #endregion
    }
}
