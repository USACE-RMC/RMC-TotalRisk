using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Analyses.Risk
{
    /// <summary>
    /// The risk analysis run options: the v1.0 option surface (names, defaults, and validation
    /// ranges preserved) extracted into its own type, plus the deliberate v1.1 additions — the
    /// sampling scheme, the analysis mode, the adaptive-refinement objective, and the
    /// system-risk tail knobs.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The full option surface was defined together, before every engine capability existed:
    /// the multi-dimensional system-risk fields (<see cref="VegasTailFocusMode"/>,
    /// <see cref="VegasTailFocusParameter"/>, <see cref="SystemConvolutionPoints"/>) and the
    /// reliability mode (<see cref="Mode"/>) were serialized ahead of the engine stages that
    /// now read them — writing the serialized attribute surface once keeps the append-only
    /// contract churn-free. The effective v1.0 integration defaults are the
    /// <see cref="SetIntegrationDefaults"/> values (the legacy field initializers were dead
    /// because <c>UseDefaults</c> invoked the reset): one million evaluations, depth 100,
    /// tolerance 1e-8, warm-up 1000·D capped at 50000 over five cycles, and 10000·D final
    /// evaluations capped at 100000 — the component-count scaling of the final evaluations is
    /// a deliberate v1.1 extension, identical to v1.0 at one component.
    /// </para>
    /// <para>
    /// <b>Hashing:</b> every compute-relevant field participates in the canonical hash through
    /// <see cref="ToXElement"/>; <see cref="UseDefaults"/> is stripped (it records who wrote the
    /// integration settings, not what they are), and the correlation matrix serializes (and
    /// therefore hashes) only under <see cref="DependencyType.CorrelationMatrix"/> — in the
    /// automatic modes it is derived state. The tolerable-risk criteria serialize (and
    /// therefore hash) only when configured, so every criteria-free options form and hash is
    /// unchanged. The options hash never feeds Monte Carlo seeds
    /// (only <see cref="PRNGSeed"/> does).
    /// </para>
    /// </remarks>
    public class RiskAnalysisOptions : INotifyPropertyChanged
    {
        #region Construction

        /// <summary>
        /// Initializes options with the v1.0 defaults.
        /// </summary>
        public RiskAnalysisOptions()
        {
            _tolerableRiskCriteria.CollectionChanged += TolerableRiskCriteriaChanged;
        }

        /// <summary>
        /// Restores options from their serialized form. Missing attributes fall back to the
        /// defaults, so older forms load forward.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public RiskAnalysisOptions(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            _estimateMeanRiskOnly = SerializationUtilities.ReadBoolean(xElement, nameof(EstimateMeanRiskOnly), true);
            _realizations = SerializationUtilities.ReadInt32(xElement, nameof(Realizations), 1000);
            _confidenceIntervalWidth = SerializationUtilities.ReadDouble(xElement, nameof(ConfidenceIntervalWidth), 0.9d);
            _prngSeed = SerializationUtilities.ReadInt32(xElement, nameof(PRNGSeed), 12345);
            _lecOutputLength = SerializationUtilities.ReadInt32(xElement, nameof(LECOutputLength), 200);
            _samplingScheme = SerializationUtilities.ReadEnum(xElement, nameof(SamplingScheme), SamplingScheme.LatinHypercube);
            _mode = SerializationUtilities.ReadEnum(xElement, nameof(Mode), RiskAnalysisMode.Risk);
            _riskIntegrand = SerializationUtilities.ReadEnum(xElement, nameof(RiskIntegrand), RiskIntegrand.MeanTotalRisk);
            _systemRiskMethod = SerializationUtilities.ReadEnum(xElement, nameof(SystemRiskMethod), SystemRiskType.AdditiveRiskMethod);
            _jointConsequences = SerializationUtilities.ReadEnum(xElement, nameof(JointConsequences), JointConsequenceType.Additive);
            _componentHazardDependency = SerializationUtilities.ReadEnum(xElement, nameof(ComponentHazardDependency), DependencyType.Independent);
            _hazardCorrelationMatrix = SerializationUtilities.ParseMatrix(xElement.Attribute(nameof(HazardCorrelationMatrix))?.Value);
            _consequenceThreshold = SerializationUtilities.ReadDouble(xElement, nameof(ConsequenceThreshold), 0d);
            _alpha = SerializationUtilities.ReadDouble(xElement, nameof(Alpha), 0.01d);
            _maxEvaluations = SerializationUtilities.ReadInt32(xElement, nameof(MaxEvaluations), 1_000_000);
            _maxDepth = SerializationUtilities.ReadInt32(xElement, nameof(MaxDepth), 100);
            _tolerance = SerializationUtilities.ReadDouble(xElement, nameof(Tolerance), 1e-8);
            _warmupEvaluations = SerializationUtilities.ReadInt32(xElement, nameof(WarmupEvaluations), 1000);
            _warmupCycles = SerializationUtilities.ReadInt32(xElement, nameof(WarmupCycles), 5);
            _finalEvaluations = SerializationUtilities.ReadInt32(xElement, nameof(FinalEvaluations), 10_000);
            _useDefaults = SerializationUtilities.ReadBoolean(xElement, nameof(UseDefaults), true);
            _vegasTailFocusMode = SerializationUtilities.ReadEnum(xElement, nameof(VegasTailFocusMode), VegasTailFocusMode.Automatic);
            _vegasTailFocusParameter = SerializationUtilities.ReadDouble(xElement, nameof(VegasTailFocusParameter), 1d);
            _systemConvolutionPoints = SerializationUtilities.ReadInt32(xElement, nameof(SystemConvolutionPoints), 4096);
            _ensembleTolerance = SerializationUtilities.ReadDouble(xElement, nameof(EnsembleTolerance), 1e-4);
            _ensembleMinDepth = SerializationUtilities.ReadInt32(xElement, nameof(EnsembleMinDepth), 0);
            _maxSystemCombinations = SerializationUtilities.ReadInt32(xElement, nameof(MaxSystemCombinations), 65_536);
            _maxPathwayCombinations = SerializationUtilities.ReadInt32(xElement, nameof(MaxPathwayCombinations), 4_096);
            _riskMeasures = SerializationUtilities.ReadEnum(xElement, nameof(RiskMeasures), RiskMeasureOptions.All);
            _outputAdjustedFailureModeCurves = SerializationUtilities.ReadBoolean(xElement, nameof(OutputAdjustedFailureModeCurves), false);
            _useSobolJointSampling = SerializationUtilities.ReadBoolean(xElement, nameof(UseSobolJointSampling), false);

            // Conditional-presence child: absent on every criteria-free form.
            var criteriaElement = xElement.Element(nameof(TolerableRiskCriteria));
            if (criteriaElement != null)
            {
                foreach (var child in criteriaElement.Elements(nameof(TolerableRiskCriterion)))
                {
                    _tolerableRiskCriteria.Add(new TolerableRiskCriterion(child));
                }
            }
            _tolerableRiskCriteria.CollectionChanged += TolerableRiskCriteriaChanged;
        }

        #endregion

        #region Members

        /// <summary>Backing field for <see cref="EstimateMeanRiskOnly"/>.</summary>
        private bool _estimateMeanRiskOnly = true;

        /// <summary>Backing field for <see cref="Realizations"/>.</summary>
        private int _realizations = 1000;

        /// <summary>Backing field for <see cref="ConfidenceIntervalWidth"/>.</summary>
        private double _confidenceIntervalWidth = 0.9d;

        /// <summary>Backing field for <see cref="PRNGSeed"/>.</summary>
        private int _prngSeed = 12345;

        /// <summary>Backing field for <see cref="LECOutputLength"/>.</summary>
        private int _lecOutputLength = 200;

        /// <summary>Backing field for <see cref="SamplingScheme"/>.</summary>
        private SamplingScheme _samplingScheme = SamplingScheme.LatinHypercube;

        /// <summary>Backing field for <see cref="Mode"/>.</summary>
        private RiskAnalysisMode _mode = RiskAnalysisMode.Risk;

        /// <summary>Backing field for <see cref="RiskIntegrand"/>.</summary>
        private RiskIntegrand _riskIntegrand = RiskIntegrand.MeanTotalRisk;

        /// <summary>Backing field for <see cref="SystemRiskMethod"/>.</summary>
        private SystemRiskType _systemRiskMethod = SystemRiskType.AdditiveRiskMethod;

        /// <summary>Backing field for <see cref="JointConsequences"/>.</summary>
        private JointConsequenceType _jointConsequences = JointConsequenceType.Additive;

        /// <summary>Backing field for <see cref="ComponentHazardDependency"/>.</summary>
        private DependencyType _componentHazardDependency = DependencyType.Independent;

        /// <summary>Backing field for <see cref="HazardCorrelationMatrix"/>.</summary>
        private double[,]? _hazardCorrelationMatrix;

        /// <summary>Backing field for <see cref="ConsequenceThreshold"/>.</summary>
        private double _consequenceThreshold;

        /// <summary>Backing field for <see cref="Alpha"/>.</summary>
        private double _alpha = 0.01d;

        /// <summary>Backing field for <see cref="MaxEvaluations"/>.</summary>
        private int _maxEvaluations = 1_000_000;

        /// <summary>Backing field for <see cref="MaxDepth"/>.</summary>
        private int _maxDepth = 100;

        /// <summary>Backing field for <see cref="Tolerance"/>.</summary>
        private double _tolerance = 1e-8;

        /// <summary>Backing field for <see cref="WarmupEvaluations"/>.</summary>
        private int _warmupEvaluations = 1000;

        /// <summary>Backing field for <see cref="WarmupCycles"/>.</summary>
        private int _warmupCycles = 5;

        /// <summary>Backing field for <see cref="FinalEvaluations"/>.</summary>
        private int _finalEvaluations = 10_000;

        /// <summary>Backing field for <see cref="UseDefaults"/>.</summary>
        private bool _useDefaults = true;
        /// <summary>Component count used when materializing integration defaults.</summary>
        private int _defaultComponentCount = 1;

        /// <summary>
        /// Suppresses explicit-assignment tracking while defaults are materialized internally.
        /// </summary>
        private bool _applyingIntegrationDefaults;


        /// <summary>Backing field for <see cref="VegasTailFocusMode"/>.</summary>
        private VegasTailFocusMode _vegasTailFocusMode = VegasTailFocusMode.Automatic;

        /// <summary>Backing field for <see cref="VegasTailFocusParameter"/>.</summary>
        private double _vegasTailFocusParameter = 1d;

        /// <summary>Backing field for <see cref="SystemConvolutionPoints"/>.</summary>
        private int _systemConvolutionPoints = 4096;

        /// <summary>Backing collection for <see cref="TolerableRiskCriteria"/>.</summary>
        private readonly ObservableCollection<TolerableRiskCriterion> _tolerableRiskCriteria =
            new ObservableCollection<TolerableRiskCriterion>();

        /// <summary>
        /// Backing field for <see cref="MaxSystemCombinations"/>.
        /// </summary>
        private int _maxSystemCombinations = 65_536;

        /// <summary>
        /// Backing field for <see cref="MaxPathwayCombinations"/>.
        /// </summary>
        private int _maxPathwayCombinations = 4_096;

        /// <summary>
        /// Backing field for <see cref="RiskMeasures"/>.
        /// </summary>
        private RiskMeasureOptions _riskMeasures = RiskMeasureOptions.All;

        /// <summary>
        /// Backing field for <see cref="OutputAdjustedFailureModeCurves"/>.
        /// </summary>
        private bool _outputAdjustedFailureModeCurves;

        /// <summary>Backing field for <see cref="UseSobolJointSampling"/>.</summary>
        private bool _useSobolJointSampling;

        /// <summary>Backing field for <see cref="EnsembleTolerance"/>.</summary>
        private double _ensembleTolerance = 1e-4;

        /// <summary>Backing field for <see cref="EnsembleMinDepth"/>.</summary>
        private int _ensembleMinDepth;

        /// <summary>
        /// Determines whether the run estimates mean risk only (a single pass on the expected
        /// input functions) instead of the full knowledge-uncertainty ensemble. The v1.0 default.
        /// </summary>
        public bool EstimateMeanRiskOnly
        {
            get { return _estimateMeanRiskOnly; }
            set { SetField(ref _estimateMeanRiskOnly, value, nameof(EstimateMeanRiskOnly)); }
        }

        /// <summary>
        /// The number of knowledge-uncertainty realizations in a full run, in [100, 10000].
        /// </summary>
        public int Realizations
        {
            get { return _realizations; }
            set { SetField(ref _realizations, value, nameof(Realizations)); }
        }

        /// <summary>
        /// The width of the reported confidence interval, in (0, 1) — drives the percentile
        /// curves and the per-function uncertainty summaries.
        /// </summary>
        public double ConfidenceIntervalWidth
        {
            get { return _confidenceIntervalWidth; }
            set { SetField(ref _confidenceIntervalWidth, value, nameof(ConfidenceIntervalWidth)); }
        }

        /// <summary>
        /// The pseudo-random seed the content-based seed derivation folds with each component's
        /// canonical hash. Must be positive.
        /// </summary>
        public int PRNGSeed
        {
            get { return _prngSeed; }
            set { SetField(ref _prngSeed, value, nameof(PRNGSeed)); }
        }

        /// <summary>
        /// The output resolution of stored loss exceedance curves, in [50, 1000]. Purely an
        /// output knob — the exact construction computes statistics before thinning
        /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.7).
        /// </summary>
        public int LECOutputLength
        {
            get { return _lecOutputLength; }
            set { SetField(ref _lecOutputLength, value, nameof(LECOutputLength)); }
        }

        /// <summary>
        /// The knowledge-uncertainty sampling scheme. Latin hypercube by default (the deliberate
        /// v1.1 variance-reduction upgrade); <see cref="SamplingScheme.MonteCarlo"/> preserves
        /// v1.0 independent sampling.
        /// </summary>
        public SamplingScheme SamplingScheme
        {
            get { return _samplingScheme; }
            set { SetField(ref _samplingScheme, value, nameof(SamplingScheme)); }
        }

        /// <summary>
        /// What the analysis computes: full risk, or reliability (annual failure probability)
        /// only.
        /// </summary>
        public RiskAnalysisMode Mode
        {
            get { return _mode; }
            set { SetField(ref _mode, value, nameof(Mode)); }
        }

        /// <summary>
        /// The adaptive-refinement objective steering where the risk integrator concentrates its
        /// evaluations. All risk-type curves and measures are produced regardless; the default
        /// reproduces v1.0 point placement.
        /// </summary>
        public RiskIntegrand RiskIntegrand
        {
            get { return _riskIntegrand; }
            set { SetField(ref _riskIntegrand, value, nameof(RiskIntegrand)); }
        }

        /// <summary>
        /// The system risk aggregation method (v1.0 name). The additive method assumes strictly
        /// independent components (a deliberate v1.1 redefinition —
        /// docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.8); cross-component dependence
        /// belongs to the joint method.
        /// </summary>
        public SystemRiskType SystemRiskMethod
        {
            get { return _systemRiskMethod; }
            set { SetField(ref _systemRiskMethod, value, nameof(SystemRiskMethod)); }
        }

        /// <summary>
        /// How consequences combine when multiple components fail jointly (the joint method's
        /// combination rule).
        /// </summary>
        public JointConsequenceType JointConsequences
        {
            get { return _jointConsequences; }
            set { SetField(ref _jointConsequences, value, nameof(JointConsequences)); }
        }

        /// <summary>
        /// The statistical dependence between component hazards (joint method only — the
        /// additive method requires independence).
        /// </summary>
        public DependencyType ComponentHazardDependency
        {
            get { return _componentHazardDependency; }
            set { SetField(ref _componentHazardDependency, value, nameof(ComponentHazardDependency)); }
        }

        /// <summary>
        /// The cross-component hazard correlation matrix — user content under
        /// <see cref="DependencyType.CorrelationMatrix"/> (one row per component; validated
        /// positive definite at the analysis level, where the component count is known).
        /// Serialized and hashed only in that mode.
        /// </summary>
        public double[,]? HazardCorrelationMatrix
        {
            get { return _hazardCorrelationMatrix == null ? null : (double[,])_hazardCorrelationMatrix.Clone(); }
            set
            {
                _hazardCorrelationMatrix = value == null ? null : (double[,])value.Clone();
                RaisePropertyChange(nameof(HazardCorrelationMatrix));
            }
        }

        /// <summary>
        /// The consequence threshold behind the assurance measure, P(C &gt; threshold).
        /// </summary>
        public double ConsequenceThreshold
        {
            get { return _consequenceThreshold; }
            set { SetField(ref _consequenceThreshold, value, nameof(ConsequenceThreshold)); }
        }

        /// <summary>
        /// The exceedance level for value-at-risk and conditional value-at-risk, in
        /// [1e-16, 1 − 1e-16].
        /// </summary>
        public double Alpha
        {
            get { return _alpha; }
            set { SetField(ref _alpha, value, nameof(Alpha)); }
        }

        /// <summary>
        /// The adaptive integrator's evaluation cap, in [10000, 1000000].
        /// </summary>
        public int MaxEvaluations
        {
            get { return _maxEvaluations; }
            set { SetIntegrationField(ref _maxEvaluations, value, nameof(MaxEvaluations)); }
        }

        /// <summary>
        /// The adaptive integrator's recursion depth cap, in [10, 500].
        /// </summary>
        public int MaxDepth
        {
            get { return _maxDepth; }
            set { SetIntegrationField(ref _maxDepth, value, nameof(MaxDepth)); }
        }

        /// <summary>
        /// The adaptive integrator's relative tolerance, in [1e-15, 0.01].
        /// </summary>
        public double Tolerance
        {
            get { return _tolerance; }
            set { SetIntegrationField(ref _tolerance, value, nameof(Tolerance)); }
        }

        /// <summary>
        /// The 1D adaptive integrator's relative tolerance inside ensemble (full-uncertainty)
        /// realizations, in [1e-15, 0.01]. Defaults to 1e-4 — the v1.0 discipline: ensemble
        /// statistics average integration noise across realizations, so per-realization
        /// integration need not carry the mean pass's 1e-8 rigor (the mean pass, the probes,
        /// and mean-only runs always use <see cref="Tolerance"/>). Tighten toward
        /// <see cref="Tolerance"/> to make individual ensemble realizations
        /// quadrature-converged (e.g., for per-realization parity studies).
        /// </summary>
        public double EnsembleTolerance
        {
            get { return _ensembleTolerance; }
            set { SetIntegrationField(ref _ensembleTolerance, value, nameof(EnsembleTolerance)); }
        }

        /// <summary>
        /// The 1D adaptive integrator's minimum subdivision depth inside ensemble
        /// (full-uncertainty) realizations, in [0, 10]. Defaults to 0 — with 50 stratified
        /// seed bins the forced-depth evaluation floor is what made every ensemble realization
        /// cost ~4,200 evaluations regardless of convergence; the mean pass, the probes, and
        /// mean-only runs always run at minimum depth 2.
        /// </summary>
        public int EnsembleMinDepth
        {
            get { return _ensembleMinDepth; }
            set { SetIntegrationField(ref _ensembleMinDepth, value, nameof(EnsembleMinDepth)); }
        }

        /// <summary>
        /// The VEGAS warm-up evaluations per cycle (joint method), in [100, 50000].
        /// </summary>
        public int WarmupEvaluations
        {
            get { return _warmupEvaluations; }
            set { SetIntegrationField(ref _warmupEvaluations, value, nameof(WarmupEvaluations)); }
        }

        /// <summary>
        /// The VEGAS warm-up cycles (joint method), in [1, 100].
        /// </summary>
        public int WarmupCycles
        {
            get { return _warmupCycles; }
            set { SetIntegrationField(ref _warmupCycles, value, nameof(WarmupCycles)); }
        }

        /// <summary>
        /// The VEGAS recording-pass evaluations (joint method), in [1000, 100000].
        /// </summary>
        public int FinalEvaluations
        {
            get { return _finalEvaluations; }
            set { SetIntegrationField(ref _finalEvaluations, value, nameof(FinalEvaluations)); }
        }

        /// <summary>
        /// Determines whether the integration settings track the defaults: setting <c>true</c>
        /// immediately materializes <see cref="SetIntegrationDefaults"/> for the owning
        /// analysis's component count. Convenience metadata — stripped from the canonical hash
        /// because the effective settings themselves are already materialized and hashed.
        /// </summary>
        public bool UseDefaults
        {
            get { return _useDefaults; }
            set
            {
                if (!value)
                {
                    if (_useDefaults)
                    {
                        _useDefaults = false;
                        RaisePropertyChange(nameof(UseDefaults));
                    }
                    return;
                }

                bool changed = !_useDefaults;
                _useDefaults = true;
                ApplyIntegrationDefaults(_defaultComponentCount);
                if (changed) RaisePropertyChange(nameof(UseDefaults));
            }
        }

        /// <summary>
        /// How the joint method sets the VEGAS power-transform tail focus. Automatic by default;
        /// read by the joint (multi-dimensional) system method.
        /// </summary>
        public VegasTailFocusMode VegasTailFocusMode
        {
            get { return _vegasTailFocusMode; }
            set { SetField(ref _vegasTailFocusMode, value, nameof(VegasTailFocusMode)); }
        }

        /// <summary>
        /// The manual VEGAS tail-focus parameter γ, in [1, 20]; γ = 1 is the identity transform
        /// (exact v1.0 sampling).
        /// </summary>
        public double VegasTailFocusParameter
        {
            get { return _vegasTailFocusParameter; }
            set { SetField(ref _vegasTailFocusParameter, value, nameof(VegasTailFocusParameter)); }
        }

        /// <summary>
        /// The output resolution of the additive method's FFT system convolution, in
        /// [4096, 1048576] — the linear convolution grid starves order-of-magnitude consequence
        /// tails below 4096 points.
        /// </summary>
        public int SystemConvolutionPoints
        {
            get { return _systemConvolutionPoints; }
            set { SetField(ref _systemConvolutionPoints, value, nameof(SystemConvolutionPoints)); }
        }

        /// <summary>
        /// The cap on exclusive component-failure combinations enumerated per joint-system
        /// evaluation. Default 65,536.
        /// </summary>
        /// <remarks>
        /// The inclusion-exclusion expansion normally closes on its own convergence bracket well
        /// inside this, and does so after the third subset size at the failure probabilities a risk
        /// model carries. The cap bounds the one regime the bracket cannot close — many components
        /// near probability one half — where the run would otherwise enumerate 2^D combinations per
        /// evaluation. Reaching it truncates rather than throws, and the dropped mass is reported
        /// as a computation warning.
        /// </remarks>
        public int MaxSystemCombinations
        {
            get { return _maxSystemCombinations; }
            set { SetField(ref _maxSystemCombinations, value, nameof(MaxSystemCombinations)); }
        }

        /// <summary>
        /// The cap on exclusive failure pathways enumerated per component per evaluation under the
        /// joint failure-mode method. Default 4,096.
        /// </summary>
        /// <remarks>
        /// Tighter than <see cref="MaxSystemCombinations"/> because this expansion runs once per
        /// integrand evaluation per component rather than once per system evaluation.
        /// </remarks>
        public int MaxPathwayCombinations
        {
            get { return _maxPathwayCombinations; }
            set { SetField(ref _maxPathwayCombinations, value, nameof(MaxPathwayCombinations)); }
        }

        /// <summary>
        /// Which optional risk measures to compute. Every measure by default.
        /// </summary>
        /// <remarks>
        /// The mean, standard deviation, total probability, and mass balance are always computed.
        /// Switching a measure off leaves it <see cref="double.NaN"/> and saves its cost on every
        /// stream of every consequence type of every realization.
        /// </remarks>
        public RiskMeasureOptions RiskMeasures
        {
            get { return _riskMeasures; }
            set { SetField(ref _riskMeasures, value, nameof(RiskMeasures)); }
        }

        /// <summary>
        /// Whether to also output each failure mode's combination-adjusted loss exceedance curves
        /// alongside its unadjusted ones. Off by default.
        /// </summary>
        /// <remarks>
        /// A failure mode's own curves carry its raw marginal response probability, which is what
        /// an investment decision compares across modes. The adjusted curves carry the mode's share
        /// after the component's combination method has resolved the modes against one another, so
        /// they sum to the component total. Both are useful; they answer different questions.
        /// </remarks>
        public bool OutputAdjustedFailureModeCurves
        {
            get { return _outputAdjustedFailureModeCurves; }
            set { SetField(ref _outputAdjustedFailureModeCurves, value, nameof(OutputAdjustedFailureModeCurves)); }
        }

        /// <summary>
        /// Whether the joint (VEGAS) integration drives its sampling with seeded scrambled
        /// Sobol points instead of the seeded pseudo-random generator. Default false — the
        /// existing driver, byte-for-byte. When enabled, the joint path seeds the scrambled
        /// sequence from the same content-derived per-realization seed that drives the
        /// pseudo-random generator, so the v1.0 quasi-random driver returns without giving up
        /// the content-seed reproducibility contract (the unrandomized sequence v1.0 used is
        /// seed-independent, which is why v1.1 originally switched it off). A deliberate,
        /// hashed, value-moving selection: the attribute serializes only when enabled, so every
        /// existing options form and hash is unchanged, while an enabled option is
        /// compute-relevant hashed content.
        /// </summary>
        public bool UseSobolJointSampling
        {
            get { return _useSobolJointSampling; }
            set { SetField(ref _useSobolJointSampling, value, nameof(UseSobolJointSampling)); }
        }

        /// <summary>
        /// The tolerable-risk criteria the full-uncertainty run evaluates over its ensemble —
        /// one epistemic confidence statement P(measure &gt; threshold) per entry, at the
        /// system scope, weighted by any realization weights. Empty by default (no criteria,
        /// no output block). Entries are immutable — replace an entry to edit it. The criteria
        /// serialize as a conditional child and are compute-relevant hashed content when
        /// present; an empty collection leaves the serialized form and hash of every earlier
        /// options configuration unchanged.
        /// </summary>
        public ObservableCollection<TolerableRiskCriterion> TolerableRiskCriteria => _tolerableRiskCriteria;

        /// <summary>
        /// Forwards criteria-collection changes to the property-change surface so the owning
        /// analysis invalidates its results like any other option edit.
        /// </summary>
        /// <param name="sender">The collection.</param>
        /// <param name="e">The change arguments.</param>
        private void TolerableRiskCriteriaChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RaisePropertyChange(nameof(TolerableRiskCriteria));
        }

        /// <summary>
        /// Raised when an option changes. The owning analysis invalidates its results on any
        /// option change.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        #endregion

        #region Methods

        /// <summary>
        /// Enables and materializes the integration defaults, with the warm-up and final
        /// evaluations scaled by the component count (identical to the one-component defaults;
        /// multi-component analyses receive proportionate tail-resolution budgets within the
        /// resolution).
        /// </summary>
        /// <param name="componentCount">The analysis component count; values below one are treated as one.</param>
        public void SetIntegrationDefaults(int componentCount = 1)
        {
            _defaultComponentCount = Math.Max(1, componentCount);
            bool changed = !_useDefaults;
            _useDefaults = true;
            ApplyIntegrationDefaults(_defaultComponentCount);
            if (changed) RaisePropertyChange(nameof(UseDefaults));
        }

        /// <summary>
        /// Supplies the owning analysis's component count and materializes defaults immediately
        /// when default tracking is enabled.
        /// </summary>
        /// <param name="componentCount">The owning analysis's component count.</param>
        internal void SetDefaultComponentCount(int componentCount)
        {
            _defaultComponentCount = Math.Max(1, componentCount);
            if (_useDefaults)
            {
                ApplyIntegrationDefaults(_defaultComponentCount);
            }
        }

        /// <summary>
        /// Writes the effective integration defaults without treating those internal writes as
        /// explicit caller overrides.
        /// </summary>
        /// <param name="componentCount">The normalized component count.</param>
        private void ApplyIntegrationDefaults(int componentCount)
        {
            _applyingIntegrationDefaults = true;
            try
            {
                MaxEvaluations = 1_000_000;
                MaxDepth = 100;
                Tolerance = 1e-8;
                WarmupEvaluations = (int)Math.Min(1000L * componentCount, 50_000L);
                WarmupCycles = 5;
                FinalEvaluations = (int)Math.Min(10_000L * componentCount, 100_000L);
                EnsembleTolerance = 1e-4;
                EnsembleMinDepth = 0;
            }
            finally
            {
                _applyingIntegrationDefaults = false;
            }
        }

        /// <summary>
        /// Validates the options and reports any issues found.
        /// </summary>
        /// <returns>
        /// A tuple containing:
        /// <list type="bullet">
        /// <item>
        /// <description><c>IsValid</c>: <c>true</c> if the options pass all validation checks; otherwise <c>false</c>.</description>
        /// </item>
        /// <item>
        /// <description><c>ValidationMessages</c>: messages describing validation errors ("Error: …", invalidating) and warnings ("Warning: …", advisory).</description>
        /// </item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// Range checks per the v1.0 surface; a realization count below 1000 is an advisory
        /// warning (thin ensembles make noisy percentile curves). Cross-field checks that need
        /// the component count (the correlation matrix shape) run at the analysis level.
        /// </remarks>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (_realizations < 100 || _realizations > 10_000)
                messages.Add("Error: The number of realizations must be between 100 and 10,000.");
            else if (_realizations < 1000)
                messages.Add("Warning: Fewer than 1,000 realizations produce noisy uncertainty percentiles.");
            if (double.IsNaN(_confidenceIntervalWidth) || _confidenceIntervalWidth <= 0d || _confidenceIntervalWidth >= 1d)
                messages.Add("Error: The confidence interval width must be between 0 and 1.");
            if (_prngSeed <= 0)
                messages.Add("Error: The PRNG seed must be positive.");
            if (_lecOutputLength < 50 || _lecOutputLength > 1000)
                messages.Add("Error: The LEC output length must be between 50 and 1,000.");
            if (!Enum.IsDefined(_samplingScheme))
                messages.Add("Error: The sampling scheme is not a recognized member.");
            if (!Enum.IsDefined(_mode))
                messages.Add("Error: The analysis mode is not a recognized member.");
            if (!Enum.IsDefined(_riskIntegrand))
                messages.Add("Error: The risk integrand is not a recognized member.");
            if (!Enum.IsDefined(_systemRiskMethod))
                messages.Add("Error: The system risk method is not a recognized member.");
            if (!Enum.IsDefined(_jointConsequences))
                messages.Add("Error: The joint consequence type is not a recognized member.");
            if (!Enum.IsDefined(_componentHazardDependency))
                messages.Add("Error: The component hazard dependency is not a recognized member.");
            if (double.IsNaN(_alpha) || _alpha < 1e-16 || _alpha > 1d - 1e-16)
                messages.Add("Error: The exceedance level alpha must be between 1e-16 and 1 − 1e-16.");
            if (_maxEvaluations < 10_000 || _maxEvaluations > 1_000_000)
                messages.Add("Error: The maximum integrator evaluations must be between 10,000 and 1,000,000.");
            if (_maxDepth < 10 || _maxDepth > 500)
                messages.Add("Error: The maximum integrator depth must be between 10 and 500.");
            if (double.IsNaN(_tolerance) || _tolerance < 1e-15 || _tolerance > 0.01d)
                messages.Add("Error: The integrator tolerance must be between 1e-15 and 0.01.");
            if (_warmupEvaluations < 100 || _warmupEvaluations > 50_000)
                messages.Add("Error: The VEGAS warm-up evaluations must be between 100 and 50,000.");
            if (_warmupCycles < 1 || _warmupCycles > 100)
                messages.Add("Error: The VEGAS warm-up cycles must be between 1 and 100.");
            if (_finalEvaluations < 1000 || _finalEvaluations > 100_000)
                messages.Add("Error: The VEGAS final evaluations must be between 1,000 and 100,000.");
            if (!Enum.IsDefined(_vegasTailFocusMode))
                messages.Add("Error: The VEGAS tail focus mode is not a recognized member.");
            if (double.IsNaN(_vegasTailFocusParameter) || _vegasTailFocusParameter < 1d || _vegasTailFocusParameter > 20d)
                messages.Add("Error: The VEGAS tail focus parameter must be between 1 and 20.");
            if (_systemConvolutionPoints < 4096 || _systemConvolutionPoints > 1_048_576)
                messages.Add("Error: The system convolution points must be between 4,096 and 1,048,576.");
            if (double.IsNaN(_ensembleTolerance) || _ensembleTolerance < 1e-15 || _ensembleTolerance > 0.01d)
                messages.Add("Error: The ensemble integrator tolerance must be between 1e-15 and 0.01.");
            if (_ensembleMinDepth < 0 || _ensembleMinDepth > 10)
                messages.Add("Error: The ensemble integrator minimum depth must be between 0 and 10.");
            for (int i = 0; i < _tolerableRiskCriteria.Count; i++)
            {
                if (_tolerableRiskCriteria[i] == null)
                {
                    messages.Add($"Error: Tolerable-risk criterion {i + 1} is null.");
                    continue;
                }
                var criterion = _tolerableRiskCriteria[i].Validate();
                for (int m = 0; m < criterion.ValidationMessages.Count; m++)
                {
                    messages.Add($"{criterion.ValidationMessages[m]} [Criterion {i + 1}]");
                }
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <summary>
        /// Computes the options' canonical SHA-256 content hash: every compute-relevant field
        /// through the serialized form, with <see cref="UseDefaults"/> stripped by the audited
        /// canonicalization rules.
        /// </summary>
        /// <returns>The 32-byte SHA-256 hash of the canonicalized <see cref="ToXElement"/> form.</returns>
        public byte[] CanonicalHash()
        {
            return CanonicalContentHasher.Hash(ToXElement(), CanonicalizationRules.ModelRules);
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the options — the persistence contract AND the canonical-hash identity
        /// surface: attribute names and order are append-only. The correlation matrix serializes
        /// (G17 row-major, rows ';'-separated, values ','-separated) only under the
        /// correlation-matrix dependency mode.
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(RiskAnalysisOptions));
            element.SetAttributeValue(nameof(EstimateMeanRiskOnly), _estimateMeanRiskOnly);
            element.SetAttributeValue(nameof(Realizations), _realizations);
            element.SetAttributeValue(nameof(ConfidenceIntervalWidth), SerializationUtilities.FormatDouble(_confidenceIntervalWidth));
            element.SetAttributeValue(nameof(PRNGSeed), _prngSeed);
            element.SetAttributeValue(nameof(LECOutputLength), _lecOutputLength);
            element.SetAttributeValue(nameof(SamplingScheme), _samplingScheme.ToString());
            element.SetAttributeValue(nameof(Mode), _mode.ToString());
            element.SetAttributeValue(nameof(RiskIntegrand), _riskIntegrand.ToString());
            element.SetAttributeValue(nameof(SystemRiskMethod), _systemRiskMethod.ToString());
            element.SetAttributeValue(nameof(JointConsequences), _jointConsequences.ToString());
            element.SetAttributeValue(nameof(ComponentHazardDependency), _componentHazardDependency.ToString());
            element.SetAttributeValue(nameof(HazardCorrelationMatrix),
                _componentHazardDependency == DependencyType.CorrelationMatrix ? SerializationUtilities.FormatMatrix(_hazardCorrelationMatrix) : string.Empty);
            element.SetAttributeValue(nameof(ConsequenceThreshold), SerializationUtilities.FormatDouble(_consequenceThreshold));
            element.SetAttributeValue(nameof(Alpha), SerializationUtilities.FormatDouble(_alpha));
            element.SetAttributeValue(nameof(MaxEvaluations), _maxEvaluations);
            element.SetAttributeValue(nameof(MaxDepth), _maxDepth);
            element.SetAttributeValue(nameof(Tolerance), SerializationUtilities.FormatDouble(_tolerance));
            element.SetAttributeValue(nameof(WarmupEvaluations), _warmupEvaluations);
            element.SetAttributeValue(nameof(WarmupCycles), _warmupCycles);
            element.SetAttributeValue(nameof(FinalEvaluations), _finalEvaluations);
            element.SetAttributeValue(nameof(UseDefaults), _useDefaults);
            element.SetAttributeValue(nameof(VegasTailFocusMode), _vegasTailFocusMode.ToString());
            element.SetAttributeValue(nameof(VegasTailFocusParameter), SerializationUtilities.FormatDouble(_vegasTailFocusParameter));
            element.SetAttributeValue(nameof(SystemConvolutionPoints), _systemConvolutionPoints);
            element.SetAttributeValue(nameof(MaxSystemCombinations), _maxSystemCombinations);
            element.SetAttributeValue(nameof(MaxPathwayCombinations), _maxPathwayCombinations);
            element.SetAttributeValue(nameof(RiskMeasures), _riskMeasures);
            element.SetAttributeValue(nameof(OutputAdjustedFailureModeCurves), _outputAdjustedFailureModeCurves);
            element.SetAttributeValue(nameof(EnsembleTolerance), SerializationUtilities.FormatDouble(_ensembleTolerance));
            element.SetAttributeValue(nameof(EnsembleMinDepth), _ensembleMinDepth);
            // Conditional presence: written only when enabled, so every pre-existing options
            // form — and its canonical hash — is unchanged.
            if (_useSobolJointSampling)
            {
                element.SetAttributeValue(nameof(UseSobolJointSampling), _useSobolJointSampling);
            }

            // Conditional-presence child: written only when criteria are configured, so every
            // criteria-free options form — and its canonical hash — is unchanged.
            if (_tolerableRiskCriteria.Count > 0)
            {
                var criteria = new XElement(nameof(TolerableRiskCriteria));
                for (int i = 0; i < _tolerableRiskCriteria.Count; i++)
                {
                    criteria.Add(_tolerableRiskCriteria[i].ToXElement());
                }
                element.Add(criteria);
            }
            return element;
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Applies an integration-setting assignment and disables default tracking when the
        /// assignment came from a caller rather than internal materialization.
        /// </summary>
        /// <typeparam name="T">The field type.</typeparam>
        /// <param name="field">The backing field.</param>
        /// <param name="value">The assigned value.</param>
        /// <param name="propertyName">The property name to report.</param>
        private void SetIntegrationField<T>(ref T field, T value, string propertyName)
        {
            if (!_applyingIntegrationDefaults && _useDefaults)
            {
                _useDefaults = false;
                RaisePropertyChange(nameof(UseDefaults));
            }
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                RaisePropertyChange(propertyName);
            }
        }

        /// <summary>
        /// Sets a backing field and raises the change notification when the value differs.
        /// </summary>
        /// <typeparam name="T">The field type.</typeparam>
        /// <param name="field">The backing field.</param>
        /// <param name="value">The new value.</param>
        /// <param name="propertyName">The property name to report.</param>
        private void SetField<T>(ref T field, T value, string propertyName)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                RaisePropertyChange(propertyName);
            }
        }

        /// <summary>
        /// Raises the <see cref="PropertyChanged"/> event.
        /// </summary>
        /// <param name="propertyName">The name of the property that changed.</param>
        private void RaisePropertyChange(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
