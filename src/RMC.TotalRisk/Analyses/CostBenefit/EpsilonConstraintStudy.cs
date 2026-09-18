using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Numerics;
using Numerics.Mathematics.Optimization;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Analyses.CostBenefit
{
    /// <summary>
    /// The declaration of a discrete ε-constraint study: the primary objective, the swept ε
    /// objective, the ε grid (explicit, or automatic across the feasible payoff range), and
    /// the fixed constraints every candidate must satisfy.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The declaration is immutable (replace it to edit). Three convenience templates cover
    /// the standard formulations: the tolerable-life-risk study (minimize total expected
    /// annual cost subject to the Excess annualized life-loss guideline, sweeping a declared
    /// tail metric), the mean-variance study (minimize mean risk, sweeping its standard
    /// deviation), and the reliability study (minimize cost, sweeping the annualized failure
    /// probability). Guideline numbers appear only as default arguments — never as
    /// hard-coded policy constants. Element and attribute names are append-only serialized
    /// contract; a null grid means automatic and writes no grid attribute.
    /// </para>
    /// </remarks>
    public sealed class EpsilonConstraintStudy
    {
        #region Construction

        /// <summary>
        /// Initializes an ε-constraint study declaration.
        /// </summary>
        /// <param name="primary">The primary objective optimized at each ε.</param>
        /// <param name="epsilonObjective">The metric the ε bound sweeps.</param>
        /// <param name="epsilonGrid">The explicit ε values, or null for an automatic uniform grid.</param>
        /// <param name="gridPoints">The automatic grid's point count (used only when the grid is null).</param>
        /// <param name="fixedConstraints">The fixed constraints every candidate must satisfy; null or empty for none.</param>
        /// <exception cref="ArgumentNullException">Thrown when the primary objective or ε metric is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the automatic grid has fewer than two points.</exception>
        /// <exception cref="ArgumentException">Thrown when a supplied list contains a null entry.</exception>
        public EpsilonConstraintStudy(ObjectiveDeclaration primary, CostBenefitMetric epsilonObjective,
            IReadOnlyList<double>? epsilonGrid = null, int gridPoints = 10,
            IReadOnlyList<CostBenefitConstraint>? fixedConstraints = null)
        {
            Primary = primary ?? throw new ArgumentNullException(nameof(primary));
            EpsilonObjective = epsilonObjective ?? throw new ArgumentNullException(nameof(epsilonObjective));
            if (gridPoints < 2)
                throw new ArgumentOutOfRangeException(nameof(gridPoints),
                    "The automatic ε grid needs at least two points.");
            GridPoints = gridPoints;
            EpsilonGrid = epsilonGrid == null ? null : Array.AsReadOnly(epsilonGrid.ToArray());

            var snapshot = fixedConstraints == null
                ? Array.Empty<CostBenefitConstraint>()
                : fixedConstraints.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] == null)
                    throw new ArgumentException("The fixed-constraint list contains a null entry.",
                        nameof(fixedConstraints));
            }
            FixedConstraints = Array.AsReadOnly(snapshot);
        }

        /// <summary>
        /// Restores a declaration from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public EpsilonConstraintStudy(XElement xElement)
            : this(ReadPrimary(SerializationUtilities.RequireElement(xElement, nameof(xElement))),
                ReadEpsilonObjective(xElement),
                ReadGrid(xElement),
                SerializationUtilities.ReadInt32(xElement, nameof(GridPoints), 10),
                ReadConstraints(xElement))
        {
        }

        /// <summary>
        /// Reads the primary objective; a missing child reads as a minimize-mean default so
        /// older forms load forward.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The primary objective.</returns>
        private static ObjectiveDeclaration ReadPrimary(XElement xElement)
        {
            XElement? wrapper = xElement.Element(nameof(Primary));
            XElement? child = wrapper?.Element(nameof(ObjectiveDeclaration));
            return child == null
                ? new ObjectiveDeclaration("Objective",
                    CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total),
                    ObjectiveDirection.Minimize)
                : new ObjectiveDeclaration(child);
        }

        /// <summary>
        /// Reads the ε-objective metric; a missing child reads as the mean-total-risk selector.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The ε metric.</returns>
        private static CostBenefitMetric ReadEpsilonObjective(XElement xElement)
        {
            XElement? wrapper = xElement.Element(nameof(EpsilonObjective));
            XElement? child = wrapper?.Element(nameof(CostBenefitMetric));
            return child == null
                ? CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total)
                : new CostBenefitMetric(child);
        }

        /// <summary>
        /// Reads the explicit ε grid; an absent attribute means automatic (null).
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The grid, or null.</returns>
        private static IReadOnlyList<double>? ReadGrid(XElement xElement)
        {
            XAttribute? attribute = xElement.Attribute(nameof(EpsilonGrid));
            if (attribute == null) return null;
            string[] parts = attribute.Value.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var grid = new List<double>(parts.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                grid.Add(SerializationUtilities.ParseDouble(parts[i]));
            }
            return grid;
        }

        /// <summary>
        /// Reads the fixed constraints from the serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <returns>The constraints.</returns>
        private static IReadOnlyList<CostBenefitConstraint> ReadConstraints(XElement xElement)
        {
            XElement? wrapper = xElement.Element(nameof(FixedConstraints));
            if (wrapper == null) return Array.Empty<CostBenefitConstraint>();
            var constraints = new List<CostBenefitConstraint>();
            foreach (XElement child in wrapper.Elements(nameof(CostBenefitConstraint)))
            {
                constraints.Add(new CostBenefitConstraint(child));
            }
            return constraints;
        }

        /// <summary>
        /// Creates the tolerable-life-risk template: minimize total expected annual cost
        /// subject to the Excess annualized life-loss guideline in every epoch, sweeping a
        /// declared tail metric over ε.
        /// </summary>
        /// <param name="lifeSafetyConsequenceType">The declared life-safety consequence-type position.</param>
        /// <param name="tolerableRiskGuideline">The annualized life-loss guideline (a default argument, never a hard-coded policy).</param>
        /// <param name="sweptTailMetric">The swept tail metric; null declares the Excess-stream conditional mean of the life-safety type.</param>
        /// <param name="epsilonGrid">The explicit ε values, or null for an automatic uniform grid.</param>
        /// <param name="gridPoints">The automatic grid's point count.</param>
        /// <returns>The study declaration.</returns>
        public static EpsilonConstraintStudy CreateTolerableLifeRiskStudy(int lifeSafetyConsequenceType,
            double tolerableRiskGuideline = 1e-3, CostBenefitMetric? sweptTailMetric = null,
            IReadOnlyList<double>? epsilonGrid = null, int gridPoints = 10)
        {
            var primary = new ObjectiveDeclaration("Total expected annual cost",
                CostBenefitMetric.ForEconomic(EconomicMetric.TotalExpectedAnnualCost),
                ObjectiveDirection.Minimize);
            CostBenefitMetric epsilon = sweptTailMetric
                ?? CostBenefitMetric.ForRiskMeasure(RiskMeasure.ConditionalMean, RiskType.Excess,
                    lifeSafetyConsequenceType);
            var guideline = new CostBenefitConstraint(
                CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Excess, lifeSafetyConsequenceType),
                ConstraintType.LesserThanOrEqualTo, tolerableRiskGuideline, ConstraintScope.EveryEpoch);
            return new EpsilonConstraintStudy(primary, epsilon, epsilonGrid, gridPoints,
                new[] { guideline });
        }

        /// <summary>
        /// Creates the mean-variance template: minimize mean annual risk, sweeping its
        /// standard deviation over ε.
        /// </summary>
        /// <param name="consequenceType">The consequence-type position (0 = the primary type).</param>
        /// <param name="epsilonGrid">The explicit ε values, or null for an automatic uniform grid.</param>
        /// <param name="gridPoints">The automatic grid's point count.</param>
        /// <returns>The study declaration.</returns>
        public static EpsilonConstraintStudy CreateMeanVarianceStudy(int consequenceType = 0,
            IReadOnlyList<double>? epsilonGrid = null, int gridPoints = 10)
        {
            var primary = new ObjectiveDeclaration("Mean annual risk",
                CostBenefitMetric.ForRiskMeasure(RiskMeasure.Mean, RiskType.Total, consequenceType),
                ObjectiveDirection.Minimize);
            CostBenefitMetric epsilon = CostBenefitMetric.ForRiskMeasure(RiskMeasure.StandardDeviation,
                RiskType.Total, consequenceType);
            return new EpsilonConstraintStudy(primary, epsilon, epsilonGrid, gridPoints);
        }

        /// <summary>
        /// Creates the reliability template: minimize the present value of total cost,
        /// sweeping the annualized failure probability over ε.
        /// </summary>
        /// <param name="epsilonGrid">The explicit ε values, or null for an automatic uniform grid.</param>
        /// <param name="gridPoints">The automatic grid's point count.</param>
        /// <returns>The study declaration.</returns>
        public static EpsilonConstraintStudy CreateReliabilityStudy(
            IReadOnlyList<double>? epsilonGrid = null, int gridPoints = 10)
        {
            var primary = new ObjectiveDeclaration("Present value of total cost",
                CostBenefitMetric.ForEconomic(EconomicMetric.PresentValueOfTotalCost),
                ObjectiveDirection.Minimize);
            CostBenefitMetric epsilon = CostBenefitMetric.ForEconomic(
                EconomicMetric.AnnualizedFailureProbability);
            return new EpsilonConstraintStudy(primary, epsilon, epsilonGrid, gridPoints);
        }

        #endregion

        #region Members

        /// <summary>
        /// The primary objective optimized at each ε.
        /// </summary>
        public ObjectiveDeclaration Primary { get; }

        /// <summary>
        /// The metric the ε bound sweeps.
        /// </summary>
        public CostBenefitMetric EpsilonObjective { get; }

        /// <summary>
        /// The explicit ε values, or null for an automatic uniform grid across the feasible
        /// payoff range.
        /// </summary>
        public IReadOnlyList<double>? EpsilonGrid { get; }

        /// <summary>
        /// The automatic grid's point count (used only when <see cref="EpsilonGrid"/> is null).
        /// </summary>
        public int GridPoints { get; }

        /// <summary>
        /// The fixed constraints every candidate must satisfy at every ε.
        /// </summary>
        public IReadOnlyList<CostBenefitConstraint> FixedConstraints { get; }

        #endregion

        #region IModel Methods

        /// <summary>
        /// Validates the declaration: valid primary, ε metric, and fixed constraints, plus a
        /// finite explicit grid when one is supplied.
        /// </summary>
        /// <returns>The validity flag and messages.</returns>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();
            (_, List<string> primaryMessages) = Primary.Validate();
            messages.AddRange(primaryMessages);
            (_, List<string> epsilonMessages) = EpsilonObjective.Validate();
            messages.AddRange(epsilonMessages);
            if (EpsilonGrid != null)
            {
                if (EpsilonGrid.Count == 0)
                    messages.Add("Error: The explicit ε grid must contain at least one value.");
                for (int i = 0; i < EpsilonGrid.Count; i++)
                {
                    if (!Tools.IsFinite(EpsilonGrid[i]))
                    {
                        messages.Add("Error: The explicit ε grid must contain only finite values.");
                        break;
                    }
                }
            }
            for (int i = 0; i < FixedConstraints.Count; i++)
            {
                (_, List<string> constraintMessages) = FixedConstraints[i].Validate();
                messages.AddRange(constraintMessages);
            }
            return (messages.Count == 0, messages);
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the declaration. Element and attribute names are append-only contract;
        /// the grid attribute is written only when explicit.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(EpsilonConstraintStudy));
            element.SetAttributeValue(nameof(GridPoints), GridPoints);
            if (EpsilonGrid != null)
            {
                string[] parts = new string[EpsilonGrid.Count];
                for (int i = 0; i < EpsilonGrid.Count; i++)
                {
                    parts[i] = SerializationUtilities.FormatDouble(EpsilonGrid[i]);
                }
                element.SetAttributeValue(nameof(EpsilonGrid), string.Join("|", parts));
            }
            var primary = new XElement(nameof(Primary));
            primary.Add(Primary.ToXElement());
            element.Add(primary);
            var epsilon = new XElement(nameof(EpsilonObjective));
            epsilon.Add(EpsilonObjective.ToXElement());
            element.Add(epsilon);
            var constraints = new XElement(nameof(FixedConstraints));
            for (int i = 0; i < FixedConstraints.Count; i++)
            {
                constraints.Add(FixedConstraints[i].ToXElement());
            }
            element.Add(constraints);
            return element;
        }

        #endregion
    }
}
