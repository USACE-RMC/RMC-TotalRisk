using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Numerics;
using Numerics.Data;
using Numerics.Data.Statistics;
using Numerics.Distributions;
using Numerics.Mathematics;
using Numerics.Mathematics.Integration;
using Numerics.Mathematics.LinearAlgebra;
using Numerics.Mathematics.RootFinding;
using Numerics.Mathematics.SpecialFunctions;
using Numerics.Sampling;
using Numerics.Utilities;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results.LifeCycle;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.Results.Support;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;
using RMC.TotalRisk.Core.Support;
using RMC.TotalRisk.Analyses.Risk.Support;
using RMC.TotalRisk.Analyses.LifeCycle;
using RMC.TotalRisk.Analyses.Support;

namespace RMC.TotalRisk.Analyses.Risk
{
    /// <summary>
    /// The Monte Carlo risk analysis: content-seeded knowledge-uncertainty sampling over the
    /// owned system components, adaptive Gauss–Kronrod integration of the risk integrand, exact
    /// loss-exceedance-curve construction with the full risk-measure catalog, and percentile
    /// post-processing of the ensemble.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from the v1.0 engine with deliberate v1.1 corrections
    /// (<c>docs/technical-reference/risk-integration.md</c>,
    /// <c>docs/technical-reference/loss-exceedance-curves.md</c>): the 1D integrator is
    /// <c>AdaptiveGaussKronrod</c> (G10K21) used as an adaptive sampler — the returned integral
    /// is discarded and the recorded risk points are the product, with the refinement objective
    /// selected by <see cref="RiskAnalysisOptions.RiskIntegrand"/>; curves are built exactly from
    /// the recorded (mass, consequence) pairs; and seeding is content-based
    /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.5.4): per-component seeds derive
    /// from the analysis seed, the
    /// component's canonical hash, and its occurrence index — never from canvas order, so
    /// renaming, moving, or reordering components can never change results, and results are
    /// bit-identical at any thread count (every parallel write is index-owned; ensemble
    /// reductions run in a sequential post-pass, and percentile means are summed sequentially
    /// rather than with parallel reductions — a deliberate determinism-over-throughput choice).
    /// </para>
    /// <para>
    /// <b>Ownership and persistence (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §8):</b>
    /// components and results arrive
    /// through the constructor (the BestFit analysis shape) and <see cref="ToXElement"/> writes
    /// the analysis metadata, the estimated flag, and the options only. Results are JSON
    /// containers; the consuming layer persists them separately.
    /// </para>
    /// <para>
    /// <b>Multi-component system risk:</b> the additive method assumes strictly
    /// independent components (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.8 — a
    /// supplied dependence is a validation error) and
    /// builds the true system loss exceedance curves by zero-inflated lattice convolution
    /// (<see cref="SystemConvolution"/>) — the exact enumeration of all component
    /// failure/non-failure combinations, where v1.0 combined two moments and produced no system
    /// curve at all; the reported stream probabilities keep the v1.0 system-state semantics
    /// (failure union, its complement). The joint method integrates the correlated hazard
    /// hypercube with VEGAS, enumerating real within-component pathway/branch entries across the
    /// component combinations instead of collapsing each component to its conditional mean (the
    /// documented v1.0 system-tail defect), accumulating recorded points across five recording
    /// passes with the masses self-normalized so the exhaustive budget is exactly one, and
    /// exposing the VEGAS power-transform tail focus (γ) with an automatic heuristic driven by a
    /// deterministic per-component failure-probability quadrature probe.
    /// </para>
    /// <para>
    /// <b>Reliability mode:</b> <see cref="RiskAnalysisMode.Reliability"/> computes
    /// annualized failure probability only: consequence functions become optional (the relaxed
    /// mode-aware validation chain), the adaptive refinement objective is forced to
    /// <see cref="RiskIntegrand.TotalProbabilityOfFailure"/> (the configured objective applies to
    /// risk mode), and the annualized failure probability is read as the Fail stream's total
    /// probability at every level — failure mode, component, and system (the same containers
    /// serve both modes; a consequence-free model's curves are degenerate at zero consequence by
    /// construction).
    /// </para>
    /// <para>
    /// <b>Declared consequence-type axis:</b> the analysis declares
    /// its ordered consequence types — the primary through
    /// <see cref="SpecifiedConsequence"/>/<see cref="ConsequenceUnit"/> and every additional
    /// position through <see cref="AdditionalConsequenceTypes"/> — and validation strictly
    /// matches each component's failure and non-failure paths against the declaration: counts
    /// and order always, labels and units whenever both sides are non-blank (blank is a
    /// wildcard). The declaration is label metadata: it can never enter a canonical hash, so it
    /// never gates or rewires compute for a valid model — validity may depend on metadata
    /// consistency while results identity depends only on compute-relevant content.
    /// </para>
    /// <para>
    /// <b>Cascading response stages:</b> multi-stage response chains are legal. Each stage
    /// carries a resolved branch polarity, the sampled compute multiplies the stage polarity
    /// products, and end states classify by final polarity into combination units
    /// (docs/technical-reference/cascading-end-states.md).
    /// </para>
    /// </remarks>
    public class RiskAnalysis : AnalysisBase
    {
        #region Construction

        /// <summary>
        /// Initializes a risk analysis over the given components with default options.
        /// </summary>
        /// <param name="components">The system components the analysis owns, in declared order.</param>
        /// <exception cref="ArgumentNullException">Thrown when the component list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the component list contains a null entry.</exception>
        public RiskAnalysis(IList<SystemComponent> components)
        {
            if (components == null) throw new ArgumentNullException(nameof(components));
            _components = new List<SystemComponent>(components.Count);
            foreach (var component in components)
            {
                if (component == null) throw new ArgumentException("The component list contains a null entry.", nameof(components));
                _components.Add(component);
            }
            _options = new RiskAnalysisOptions();
            _authorComponents = _components;
            _authorComponentsView = _authorComponents.AsReadOnly();
            _computationWarningsView = _computationWarnings.AsReadOnly();
            _computationDiagnosticsView = _computationDiagnostics.AsReadOnly();
            _options.SetDefaultComponentCount(_components.Count);
            _options.PropertyChanged += OptionsPropertyChanged;
            _authorOptions = _options;
            _additionalConsequenceTypes.CollectionChanged += AdditionalConsequenceTypesChanged;
        }

        /// <summary>
        /// Initializes a risk analysis from its serialized configuration, with the components
        /// and previously computed results supplied by the consuming layer — the BestFit
        /// constructor shape (configuration from XML; model and results through arguments).
        /// </summary>
        /// <param name="components">The system components the analysis owns, in declared order.</param>
        /// <param name="xElement">The serialized configuration produced by <see cref="ToXElement"/>.</param>
        /// <param name="riskResults">The previously computed summary ensemble, or null.</param>
        /// <param name="meanResults">The previously computed mean realization, or null.</param>
        /// <param name="medianResults">The previously computed median realization, or null.</param>
        /// <param name="lowerResults">The previously computed lower confidence realization, or null.</param>
        /// <param name="upperResults">The previously computed upper confidence realization, or null.</param>
        /// <exception cref="ArgumentNullException">Thrown when the component list or element is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the component list contains a null entry.</exception>
        public RiskAnalysis(IList<SystemComponent> components, XElement xElement,
            EnsembleResults? riskResults = null, SystemRealization? meanResults = null,
            SystemRealization? medianResults = null, SystemRealization? lowerResults = null,
            SystemRealization? upperResults = null)
            : this(components)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            Name = SerializationUtilities.ReadString(xElement, nameof(Name), "Risk Analysis");
            Description = SerializationUtilities.ReadString(xElement, nameof(Description));
            SpecifiedConsequence = SerializationUtilities.ReadString(xElement, nameof(SpecifiedConsequence));
            ConsequenceUnit = SerializationUtilities.ReadString(xElement, nameof(ConsequenceUnit));

            var typesElement = xElement.Element(nameof(AdditionalConsequenceTypes));
            if (typesElement != null)
            {
                foreach (var child in typesElement.Elements(nameof(ConsequenceTypeDescriptor)))
                {
                    _additionalConsequenceTypes.Add(new ConsequenceTypeDescriptor(child));
                }
            }

            var optionsElement = xElement.Element(nameof(RiskAnalysisOptions));
            if (optionsElement != null)
            {
                Options = new RiskAnalysisOptions(optionsElement);
            }

            _riskResults = riskResults;
            _meanRiskResults = meanResults;
            _medianRiskResults = medianResults;
            _lowerRiskResults = lowerResults;
            _upperRiskResults = upperResults;

            // An estimated analysis must actually carry realizations: a results container whose
            // payload failed a load integrity check arrives cleared (empty, with load
            // diagnostics), and the analysis then reports unestimated — a rerun is required.
            _isEstimated = SerializationUtilities.ReadBoolean(xElement, nameof(IsEstimated))
                && riskResults != null && riskResults.Count > 0;
        }

        #endregion

        #region Members

        /// <summary>
        /// The engine's probability floor: the integration domain is
        /// [1e-16, 1 − 1e-16] in hazard non-exceedance probability (v1.0 constant).
        /// </summary>
        private const double ProbabilityFloor = 1e-16;

        /// <summary>
        /// The number of stratified hazard bins seeding the adaptive integrator (v1.0 constant).
        /// </summary>
        private const int HazardBinCount = 50;

        /// <summary>
        /// The number of independent VEGAS recording passes the joint path accumulates loss
        /// exceedance points across (v1.0 recorded a single pass — far too sparse for a tail
        /// ordinate in D dimensions). The recorded masses are self-normalized by
        /// the realized weight sum, so a pass count truncated by the evaluation cap stays
        /// consistent.
        /// </summary>
        private const int VegasRecordingPasses = 5;

        /// <summary>
        /// The joint path's advisory guardrail on the product of the components' worst-case
        /// recorded entry widths (the system combination cross product).
        /// </summary>
        private const long JointEntryWarningLimit = 4096;

        /// <summary>
        /// The joint path's invalidating guardrail on the product of the components' worst-case
        /// recorded entry widths.
        /// </summary>
        private const long JointEntryErrorLimit = 65_536;

        /// <summary>
        /// The logic-tree branch-combination count above which enumeration warns (the ensemble
        /// grows one block of realizations per combination).
        /// </summary>
        private const long LogicTreeCombinationWarningLimit = 4_096;

        /// <summary>
        /// The logic-tree branch-combination count above which enumeration refuses.
        /// </summary>
        private const long LogicTreeCombinationErrorLimit = 65_536;

        /// <summary>
        /// The enumerated realization count (K·M) above which enumeration warns — the ensemble
        /// holds every realization's summaries in memory, mirroring the option ceiling of a
        /// sampled run.
        /// </summary>
        private const long LogicTreeRealizationWarningLimit = 10_000;

        /// <summary>
        /// The enumerated realization count (K·M) above which enumeration refuses.
        /// </summary>
        private const long LogicTreeRealizationErrorLimit = 1_000_000;

        /// <summary>
        /// The salt distinguishing the joint path's VEGAS driving stream from the component
        /// sampler streams in the content-based seed derivation ("VEGAS" in ASCII).
        /// </summary>
        private static readonly byte[] JointStreamSalt = { 0x56, 0x45, 0x47, 0x41, 0x53 };

        /// <summary>
        /// The owned components, in declared order.
        /// </summary>
        private List<SystemComponent> _components;

        /// <summary>
        /// The mutable authoring components exposed to callers; never replaced by a run snapshot.
        /// </summary>
        private readonly List<SystemComponent> _authorComponents;

        /// <summary>The immutable public view over the owned authoring components.</summary>
        private readonly ReadOnlyCollection<SystemComponent> _authorComponentsView;

        /// <summary>
        /// The lower Cholesky factor of the joint hazard covariance, extracted once per run so
        /// the VEGAS integrand applies the latent transform in place instead of allocating
        /// through <see cref="MultivariateNormal.InverseCDF(double[])"/> on every evaluation
        /// (the in-place loop replicates the Numerics matrix–vector accumulation
        /// order exactly, so the recorded stream is bit-identical). Run-scoped runtime state.
        /// </summary>
        private double[,]? _jointCholeskyLower;

        /// <summary>
        /// The run's content-derived base seed for the VEGAS driving stream: the analysis seed
        /// folded with every component's canonical hash and occurrence index in declared order.
        /// Run-scoped runtime state.
        /// </summary>
        private int _jointSeedBase;

        /// <summary>
        /// The automatic tail-focus target probability harvested by the run's deterministic
        /// failure-probability quadrature probe (the smallest component annualized failure
        /// probability times the exceedance level, clamped to [1e-12, 1e-2]). Run-scoped runtime
        /// state.
        /// </summary>
        private double _jointTailTargetProbability = 1e-2;

        /// <summary>
        /// The additive convolution order: component indices sorted by canonical hash, so the
        /// sequential pairwise convolution associates identically however the components are
        /// declared — reordering components can never move the system curves by
        /// association-rounding. Run-scoped runtime state.
        /// </summary>
        private int[]? _additiveConvolutionOrder;

        /// <summary>
        /// The declared per-type consequence thresholds for the additional consequence types,
        /// captured once per run from the descriptors — entry k applies to
        /// additional type k at every risk-measure site. Run-scoped runtime state.
        /// </summary>
        private double[]? _runAdditionalThresholds;

        /// <summary>
        /// The effective sampler seed map the last run resolved
        /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.5.8) — the
        /// baseline a perturbation study pins onto its perturbed runs via
        /// <see cref="PinnedSamplerSeeds"/>. Populated by every <see cref="RunAsync"/>; null
        /// before the first run. Runtime-only — never serialized, never hashed.
        /// </summary>
        public SamplerSeedMap? CapturedSamplerSeeds { get; private set; }

        /// <summary>
        /// An optional pinned sampler seed map — the seed-stable
        /// perturbation mode (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.5.8): when
        /// set, the next run replaces every content-derived sampler
        /// seed — per walk ordinal, including the coupling positions and the joint VEGAS seed
        /// base — with the captured value, so a small numeric perturbation cannot re-roll the
        /// Monte Carlo streams and result deltas are pure parameter effects. The map must fit
        /// the model's walk shape (validated loudly). Runtime-only — never serialized, never
        /// hashed, never part of any identity surface; clear it to restore content-based
        /// seeding.
        /// </summary>
        /// <remarks>
        /// Workflow: run the baseline → read <see cref="CapturedSamplerSeeds"/> → perturb the
        /// parameter under study → assign the captured map here → run → difference the
        /// results. Documented residual: if the perturbation flips the canonical-hash order of
        /// components, the additive convolution re-associates at the last bit.
        /// </remarks>
        public SamplerSeedMap? PinnedSamplerSeeds { get; set; }

        /// <summary>Backing field for <see cref="Options"/>.</summary>
        private RiskAnalysisOptions _options;

        /// <summary>
        /// The mutable authoring options exposed to callers; the active engine field points to a
        /// deep snapshot while a run is in progress.
        /// </summary>
        private RiskAnalysisOptions _authorOptions;
        /// <summary>The immutable consequence declarations captured for the active run.</summary>
        private IReadOnlyList<ConsequenceTypeDescriptor>? _runAdditionalConsequenceTypes;

        /// <summary>The primary consequence label captured for the active run.</summary>
        private string? _runSpecifiedConsequence;

        /// <summary>The primary consequence unit captured for the active run.</summary>
        private string? _runConsequenceUnit;

        /// <summary>
        /// Gets the active run's declarations, or the authoring collection while idle.
        /// </summary>
        private IReadOnlyList<ConsequenceTypeDescriptor> RunAdditionalConsequenceTypes =>
            _runAdditionalConsequenceTypes ?? _additionalConsequenceTypes;

        /// <summary>Gets the active run's primary consequence label.</summary>
        private string RunSpecifiedConsequence => _runSpecifiedConsequence ?? _specifiedConsequence;

        /// <summary>Gets the active run's primary consequence unit.</summary>
        private string RunConsequenceUnit => _runConsequenceUnit ?? _consequenceUnit;


        /// <summary>
        /// Internal run-worker observer used by lifecycle fault and cancellation tests. Null in
        /// production; it is runtime-only and never serialized, hashed, or copied into a model.
        /// </summary>
        internal Action? RunWorkerObserver { get; set; }

        /// <summary>
        /// Optional internal cap on ensemble scheduling concurrency. Null preserves the production
        /// <see cref="ParallelOptions"/> default; friend tests set a positive value to prove that
        /// index-owned realization writes and sequential reductions are thread-count invariant.
        /// Runtime-only: never serialized, hashed, or copied into a model/run snapshot.
        /// </summary>
        internal int? MaximumDegreeOfParallelismOverride { get; set; }

        /// <summary>
        /// Optional internal ensemble-iteration observer. Friend tests record executing thread
        /// IDs to prove a capped run actually used multiple workers. Null in production and
        /// runtime-only: never serialized, hashed, or copied into a model/run snapshot.
        /// </summary>
        internal Action? EnsembleWorkerObserver { get; set; }


        /// <summary>Backing field for <see cref="Name"/>.</summary>
        private string _name = "Risk Analysis";

        /// <summary>Backing field for <see cref="Description"/>.</summary>
        private string _description = string.Empty;

        /// <summary>
        /// A complete successful run staged off the public result surface until publication.
        /// </summary>
        private sealed class AnalysisRunPublication
        {
            /// <summary>Gets or sets the persisted ensemble results.</summary>
            internal EnsembleResults Results { get; set; } = null!;

            /// <summary>Gets or sets the mean realization.</summary>
            internal SystemRealization? Mean { get; set; }

            /// <summary>Gets or sets the median realization.</summary>
            internal SystemRealization? Median { get; set; }

            /// <summary>Gets or sets the lower confidence realization.</summary>
            internal SystemRealization? Lower { get; set; }

            /// <summary>Gets or sets the upper confidence realization.</summary>
            internal SystemRealization? Upper { get; set; }

            /// <summary>Gets or sets the captured sampler seed map.</summary>
            internal SamplerSeedMap CapturedSeeds { get; set; } = null!;

            /// <summary>Gets or sets the retained per-realization ensemble, when retention is enabled.</summary>
            internal SystemRealization[]? RetainedRealizations { get; set; }

            /// <summary>Gets or sets the retained integration-detail realization, when one is selected.</summary>
            internal SystemRealization? RetainedIntegrationDetail { get; set; }

            /// <summary>Gets or sets the logic-tree enumeration design, or null for a sampled run.</summary>
            internal LogicTreeEnumerationMap? LogicTree { get; set; }

            /// <summary>Gets the structured computation diagnostics staged with the result.</summary>
            internal List<ComputationDiagnostic> Diagnostics { get; } = new List<ComputationDiagnostic>();
        }

        /// <summary>Backing field for <see cref="SpecifiedConsequence"/>.</summary>
        private string _specifiedConsequence = string.Empty;

        /// <summary>Backing field for <see cref="ConsequenceUnit"/>.</summary>
        private string _consequenceUnit = string.Empty;

        /// <summary>Backing field for <see cref="AdditionalConsequenceTypes"/>.</summary>
        private readonly ObservableCollection<ConsequenceTypeDescriptor> _additionalConsequenceTypes =
            new ObservableCollection<ConsequenceTypeDescriptor>();

        /// <summary>Backing field for <see cref="RiskResults"/>.</summary>
        private EnsembleResults? _riskResults;

        /// <summary>Backing field for <see cref="MeanRiskResults"/>.</summary>
        private SystemRealization? _meanRiskResults;

        /// <summary>Backing field for <see cref="MedianRiskResults"/>.</summary>
        private SystemRealization? _medianRiskResults;

        /// <summary>Backing field for <see cref="LowerRiskResults"/>.</summary>
        private SystemRealization? _lowerRiskResults;

        /// <summary>Backing field for <see cref="UpperRiskResults"/>.</summary>
        private SystemRealization? _upperRiskResults;

        /// <summary>Backing field for <see cref="RealizationWeights"/>.</summary>
        private double[]? _realizationWeights;

        /// <summary>The immutable public view over <see cref="_realizationWeights"/>.</summary>
        private ReadOnlyCollection<double>? _realizationWeightsView;

        /// <summary>Backing field for <see cref="FractilePins"/> (a defensive copy).</summary>
        private FractilePin[]? _fractilePins;

        /// <summary>The immutable public view over <see cref="_fractilePins"/>.</summary>
        private ReadOnlyCollection<FractilePin>? _fractilePinsView;

        /// <summary>Backing field for <see cref="RetainRealizations"/>.</summary>
        private bool _retainRealizations;

        /// <summary>The per-realization recording-pass chi-squared captures for the joint certificate; null outside a joint ensemble run.</summary>
        private double[]? _jointCertificateChiSquared;

        /// <summary>The per-realization recording relative-standard-error captures for the joint certificate; null outside a joint ensemble run.</summary>
        private double[]? _jointCertificateRelativeSe;

        /// <summary>Whether the joint mean pass recorded its certificate capture this run.</summary>
        private bool _jointCertificateMeanPassCaptured;

        /// <summary>The mean pass's recording-pass chi-squared capture.</summary>
        private double _jointCertificateMeanPassChiSquared = double.NaN;

        /// <summary>The mean pass's recording relative-standard-error capture.</summary>
        private double _jointCertificateMeanPassRelativeSe = double.NaN;

        /// <summary>Backing field for <see cref="JointCertificate"/>.</summary>
        private JointConvergenceCertificate? _jointCertificate;

        /// <summary>Backing field for <see cref="RetainedIntegrationDetailIndex"/>.</summary>
        private int? _retainedIntegrationDetailIndex;

        /// <summary>Backing field for <see cref="LogicTreeEnumerationRealizations"/>.</summary>
        private int? _logicTreeEnumerationRealizations;

        /// <summary>Backing field for <see cref="LogicTreeEnumeration"/>.</summary>
        private LogicTreeEnumerationMap? _logicTreeEnumeration;

        /// <summary>Backing field for <see cref="RetainedRealizations"/>.</summary>
        private SystemRealization[]? _retainedRealizations;

        /// <summary>Backing field for <see cref="RetainedIntegrationDetail"/>.</summary>
        private SystemRealization? _retainedIntegrationDetail;

        /// <summary>The run-start snapshot of <see cref="RetainRealizations"/>.</summary>
        private bool _runRetainRealizations;

        /// <summary>
        /// The run-start snapshot of <see cref="RetainedIntegrationDetailIndex"/> the
        /// realization loop compares against — a mid-run reassignment never reaches the
        /// running computation.
        /// </summary>
        private int? _runRetainedDetailIndex;

        /// <summary>Backing field for <see cref="ComputationWarnings"/>.</summary>
        private readonly List<string> _computationWarnings = new List<string>();

        /// <summary>Backing field for <see cref="ComputationDiagnostics"/>.</summary>
        private readonly List<ComputationDiagnostic> _computationDiagnostics = new List<ComputationDiagnostic>();

        /// <summary>The immutable public view over the last run's structured diagnostics.</summary>
        private readonly ReadOnlyCollection<ComputationDiagnostic> _computationDiagnosticsView;

        /// <summary>The immutable public view over the last run's warnings.</summary>
        private readonly ReadOnlyCollection<string> _computationWarningsView;

        /// <summary>
        /// The analysis display name. Metadata — serialized, stripped from the canonical hash.
        /// </summary>
        public string Name
        {
            get { return _name; }
            set
            {
                if (_name != value)
                {
                    _name = value;
                    RaisePropertyChange(nameof(Name));
                }
            }
        }

        /// <summary>
        /// The analysis description. Metadata — serialized, stripped from the canonical hash.
        /// </summary>
        public string Description
        {
            get { return _description; }
            set
            {
                if (_description != value)
                {
                    _description = value;
                    RaisePropertyChange(nameof(Description));
                }
            }
        }

        /// <summary>
        /// The consequence type label of the analysis results (e.g., "Life Loss"). Metadata.
        /// </summary>
        public string SpecifiedConsequence
        {
            get { return _specifiedConsequence; }
            set
            {
                if (_specifiedConsequence != value)
                {
                    _specifiedConsequence = value;
                    RaisePropertyChange(nameof(SpecifiedConsequence));
                }
            }
        }

        /// <summary>
        /// The consequence unit label of the analysis results (e.g., "lives"). Metadata.
        /// </summary>
        public string ConsequenceUnit
        {
            get { return _consequenceUnit; }
            set
            {
                if (_consequenceUnit != value)
                {
                    _consequenceUnit = value;
                    RaisePropertyChange(nameof(ConsequenceUnit));
                }
            }
        }

        /// <summary>
        /// The declared consequence types beyond the primary, in order: entry k − 1 declares
        /// consequence-type position k of the analysis's axis (position 0 is the
        /// <see cref="SpecifiedConsequence"/>/<see cref="ConsequenceUnit"/> pair). Empty declares
        /// the legacy single-type axis. Validation strictly matches every component's failure and
        /// non-failure paths against the declared axis in risk mode; reliability mode carries no
        /// consequences and ignores it. Metadata — serialized, never hashed.
        /// </summary>
        public ObservableCollection<ConsequenceTypeDescriptor> AdditionalConsequenceTypes => _additionalConsequenceTypes;

        /// <summary>
        /// The run options. Assigning replaces the subscription; any option change invalidates
        /// the current results. Assigning null coerces to fresh defaults.
        /// </summary>
        public RiskAnalysisOptions Options
        {
            get { return _authorOptions; }
            set
            {
                if (ReferenceEquals(_authorOptions, value)) return;
                if (_authorOptions != null) _authorOptions.PropertyChanged -= OptionsPropertyChanged;
                _authorOptions = value ?? new RiskAnalysisOptions();
                _authorOptions.SetDefaultComponentCount(_authorComponents.Count);
                _authorOptions.PropertyChanged += OptionsPropertyChanged;
                if (!IsRunning) _options = _authorOptions;
                IsEstimated = false;
                RaisePropertyChange(nameof(Options));
            }
        }

        /// <summary>
        /// The owned system components, in declared order.
        /// </summary>
        public IReadOnlyList<SystemComponent> Components => _authorComponentsView;

        /// <summary>
        /// The per-realization summary ensemble from the last run (one entry per realization; a
        /// mean-only run publishes a single-entry ensemble). Null until estimated.
        /// </summary>
        public EnsembleResults? RiskResults
        {
            get { return _riskResults; }
            private set
            {
                _riskResults = value;
                RaisePropertyChange(nameof(RiskResults));
            }
        }

        /// <summary>
        /// The mean results: the mean-only realization, or the ensemble mean curves from a full
        /// run. Null until estimated.
        /// </summary>
        public SystemRealization? MeanRiskResults
        {
            get { return _meanRiskResults; }
            private set
            {
                _meanRiskResults = value;
                RaisePropertyChange(nameof(MeanRiskResults));
            }
        }

        /// <summary>
        /// The ensemble median curves from a full run. Null until estimated or for mean-only runs.
        /// </summary>
        public SystemRealization? MedianRiskResults
        {
            get { return _medianRiskResults; }
            private set
            {
                _medianRiskResults = value;
                RaisePropertyChange(nameof(MedianRiskResults));
            }
        }

        /// <summary>
        /// The lower confidence-bound curves from a full run at the configured interval width.
        /// Null until estimated or for mean-only runs.
        /// </summary>
        public SystemRealization? LowerRiskResults
        {
            get { return _lowerRiskResults; }
            private set
            {
                _lowerRiskResults = value;
                RaisePropertyChange(nameof(LowerRiskResults));
            }
        }

        /// <summary>
        /// The upper confidence-bound curves from a full run at the configured interval width.
        /// Null until estimated or for mean-only runs.
        /// </summary>
        public SystemRealization? UpperRiskResults
        {
            get { return _upperRiskResults; }
            private set
            {
                _upperRiskResults = value;
                RaisePropertyChange(nameof(UpperRiskResults));
            }
        }

        /// <summary>
        /// The optional epistemic realization weights for the next full-uncertainty run — one
        /// weight per realization index. Runtime-only input state: never serialized here, never
        /// part of a canonical hash, and never an influence on sampling seeds — a weighted run
        /// draws exactly the realizations the unweighted run draws, and the weights enter only
        /// the ensemble reductions (the percentile band assembly and the summary catalog). The
        /// run stamps the vector into <see cref="EnsembleResults.RealizationWeights"/> — the
        /// stored authoritative copy — and records its fingerprint in the run manifest. Weights
        /// are raw reliability (importance) weights; only relative values carry meaning.
        /// Assigning invalidates <see cref="AnalysisBase.IsEstimated"/> because published
        /// results depend on the weights; the vector is validated by <see cref="Validate"/> and
        /// enforced at run start (a mean-only run refuses weights). Null runs unweighted.
        /// </summary>
        public IReadOnlyList<double>? RealizationWeights
        {
            get { return _realizationWeightsView; }
            set
            {
                if (value == null)
                {
                    _realizationWeights = null;
                    _realizationWeightsView = null;
                }
                else
                {
                    var copy = new double[value.Count];
                    for (int i = 0; i < value.Count; i++)
                    {
                        copy[i] = value[i];
                    }
                    _realizationWeights = copy;
                    _realizationWeightsView = new ReadOnlyCollection<double>(copy);
                }
                IsEstimated = false;
                RaisePropertyChange(nameof(RealizationWeights));
            }
        }

        /// <summary>
        /// The optional epistemic conditioning pins for the next full-uncertainty run — each
        /// holds one named function (<see cref="FractilePin.FunctionId"/>) at a fixed knowledge
        /// percentile while the rest of the ensemble varies, producing conditional risk
        /// statements ("risk given the 95th-percentile hazard curve"). Runtime-only input state:
        /// never serialized, never part of a canonical hash, and never an influence on sampling
        /// seeds — pins are applied by overwriting the target function's own pre-allocated
        /// percentile matrix after seeding, so every other function's draws are bit-identical to
        /// the unpinned run and the captured seed map is untouched. Pins reach the functions the
        /// component walk samples directly (the hazard, stage transforms and responses, trailing
        /// and secondary-chain transforms, and profile transforms); <see cref="Validate"/> flags
        /// unmatched ids as errors and warns for pins with no effect (a deterministic function, a
        /// function with no percentile surface such as a posterior-indexed parametric, or a
        /// consequence function — consequence draws come from each mode's coupling matrix), while
        /// a matched pin the walk cannot reach (a function sampled only inside a tree's own setup
        /// clones) stops the run loudly rather than silently not conditioning. The mean pass
        /// samples the mean functions and is unaffected. Assigning invalidates
        /// <see cref="AnalysisBase.IsEstimated"/>; null runs unconditioned.
        /// </summary>
        public IReadOnlyList<FractilePin>? FractilePins
        {
            get { return _fractilePinsView; }
            set
            {
                if (value == null)
                {
                    _fractilePins = null;
                    _fractilePinsView = null;
                }
                else
                {
                    var copy = new FractilePin[value.Count];
                    for (int i = 0; i < value.Count; i++)
                    {
                        copy[i] = value[i];
                    }
                    _fractilePins = copy;
                    _fractilePinsView = new ReadOnlyCollection<FractilePin>(copy);
                }
                IsEstimated = false;
                RaisePropertyChange(nameof(FractilePins));
            }
        }

        /// <summary>
        /// Whether the next full-uncertainty run keeps its complete per-realization ensemble on
        /// <see cref="RetainedRealizations"/> instead of discarding it after the percentile
        /// assembly. Runtime-only diagnostic state: never serialized, never hashed, never an
        /// influence on any computed value — the run's published results are byte-identical
        /// either way. Off by default because retention holds every realization's full curve
        /// sets in memory (<see cref="Validate"/> warns while it is on); a mean-only run has no
        /// ensemble and ignores it. Enables <see cref="ReassemblePercentileBands"/>.
        /// </summary>
        public bool RetainRealizations
        {
            get { return _retainRealizations; }
            set
            {
                _retainRealizations = value;
                RaisePropertyChange(nameof(RetainRealizations));
            }
        }

        /// <summary>
        /// The realization whose recorded integration detail the next run keeps: −1 for the
        /// mean-only pass, a realization index for the full-uncertainty ensemble, null for
        /// none (the default — every realization releases its recorded risk points after
        /// post-processing). Runtime-only diagnostic state with no influence on any computed or
        /// serialized value: risk points never serialize, so the selection cannot move a stored
        /// byte. The matching realization skips its memory dump and is exposed on
        /// <see cref="RetainedIntegrationDetail"/> with every recorded
        /// (hazard, probability, mass, consequence) point intact.
        /// </summary>
        public int? RetainedIntegrationDetailIndex
        {
            get { return _retainedIntegrationDetailIndex; }
            set
            {
                _retainedIntegrationDetailIndex = value;
                RaisePropertyChange(nameof(RetainedIntegrationDetailIndex));
            }
        }

        /// <summary>
        /// The per-combination realization count M of the exact logic-tree enumeration, or null
        /// (the default) to sample the epistemic branches like any other knowledge quantity.
        /// When set, the next full-uncertainty run enumerates every branch combination of the
        /// model's epistemic-mixture composites — shared variables force every binder together;
        /// an unbound composite is its own axis — computing one ensemble of K·M realizations in
        /// which each combination holds one block of M continuous-knowledge realizations and the
        /// published <see cref="EnsembleResults.RealizationWeights"/> carry the exact
        /// branch-weight products, so the weighted fractiles have zero Monte Carlo noise on the
        /// branch axis (and are exact outright when every branch chain is deterministic — set
        /// M = 1 there). Runtime-only input state: never serialized, never part of a canonical
        /// hash, and never an influence on sampling seeds — the branch forcing overwrites only
        /// the composites' selector columns after seeding, exactly like a fractile pin, and every
        /// continuous knowledge stream is the stream a sampled run of the same size draws. The
        /// run sizes itself at K·M on its isolated options snapshot;
        /// <see cref="RiskAnalysisOptions.Realizations"/> is not consulted (resource estimates
        /// still read it, so mind the enumeration warnings for large K·M). Enumeration owns the
        /// weight vector (<see cref="RealizationWeights"/> must be null; re-weight a retained
        /// enumerated run post hoc instead), refuses a mean-only run, refuses fractile pins on
        /// the enumerated composites, refuses an epistemic composite referenced through a tree
        /// probability source (the tree samples it in isolated setup clones the forcing scope's
        /// discovery cannot see), and requires every epistemic-mixture consequence composite
        /// to be removed or made aleatory — its branch rides the failure mode's coupling draw,
        /// which the enumerator cannot force. Assigning invalidates
        /// <see cref="AnalysisBase.IsEstimated"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when a value below one is assigned.</exception>
        public int? LogicTreeEnumerationRealizations
        {
            get { return _logicTreeEnumerationRealizations; }
            set
            {
                if (value is int perCombination && perCombination < 1)
                    throw new ArgumentOutOfRangeException(nameof(value), "The per-combination realization count must be at least one.");
                _logicTreeEnumerationRealizations = value;
                IsEstimated = false;
                RaisePropertyChange(nameof(LogicTreeEnumerationRealizations));
            }
        }

        /// <summary>
        /// The last run's logic-tree enumeration design, when
        /// <see cref="LogicTreeEnumerationRealizations"/> was set: the axes crossed, every
        /// combination's exact weight, and the realization-to-combination assignment — the
        /// post-run branch-attribution surface (the run computes on component clones, so reading
        /// branch attribution from the authoring composites after a run is invalid). Null for a
        /// sampled run, and null on an analysis restored from stored results — the map is
        /// runtime-only and never serialized.
        /// </summary>
        public LogicTreeEnumerationMap? LogicTreeEnumeration => _logicTreeEnumeration;

        /// <summary>
        /// The retained per-realization ensemble of the last run, when
        /// <see cref="RetainRealizations"/> was enabled; null otherwise. The realizations are
        /// the run's own objects — read them, never mutate them. Valid for the model state
        /// that produced them (the same contract every diagnostic over stored results carries).
        /// </summary>
        public IReadOnlyList<SystemRealization>? RetainedRealizations => _retainedRealizations;

        /// <summary>
        /// The realization retained with its full recorded integration detail, when
        /// <see cref="RetainedIntegrationDetailIndex"/> selected one; null otherwise. Its
        /// curves keep their recorded risk points — the (hazard level, non-exceedance, mass,
        /// per-entry response probabilities, per-entry consequences) ledger the integral was
        /// built from.
        /// </summary>
        public SystemRealization? RetainedIntegrationDetail => _retainedIntegrationDetail;

        /// <summary>
        /// The joint integration's convergence certificate for the last run — the
        /// recording-pass chi-squared consistency and relative standard error of the mean pass
        /// and the ensemble — or null when the last run did not use the joint method. Runtime
        /// only: never serialized, no influence on any computed or stored value; the persisted
        /// per-realization chi-squared keeps its warm-up semantics (see
        /// <see cref="JointConvergenceCertificate"/>).
        /// </summary>
        public JointConvergenceCertificate? JointCertificate => _jointCertificate;

        /// <summary>
        /// The computational warnings raised by the last run (negative consequences clamped,
        /// mutually-exclusive probabilities normalized, exhaustive mass-balance drift) — the
        /// headless replacement for the v1.0 messenger surface.
        /// </summary>
        public IReadOnlyList<string> ComputationWarnings => _computationWarningsView;

        /// <summary>
        /// The machine-readable computation diagnostics from the last successful run.
        /// </summary>
        public IReadOnlyList<ComputationDiagnostic> ComputationDiagnostics => _computationDiagnosticsView;

        #endregion

        #region IAnalysis Methods

        /// <inheritdoc/>
        public override IReadOnlyList<ValidationIssue> ValidateIssues()
        {
            var raw = ValidateMessages();
            var issues = new List<ValidationIssue>(raw.ValidationMessages.Count);
            for (int i = 0; i < raw.ValidationMessages.Count; i++)
            {
                issues.Add(ValidationIssue.FromLegacyMessage(raw.ValidationMessages[i]));
            }
            return issues.AsReadOnly();
        }

        /// <inheritdoc/>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var issues = ValidateIssues();
            var messages = new List<string>(issues.Count);
            bool isValid = true;
            for (int i = 0; i < issues.Count; i++)
            {
                messages.Add(issues[i].ToLegacyMessage());
                if (issues[i].Severity == DiagnosticSeverity.Error) isValid = false;
            }
            return (isValid, messages);
        }

        /// <summary>
        /// Executes the established validation rules and gathers their compatibility messages.
        /// Structured conversion is centralized in <see cref="ValidateIssues"/>.
        /// </summary>
        /// <returns>The validity flag and established validation messages.</returns>
        /// <remarks>
        /// Errors: no components; the additive method with a component-hazard dependence (the
        /// strict-independence definition of the additive method — dependence belongs to the
        /// joint method); the joint method above twenty components (the VEGAS dimension limit), with a
        /// missing, mis-shaped, or non-positive-definite correlation matrix under the
        /// correlation-matrix dependency, or with a combination cross product beyond the
        /// guardrail; any failure or non-failure path that does not carry the declared
        /// consequence-type axis (count and order always; labels and units when both sides are
        /// non-blank — risk mode only); invalid options; realization weights on a mean-only run,
        /// or a weight vector that fails the shared weight rule (length equal to the realization
        /// count; finite, non-negative, not all zero); and every component's own errors,
        /// aggregated with the component name. Component validation runs mode-aware: reliability
        /// relaxes exactly the consequence-content requirements. Advisory: components
        /// whose driving hazards disagree on non-blank axis labels warn — one analysis models one
        /// hazard axis.
        /// </remarks>
        /// <summary>
        /// Classifies the configured fractile pins against the current component set: duplicate
        /// or unmatched ids are errors; matched pins that cannot condition anything — a
        /// consequence function (its draws come from each mode's coupling matrix), a function
        /// with no percentile sampling surface, or a deterministic function — are warnings; the
        /// remainder are expected to be applied by the sampler walk, which the run reconciles
        /// loudly. A deterministic function's pin still enters the walk map (the overwrite is a
        /// harmless no-op on unused draws) but is never expected-applied.
        /// </summary>
        /// <param name="pins">The configured pins.</param>
        /// <param name="errors">The sink for error messages.</param>
        /// <param name="warnings">The sink for warning messages.</param>
        /// <param name="pinMap">The sink for the id → percentile map handed to the walk, or null when only messages are wanted.</param>
        /// <param name="expectedApplied">The sink for ids the walk must apply, or null.</param>
        private void ClassifyFractilePins(FractilePin[] pins, List<string> errors, List<string> warnings,
            Dictionary<Guid, double>? pinMap, HashSet<Guid>? expectedApplied)
        {
            var referenced = new Dictionary<Guid, IRiskFunction>();
            var consequenceIds = new HashSet<Guid>();
            for (int c = 0; c < _components.Count; c++)
            {
                foreach (var function in _components[c].GetReferencedFunctions())
                {
                    if (function != null && !referenced.ContainsKey(function.Id))
                    {
                        referenced.Add(function.Id, function);
                    }
                }
                var modes = _components[c].FailureModes;
                for (int m = 0; m < modes.Count; m++)
                {
                    var consequences = modes[m].ConsequenceFunctions;
                    for (int k = 0; k < consequences.Count; k++)
                    {
                        if (consequences[k] != null) consequenceIds.Add(consequences[k].Id);
                    }
                }
            }

            var seen = new HashSet<Guid>();
            for (int i = 0; i < pins.Length; i++)
            {
                var pin = pins[i];
                if (!seen.Add(pin.FunctionId))
                {
                    errors.Add($"Error: Fractile pin {i + 1} repeats function id {pin.FunctionId:D}; configure one pin per function.");
                    continue;
                }
                if (!referenced.TryGetValue(pin.FunctionId, out var function))
                {
                    errors.Add($"Error: Fractile pin {i + 1} targets function id {pin.FunctionId:D}, which no component references.");
                    continue;
                }
                if (consequenceIds.Contains(pin.FunctionId))
                {
                    warnings.Add($"Warning: Fractile pin {i + 1} targets consequence function '{function.Name}'; consequence draws come from each mode's coupling matrix, so the pin has no effect.");
                    continue;
                }
                if (function.SamplingDimensions <= 0)
                {
                    warnings.Add($"Warning: Fractile pin {i + 1} targets function '{function.Name}', which has no percentile sampling surface, so the pin has no effect.");
                    continue;
                }
                if (function.IsDeterministic)
                {
                    warnings.Add($"Warning: Fractile pin {i + 1} targets deterministic function '{function.Name}'; its draws are unused, so the pin has no effect.");
                    pinMap?.Add(pin.FunctionId, pin.Percentile);
                    continue;
                }
                string? boundVariable = function switch
                {
                    CompositeHazard h when h.EpistemicVariable.Length > 0 => h.EpistemicVariable,
                    CompositeResponse r when r.EpistemicVariable.Length > 0 => r.EpistemicVariable,
                    CompositeTransform t when t.EpistemicVariable.Length > 0 => t.EpistemicVariable,
                    _ => null,
                };
                if (boundVariable != null)
                {
                    warnings.Add($"Warning: Fractile pin {i + 1} targets function '{function.Name}', which is bound to the shared epistemic variable '{boundVariable}'; the pin overrides the shared draw for this function alone, decoupling it from the other binders.");
                }
                pinMap?.Add(pin.FunctionId, pin.Percentile);
                expectedApplied?.Add(pin.FunctionId);
            }
        }

        /// <summary>
        /// Builds the id → percentile map of the currently configured fractile pins for the
        /// internal diagnostic re-seeds, so post-hoc sensitivity and conditioning queries see
        /// the same conditioned design the run used; null when no pins are configured.
        /// </summary>
        /// <returns>The pin map, or null.</returns>
        private Dictionary<Guid, double>? BuildCurrentFractilePinMap()
        {
            var pins = _fractilePins;
            if (pins == null || pins.Length == 0) return null;
            var map = new Dictionary<Guid, double>();
            for (int i = 0; i < pins.Length; i++)
            {
                map[pins[i].FunctionId] = pins[i].Percentile;
            }
            return map;
        }

        /// <summary>
        /// Builds the shared-epistemic-variable selector columns for a sampler setup at the given
        /// size: the distinct variable names collected across every component's epistemic
        /// composites, each column derived from the run seed and the variable name alone
        /// (<see cref="EpistemicSharingScope.BuildColumns"/>) — so every binder of one variable
        /// receives the same per-realization branch draw, at any component and any nesting depth.
        /// </summary>
        /// <param name="sampleSize">The realization count the columns must match.</param>
        /// <returns>The columns keyed by variable name, or null when no component names one.</returns>
        private Dictionary<string, double[]>? BuildEpistemicSharingColumns(int sampleSize)
        {
            var variables = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _components.Count; i++)
            {
                _components[i].CollectEpistemicVariables(variables);
            }
            if (variables.Count == 0) return null;
            return EpistemicSharingScope.BuildColumns(variables, _options.PRNGSeed, sampleSize, _options.SamplingScheme);
        }

        /// <summary>
        /// The aggregated result of one logic-tree axis discovery: the ordered axes, the
        /// disagreements that block exact enumeration, every epistemic composite id (the
        /// pin-conflict gate), and whether an epistemic consequence composite exists.
        /// </summary>
        private sealed class LogicTreeDiscovery
        {
            /// <summary>The enumeration axes in deterministic order: bound variables by ordinal name, then unbound composites by function id.</summary>
            internal List<LogicTreeAxisSeed> Axes { get; } = new List<LogicTreeAxisSeed>();

            /// <summary>The shared variables whose binders declare differing weight vectors.</summary>
            internal SortedSet<string> MismatchedVariables { get; } = new SortedSet<string>(StringComparer.Ordinal);

            /// <summary>The names of unbound composites whose equal-id self-contained copies declare differing weight vectors.</summary>
            internal SortedSet<string> MismatchedFunctionCopies { get; } = new SortedSet<string>(StringComparer.Ordinal);

            /// <summary>Every epistemic-mixture composite id discovered on the walked clusters.</summary>
            internal HashSet<Guid> EpistemicFunctionIds { get; } = new HashSet<Guid>();

            /// <summary>True when any consequence composite uses the epistemic mode anywhere in its subtree.</summary>
            internal bool ConsequenceEpistemic { get; set; }
        }

        /// <summary>
        /// Discovers the logic-tree axes across every component's walked epistemic composites:
        /// one axis per distinct shared variable (ordinal name order), then one per unbound
        /// epistemic composite (function-id order, equal-id self-contained copies merged as one
        /// knowledge quantity with bitwise weight agreement required).
        /// </summary>
        /// <param name="components">The components to discover over (author state at validation, run clones at run start).</param>
        /// <returns>The aggregated discovery.</returns>
        private LogicTreeDiscovery DiscoverLogicTreeAxes(List<SystemComponent> components)
        {
            var discovery = new LogicTreeDiscovery();
            var boundAxes = new Dictionary<string, LogicTreeAxisSeed>(StringComparer.Ordinal);
            var unboundRaw = new List<LogicTreeAxisSeed>();
            var visited = new HashSet<IRiskFunction>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < components.Count; i++)
            {
                components[i].CollectLogicTreeAxes(boundAxes, unboundRaw, discovery.MismatchedVariables,
                    discovery.EpistemicFunctionIds, visited);
                foreach (var function in components[i].GetReferencedFunctions())
                {
                    if (function is CompositeConsequence consequence && consequence.UsesEpistemicMode())
                    {
                        discovery.ConsequenceEpistemic = true;
                    }
                }
            }

            var unboundById = new Dictionary<Guid, LogicTreeAxisSeed>();
            foreach (var seed in unboundRaw)
            {
                if (unboundById.TryGetValue(seed.FunctionId, out var existing))
                {
                    if (!existing.WeightsMatch(seed)) discovery.MismatchedFunctionCopies.Add(existing.FunctionName);
                    continue;
                }
                unboundById.Add(seed.FunctionId, seed);
            }

            var names = new List<string>(boundAxes.Keys);
            names.Sort(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                discovery.Axes.Add(boundAxes[names[i]]);
            }
            var unbound = new List<LogicTreeAxisSeed>(unboundById.Values);
            unbound.Sort((a, b) => a.FunctionId.CompareTo(b.FunctionId));
            discovery.Axes.AddRange(unbound);
            return discovery;
        }

        /// <summary>
        /// Reports whether an epistemic-mixture composite is reachable through a tree
        /// probability source anywhere under the given response function. Trees sample their
        /// referenced sources — and their sources' hazard-transform chains — through isolated
        /// self-contained setup clones the walked-cluster axis discovery cannot see, so a
        /// tree-carried epistemic composite (a response composite behind a source, or an
        /// epistemic composite transform inside a source's chain) selects branches the enumerator
        /// cannot force and the mean pass would silently blend — both gates treat it explicitly.
        /// The containment recursion crosses composite children into nested trees, tree sources
        /// into nested composites, event-tree links, and external fault-tree transfer targets; an
        /// epistemic composite reached purely through walked composite nesting is a discovered
        /// axis, not a tree-carried one.
        /// </summary>
        /// <param name="function">The response function to search under.</param>
        /// <param name="insideTreeSource">True once the recursion has crossed a tree probability-source edge.</param>
        /// <param name="visited">The (function, inside-tree) states already searched — one
        /// function can be reachable both directly and through a tree edge, and the two states
        /// answer differently.</param>
        /// <returns>True when a tree-carried epistemic composite exists.</returns>
        private static bool TreeCarriesEpistemicComposite(IResponseFunction function, bool insideTreeSource,
            HashSet<(IResponseFunction Function, bool InsideTree)> visited)
        {
            if (!visited.Add((function, insideTreeSource))) return false;
            switch (function)
            {
                case CompositeResponse composite:
                    if (insideTreeSource && composite.UsesEpistemicMode()) return true;
                    for (int i = 0; i < composite.ResponseFunctions.Count; i++)
                    {
                        var child = composite.ResponseFunctions[i].ResponseFunction;
                        if (child != null && TreeCarriesEpistemicComposite(child, insideTreeSource, visited)) return true;
                    }
                    return false;
                case EventTreeResponse eventTree:
                    foreach (var node in eventTree.EventTree.Nodes)
                    {
                        if (node is ChanceNode chance && ChainCarriesEpistemicTransform(chance.ProbabilitySource)) return true;
                        IResponseFunction? source = node switch
                        {
                            ChanceNode chanceSource => chanceSource.ProbabilitySource?.ResponseFunction,
                            EventTreeLinkNode link => link.TargetFunction,
                            _ => null,
                        };
                        if (source != null && TreeCarriesEpistemicComposite(source, true, visited)) return true;
                    }
                    return false;
                case FaultTreeResponse faultTree:
                    foreach (var node in faultTree.FaultTree.Nodes)
                    {
                        if (node is FaultTreeBasicEventNode basic)
                        {
                            if (ChainCarriesEpistemicTransform(basic.ProbabilitySource)) return true;
                            var source = basic.ProbabilitySource?.ResponseFunction;
                            if (source != null && TreeCarriesEpistemicComposite(source, true, visited)) return true;
                        }
                        else if (node is FaultTreeTransferNode transfer && transfer.TargetFunction != null)
                        {
                            if (TreeCarriesEpistemicComposite(transfer.TargetFunction, true, visited)) return true;
                        }
                    }
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Reports whether a probability source's hazard-transform chain carries an
        /// epistemic-mixture composite transform. Chain transforms sample inside the owning
        /// tree's isolated setup clones, so an epistemic composite there is tree-carried by
        /// construction.
        /// </summary>
        /// <param name="source">The probability source, or null.</param>
        /// <returns>True when a chain entry uses the epistemic-mixture mode.</returns>
        private static bool ChainCarriesEpistemicTransform(ProbabilitySource? source)
        {
            if (source == null) return false;
            for (int i = 0; i < source.HazardTransforms.Count; i++)
            {
                if (source.HazardTransforms[i] is CompositeTransform composite
                    && composite.UsesEpistemicMode()) return true;
            }
            return false;
        }

        /// <summary>
        /// The single gate generator behind the logic-tree enumeration: discovers the axes and
        /// produces every configuration error and warning — <see cref="Validate"/> appends them,
        /// and the run start throws on any error over the captured run state (the race-safe
        /// re-check pattern the realization weights and fractile pins follow).
        /// </summary>
        /// <param name="components">The components to gate over.</param>
        /// <param name="realizationsPerCombination">The configured per-combination count M.</param>
        /// <param name="fractilePins">The configured fractile pins, or null.</param>
        /// <param name="realizationWeights">The configured run-input weights, or null.</param>
        /// <param name="meanOnly">Whether the options request a mean-only run.</param>
        /// <returns>The discovery and the accumulated error and warning messages.</returns>
        private (LogicTreeDiscovery Discovery, List<string> Errors, List<string> Warnings) AnalyzeLogicTreeConfiguration(
            List<SystemComponent> components, int realizationsPerCombination,
            IReadOnlyList<FractilePin>? fractilePins, double[]? realizationWeights, bool meanOnly)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var discovery = DiscoverLogicTreeAxes(components);

            if (meanOnly)
            {
                errors.Add("Error: The logic-tree enumeration selects one branch combination per realization block; a mean-only run has no realizations to enumerate with. Disable the mean-only option or clear LogicTreeEnumerationRealizations.");
            }
            if (realizationWeights != null)
            {
                errors.Add("Error: The logic-tree enumeration computes the realization weight vector itself; clear RealizationWeights to enumerate (re-weight a retained enumerated run post hoc instead).");
            }
            if (discovery.ConsequenceEpistemic)
            {
                errors.Add("Error: An epistemic-mixture consequence composite selects from its failure mode's coupling draw, which the enumerator cannot force; switch it to the aleatory Mixture (exposure branches enumerate exactly within every realization) or remove it before enumerating.");
            }
            var treeVisited = new HashSet<(IResponseFunction Function, bool InsideTree)>();
            bool treeCarried = false;
            for (int i = 0; i < components.Count && !treeCarried; i++)
            {
                foreach (var function in components[i].GetReferencedFunctions())
                {
                    if (function is IResponseFunction response && TreeCarriesEpistemicComposite(response, false, treeVisited))
                    {
                        treeCarried = true;
                        break;
                    }
                }
            }
            if (treeCarried)
            {
                errors.Add("Error: An epistemic-mixture composite is referenced through a tree probability source; the tree samples it in isolated setup clones the enumerator cannot force. Remove it from the tree, or restructure the branch alternatives outside the tree, before enumerating.");
            }
            if (discovery.Axes.Count == 0)
            {
                errors.Add("Error: The logic-tree enumeration found no epistemic-mixture composite on the walked clusters; there is nothing to enumerate.");
            }
            foreach (string variable in discovery.MismatchedVariables)
            {
                errors.Add($"Error: The shared epistemic variable '{variable}' is bound by composites with differing weight vectors; exact enumeration needs one well-defined branch set per axis.");
            }
            foreach (string name in discovery.MismatchedFunctionCopies)
            {
                errors.Add($"Error: Epistemic-mixture composite '{name}' appears as equal-id self-contained copies with differing weight vectors; exact enumeration needs one well-defined branch set per axis.");
            }

            long combinationCount = 1;
            bool countable = discovery.Axes.Count > 0;
            for (int i = 0; i < discovery.Axes.Count; i++)
            {
                var seed = discovery.Axes[i];
                int positive = 0;
                for (int w = 0; w < seed.Weights.Length; w++)
                {
                    if (seed.Weights[w] > 0d) positive++;
                }
                if (positive == 0)
                {
                    errors.Add($"Error: Epistemic-mixture composite '{seed.FunctionName}' has no positively weighted branch to enumerate.");
                    countable = false;
                    continue;
                }
                combinationCount = SaturatingProduct(combinationCount, positive);
            }
            if (countable)
            {
                if (combinationCount > LogicTreeCombinationErrorLimit)
                {
                    errors.Add($"Error: The logic-tree enumeration crosses {combinationCount:N0} branch combinations, above the {LogicTreeCombinationErrorLimit:N0} limit; reduce the branch counts or the number of epistemic axes.");
                }
                else if (combinationCount > LogicTreeCombinationWarningLimit)
                {
                    warnings.Add($"Warning: The logic-tree enumeration crosses {combinationCount:N0} branch combinations; the ensemble computes one block of realizations per combination, growing cost and memory accordingly.");
                }
                long realizationCount = SaturatingProduct(combinationCount, realizationsPerCombination);
                if (realizationCount < 2)
                {
                    errors.Add("Error: The logic-tree enumeration would compute a single realization; increase LogicTreeEnumerationRealizations or the branch counts (the percentile assembly needs at least two).");
                }
                if (realizationCount > LogicTreeRealizationErrorLimit)
                {
                    errors.Add($"Error: The logic-tree enumeration would compute {realizationCount:N0} realizations (combinations × per-combination count), above the {LogicTreeRealizationErrorLimit:N0} limit; reduce LogicTreeEnumerationRealizations or the branch counts.");
                }
                else if (realizationCount > LogicTreeRealizationWarningLimit)
                {
                    warnings.Add($"Warning: The logic-tree enumeration holds {realizationCount:N0} realizations' summaries in memory (combinations × per-combination count) — above the {LogicTreeRealizationWarningLimit:N0} a sampled run can configure; intended for deliberate studies.");
                }
                if (_options.SamplingScheme == SamplingScheme.ScrambledSobol && realizationCount <= int.MaxValue
                    && (realizationCount & (realizationCount - 1)) != 0)
                {
                    warnings.Add($"Warning: Scrambled-Sobol sampling stratifies most evenly at power-of-two realization counts; the enumeration computes {realizationCount:N0}, which is not one.");
                }
            }

            if (fractilePins != null)
            {
                for (int i = 0; i < fractilePins.Count; i++)
                {
                    if (discovery.EpistemicFunctionIds.Contains(fractilePins[i].FunctionId))
                    {
                        errors.Add($"Error: Fractile pin {i + 1} targets epistemic-mixture composite {fractilePins[i].FunctionId:D}; a pin would overwrite the enumerator's forced branch selection. Pins on non-enumerated functions remain valid.");
                    }
                }
            }
            return (discovery, errors, warnings);
        }

        /// <summary>
        /// Builds the enumeration design from the discovered axes: per axis, the positively
        /// weighted branches with their forcing percentiles (the midpoint of each branch's
        /// cumulative-weight interval — the sampled mode's inclusive-upper inverse-CDF algebra)
        /// and the per-axis normalized weights whose lexicographic products are the exact
        /// combination weights.
        /// </summary>
        /// <param name="axes">The discovered axes, in deterministic order.</param>
        /// <param name="realizationsPerCombination">The per-combination realization count M.</param>
        /// <returns>The enumeration design.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a branch weight is too small to force a distinct selector percentile at its cumulative position.</exception>
        private static LogicTreeEnumerationMap BuildLogicTreeMap(List<LogicTreeAxisSeed> axes, int realizationsPerCombination)
        {
            var builtAxes = new List<LogicTreeAxis>(axes.Count);
            var normalizedWeights = new List<double[]>(axes.Count);
            for (int a = 0; a < axes.Count; a++)
            {
                var seed = axes[a];
                double positiveSum = 0d;
                for (int i = 0; i < seed.Weights.Length; i++)
                {
                    if (seed.Weights[i] > 0d) positiveSum += seed.Weights[i];
                }
                var branches = new List<LogicTreeBranch>();
                var normalized = new List<double>();
                double cumulative = 0d;
                for (int i = 0; i < seed.Weights.Length; i++)
                {
                    double weight = seed.Weights[i];
                    if (weight <= 0d) continue;
                    double prior = cumulative;
                    cumulative += weight;
                    double forced = prior + weight / 2d;
                    if (!(forced > prior && forced <= cumulative))
                    {
                        throw new InvalidOperationException(
                            $"Epistemic-mixture composite '{seed.FunctionName}' declares a branch whose weight is too small to force a distinct selector percentile; remove the branch or rebalance the weights.");
                    }
                    branches.Add(new LogicTreeBranch(i, weight, forced));
                    normalized.Add(weight / positiveSum);
                }
                bool isSharedVariable = seed.Variable.Length > 0;
                builtAxes.Add(new LogicTreeAxis(isSharedVariable ? seed.Variable : seed.FunctionName,
                    isSharedVariable, isSharedVariable ? null : seed.FunctionId, branches));
                normalizedWeights.Add(normalized.ToArray());
            }

            long combinationCount = 1;
            for (int a = 0; a < builtAxes.Count; a++)
            {
                combinationCount *= builtAxes[a].Branches.Count;
            }
            var combinationWeights = new double[combinationCount];
            var digits = new int[builtAxes.Count];
            for (long c = 0; c < combinationCount; c++)
            {
                double product = 1d;
                for (int a = 0; a < builtAxes.Count; a++)
                {
                    product *= normalizedWeights[a][digits[a]];
                }
                combinationWeights[c] = product;
                for (int a = builtAxes.Count - 1; a >= 0; a--)
                {
                    digits[a]++;
                    if (digits[a] < builtAxes[a].Branches.Count) break;
                    digits[a] = 0;
                }
            }
            return new LogicTreeEnumerationMap(builtAxes, realizationsPerCombination, combinationWeights);
        }

        /// <summary>
        /// Builds the constant-per-block forcing columns of an enumeration design: for every
        /// axis, one column of the run's length whose block [c·M, (c+1)·M) holds the forcing
        /// percentile of the branch combination c selects on that axis — keyed by variable name
        /// for shared-variable axes and by function id for unbound composites.
        /// </summary>
        /// <param name="map">The enumeration design.</param>
        /// <param name="nameColumns">The name-keyed forcing columns.</param>
        /// <param name="idColumns">The id-keyed forcing columns.</param>
        private static void BuildLogicTreeColumns(LogicTreeEnumerationMap map,
            out Dictionary<string, double[]> nameColumns, out Dictionary<Guid, double[]> idColumns)
        {
            nameColumns = new Dictionary<string, double[]>(StringComparer.Ordinal);
            idColumns = new Dictionary<Guid, double[]>();
            int perCombination = map.RealizationsPerCombination;
            for (int a = 0; a < map.Axes.Count; a++)
            {
                var axis = map.Axes[a];
                var column = new double[map.RealizationCount];
                for (int c = 0; c < map.CombinationCount; c++)
                {
                    double forced = axis.Branches[map.BranchIndexOf(c, a)].ForcedPercentile;
                    int start = c * perCombination;
                    for (int i = 0; i < perCombination; i++)
                    {
                        column[start + i] = forced;
                    }
                }
                if (axis.IsSharedVariable)
                {
                    nameColumns[axis.Name] = column;
                }
                else
                {
                    idColumns[axis.FunctionId!.Value] = column;
                }
            }
        }

        /// <summary>
        /// Builds the enumeration's exact per-realization weight vector: each realization
        /// carries its combination's weight divided by the per-combination count, so the
        /// weighted reductions integrate the logic tree with zero Monte Carlo noise on the
        /// branch axis.
        /// </summary>
        /// <param name="map">The enumeration design.</param>
        /// <returns>The weight vector, one entry per realization.</returns>
        private static double[] BuildLogicTreeRealizationWeights(LogicTreeEnumerationMap map)
        {
            var weights = new double[map.RealizationCount];
            int perCombination = map.RealizationsPerCombination;
            for (int c = 0; c < map.CombinationCount; c++)
            {
                double weight = map.CombinationWeights[c] / perCombination;
                int start = c * perCombination;
                for (int i = 0; i < perCombination; i++)
                {
                    weights[start + i] = weight;
                }
            }
            return weights;
        }

        /// <summary>
        /// Enters the epistemic sharing scope a post-run re-derivation needs to reproduce the
        /// last run's streams: when the last run enumerated and the design size matches, the
        /// enumeration's forcing columns are rebuilt (deterministic from the published map);
        /// otherwise the standard seed-derived shared columns apply. Null when no scope is
        /// needed at all.
        /// </summary>
        /// <param name="sampleSize">The re-derivation design size.</param>
        /// <returns>The scope restoration token, or null.</returns>
        private IDisposable? EnterEpistemicScopeForRederivation(int sampleSize)
        {
            var map = _logicTreeEnumeration;
            if (map != null && map.RealizationCount == sampleSize)
            {
                BuildLogicTreeColumns(map, out var forcedNameColumns, out var forcedIdColumns);
                return EpistemicSharingScope.Enter(forcedNameColumns, forcedIdColumns, null, null);
            }
            var columns = BuildEpistemicSharingColumns(sampleSize);
            return columns == null ? null : EpistemicSharingScope.Enter(columns);
        }

        /// <summary>
        /// The epistemic-mixture validation gates. A mean-only run cannot select a branch, so a
        /// walked-cluster epistemic composite's analytic blend would silently answer with the
        /// wrong (Jensen-gap) number — an Error; an epistemic consequence composite's blend keeps
        /// the exact mean and loses only the spread — a Warning. Shared variables bound by root
        /// composites with disagreeing weight vectors are flagged, because the shared draw then
        /// still ranks consistently but branches no longer correspond one-to-one.
        /// </summary>
        /// <param name="messages">The accumulating validation messages.</param>
        private void ValidateEpistemicComposites(List<string> messages)
        {
            bool anyWalkedEpistemic = false;
            bool anyConsequenceEpistemic = false;
            var binderSignatures = new Dictionary<string, string>(StringComparer.Ordinal);
            var mismatched = new SortedSet<string>(StringComparer.Ordinal);

            void NoteBinder(string variable, string signature)
            {
                if (variable.Length == 0) return;
                if (binderSignatures.TryGetValue(variable, out string? existing))
                {
                    if (!string.Equals(existing, signature, StringComparison.Ordinal)) mismatched.Add(variable);
                }
                else
                {
                    binderSignatures.Add(variable, signature);
                }
            }

            var seen = new HashSet<IRiskFunction>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < _components.Count; i++)
            {
                foreach (var function in _components[i].GetReferencedFunctions())
                {
                    if (!seen.Add(function)) continue;
                    switch (function)
                    {
                        case CompositeHazard hazard:
                            if (hazard.UsesEpistemicMode()) anyWalkedEpistemic = true;
                            if (hazard.CompositeCombinationType == CompositeCombinationType.EpistemicMixture && hazard.EpistemicVariable.Length > 0)
                            {
                                var weights = new double[hazard.HazardFunctions.Count];
                                for (int w = 0; w < weights.Length; w++) weights[w] = hazard.HazardFunctions[w].Weight;
                                NoteBinder(hazard.EpistemicVariable, WeightSignature(weights));
                            }
                            break;
                        case CompositeResponse response:
                            if (response.UsesEpistemicMode()) anyWalkedEpistemic = true;
                            if (response.CompositeCombinationType == CompositeCombinationType.EpistemicMixture && response.EpistemicVariable.Length > 0)
                            {
                                var weights = new double[response.ResponseFunctions.Count];
                                for (int w = 0; w < weights.Length; w++) weights[w] = response.ResponseFunctions[w].Weight;
                                NoteBinder(response.EpistemicVariable, WeightSignature(weights));
                            }
                            break;
                        case CompositeTransform transform:
                            if (transform.UsesEpistemicMode()) anyWalkedEpistemic = true;
                            if (transform.CompositeFunctionType == CompositeFunctionType.EpistemicMixture && transform.EpistemicVariable.Length > 0)
                            {
                                var weights = new double[transform.TransformFunctions.Count];
                                for (int w = 0; w < weights.Length; w++) weights[w] = transform.TransformFunctions[w].Weight;
                                NoteBinder(transform.EpistemicVariable, WeightSignature(weights));
                            }
                            break;
                        case CompositeConsequence consequence:
                            if (consequence.UsesEpistemicMode()) anyConsequenceEpistemic = true;
                            break;
                    }
                }
            }

            // A tree-carried epistemic composite (a composite referenced through a tree
            // probability source) is invisible to the walked-function switch above, but a mean
            // pass evaluates it through the tree's mean clone as the same silent analytic blend
            // — the identical Jensen-gap wrong answer, so the shared predicate covers both.
            if (_options.EstimateMeanRiskOnly && (anyWalkedEpistemic || CarriesWalkedEpistemicComposite()))
            {
                messages.Add("Error: An epistemic-mixture composite selects one branch per realization; a mean-only run has no realizations to select with, and its analytic blend is the wrong answer for a nonlinear downstream chain. Run the full-uncertainty analysis, or change the composite mode.");
            }
            if (_options.EstimateMeanRiskOnly && anyConsequenceEpistemic)
            {
                messages.Add("Warning: An epistemic-mixture consequence composite blends to the exact mean in a mean-only run, but the epistemic spread is absent; run the full-uncertainty analysis to carry it.");
            }
            foreach (string variable in mismatched)
            {
                messages.Add($"Warning: The shared epistemic variable '{variable}' is bound by composites with differing weight vectors; the shared draw still selects consistently by rank, but branches no longer correspond one-to-one across binders.");
            }
        }

        /// <summary>
        /// Whether any component references a walked-cluster epistemic-mixture composite —
        /// directly, nested inside another composite, or carried through a tree probability
        /// source. This is the predicate behind the mean-only refusal: a mean pass cannot
        /// select an epistemic branch, so its analytic blend is the wrong (Jensen-gap) answer
        /// for a nonlinear downstream chain. Internal so the cost-benefit layer refuses an
        /// epistemic alternative through the same authority — its life-cycle trajectories are
        /// mean-only quantifications.
        /// </summary>
        /// <returns>True when a walked epistemic composite is reachable.</returns>
        internal bool CarriesWalkedEpistemicComposite()
        {
            var seen = new HashSet<IRiskFunction>(ReferenceEqualityComparer.Instance);
            var treeVisited = new HashSet<(IResponseFunction Function, bool InsideTree)>();
            for (int i = 0; i < _components.Count; i++)
            {
                foreach (var function in _components[i].GetReferencedFunctions())
                {
                    if (!seen.Add(function)) continue;
                    switch (function)
                    {
                        case CompositeHazard hazard when hazard.UsesEpistemicMode():
                            return true;
                        case CompositeResponse response when response.UsesEpistemicMode():
                            return true;
                        case CompositeTransform transform when transform.UsesEpistemicMode():
                            return true;
                    }
                    if (function is IResponseFunction responseFunction
                        && TreeCarriesEpistemicComposite(responseFunction, false, treeVisited))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// The ordinal signature of a weight vector, for cross-binder comparison.
        /// </summary>
        /// <param name="weights">The declared entry weights.</param>
        /// <returns>The G17 pipe-joined signature.</returns>
        private static string WeightSignature(double[] weights)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < weights.Length; i++)
            {
                if (i > 0) builder.Append('|');
                builder.Append(weights[i].ToString("G17", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private (bool IsValid, List<string> ValidationMessages) ValidateMessages()
        {
            var messages = new List<string>();

            messages.AddRange(_options.Validate().ValidationMessages);

            if (_realizationWeights != null)
            {
                if (_options.EstimateMeanRiskOnly)
                {
                    messages.Add("Error: Realization weights apply to the full-uncertainty ensemble; clear RealizationWeights or disable the mean-only run option.");
                }
                else
                {
                    string? weightReason = EnsembleResults.DescribeInvalidWeights(_realizationWeights, _options.Realizations, out _);
                    if (weightReason != null)
                    {
                        messages.Add($"Error: The realization weights are invalid. {weightReason}");
                    }
                }
            }

            if (_fractilePins != null && _fractilePins.Length > 0)
            {
                if (_options.EstimateMeanRiskOnly)
                {
                    messages.Add("Warning: Fractile pins condition the full-uncertainty ensemble; a mean-only run samples the mean functions and ignores them.");
                }
                else
                {
                    var pinErrors = new List<string>();
                    var pinWarnings = new List<string>();
                    ClassifyFractilePins(_fractilePins, pinErrors, pinWarnings, null, null);
                    messages.AddRange(pinErrors);
                    messages.AddRange(pinWarnings);
                }
            }

            if (_logicTreeEnumerationRealizations is int logicTreeRealizations)
            {
                var logicTree = AnalyzeLogicTreeConfiguration(_components, logicTreeRealizations,
                    _fractilePins, _realizationWeights, _options.EstimateMeanRiskOnly);
                messages.AddRange(logicTree.Errors);
                messages.AddRange(logicTree.Warnings);
            }

            if (_options.SamplingScheme == SamplingScheme.ScrambledSobol && !_options.EstimateMeanRiskOnly
                && _logicTreeEnumerationRealizations == null
                && _options.Realizations > 0 && (_options.Realizations & (_options.Realizations - 1)) != 0)
            {
                messages.Add($"Warning: Scrambled-Sobol sampling stratifies most evenly at power-of-two realization counts; {_options.Realizations:N0} is not one.");
            }

            ValidateEpistemicComposites(messages);

            for (int i = 0; i < _options.TolerableRiskCriteria.Count; i++)
            {
                var criterion = _options.TolerableRiskCriteria[i];
                if (criterion != null && criterion.ConsequenceTypeIndex > _additionalConsequenceTypes.Count)
                {
                    messages.Add($"Error: Tolerable-risk criterion {i + 1} reads consequence-type position {criterion.ConsequenceTypeIndex}, but the analysis declares positions 0 through {_additionalConsequenceTypes.Count}.");
                }
            }
            if (_options.TolerableRiskCriteria.Count > 0 && _options.EstimateMeanRiskOnly)
            {
                messages.Add("Warning: Tolerable-risk criteria evaluate on the full-uncertainty ensemble; a mean-only run does not produce them.");
            }

            if (_retainRealizations)
            {
                if (_options.EstimateMeanRiskOnly)
                {
                    messages.Add("Warning: Realization retention applies to the full-uncertainty ensemble; a mean-only run retains nothing.");
                }
                else
                {
                    messages.Add($"Warning: Retaining the realization ensemble holds all {_options.Realizations:N0} per-realization curve sets in memory after the run; intended for diagnostics and re-banding, not routine runs.");
                }
            }
            if (_retainedIntegrationDetailIndex is int retainedIndex)
            {
                bool matchable = _options.EstimateMeanRiskOnly
                    ? retainedIndex == -1
                    : retainedIndex >= 0 && retainedIndex < _options.Realizations;
                if (!matchable)
                {
                    messages.Add($"Warning: The retained integration-detail index ({retainedIndex}) matches no realization of this run (−1 selects the mean-only pass; a full run computes indices 0 through {_options.Realizations - 1:N0}); nothing will be retained.");
                }
            }

            if (_components.Count == 0)
            {
                messages.Add("Error: The analysis has no system components.");
            }

            if (_components.Count > 1)
            {
                if (_options.SystemRiskMethod == SystemRiskType.AdditiveRiskMethod)
                {
                    if (_options.ComponentHazardDependency != DependencyType.Independent)
                    {
                        messages.Add("Error: The additive system risk method assumes strictly independent components; select the joint method to model cross-component hazard dependence.");
                    }
                }
                else
                {
                    if (_options.ComponentHazardDependency == DependencyType.CorrelationMatrix &&
                        !IsHazardCorrelationMatrixValid())
                    {
                        messages.Add($"Error: The component hazard correlation matrix must be a positive-definite {_components.Count}×{_components.Count} matrix (one row per component).");
                    }

                    // The system-level combination guardrail: the joint integrand crosses the
                    // components' recorded entry lists, so the product of their worst-case widths
                    // bounds the work per evaluation.
                    long entryProduct = 1;
                    for (int i = 0; i < _components.Count && entryProduct <= JointEntryErrorLimit; i++)
                    {
                        entryProduct *= _components[i].EstimateRecordedFailureEntries();
                    }
                    if (entryProduct > JointEntryErrorLimit)
                    {
                        messages.Add($"Error: The joint system combination cross product exceeds {JointEntryErrorLimit} entries per evaluation (the product of the components' pathway/branch widths); reduce the mixture branch counts or failure mode counts.");
                    }
                    else if (entryProduct > JointEntryWarningLimit)
                    {
                        messages.Add($"Warning: The joint system combination cross product ({entryProduct}) exceeds {JointEntryWarningLimit} entries per evaluation; the enumeration grows compute cost accordingly.");
                    }
                }
            }

            for (int i = 0; i < _components.Count; i++)
            {
                var component = _components[i];
                foreach (string message in component.Validate(_options.Mode).ValidationMessages)
                {
                    messages.Add($"{message} [{component.Name}]");
                }
            }

            ValidateConsequenceTypeAxis(messages);
            ValidateHazardAxisConsistency(messages);
            messages.AddRange(EstimateResourceRequirements().Messages(ResourceSeverity.Warning));

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <summary>
        /// Estimates what this run will cost before it starts: the memory it will hold and the
        /// work that dominates it, line by line.
        /// </summary>
        /// <returns>The estimate.</returns>
        /// <remarks>
        /// <para>
        /// Peak LIVE memory, not total allocation — a long ensemble run churns far more than it
        /// holds at any instant. <see cref="Validate"/> folds the warning and error lines into its
        /// messages, so a configuration that cannot run says which option to change rather than
        /// failing later with an allocation exception.
        /// </para>
        /// <para>
        /// Estimates only. The recorded-point figure follows the adaptive integrator, whose
        /// evaluation count is bounded only by <see cref="RiskAnalysisOptions.MaxEvaluations"/>,
        /// and the combination figures assume the inclusion-exclusion bracket does not converge —
        /// the pessimistic end of a range whose usual case is far cheaper.
        /// </para>
        /// </remarks>
        public ResourceEstimate EstimateResourceRequirements()
        {
            const long bytesPerDouble = 8;
            const long bytesPerInt = 4;
            var items = new List<ResourceEstimateItem>();

            int componentCount = _components.Count;
            int typeCount = 1 + _additionalConsequenceTypes.Count;
            int realizations = _options.EstimateMeanRiskOnly ? 1 : _options.Realizations;
            int concurrency = Math.Max(1, Math.Min(Environment.ProcessorCount, realizations));
            bool joint = componentCount > 1 && _options.SystemRiskMethod == SystemRiskType.JointRiskMethod;

            // The joint system's exclusive combination enumeration, per evaluation in flight.
            if (joint)
            {
                if (componentCount > VegasMaxDimensions)
                {
                    items.Add(new ResourceEstimateItem("Joint system dimension", 0, componentCount, false, ResourceSeverity.Error,
                        $"Error: The joint system risk method integrates over one dimension per component and supports at most {VegasMaxDimensions}; the analysis has {componentCount}. Use the additive system risk method, which convolves the components' loss distributions and carries no dimension limit, or group the components."));
                }

                long cap = Math.Max(1, _options.MaxSystemCombinations);
                long enumerated = componentCount >= 62 ? cap : Math.Min(cap, (1L << componentCount));
                long entryBytes = SaturatingAdd(32L, SaturatingProduct(componentCount, bytesPerInt));
                long bytes = SaturatingProduct(enumerated, entryBytes, concurrency);

                // An upper bound only. The inclusion-exclusion bracket normally closes far short
                // of the cap, and whether it does depends on the failure probabilities the run
                // encounters, which is not knowable here — the run reports actual truncation.
                items.Add(new ResourceEstimateItem("Joint system combination enumeration", bytes, enumerated, false,
                    ResourceSeverity.Informational,
                    $"The joint system enumerates at most {enumerated:N0} exclusive component combinations per evaluation, holding about {FormatBytes(bytes)}."));
            }

            // Per-component structures: lazy joint-failure output buffers, correlation matrices,
            // and dependent competing-risk pre-processing.
            double genzEvaluations = 0d;
            for (int i = 0; i < componentCount; i++)
            {
                var component = _components[i];
                int units = component.CombinationUnitCountForEstimate();
                if (units <= 0) continue;

                if (component.FailureModeMethod == FailureModeMethod.JointFailures)
                {
                    long singles = units;
                    long pairs = units >= 2 ? SaturatingProduct(units, units - 1L) / 2L : 0L;
                    long triples = units >= 3 ? SaturatingProduct(units, units - 1L, units - 2L) / 6L : 0L;
                    long firstConvergenceRows = SaturatingAdd(
                        SaturatingAdd(singles, pairs),
                        SaturatingAdd(triples, 1L));
                    long completeRows = units >= 63 ? long.MaxValue : (1L << units) - 1L;
                    firstConvergenceRows = Math.Min(firstConvergenceRows, completeRows);

                    long bytesPerRow = SaturatingAdd(32L, SaturatingProduct(units, bytesPerInt));
                    long bufferBytes = SaturatingProduct(firstConvergenceRows, bytesPerRow, concurrency);
                    var severity = units > 20 ? ResourceSeverity.Warning : ResourceSeverity.Informational;
                    string prefix = severity == ResourceSeverity.Warning ? "Warning: " : string.Empty;
                    items.Add(new ResourceEstimateItem($"Lazy combination buffers — {component.Name}",
                        bufferBytes, firstConvergenceRows, false, severity,
                        $"{prefix}System component '{component.Name}' lazily enumerates {units} failure paths. " +
                        $"If the inclusion-exclusion bracket closes at its first eligible check, its in-flight output buffers hold about {firstConvergenceRows:N0} rows ({FormatBytes(bufferBytes)} at current concurrency); " +
                        $"if convergence is slow, enumeration can grow toward {completeRows:N0} rows and compute remains combinatorial. No dense U×(2^U−1) matrix is allocated."));
                }
                if (component.FailureModeDependency != DependencyType.Independent)
                {
                    items.Add(new ResourceEstimateItem($"Correlation matrix — {component.Name}",
                        SaturatingProduct(units, units, bytesPerDouble), 0, true, ResourceSeverity.Informational,
                        $"System component '{component.Name}' holds a {units}×{units} correlation matrix."));
                }

                // The dependent competing branches evaluate a multivariate-normal rectangle
                // integral per unit per hazard level, once per realization unless the component is
                // deterministic and the run can share one pre-processing.
                bool dependentCompeting = component.FailureModeMethod == FailureModeMethod.CompetingFailures
                    && units > 1
                    && component.FailureModeDependency != DependencyType.Independent
                    && component.FailureModeDependency != DependencyType.PerfectlyPositive;
                if (dependentCompeting)
                {
                    double passes = component.IsDeterministic ? 1d : realizations;
                    double evaluations = SaturatingProductDouble(passes, units, CompetingIncidenceBins + 1d);
                    genzEvaluations = SaturatingAddDouble(genzEvaluations, evaluations);
                    var severity = evaluations > 5e6 ? ResourceSeverity.Warning : ResourceSeverity.Informational;
                    items.Add(new ResourceEstimateItem($"Competing-risk pre-processing — {component.Name}", 0, evaluations, false, severity,
                        severity == ResourceSeverity.Informational
                            ? $"System component '{component.Name}' evaluates {evaluations:N0} multivariate-normal rectangle integrals building its incidence functions."
                            : $"Warning: System component '{component.Name}' evaluates {evaluations:N0} multivariate-normal rectangle integrals building its incidence functions, once per realization because it carries knowledge uncertainty. Reduce Realizations, use the Independent or Perfectly Positive failure-mode dependency, or remove the uncertainty from its hazard and fragilities so the run can build them once."));
                }
            }

            // Integration-work range. The lower bound is the forced subdivision depth plus the
            // two Appendix-D endpoint evaluations; the upper bound is the configured AGK cap
            // plus endpoints. Joint VEGAS has a fixed production schedule, with an adaptive
            // mean-function probe range added only for automatic tail focus.
            int minimumDepth = _options.EstimateMeanRiskOnly ? 2 : _options.EnsembleMinDepth;
            long leafPanelsPerBin = 1L << Math.Min(30, minimumDepth);
            long evaluatedPanelsPerBin = SaturatingProduct(2L, leafPanelsPerBin) - 1L;
            long minimumRecordedInteriorNodes = SaturatingProduct(HazardBinCount, GaussKronrodNodes,
                leafPanelsPerBin);
            long minimumFunctionEvaluations = SaturatingAdd(
                SaturatingProduct(HazardBinCount, GaussKronrodNodes, evaluatedPanelsPerBin), 2L);
            long panelOverhead = SaturatingProduct(HazardBinCount + 1L, GaussKronrodNodes);
            long maximumFunctionEvaluations = SaturatingAdd(
                SaturatingAdd(_options.MaxEvaluations, panelOverhead), 2L);
            long minimumNodesPerComponent = SaturatingAdd(minimumRecordedInteriorNodes, 2L);
            long maximumNodesPerComponent = maximumFunctionEvaluations;
            double evaluationFloor;
            double evaluationCeiling;
            if (joint)
            {
                double fixedVegas = SaturatingProductDouble(realizations,
                    SaturatingAdd(SaturatingProduct(_options.WarmupEvaluations, _options.WarmupCycles),
                        SaturatingProduct(_options.FinalEvaluations, VegasRecordingPasses)));
                bool automaticProbe = _options.VegasTailFocusMode == VegasTailFocusMode.Automatic;
                double probeFloor = automaticProbe ? SaturatingProductDouble(componentCount, minimumFunctionEvaluations) : 0d;
                double probeCeiling = automaticProbe ? SaturatingProductDouble(componentCount, maximumFunctionEvaluations) : 0d;
                evaluationFloor = SaturatingAddDouble(fixedVegas, probeFloor);
                evaluationCeiling = SaturatingAddDouble(fixedVegas, probeCeiling);
            }
            else
            {
                evaluationFloor = SaturatingProductDouble(realizations, componentCount, minimumFunctionEvaluations);
                evaluationCeiling = SaturatingProductDouble(realizations, componentCount, maximumFunctionEvaluations);
            }

            long streams = SaturatingAdd(5L, SaturatingProduct(2L, MaxFailureModeCount()));
            long pointBytesLower = SaturatingProduct(minimumNodesPerComponent, streams, typeCount, 96L,
                componentCount, concurrency);
            long pointBytesUpper = SaturatingProduct(maximumNodesPerComponent, streams, typeCount, 96L,
                componentCount, concurrency);
            items.Add(new ResourceEstimateItem("Recorded risk-point workspaces", pointBytesUpper,
                evaluationCeiling, false, ResourceSeverity.Informational,
                $"Realizations in flight hold approximately {FormatBytes(pointBytesLower)} to {FormatBytes(pointBytesUpper)} of recorded risk points; peak sizing uses the upper bound."));

            if (!joint && componentCount > 0)
            {
                long ledgerCapacity = Math.Min(maximumNodesPerComponent,
                    Math.Max(256L, SaturatingProduct(_options.LECOutputLength, 16L)));
                long ledgerBytes = SaturatingProduct(ledgerCapacity, 3L, bytesPerDouble, concurrency);
                items.Add(new ResourceEstimateItem("Pooled quadrature mass ledgers", ledgerBytes,
                    0d, false, ResourceSeverity.Informational,
                    $"The in-flight pooled quadrature ledgers reserve at most {FormatBytes(ledgerBytes)} and are returned immediately after curve and contribution assembly."));
            }

            long ensembleBytes = SaturatingProduct(realizations, componentCount, typeCount, streams,
                _options.LECOutputLength, bytesPerDouble, 2L);
            var ensembleSeverity = ensembleBytes > 8L * 1024 * 1024 * 1024 ? ResourceSeverity.Error
                : ensembleBytes > 2L * 1024 * 1024 * 1024 ? ResourceSeverity.Warning
                : ResourceSeverity.Informational;
            items.Add(new ResourceEstimateItem("Curve workspaces and percentile inputs", ensembleBytes, 0, true, ensembleSeverity,
                ensembleSeverity == ResourceSeverity.Informational
                    ? $"The retained curve workspaces hold up to about {FormatBytes(ensembleBytes)} before percentile reduction."
                    : $"{(ensembleSeverity == ResourceSeverity.Error ? "Error" : "Warning")}: Curve workspaces need up to about {FormatBytes(ensembleBytes)} for {realizations:N0} realizations at an output length of {_options.LECOutputLength}. Reduce Realizations or LECOutputLength, or set EstimateMeanRiskOnly."));

            if ((_options.RiskMeasures & RiskMeasureOptions.RiskProfiles) != 0)
            {
                long profileArrays = SaturatingProduct(componentCount, SaturatingAdd(SaturatingProduct(5L, typeCount), 2L));
                if (_options.EstimateMeanRiskOnly)
                {
                    profileArrays = SaturatingAdd(profileArrays,
                        SaturatingProduct(componentCount, MaxFailureModeCount(),
                            SaturatingAdd(SaturatingProduct(5L, typeCount), 2L)));
                }
                long profileBytes = SaturatingProduct(realizations, maximumNodesPerComponent,
                    profileArrays, bytesPerDouble);
                items.Add(new ResourceEstimateItem("Risk profile arrays", profileBytes, 0d, true,
                    ResourceSeverity.Informational,
                    $"Enabled risk profiles can retain up to about {FormatBytes(profileBytes)} across component and mean-pass failure-mode scopes."));
            }

            if (componentCount > 1 && _options.SystemRiskMethod == SystemRiskType.AdditiveRiskMethod)
            {
                long latticeBytes = SaturatingProduct(_options.SystemConvolutionPoints,
                    SaturatingAdd(componentCount, 1L), bytesPerDouble, concurrency);
                items.Add(new ResourceEstimateItem("System convolution lattice", latticeBytes, 0, false, ResourceSeverity.Informational,
                    $"The additive system convolution holds about {FormatBytes(latticeBytes)} of lattice."));
            }

            return new ResourceEstimate(items, concurrency, evaluationFloor, evaluationCeiling, genzEvaluations);
        }

        /// <summary>
        /// The Gauss–Kronrod node count per accepted interval (G10K21).
        /// </summary>
        private const int GaussKronrodNodes = 21;

        /// <summary>
        /// The mean dropped mass above which a truncated combination enumeration is reported as an
        /// error rather than a warning.
        /// </summary>
        private const double TruncatedCombinationResidualLimit = 1e-3;

        /// <summary>
        /// The largest dimension the VEGAS integrator accepts, and therefore the largest component
        /// count the joint system risk method can carry. The additive method has no equivalent
        /// limit.
        /// </summary>
        /// <remarks>
        /// VEGAS stratifies (calls/2)^(1/D) strata per axis, which reaches one around fifteen
        /// components; beyond that the joint method is adaptive importance sampling over the
        /// correlated hazards, which stays correct but resolves the tail less sharply per
        /// evaluation.
        /// </remarks>
        private const int VegasMaxDimensions = 50;

        /// <summary>
        /// The stratified hazard levels the competing-risks incidence pre-processing spans.
        /// </summary>
        private const int CompetingIncidenceBins = 200;

        /// <summary>
        /// The largest projected failure-mode count across the analysis's components, at least one.
        /// </summary>
        /// <returns>The failure-mode count.</returns>
        private long MaxFailureModeCount()
        {
            long most = 1;
            for (int i = 0; i < _components.Count; i++)
            {
                int count = _components[i].FailureModes.Count;
                if (count > most) most = count;
            }
            return most;
        }

        /// <summary>
        /// Formats a byte count for a caller-facing message.
        /// </summary>
        /// <param name="bytes">The byte count.</param>
        /// <returns>The formatted size.</returns>
        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024d * 1024d):F1} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024d * 1024d):F0} MB";
            if (bytes >= 1024) return $"{bytes / 1024d:F0} KB";
            return $"{bytes} bytes";
        }

        /// <summary>Multiplies nonnegative integer estimates without wrapping.</summary>
        /// <param name="factors">The factors.</param>
        /// <returns>The product, saturated at <see cref="long.MaxValue"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the factor array is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when a factor is negative.</exception>
        private static long SaturatingProduct(params long[] factors)
        {
            if (factors == null) throw new ArgumentNullException(nameof(factors));
            long product = 1L;
            for (int i = 0; i < factors.Length; i++)
            {
                if (factors[i] < 0L) throw new ArgumentOutOfRangeException(nameof(factors));
                if (factors[i] == 0L) return 0L;
                if (product > long.MaxValue / factors[i]) return long.MaxValue;
                product *= factors[i];
            }
            return product;
        }

        /// <summary>Adds nonnegative integer estimates without wrapping.</summary>
        /// <param name="left">The first estimate.</param>
        /// <param name="right">The second estimate.</param>
        /// <returns>The sum, saturated at <see cref="long.MaxValue"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when either value is negative.</exception>
        private static long SaturatingAdd(long left, long right)
        {
            if (left < 0L) throw new ArgumentOutOfRangeException(nameof(left));
            if (right < 0L) throw new ArgumentOutOfRangeException(nameof(right));
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        /// <summary>Multiplies nonnegative floating-point work estimates without overflowing.</summary>
        /// <param name="factors">The factors.</param>
        /// <returns>The finite product, saturated at <see cref="double.MaxValue"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the factor array is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when a factor is negative or NaN.</exception>
        private static double SaturatingProductDouble(params double[] factors)
        {
            if (factors == null) throw new ArgumentNullException(nameof(factors));
            double product = 1d;
            for (int i = 0; i < factors.Length; i++)
            {
                if (double.IsNaN(factors[i]) || factors[i] < 0d)
                    throw new ArgumentOutOfRangeException(nameof(factors));
                if (factors[i] == 0d) return 0d;
                if (double.IsPositiveInfinity(factors[i]) || product > double.MaxValue / factors[i])
                    return double.MaxValue;
                product *= factors[i];
            }
            return product;
        }

        /// <summary>Adds nonnegative floating-point work estimates without overflowing.</summary>
        /// <param name="left">The first estimate.</param>
        /// <param name="right">The second estimate.</param>
        /// <returns>The finite sum, saturated at <see cref="double.MaxValue"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when either value is negative or NaN.</exception>
        private static double SaturatingAddDouble(double left, double right)
        {
            if (double.IsNaN(left) || left < 0d) throw new ArgumentOutOfRangeException(nameof(left));
            if (double.IsNaN(right) || right < 0d) throw new ArgumentOutOfRangeException(nameof(right));
            if (double.IsPositiveInfinity(left) || double.IsPositiveInfinity(right) || left > double.MaxValue - right)
                return double.MaxValue;
            return left + right;
        }

        /// <summary>
        /// Strictly matches every component's failure and non-failure paths against the declared
        /// consequence-type axis: each path must carry exactly one
        /// consequence function per declared type, in declared order, and a non-blank declared
        /// label or unit must agree (ordinal, case-insensitive) with a non-blank function label
        /// at the same position — blank on either side is a wildcard. Risk mode only: a
        /// reliability model carries no consequences, so the axis is inert there. Null function
        /// entries are skipped here — they are already component-level errors.
        /// </summary>
        /// <param name="messages">The message sink.</param>
        private void ValidateConsequenceTypeAxis(List<string> messages)
        {
            if (_options.Mode != RiskAnalysisMode.Risk) return;

            int declaredCount = 1 + _additionalConsequenceTypes.Count;
            var labels = new string[declaredCount];
            var units = new string[declaredCount];
            labels[0] = _specifiedConsequence;
            units[0] = _consequenceUnit;
            for (int k = 1; k < declaredCount; k++)
            {
                labels[k] = _additionalConsequenceTypes[k - 1].SpecifiedConsequence;
                units[k] = _additionalConsequenceTypes[k - 1].ConsequenceUnit;
            }

            for (int i = 0; i < _components.Count; i++)
            {
                var component = _components[i];
                var modes = component.FailureModes;
                for (int m = 0; m < modes.Count; m++)
                {
                    string pathName = modes[m].IsNonFailureMode ? "the non-failure path" : "a failure path";
                    var consequences = modes[m].ConsequenceFunctions;
                    if (consequences.Count != declaredCount)
                    {
                        messages.Add($"Error: On {pathName} of system component '{component.Name}', {consequences.Count} consequence function(s) are carried but the analysis declares {declaredCount} consequence type(s); every failure and non-failure path must carry the declared consequence-type axis.");
                        continue;
                    }
                    for (int k = 0; k < declaredCount; k++)
                    {
                        var function = consequences[k];
                        if (function is null) continue;
                        if (LabelsMismatch(labels[k], function.SpecifiedConsequence))
                        {
                            messages.Add($"Error: The consequence function at position {k} of {pathName} of system component '{component.Name}' is typed '{function.SpecifiedConsequence}' but the analysis declares '{labels[k]}' at that position.");
                        }
                        if (LabelsMismatch(units[k], function.ConsequenceUnit))
                        {
                            messages.Add($"Error: The consequence function at position {k} of {pathName} of system component '{component.Name}' has unit '{function.ConsequenceUnit}' but the analysis declares '{units[k]}' at that position.");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Determines whether two axis labels disagree: both must be non-blank and differ under
        /// an ordinal case-insensitive comparison (the graph alignment comparison semantics) —
        /// blank on either side is a wildcard.
        /// </summary>
        /// <param name="declared">The declared axis label.</param>
        /// <param name="actual">The function's label.</param>
        /// <returns>True when both are non-blank and differ.</returns>
        private static bool LabelsMismatch(string declared, string actual)
        {
            return !string.IsNullOrEmpty(declared) && !string.IsNullOrEmpty(actual) &&
                !string.Equals(declared, actual, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Warns when the components' driving hazards disagree on non-blank axis labels: one
        /// analysis models one hazard axis (the hazard-type companion of the declared
        /// consequence-type axis), so a label mismatch usually means a mis-assembled system.
        /// Advisory only — labels are unhashed display metadata.
        /// </summary>
        /// <param name="messages">The message sink.</param>
        private void ValidateHazardAxisConsistency(List<string> messages)
        {
            if (_components.Count < 2) return;

            string label = string.Empty, unit = string.Empty, labelOwner = string.Empty, unitOwner = string.Empty;
            for (int i = 0; i < _components.Count; i++)
            {
                var hazard = _components[i].HazardFunction;
                if (hazard == null) continue;
                if (!string.IsNullOrEmpty(hazard.SpecifiedHazard))
                {
                    if (string.IsNullOrEmpty(label))
                    {
                        label = hazard.SpecifiedHazard;
                        labelOwner = _components[i].Name;
                    }
                    else if (!string.Equals(label, hazard.SpecifiedHazard, StringComparison.OrdinalIgnoreCase))
                    {
                        messages.Add($"Warning: System component '{_components[i].Name}' drives on hazard '{hazard.SpecifiedHazard}' but component '{labelOwner}' drives on '{label}'; the components of one analysis should share the driving hazard axis.");
                    }
                }
                if (!string.IsNullOrEmpty(hazard.HazardUnit))
                {
                    if (string.IsNullOrEmpty(unit))
                    {
                        unit = hazard.HazardUnit;
                        unitOwner = _components[i].Name;
                    }
                    else if (!string.Equals(unit, hazard.HazardUnit, StringComparison.OrdinalIgnoreCase))
                    {
                        messages.Add($"Warning: System component '{_components[i].Name}' hazard unit '{hazard.HazardUnit}' differs from component '{unitOwner}' unit '{unit}'; the components of one analysis should share the driving hazard axis.");
                    }
                }
            }
        }

        /// <summary>
        /// Determines whether the options' hazard correlation matrix is usable for the joint
        /// method: present, one row per component, and positive definite (Cholesky) — the same
        /// check the component applies to its failure-mode matrix.
        /// </summary>
        /// <returns>True when the matrix is usable.</returns>
        private bool IsHazardCorrelationMatrixValid()
        {
            var matrix = _options.HazardCorrelationMatrix;
            if (matrix == null || matrix.GetLength(0) != _components.Count || matrix.GetLength(1) != _components.Count)
            {
                return false;
            }
            try
            {
                return new CholeskyDecomposition(new Matrix(matrix)).IsPositiveDefinite;
            }
            catch (Exception)
            {
                // A decomposition failure means the matrix is not usable — exactly what this
                // check reports; the validation message carries the remedy.
                return false;
            }
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when validation reports errors.</exception>
        /// <remarks>
        /// The run sequence (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.3): the
        /// cancelable starting event; the
        /// validation gate (which throws — an invalid analysis is a caller error, not a run
        /// outcome); a fresh cancellation source linked with the caller's token; occurrence-index
        /// assignment and the content-based per-component seed walk; then the mean-only pass or
        /// the parallel full-uncertainty ensemble with percentile post-processing. Validation,
        /// runtime faults, and cancellation propagate through the returned task after
        /// <see cref="IAnalysis.AnalysisCompleted"/> has notified observers.
        /// </remarks>
        public override async Task RunAsync(SafeProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
        {
            var startingArgs = new CancelEventArgs();
            if (!TryBeginRun())
            {
                var concurrentError = new InvalidOperationException("This analysis is already running.");
                OnAnalysisCompleted(new AnalysisRunCompletedEventArgs(wasCanceled: false, succeeded: false, error: concurrentError));
                throw concurrentError;
            }

            AnalysisRunCompletedEventArgs? completion = null;
            try
            {
                OnAnalysisStarting(startingArgs);
                if (startingArgs.Cancel)
                {
                    throw new OperationCanceledException("The analysis was canceled by an AnalysisStarting handler.");
                }

                ClearPublishedState();

                var (isValid, validationMessages) = Validate();
                if (!isValid)
                {
                    throw new InvalidOperationException(string.Join(Environment.NewLine, validationMessages));
                }

                var runContext = RiskAnalysisRunContext.Capture(
                    _authorComponents,
                    _authorOptions,
                    _additionalConsequenceTypes,
                    _specifiedConsequence,
                    _consequenceUnit,
                    PinnedSamplerSeeds);
                var pinned = runContext.PinnedSamplerSeeds;
                _components = runContext.Components;
                _options = runContext.Options;
                _runAdditionalConsequenceTypes = runContext.AdditionalConsequenceTypes;
                _runSpecifiedConsequence = runContext.SpecifiedConsequence;
                _runConsequenceUnit = runContext.ConsequenceUnit;

                var token = ResetCancellationToken(cancellationToken);
                AnalysisRunPublication? publication = null;

                // Snapshot the run-input epistemic weights with the rest of the author state:
                // the running computation must not observe a mid-run reassignment. The
                // retention selectors and the fractile pins snapshot the same way.
                double[]? runRealizationWeights = _realizationWeights == null
                    ? null
                    : (double[])_realizationWeights.Clone();
                FractilePin[]? runFractilePins = _fractilePins == null
                    ? null
                    : (FractilePin[])_fractilePins.Clone();
                _runRetainRealizations = _retainRealizations;
                _runRetainedDetailIndex = _retainedIntegrationDetailIndex;

                // The exact logic-tree enumeration derivation, gated race-safe over the captured
                // run state (Validate() checked the author state, but every runtime input can
                // move between that gate and this snapshot's use): discover the axes on the run
                // clones, size the isolated options snapshot at N = K·M — the author options are
                // untouched, and the manifest records the effective run — and take ownership of
                // the realization-weight vector with the exact branch-weight products.
                int? runLogicTreeRealizations = _logicTreeEnumerationRealizations;
                LogicTreeEnumerationMap? logicTreeMap = null;
                if (runLogicTreeRealizations is int perCombination)
                {
                    var logicTree = AnalyzeLogicTreeConfiguration(_components, perCombination,
                        runFractilePins, runRealizationWeights, _options.EstimateMeanRiskOnly);
                    if (logicTree.Errors.Count > 0)
                    {
                        throw new InvalidOperationException(
                            "The logic-tree enumeration is invalid. " + string.Join(" ", logicTree.Errors));
                    }
                    logicTreeMap = BuildLogicTreeMap(logicTree.Discovery.Axes, perCombination);
                    _options.Realizations = logicTreeMap.RealizationCount;
                    runRealizationWeights = BuildLogicTreeRealizationWeights(logicTreeMap);
                }

                await Task.Run(() =>
                {
                    RunWorkerObserver?.Invoke();
                    // The content-based seed walk
                    // (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.5.4): occurrence indices
                    // disambiguate identical-content components, and each component's functions
                    // are seeded from (analysis seed, component hash, occurrence index).
                    SystemComponent.AssignOccurrenceIndices(_components);

                    // The race-safe fractile-pin classification over the run clones: Validate()
                    // gated the author state, but the property can move between that gate and
                    // this snapshot's use. Applied AFTER seeding at each walk position, pins can
                    // never move a captured seed; the reconciliation below refuses to run when a
                    // conditioning pin could not reach its function.
                    Dictionary<Guid, double>? fractilePinMap = null;
                    HashSet<Guid>? expectedPinIds = null;
                    HashSet<Guid>? appliedPinIds = null;
                    if (runFractilePins != null && runFractilePins.Length > 0 && !_options.EstimateMeanRiskOnly)
                    {
                        var pinErrors = new List<string>();
                        var pinWarnings = new List<string>();
                        fractilePinMap = new Dictionary<Guid, double>();
                        expectedPinIds = new HashSet<Guid>();
                        ClassifyFractilePins(runFractilePins, pinErrors, pinWarnings, fractilePinMap, expectedPinIds);
                        if (pinErrors.Count > 0)
                        {
                            throw new InvalidOperationException("The fractile pins are invalid. " + string.Join(" ", pinErrors));
                        }
                        appliedPinIds = new HashSet<Guid>();
                    }

                    // The shared-epistemic-variable columns (one per distinct variable named by
                    // any component's epistemic composites), derived from the run seed and the
                    // variable name alone, and applied inside each bound composite's own
                    // SetupSampler by post-seeding overwrite — zero walk-ordinal movement, so
                    // captured seed maps stay valid and no other function's stream can move.
                    var capturedSeeds = new List<int[]>(_components.Count);
                    var contentHashes = new byte[_components.Count][];
                    HashSet<string>? appliedVariables = null;
                    HashSet<Guid>? appliedFunctionIds = null;
                    IDisposable? sharingScope;
                    if (logicTreeMap != null)
                    {
                        // The enumeration's forcing scope: constant-per-block columns in place of
                        // the seed-derived shared draws, with applied-key sinks so the walk can
                        // prove after seeding that every axis was reached.
                        BuildLogicTreeColumns(logicTreeMap, out var forcedNameColumns, out var forcedIdColumns);
                        appliedVariables = new HashSet<string>(StringComparer.Ordinal);
                        appliedFunctionIds = new HashSet<Guid>();
                        sharingScope = EpistemicSharingScope.Enter(forcedNameColumns, forcedIdColumns, appliedVariables, appliedFunctionIds);
                    }
                    else
                    {
                        var epistemicColumns = BuildEpistemicSharingColumns(_options.Realizations);
                        sharingScope = epistemicColumns == null ? null : EpistemicSharingScope.Enter(epistemicColumns);
                    }
                    using (sharingScope)
                    {
                        for (int i = 0; i < _components.Count; i++)
                        {
                            contentHashes[i] = _components[i].CanonicalHash();
                            int componentSeed = SeedHelpers.HashCombine(_options.PRNGSeed, contentHashes[i], _components[i].OccurrenceIndex);
                            var scribe = new SeedScribe(pinned?.ComponentSeeds[i]);
                            capturedSeeds.Add(_components[i].SetupSamplers(_options.Realizations, componentSeed, _options.SamplingScheme, scribe, fractilePinMap, appliedPinIds));
                        }
                    }

                    if (expectedPinIds != null)
                    {
                        foreach (var pinId in expectedPinIds)
                        {
                            if (appliedPinIds!.Contains(pinId)) continue;
                            throw new InvalidOperationException(
                                $"Fractile pin {pinId:D} could not be applied: the component walk does not sample that function directly (a function referenced only inside a tree's own sampling clones cannot be conditioned), so the run would silently not condition on it.");
                        }
                    }

                    if (logicTreeMap != null)
                    {
                        // The enumeration reconciliation (the fractile-pin refusal pattern): any
                        // axis the seeding walk did not consume would leave its branch selection
                        // sampled, silently voiding the exactness contract — refuse instead.
                        for (int a = 0; a < logicTreeMap.Axes.Count; a++)
                        {
                            var axis = logicTreeMap.Axes[a];
                            bool applied = axis.IsSharedVariable
                                ? appliedVariables!.Contains(axis.Name)
                                : appliedFunctionIds!.Contains(axis.FunctionId!.Value);
                            if (!applied)
                            {
                                throw new InvalidOperationException(
                                    $"Logic-tree axis '{axis.Name}' was not reached by the sampler walk (a composite sampled only inside a tree's own setup clones cannot be forced), so the run would silently not enumerate it.");
                            }
                        }
                    }

                    // The canonical component order (hashes sorted): the additive convolution
                    // associates in it, and the system seed base folds in it (§7.3 erratum) —
                    // so declaration order can never move the convolved curves or the VEGAS
                    // stream identity.
                    // The joint certificate's index-owned capture arrays (recording-pass
                    // values the integrator otherwise discards); null on every other method.
                    if (_options.SystemRiskMethod == SystemRiskType.JointRiskMethod && !_options.EstimateMeanRiskOnly)
                    {
                        _jointCertificateChiSquared = new double[_options.Realizations];
                        _jointCertificateRelativeSe = new double[_options.Realizations];
                        Array.Fill(_jointCertificateChiSquared, double.NaN);
                        Array.Fill(_jointCertificateRelativeSe, double.NaN);
                    }

                    var order = new int[_components.Count];
                    for (int i = 0; i < order.Length; i++) order[i] = i;
                    Array.Sort(order, (a, b) =>
                    {
                        int hashComparison = ByteArrayComparer.Instance.Compare(contentHashes[a], contentHashes[b]);
                        return hashComparison != 0
                            ? hashComparison
                            : _components[a].OccurrenceIndex.CompareTo(_components[b].OccurrenceIndex);
                    });
                    _additiveConvolutionOrder = order;

                    int systemSeed = _options.PRNGSeed;
                    for (int i = 0; i < order.Length; i++)
                    {
                        systemSeed = SeedHelpers.HashCombine(systemSeed, contentHashes[order[i]], _components[order[i]].OccurrenceIndex);
                    }
                    _jointSeedBase = pinned?.JointSeedBase ?? systemSeed;

                    // Every run captures its effective seed map (§5.5.8) — the baseline a
                    // perturbation study pins onto its perturbed runs. Capturing an applied map
                    // reproduces it, so capture(apply(map)) is the map itself.
                    var captured = new SamplerSeedMap(capturedSeeds, _jointSeedBase);

                    // The declared per-type consequence thresholds: entry k applies
                    // to additional consequence type k at every measure site this run.
                    _runAdditionalThresholds = new double[RunAdditionalConsequenceTypes.Count];
                    for (int i = 0; i < _runAdditionalThresholds.Length; i++)
                    {
                        _runAdditionalThresholds[i] = RunAdditionalConsequenceTypes[i].ConsequenceThreshold;
                    }

                    PrepareJointSystem(token);

                    // The race-safe weight guard: Validate() already gated these rules on the
                    // author state, but the property can move between that gate and this
                    // snapshot's use, so the captured run state re-verifies loudly.
                    if (runRealizationWeights != null)
                    {
                        if (_options.EstimateMeanRiskOnly)
                        {
                            throw new InvalidOperationException(
                                "Realization weights apply to the full-uncertainty ensemble; clear RealizationWeights or disable the mean-only run option.");
                        }
                        string? weightReason = EnsembleResults.DescribeInvalidWeights(
                            runRealizationWeights, _options.Realizations, out _);
                        if (weightReason != null)
                        {
                            throw new InvalidOperationException($"The realization weights are invalid. {weightReason}");
                        }
                    }

                    publication = _options.EstimateMeanRiskOnly
                        ? RunMeanOnly(progressReporter, token)
                        : RunFullUncertainty(progressReporter, token, runRealizationWeights);
                    publication.CapturedSeeds = captured;
                    publication.LogicTree = logicTreeMap;
                    var manifest = AnalysisRunManifest.Create(_options, _components, contentHashes, order,
                        RunSpecifiedConsequence, RunConsequenceUnit, RunAdditionalConsequenceTypes,
                        captured, runRealizationWeights);
                    AttachManifest(publication, manifest);
                }, token).ConfigureAwait(false);

                Publish(publication ?? throw new InvalidOperationException("The analysis completed without a staged result."));
                completion = new AnalysisRunCompletedEventArgs(wasCanceled: false, succeeded: true, error: null);
            }
            catch (OperationCanceledException)
            {
                completion = new AnalysisRunCompletedEventArgs(wasCanceled: true, succeeded: false, error: null);
                ClearPublishedState();
                throw;
            }
            catch (Exception ex)
            {
                completion = new AnalysisRunCompletedEventArgs(wasCanceled: false, succeeded: false, error: ex);
                ClearPublishedState();
                throw;
            }
            finally
            {
                _components = _authorComponents;
                _options = _authorOptions;
                _runAdditionalConsequenceTypes = null;
                _runSpecifiedConsequence = null;
                _runConsequenceUnit = null;
                EndRun();
                OnAnalysisCompleted(completion ?? new AnalysisRunCompletedEventArgs(wasCanceled: false, succeeded: false, error: new InvalidOperationException("The analysis exited without a completion state.")));
            }
        }

        /// <summary>
        /// Re-assembles the mean, median, and confidence-bound curve sets from the retained
        /// realization ensemble under new epistemic weights — post-hoc weighted re-banding with
        /// no re-simulation: the retained realizations are the run's own sampled state, so the
        /// returned bands are exactly what a run with these weights as its run input would have
        /// published. The published results are never modified; fresh staged realizations are
        /// returned.
        /// </summary>
        /// <param name="weights">
        /// The realization weights (one per retained realization, the shared weight rule), or
        /// null for the unweighted assembly.
        /// </param>
        /// <returns>The freshly assembled lower, upper, median, and mean realizations.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no retained ensemble exists — enable <see cref="RetainRealizations"/> and run first.</exception>
        /// <exception cref="ArgumentException">Thrown when the weight vector fails the shared weight rule.</exception>
        /// <remarks>
        /// Valid for the model and options state that produced the retained ensemble (the same
        /// contract every diagnostic over stored results carries); the assembly reads the
        /// configured confidence-interval width and output length.
        /// </remarks>
        public (SystemRealization? Lower, SystemRealization? Upper, SystemRealization? Median, SystemRealization? Mean)
            ReassemblePercentileBands(IList<double>? weights = null)
        {
            var retained = _retainedRealizations
                ?? throw new InvalidOperationException("No retained realization ensemble exists. Enable RetainRealizations and run the analysis first.");
            double[]? vector = null;
            if (weights != null)
            {
                vector = new double[weights.Count];
                for (int i = 0; i < vector.Length; i++) vector[i] = weights[i];
                string? reason = EnsembleResults.DescribeInvalidWeights(vector, retained.Length, out _);
                if (reason != null)
                {
                    throw new ArgumentException($"The realization weights are invalid. {reason}", nameof(weights));
                }
            }
            return PostProcessUncertainty(retained, vector, CancellationToken.None);
        }

        /// <summary>
        /// Estimates the secondary-axis discretization error of one bivariate component: the
        /// mean pass is integrated at the configured, halved, and quartered conditional bin
        /// counts, and the annual failure probability and the mean incremental risk per
        /// consequence type are Richardson-extrapolated at the trapezoid rule's second-order
        /// rate — the a-posteriori answer to "are the conditional bins fine enough", with the
        /// observed convergence ratio as the regime check.
        /// </summary>
        /// <param name="componentIndex">The component whose discretization is probed.</param>
        /// <returns>
        /// The diagnostic, or null when the analysis is invalid, the component's hazard is not
        /// bivariate, or the configured count cannot support three levels (the quartered count
        /// must stay at or above the three-bin floor).
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an out-of-range component index.</exception>
        /// <remarks>
        /// The evaluations run on diagnostic mean snapshots at the reduced counts — the stored
        /// bin count, the canonical hash, and every seed are untouched, and nothing about the
        /// query is serialized. The call re-runs the component's sampler setup (a subsequent
        /// <see cref="RunAsync"/> re-seeds itself at run start). The extrapolation assumes the
        /// second-order regime; an observed ratio far from four — the documented conditional
        /// tail-concentration mechanism — marks the estimate as indicative only, and more bins
        /// or the collapse arrangement deserve consideration.
        /// </remarks>
        public SecondaryDiscretizationDiagnostic? EstimateSecondaryDiscretizationError(int componentIndex)
        {
            if (componentIndex < 0 || componentIndex >= _components.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(componentIndex), "The component index is out of range.");
            }
            if (!Validate().IsValid) return null;
            var component = _components[componentIndex];
            if (component.HazardFunction is not BivariateHazard hazard) return null;
            int bins = hazard.SecondaryIntegrationBins;
            int halfBins = bins / 2;
            int quarterBins = bins / 4;
            if (quarterBins < 3) return null;

            SystemComponent.AssignOccurrenceIndices(_components);
            int seed = SeedHelpers.HashCombine(_options.PRNGSeed, component.CanonicalHash(), component.OccurrenceIndex);
            using (EnterEpistemicScopeForRederivation(_options.Realizations))
            {
                component.SetupSamplers(_options.Realizations, seed, _options.SamplingScheme, null, BuildCurrentFractilePinMap(), null);
            }

            var additionalThresholds = new double[RunAdditionalConsequenceTypes.Count];
            for (int k = 0; k < additionalThresholds.Length; k++)
            {
                additionalThresholds[k] = RunAdditionalConsequenceTypes[k].ConsequenceThreshold;
            }

            var levels = new[] { bins, halfBins, quarterBins };
            var failureProbabilities = new double[levels.Length];
            double[][]? meanRisk = null;
            for (int level = 0; level < levels.Length; level++)
            {
                var sampled = component.SampleWithConditionalBins(levels[level]);
                int typeCount = sampled.ConsequenceTypeCount;
                meanRisk ??= new double[levels.Length][];
                meanRisk[level] = new double[typeCount];

                var componentRealization = new ComponentRealization(sampled.FailureModeCount);
                componentRealization.EnsureAdditionalCurves(typeCount - 1);
                componentRealization.SetMeasureOptions(_options.RiskMeasures);
                var realization = new SystemRealization(new List<ComponentRealization> { componentRealization });
                realization.EnsureAdditionalCurves(typeCount - 1);
                var flags = new RiskComputeFlags();
                using (var ledger = IntegrateComponent(sampled, componentRealization, realization, flags, -1, CancellationToken.None))
                {
                    componentRealization.ApplyRecordedMass(ledger);
                    componentRealization.CreateCurves(_options.LECOutputLength);
                }
                componentRealization.ComputeRiskMeasures(_options.ConsequenceThreshold, _options.Alpha,
                    component.HazardThreshold, additionalThresholds);

                failureProbabilities[level] = componentRealization.Curves.Fail.TotalProbability;
                meanRisk[level][0] = componentRealization.Curves.Excess.Mean;
                for (int k = 1; k < typeCount; k++)
                {
                    meanRisk[level][k] = componentRealization.AdditionalCurves[k - 1].Excess.Mean;
                }
            }

            var riskEstimates = new List<DiscretizationEstimate>(meanRisk![0].Length);
            for (int k = 0; k < meanRisk[0].Length; k++)
            {
                riskEstimates.Add(new DiscretizationEstimate(meanRisk[0][k], meanRisk[1][k], meanRisk[2][k]));
            }
            return new SecondaryDiscretizationDiagnostic(component.Name, bins, halfBins, quarterBins,
                new DiscretizationEstimate(failureProbabilities[0], failureProbabilities[1], failureProbabilities[2]),
                riskEstimates);
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the analysis configuration: the metadata, the estimated flag, and the
        /// options child — nothing else (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §8).
        /// Components and results travel
        /// through the constructor; the consuming layer persists them separately.
        /// </summary>
        /// <returns>The serialized configuration.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(RiskAnalysis));
            element.SetAttributeValue(nameof(Name), _name);
            element.SetAttributeValue(nameof(Description), _description);
            element.SetAttributeValue(nameof(SpecifiedConsequence), _specifiedConsequence);
            element.SetAttributeValue(nameof(ConsequenceUnit), _consequenceUnit);
            element.SetAttributeValue(nameof(IsEstimated), _isEstimated);
            element.Add(_authorOptions.ToXElement());
            var typesElement = new XElement(nameof(AdditionalConsequenceTypes));
            for (int i = 0; i < _additionalConsequenceTypes.Count; i++)
            {
                typesElement.Add(_additionalConsequenceTypes[i].ToXElement());
            }
            element.Add(typesElement);
            return element;
        }

        #endregion

        #region Private Helpers — Run Paths

        /// <summary>
        /// Clears every publicly visible artifact from a prior or failed run.
        /// </summary>
        private void ClearPublishedState()
        {
            _riskResults = null;
            _meanRiskResults = null;
            _medianRiskResults = null;
            _lowerRiskResults = null;
            _upperRiskResults = null;
            _retainedRealizations = null;
            _retainedIntegrationDetail = null;
            _logicTreeEnumeration = null;
            _jointCertificate = null;
            _jointCertificateChiSquared = null;
            _jointCertificateRelativeSe = null;
            _jointCertificateMeanPassCaptured = false;
            _jointCertificateMeanPassChiSquared = double.NaN;
            _jointCertificateMeanPassRelativeSe = double.NaN;
            CapturedSamplerSeeds = null;
            _computationWarnings.Clear();
            _computationDiagnostics.Clear();
            IsEstimated = false;
            RaisePropertyChange(nameof(RiskResults));
            RaisePropertyChange(nameof(MeanRiskResults));
            RaisePropertyChange(nameof(MedianRiskResults));
            RaisePropertyChange(nameof(LowerRiskResults));
            RaisePropertyChange(nameof(UpperRiskResults));
            RaisePropertyChange(nameof(RetainedRealizations));
            RaisePropertyChange(nameof(RetainedIntegrationDetail));
            RaisePropertyChange(nameof(LogicTreeEnumeration));
            RaisePropertyChange(nameof(JointCertificate));
            RaisePropertyChange(nameof(ComputationWarnings));
            RaisePropertyChange(nameof(ComputationDiagnostics));
        }

        /// <summary>
        /// Publishes a complete successful run after every computation and invariant has passed.
        /// All backing references are assigned before any result notification is raised.
        /// </summary>
        /// <param name="publication">The staged result envelope.</param>
        private void Publish(AnalysisRunPublication publication)
        {
            _riskResults = publication.Results;
            _meanRiskResults = publication.Mean;
            _medianRiskResults = publication.Median;
            _lowerRiskResults = publication.Lower;
            _upperRiskResults = publication.Upper;
            _retainedRealizations = publication.RetainedRealizations;
            _retainedIntegrationDetail = publication.RetainedIntegrationDetail;
            _logicTreeEnumeration = publication.LogicTree;
            _jointCertificate = AssembleJointCertificate();
            CapturedSamplerSeeds = publication.CapturedSeeds;
            _computationWarnings.Clear();
            _computationDiagnostics.Clear();
            _computationDiagnostics.AddRange(publication.Diagnostics);
            for (int i = 0; i < publication.Diagnostics.Count; i++)
            {
                _computationWarnings.Add(publication.Diagnostics[i].ToLegacyMessage());
            }
            IsEstimated = true;
            RaisePropertyChange(nameof(RiskResults));
            RaisePropertyChange(nameof(MeanRiskResults));
            RaisePropertyChange(nameof(MedianRiskResults));
            RaisePropertyChange(nameof(LowerRiskResults));
            RaisePropertyChange(nameof(UpperRiskResults));
            RaisePropertyChange(nameof(RetainedRealizations));
            RaisePropertyChange(nameof(RetainedIntegrationDetail));
            RaisePropertyChange(nameof(LogicTreeEnumeration));
            RaisePropertyChange(nameof(JointCertificate));
            RaisePropertyChange(nameof(ComputationWarnings));
            RaisePropertyChange(nameof(ComputationDiagnostics));
        }

        /// <summary>
        /// Assembles the joint convergence certificate from the run's recording-pass captures;
        /// null when the run did not use the joint method.
        /// </summary>
        /// <returns>The certificate, or null.</returns>
        private JointConvergenceCertificate? AssembleJointCertificate()
        {
            var chi = _jointCertificateChiSquared;
            if (!_jointCertificateMeanPassCaptured && chi == null) return null;
            int count = 0;
            double chiSum = 0d;
            double chiMax = double.NaN;
            double seSum = 0d;
            double seMax = double.NaN;
            if (chi != null)
            {
                var se = _jointCertificateRelativeSe!;
                for (int i = 0; i < chi.Length; i++)
                {
                    if (double.IsNaN(chi[i]) || double.IsNaN(se[i])) continue;
                    count++;
                    chiSum += chi[i];
                    seSum += se[i];
                    if (double.IsNaN(chiMax) || chi[i] > chiMax) chiMax = chi[i];
                    if (double.IsNaN(seMax) || se[i] > seMax) seMax = se[i];
                }
            }
            return new JointConvergenceCertificate(count,
                _jointCertificateMeanPassChiSquared, _jointCertificateMeanPassRelativeSe,
                count > 0 ? chiSum / count : double.NaN, chiMax,
                count > 0 ? seSum / count : double.NaN, seMax);
        }

        /// <summary>Attaches one immutable manifest to every persisted root in a staged run.</summary>
        /// <param name="publication">The staged successful run.</param>
        /// <param name="manifest">The deterministic run manifest.</param>
        private static void AttachManifest(AnalysisRunPublication publication, AnalysisRunManifest manifest)
        {
            publication.Results.Manifest = manifest;
            if (publication.Mean != null) publication.Mean.Manifest = manifest;
            if (publication.Median != null) publication.Median.Manifest = manifest;
            if (publication.Lower != null) publication.Lower.Manifest = manifest;
            if (publication.Upper != null) publication.Upper.Manifest = manifest;
        }


        /// <summary>
        /// Invalidates the results when any option changes.
        /// </summary>
        /// <param name="sender">The options instance.</param>
        /// <param name="e">The change arguments.</param>
        private void OptionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            IsEstimated = false;
        }

        /// <summary>Invalidates results when the mutable consequence declaration axis changes.</summary>
        /// <param name="sender">The declaration collection.</param>
        /// <param name="e">The collection change.</param>
        private void AdditionalConsequenceTypesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            IsEstimated = false;
        }

        /// <summary>
        /// The mean-only pass: one realization on the expected input functions (mixture
        /// exposure branches enumerated, never flattened — §6.4.1), published as the mean
        /// results and a single-entry summary ensemble, staged for atomic publication.
        /// </summary>
        /// <param name="progressReporter">The optional progress sink.</param>
        /// <param name="token">The run cancellation token.</param>
        /// <returns>The complete staged run output.</returns>
        private AnalysisRunPublication RunMeanOnly(SafeProgressReporter? progressReporter, CancellationToken token)
        {
            var flags = new RiskComputeFlags();
            var realization = ComputeRealization(-1, flags, token);
            realization.Name = "Mean";

            var ensemble = new EnsembleResults(1);
            ensemble[0] = new SystemRiskResults(realization);
            var publication = new AnalysisRunPublication
            {
                Results = ensemble,
                Mean = realization,
            };
            if (_runRetainedDetailIndex == -1) publication.RetainedIntegrationDetail = realization;
            CollectDiagnostics(flags, publication.Diagnostics, realization);
            progressReporter?.ReportProgress(100d);
            return publication;
        }

        /// <summary>
        /// The full-uncertainty pass: the parallel realization ensemble with index-owned writes,
        /// sequential post-pass reductions (bit-identical at any thread count), percentile
        /// post-processing, and the compact summary ensemble. Optional run-input realization
        /// weights make the percentile assembly and the summary reduction weighted; the sampled
        /// realizations themselves are identical either way — weights never touch a seed.
        /// </summary>
        /// <param name="progressReporter">The optional progress sink.</param>
        /// <param name="token">The run cancellation token.</param>
        /// <param name="realizationWeights">The validated run-input epistemic realization weights, or null for an unweighted run.</param>
        /// <returns>The complete staged run output.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a supplied weight vector no longer matches the run's realization count.</exception>
        private AnalysisRunPublication RunFullUncertainty(SafeProgressReporter? progressReporter, CancellationToken token,
            double[]? realizationWeights)
        {
            int realizationCount = _options.Realizations;
            if (realizationWeights != null && realizationWeights.Length != realizationCount)
            {
                throw new InvalidOperationException(
                    $"The realization weight count ({realizationWeights.Length}) must equal the realization count ({realizationCount}).");
            }
            var realizations = new SystemRealization[realizationCount];
            var summaries = new SystemRiskResults[realizationCount];
            var flagsPerRealization = new RiskComputeFlags[realizationCount];
            long completed = 0;

            var parallelOptions = new ParallelOptions { CancellationToken = token };
            if (MaximumDegreeOfParallelismOverride.HasValue)
                parallelOptions.MaxDegreeOfParallelism = MaximumDegreeOfParallelismOverride.Value;
            Parallel.For(0, realizationCount, parallelOptions, index =>
            {
                EnsembleWorkerObserver?.Invoke();
                var flags = new RiskComputeFlags();
                var realization = ComputeRealization(index, flags, token);
                realizations[index] = realization;
                summaries[index] = new SystemRiskResults(realization);
                flagsPerRealization[index] = flags;
                long done = Interlocked.Increment(ref completed);
                progressReporter?.ReportProgress(done * 100d / realizationCount);
            });

            // Sequential post-pass reductions: warning flags and ensemble extents. In-loop
            // shared updates would be racy (the v1.0 defect) and break thread-count bit-identity.
            var mergedFlags = new RiskComputeFlags();
            for (int i = 0; i < realizationCount; i++)
            {
                mergedFlags.MergeWith(flagsPerRealization[i]);
            }

            var percentiles = PostProcessUncertainty(realizations, realizationWeights, token);

            var ensemble = new EnsembleResults(realizationCount);
            for (int i = 0; i < realizationCount; i++)
            {
                ensemble[i] = summaries[i];
            }

            // The run-input weights become the ensemble's stored weights — the single
            // authoritative copy the summary reduction below and every later re-reduction read.
            ensemble.RealizationWeights = realizationWeights;

            // The scalar-measure percentile summary and convergence diagnostics:
            // curves carry bands through the percentile realizations; the scalar catalog gets
            // its intervals here, reduced from the stored per-realization summaries.
            ensemble.Summary = ensemble.ComputeSummary(_options.ConfidenceIntervalWidth);
            if (ensemble.Summary != null && _options.TolerableRiskCriteria.Count > 0)
            {
                ensemble.Summary.TolerableRiskConfidence =
                    EvaluateTolerableRiskCriteria(_options.TolerableRiskCriteria, summaries, realizationWeights);
            }
            var publication = new AnalysisRunPublication
            {
                Results = ensemble,
                Mean = percentiles.Mean,
                Median = percentiles.Median,
                Lower = percentiles.Lower,
                Upper = percentiles.Upper,
            };
            if (_runRetainRealizations) publication.RetainedRealizations = realizations;
            if (_runRetainedDetailIndex is int detailIndex && detailIndex >= 0 && detailIndex < realizationCount)
            {
                publication.RetainedIntegrationDetail = realizations[detailIndex];
            }
            CollectDiagnostics(mergedFlags, publication.Diagnostics, publication.Mean);
            return publication;
        }

        /// <summary>
        /// Translates merged computational flags into structured diagnostics and validates the
        /// mean realization's exhaustive mass.
        /// </summary>
        /// <param name="flags">The merged flags.</param>
        /// <param name="diagnostics">The staged diagnostic sink.</param>
        /// <param name="mean">The staged mean realization, or null when unavailable.</param>
        /// <exception cref="InvalidOperationException">Thrown when a computation diagnostic is an error.</exception>
        private void CollectDiagnostics(RiskComputeFlags flags, List<ComputationDiagnostic> diagnostics,
            SystemRealization? mean)
        {
            if (flags.HasNegativeFailureConsequence)
                diagnostics.Add(new ComputationDiagnostic("TRC1001", DiagnosticSeverity.Warning,
                    "Negative failure consequences were computed and set to zero.", "/Consequences/Failure"));
            if (flags.HasNegativeNonFailureConsequence)
                diagnostics.Add(new ComputationDiagnostic("TRC1002", DiagnosticSeverity.Warning,
                    "Negative non-failure consequences were computed and set to zero.", "/Consequences/NonFailure"));
            if (flags.HasNegativeExcessConsequence)
                diagnostics.Add(new ComputationDiagnostic("TRC1003", DiagnosticSeverity.Warning,
                    "Negative excess consequences were computed and set to zero.", "/Consequences/Excess"));
            if (flags.HasProbabilityGreaterThanOne)
                diagnostics.Add(new ComputationDiagnostic("TRC1004", DiagnosticSeverity.Warning,
                    "Mutually exclusive failure mode probabilities summed above one and were normalized.",
                    "/Components/FailureModes"));
            if (flags.HasExcessiveTruncatedMass)
                diagnostics.Add(new ComputationDiagnostic("TRC1005", DiagnosticSeverity.Error,
                    $"The joint system's combination enumeration reached the {_options.MaxSystemCombinations:N0}-combination cap and dropped more than {TruncatedCombinationResidualLimit:P1} of the exclusive probability mass onto the all-components-fail combination, which distorts the system tail. Raise MaxSystemCombinations, or model fewer components under the joint method.",
                    "/System/CombinationEnumeration"));
            else if (flags.HasTruncatedCombinationEnumeration)
                diagnostics.Add(new ComputationDiagnostic("TRC1006", DiagnosticSeverity.Warning,
                    $"The joint system's combination enumeration reached the {_options.MaxSystemCombinations:N0}-combination cap on some evaluations; the deepest combinations were not enumerated and their mass was attributed to the all-components-fail combination. Raise MaxSystemCombinations to enumerate further.",
                    "/System/CombinationEnumeration"));
            if (mean != null)
            {
                CheckMassBalance(mean);
            }
            for (int i = 0; i < diagnostics.Count; i++)
            {
                if (diagnostics[i].Severity == DiagnosticSeverity.Error)
                {
                    throw new InvalidOperationException(diagnostics[i].ToLegacyMessage());
                }
            }
        }

        /// <summary>
        /// Enforces exact unit mass on every populated exhaustive Total stream, including
        /// reliability-mode zero-consequence streams and every declared consequence type.
        /// </summary>
        /// <param name="realization">The realization to inspect.</param>
        private void CheckMassBalance(SystemRealization realization)
        {
            ValidateTotalMass(realization.Curves.Total, "System/Total", required: true);
            for (int k = 0; k < realization.AdditionalCurves.Count; k++)
            {
                ValidateTotalMass(realization.AdditionalCurves[k].Total, $"System/Consequence[{k + 1}]/Total", required: true);
            }

            for (int i = 0; i < realization.Components.Count; i++)
            {
                var component = realization.Components[i];
                ValidateTotalMass(component.Curves.Total, $"Component[{i}]/Total", required: true);
                for (int k = 0; k < component.AdditionalCurves.Count; k++)
                {
                    ValidateTotalMass(component.AdditionalCurves[k].Total, $"Component[{i}]/Consequence[{k + 1}]/Total", required: true);
                }
                for (int j = 0; j < component.FailureModes.Count; j++)
                {
                    var failureMode = component.FailureModes[j];
                    ValidateTotalMass(failureMode.Curves.Total, $"Component[{i}]/FailureMode[{j}]/Total", required: false);
                    for (int k = 0; k < failureMode.AdditionalCurves.Count; k++)
                    {
                        ValidateTotalMass(failureMode.AdditionalCurves[k].Total,
                            $"Component[{i}]/FailureMode[{j}]/Consequence[{k + 1}]/Total", required: false);
                    }
                }
            }
        }

        /// <summary>
        /// Validates one exhaustive Total stream.
        /// </summary>
        /// <param name="total">The Total curve.</param>
        /// <param name="path">The diagnostic object path.</param>
        /// <param name="required">Whether an empty stream is an error.</param>
        /// <exception cref="InvalidOperationException">Thrown when the stream violates exhaustive mass.</exception>
        private static void ValidateTotalMass(Curve total, string path, bool required)
        {
            if (total.LECConsequences.Length == 0)
            {
                if (required) throw new InvalidOperationException($"The exhaustive stream '{path}' has no recorded distribution.");
                return;
            }
            if (!double.IsFinite(total.MassBalance) || total.MassBalance != 1d || total.TotalProbability != 1d)
            {
                throw new InvalidOperationException($"The exhaustive stream '{path}' carries recorded mass {total.MassBalance:R} and published probability {total.TotalProbability:R} instead of exactly one.");
            }
        }

        #endregion

        #region Sensitivity Analysis

        /// <summary>
        /// Computes the sensitivity of one stored scalar risk measure to every knowledge input:
        /// the per-realization measure values already persisted in
        /// <see cref="RiskResults"/> are correlated against the per-function percentile draws,
        /// re-derived bit-exactly from the content seeds — no re-simulation, no integration.
        /// </summary>
        /// <param name="outputMeasure">The scalar measure to explain.</param>
        /// <param name="riskType">The risk-type stream the measure is read from.</param>
        /// <param name="measure">The association measure to report.</param>
        /// <param name="componentIndex">
        /// The output scope: −1 for the overall system (inputs = every component's knowledge
        /// columns), or a component position (inputs = that component's columns only — other
        /// components' draws are independent of its results by construction).
        /// </param>
        /// <param name="failureModeIndex">
        /// Narrows the output to one failure mode's summaries (Excess and Fail streams only);
        /// −1 for the component or system scope. Requires a component scope.
        /// </param>
        /// <param name="consequenceType">The consequence-type position (0 is the primary).</param>
        /// <returns>
        /// The labeled associations in the sampler walk order, or null when no full-uncertainty
        /// results are stored, the scope's outputs are unavailable, fewer than three valid
        /// realization pairs remain after NaN filtering, or the scope has no knowledge inputs.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an out-of-range scope argument.</exception>
        /// <exception cref="ArgumentException">Thrown for a failure-mode scope with a stream other than Excess or Fail.</exception>
        /// <remarks>
        /// The inputs pair with the stored outputs through the content seeds, so the model must
        /// be unchanged since the run (the same guarantee every diagnostic over stored results
        /// carries). The call re-runs the component sampler setup at the ensemble size; a
        /// subsequent <see cref="RunAsync"/> re-seeds itself at run start.
        /// </remarks>
        public SensitivityResults? MeasureSensitivity(RiskMeasure outputMeasure, RiskType riskType, SensitivityMeasure measure,
            int componentIndex = -1, int failureModeIndex = -1, int consequenceType = 0)
        {
            ValidateSensitivityScope(componentIndex, failureModeIndex, riskType, consequenceType);
            var results = RiskResults;
            if (!IsEstimated || results == null || results.Count < 2) return null;

            int count = results.Count;
            var outputs = new double[count];
            for (int i = 0; i < count; i++)
            {
                var summary = results[i];
                var scope = summary == null ? null : SelectScope(summary, componentIndex, failureModeIndex, riskType, consequenceType);
                outputs[i] = scope == null ? double.NaN : ExtractMeasure(scope, outputMeasure);
            }

            var inputs = BuildSensitivityInputs(componentIndex, count);
            if (inputs.Count == 0) return null;
            string scopeLabel = componentIndex < 0
                ? "System"
                : failureModeIndex < 0 ? _components[componentIndex].Name : $"{_components[componentIndex].Name} mode {failureModeIndex + 1}";
            return Correlate(inputs, outputs, measure, riskType, $"{outputMeasure} — {riskType} — {scopeLabel}");
        }

        /// <summary>
        /// Computes <see cref="MeasureSensitivity"/> for the full scalar-measure catalog in one
        /// pass, reusing one input-matrix derivation across all ten measures.
        /// </summary>
        /// <param name="riskType">The risk-type stream the measures are read from.</param>
        /// <param name="measure">The association measure to report.</param>
        /// <param name="componentIndex">The output scope (see <see cref="MeasureSensitivity"/>).</param>
        /// <param name="failureModeIndex">The failure-mode narrowing (see <see cref="MeasureSensitivity"/>).</param>
        /// <param name="consequenceType">The consequence-type position (0 is the primary).</param>
        /// <returns>One result per measure that produced valid associations; empty when none did.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an out-of-range scope argument.</exception>
        /// <exception cref="ArgumentException">Thrown for a failure-mode scope with a stream other than Excess or Fail.</exception>
        public IReadOnlyList<SensitivityResults> MeasureSensitivityMatrix(RiskType riskType, SensitivityMeasure measure,
            int componentIndex = -1, int failureModeIndex = -1, int consequenceType = 0)
        {
            ValidateSensitivityScope(componentIndex, failureModeIndex, riskType, consequenceType);
            var matrix = new List<SensitivityResults>();
            var results = RiskResults;
            if (!IsEstimated || results == null || results.Count < 2) return matrix;

            int count = results.Count;
            var inputs = BuildSensitivityInputs(componentIndex, count);
            if (inputs.Count == 0) return matrix;

            var outputs = new double[count];
            foreach (RiskMeasure outputMeasure in Enum.GetValues<RiskMeasure>())
            {
                for (int i = 0; i < count; i++)
                {
                    var summary = results[i];
                    var scope = summary == null ? null : SelectScope(summary, componentIndex, failureModeIndex, riskType, consequenceType);
                    outputs[i] = scope == null ? double.NaN : ExtractMeasure(scope, outputMeasure);
                }
                string scopeLabel = componentIndex < 0
                    ? "System"
                    : failureModeIndex < 0 ? _components[componentIndex].Name : $"{_components[componentIndex].Name} mode {failureModeIndex + 1}";
                var result = Correlate(inputs, outputs, measure, riskType, $"{outputMeasure} — {riskType} — {scopeLabel}");
                if (result != null) matrix.Add(result);
            }
            return matrix;
        }

        /// <summary>
        /// Computes the sensitivity of the risk at one hazard level to every knowledge input
        /// (the tornado diagnostic): a dedicated content-seeded design (default 100
        /// realizations) evaluates the failure-mode combination decomposition at the level —
        /// one evaluation per realization, no integration — and correlates the weighted
        /// expected consequence for the risk type against the percentile draws.
        /// </summary>
        /// <param name="componentIndex">The component whose risk is decomposed.</param>
        /// <param name="hazardLevel">
        /// The hazard level — interpreted on the component's selected profile hazard axis when
        /// one is set (each realization inverts its own sampled profile chain back to the
        /// driving hazard), the raw driving axis otherwise.
        /// </param>
        /// <param name="measure">The association measure to report.</param>
        /// <param name="riskType">The risk type whose expected consequence is the output.</param>
        /// <param name="realizations">The dedicated design size (a fresh stratified design — not the ensemble's rows).</param>
        /// <param name="consequenceType">The consequence-type position (0 is the primary).</param>
        /// <returns>
        /// The labeled associations in the sampler walk order, or null when the analysis is
        /// invalid (the v1.0 contract), the component is deterministic, or no knowledge inputs
        /// exist.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an out-of-range component index, consequence type, or a design below three realizations.</exception>
        /// <remarks>
        /// Content-seeded — the design derives from (analysis seed, component hash, occurrence
        /// index), never a wall clock — and re-runs the component's sampler setup at the design
        /// size (a subsequent <see cref="RunAsync"/> re-seeds itself). The legacy hazard bin
        /// weight is preserved: the sampled hazard's probability mass over ±(range/200) around
        /// the level, with the tail masses at the domain ends. When a profile element is
        /// selected, every transform on its chain must declare an ordered output axis — the
        /// per-realization inversion reads the sampled curves' <c>InverseFunction</c>, which
        /// requires monotone outputs (rating curves are; an unordered declaration faults the
        /// query loudly rather than inverting ambiguously).
        /// </remarks>
        public SensitivityResults? HazardLevelSensitivity(int componentIndex, double hazardLevel, SensitivityMeasure measure,
            RiskType riskType, int realizations = 100, int consequenceType = 0)
        {
            if (componentIndex < 0 || componentIndex >= _components.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(componentIndex), "The component index is out of range.");
            }
            if (realizations < 3)
            {
                throw new ArgumentOutOfRangeException(nameof(realizations), "The sensitivity design needs at least three realizations.");
            }
            if (consequenceType < 0 || consequenceType > RunAdditionalConsequenceTypes.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(consequenceType), "The consequence-type position is not declared.");
            }
            if (!Validate().IsValid) return null;
            var component = _components[componentIndex];
            if (component.IsDeterministic) return null;

            SystemComponent.AssignOccurrenceIndices(_components);
            int seed = SeedHelpers.HashCombine(_options.PRNGSeed, component.CanonicalHash(), component.OccurrenceIndex);
            using (EnterEpistemicScopeForRederivation(realizations))
            {
                component.SetupSamplers(realizations, seed, _options.SamplingScheme, null, BuildCurrentFractilePinMap(), null);
            }
            var inputs = new List<SensitivityInput>();
            component.CollectSensitivityInputs(inputs);
            if (inputs.Count == 0) return null;

            var outputs = new double[realizations];
            Parallel.For(0, realizations, index =>
            {
                var flags = new RiskComputeFlags();
                var sampled = component.Sample(index);
                var scratch = new ComponentRealization(sampled.FailureModeCount);
                scratch.EnsureAdditionalCurves(sampled.ConsequenceTypeCount - 1);
                double raw = sampled.InverseProfileHazard(hazardLevel);

                // The legacy hazard bin weight on this realization's sampled hazard.
                double minHazard = sampled.Hazard.InverseCDF(ProbabilityFloor);
                double maxHazard = sampled.Hazard.InverseCDF(1d - ProbabilityFloor);
                double dx = (maxHazard - minHazard) / 200d;
                double weight;
                if (raw <= minHazard)
                {
                    weight = sampled.Hazard.CDF(minHazard + dx);
                }
                else if (raw >= maxHazard)
                {
                    weight = 1d - sampled.Hazard.CDF(maxHazard - dx);
                }
                else
                {
                    weight = sampled.Hazard.CDF(raw + dx) - sampled.Hazard.CDF(raw - dx);
                }

                var typeOutputs = new ComponentRiskOutput[sampled.ConsequenceTypeCount];

                // The probability argument is a placeholder coordinate here (nothing records),
                // so a bivariate component derives its slice's true non-exceedance probability
                // from its own sampled primary marginal (hazardNonExceedance left NaN).
                sampled.ComputeRisk(0.5d, raw, flags, scratch, recordOutput: false, typeOutputs);
                var output = typeOutputs[consequenceType];
                double expectedFailure = output.ProbabilityOfFailure * output.MeanFailureConsequences;
                double expectedExcess = output.ProbabilityOfFailure * output.MeanExcessConsequences;
                double nonFailure = output.NonFailureConsequences;
                double nonFailureProbability = output.ProbabilityOfNonFailure;
                outputs[index] = riskType switch
                {
                    RiskType.Excess => weight * expectedExcess,
                    RiskType.Background => weight * nonFailure,
                    RiskType.Total => weight * (expectedFailure + nonFailureProbability * nonFailure),
                    RiskType.Fail => weight * expectedFailure,
                    _ => weight * nonFailureProbability * nonFailure,
                };
            });

            return Correlate(inputs, outputs, measure, riskType,
                $"Risk at {hazardLevel.ToString("G6", CultureInfo.InvariantCulture)} — {riskType} — {component.Name}");
        }

        /// <summary>
        /// Validates a measure-sensitivity scope: index bounds and the failure-mode stream
        /// restriction (mode summaries carry Excess and Fail only).
        /// </summary>
        /// <param name="componentIndex">The component scope (−1 = system).</param>
        /// <param name="failureModeIndex">The mode narrowing (−1 = none).</param>
        /// <param name="riskType">The requested stream.</param>
        /// <param name="consequenceType">The consequence-type position.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for out-of-range indices.</exception>
        /// <exception cref="ArgumentException">Thrown for a mode scope with a stream other than Excess or Fail.</exception>
        private void ValidateSensitivityScope(int componentIndex, int failureModeIndex, RiskType riskType, int consequenceType)
        {
            if (componentIndex < -1 || componentIndex >= _components.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(componentIndex), "The component index is out of range.");
            }
            if (failureModeIndex >= 0 && componentIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(failureModeIndex), "A failure-mode scope requires a component scope.");
            }
            if (failureModeIndex >= 0 && riskType != RiskType.Excess && riskType != RiskType.Fail)
            {
                throw new ArgumentException("Failure-mode summaries carry the Excess and Fail streams only.", nameof(riskType));
            }
            if (consequenceType < 0 || consequenceType > RunAdditionalConsequenceTypes.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(consequenceType), "The consequence-type position is not declared.");
            }
        }

        /// <summary>
        /// Re-derives the knowledge-input columns for a sensitivity scope at the given design
        /// size: the content-seed walk (occurrence indices, component seeds, sampler setup) and
        /// the labeled column collection — identical streams to a run at that size.
        /// </summary>
        /// <param name="componentIndex">The scope (−1 = every component, in analysis order).</param>
        /// <param name="sampleSize">The design size.</param>
        /// <returns>The labeled columns in walk order.</returns>
        private List<SensitivityInput> BuildSensitivityInputs(int componentIndex, int sampleSize)
        {
            SystemComponent.AssignOccurrenceIndices(_components);
            var inputs = new List<SensitivityInput>();
            var fractilePinMap = BuildCurrentFractilePinMap();
            var sharedEpistemicSeen = new HashSet<string>(StringComparer.Ordinal);
            using (EnterEpistemicScopeForRederivation(sampleSize))
            {
                for (int i = 0; i < _components.Count; i++)
                {
                    if (componentIndex >= 0 && i != componentIndex) continue;
                    int seed = SeedHelpers.HashCombine(_options.PRNGSeed, _components[i].CanonicalHash(), _components[i].OccurrenceIndex);
                    _components[i].SetupSamplers(sampleSize, seed, _options.SamplingScheme, null, fractilePinMap, null);
                    _components[i].CollectSensitivityInputs(inputs, sharedEpistemicSeen);
                }
            }
            return inputs;
        }

        /// <summary>
        /// Re-evaluates the configured tolerable-risk criteria over the stored result ensemble —
        /// including any realization weights assigned after the run — without re-simulation.
        /// The returned entries are freshly computed; the stored summary block is not modified.
        /// </summary>
        /// <returns>
        /// The confidence entries in criteria order, or null when the analysis configures no
        /// criteria or carries no stored ensemble.
        /// </returns>
        /// <exception cref="InvalidOperationException">Thrown when the stored ensemble carries an invalid realization weight vector.</exception>
        public IReadOnlyList<TolerableRiskConfidence>? ComputeTolerableRiskConfidence()
        {
            var results = _riskResults;
            if (results == null || results.Count == 0 || _authorOptions.TolerableRiskCriteria.Count == 0) return null;
            var weights = results.RealizationWeights;
            if (weights != null)
            {
                string? reason = EnsembleResults.DescribeInvalidWeights(weights, results.Count, out _);
                if (reason != null)
                {
                    throw new InvalidOperationException($"The stored realization weights are invalid. {reason}");
                }
            }
            return EvaluateTolerableRiskCriteria(_authorOptions.TolerableRiskCriteria, results.Realizations, weights).AsReadOnly();
        }

        /// <summary>
        /// The bin count behind every given-data estimator: the value-of-information
        /// conditioning and the given-data sensitivity measures share this documented
        /// convention with the upstream global-sensitivity defaults.
        /// </summary>
        private const int GivenDataBins = 20;

        /// <summary>
        /// Computes the value of information for one stored scalar risk measure: for every
        /// knowledge input, the epistemic variance of the measure a study resolving that input
        /// could remove — estimated by weighted equal-frequency conditioning of the stored
        /// per-realization measures on the input's percentile column, with no re-simulation —
        /// reported per input, rolled up per function (the candidate study a decision maker
        /// would commission), with the perfect-information total and, at the system scope, the
        /// expected movement of every configured tolerable-risk confidence statement.
        /// </summary>
        /// <param name="outputMeasure">The scalar measure to explain.</param>
        /// <param name="riskType">The risk-type stream the measure is read from.</param>
        /// <param name="componentIndex">
        /// The output scope: −1 for the overall system (inputs = every component's knowledge
        /// columns), or a component position (that component's columns only).
        /// </param>
        /// <param name="failureModeIndex">
        /// Narrows the output to one failure mode's summaries (Excess and Fail streams only);
        /// −1 for the component or system scope. Requires a component scope.
        /// </param>
        /// <param name="consequenceType">The consequence-type position (0 is the primary).</param>
        /// <returns>
        /// The ranked-table result, or null when no full-uncertainty results are stored, fewer
        /// than two valid realizations remain, the scope has no knowledge inputs, or the total
        /// epistemic variance is inestimable.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for an out-of-range scope argument.</exception>
        /// <exception cref="ArgumentException">Thrown for a failure-mode scope with a stream other than Excess or Fail.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the stored ensemble carries an invalid realization weight vector.</exception>
        /// <remarks>
        /// <para>
        /// The quantities are measure-based — no remediation action model is assumed: an
        /// entry's resolvable variance is Var(E[measure | input]) across the input's
        /// equal-weight bins, whose square root is the epistemic uncertainty, in the measure's
        /// own units, the study could remove; the total variance is the perfect-information
        /// ceiling, exact because a realization's measure is deterministic given its knowledge
        /// draws. Function rollups sum their member columns' main effects — exact when the
        /// members enter additively, conservative otherwise. Estimates carry the given-data
        /// binning biases (order 1/bins² from discretization, order bins/n from bin-mean
        /// noise); realization weights assigned after the run are honored in every conditional
        /// reduction. Tolerable-risk movement blocks are computed only at the system scope,
        /// where the criteria are defined.
        /// </para>
        /// <para>
        /// The inputs pair with the stored outputs through the content seeds, so the model must
        /// be unchanged since the run. The call re-runs the component sampler setup at the
        /// ensemble size; a subsequent <see cref="RunAsync"/> re-seeds itself at run start.
        /// </para>
        /// </remarks>
        public ValueOfInformationResults? MeasureValueOfInformation(RiskMeasure outputMeasure, RiskType riskType,
            int componentIndex = -1, int failureModeIndex = -1, int consequenceType = 0)
        {
            ValidateSensitivityScope(componentIndex, failureModeIndex, riskType, consequenceType);
            var results = RiskResults;
            if (!IsEstimated || results == null || results.Count < 2) return null;
            var weights = results.RealizationWeights;
            if (weights != null)
            {
                string? reason = EnsembleResults.DescribeInvalidWeights(weights, results.Count, out _);
                if (reason != null)
                {
                    throw new InvalidOperationException($"The stored realization weights are invalid. {reason}");
                }
            }

            int count = results.Count;
            var outputs = new double[count];
            for (int i = 0; i < count; i++)
            {
                var summary = results[i];
                var scope = summary == null ? null : SelectScope(summary, componentIndex, failureModeIndex, riskType, consequenceType);
                outputs[i] = scope == null ? double.NaN : ExtractMeasure(scope, outputMeasure);
            }

            var inputs = BuildSensitivityInputs(componentIndex, count);
            if (inputs.Count == 0) return null;

            // Materialize the percentile columns once; every conditional estimate reuses them.
            var columns = new double[inputs.Count][];
            for (int c = 0; c < inputs.Count; c++)
            {
                var column = new double[count];
                var read = inputs[c].Read;
                for (int i = 0; i < count; i++) column[i] = read(i);
                columns[c] = column;
            }

            // The total epistemic variance of the measure (weighted population form, NaN
            // outputs skipped with their weights).
            double totalWeight = 0d;
            double mean = 0d;
            int valid = 0;
            for (int i = 0; i < count; i++)
            {
                if (double.IsNaN(outputs[i])) continue;
                double w = weights == null ? 1d : weights[i];
                totalWeight += w;
                mean += w * outputs[i];
                valid++;
            }
            if (valid < 2 || totalWeight <= 0d) return null;
            mean /= totalWeight;
            double totalVariance = 0d;
            for (int i = 0; i < count; i++)
            {
                if (double.IsNaN(outputs[i])) continue;
                double w = weights == null ? 1d : weights[i];
                double deviation = outputs[i] - mean;
                totalVariance += w * deviation * deviation;
            }
            totalVariance /= totalWeight;

            var entries = new List<ValueOfInformationEntry>(inputs.Count);
            for (int c = 0; c < inputs.Count; c++)
            {
                var effect = ValueOfInformationEstimator.MainEffect(columns[c], outputs, weights, GivenDataBins);
                double share = totalVariance > 0d ? effect.ResolvableVariance / totalVariance : double.NaN;
                entries.Add(new ValueOfInformationEntry(inputs[c].Label, inputs[c].GroupLabel,
                    effect.ResolvableVariance, share, effect.Pairs));
            }

            // The candidate-study rollups, in first-appearance order.
            var groupOrder = new List<string>();
            var groupMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var groupVariances = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int c = 0; c < entries.Count; c++)
            {
                var entry = entries[c];
                if (!groupMembers.TryGetValue(entry.GroupLabel, out var members))
                {
                    members = new List<string>();
                    groupMembers.Add(entry.GroupLabel, members);
                    groupVariances.Add(entry.GroupLabel, 0d);
                    groupOrder.Add(entry.GroupLabel);
                }
                members.Add(entry.Label);
                groupVariances[entry.GroupLabel] += entry.ResolvableVariance;
            }
            var groups = new List<ValueOfInformationGroup>(groupOrder.Count);
            for (int g = 0; g < groupOrder.Count; g++)
            {
                string key = groupOrder[g];
                double variance = groupVariances[key];
                double share = totalVariance > 0d ? variance / totalVariance : double.NaN;
                groups.Add(new ValueOfInformationGroup(key, groupMembers[key], variance, share));
            }

            // The tolerable-risk movement blocks — system scope only, where the criteria live.
            var movements = new List<TolerableRiskConfidenceMovement>();
            if (componentIndex < 0)
            {
                var criteria = _authorOptions.TolerableRiskCriteria;
                for (int k = 0; k < criteria.Count; k++)
                {
                    var criterion = criteria[k];
                    var criterionOutputs = new double[count];
                    double exceedingWeight = 0d;
                    double criterionWeight = 0d;
                    for (int i = 0; i < count; i++)
                    {
                        var summary = results[i];
                        var stream = summary == null ? null : SelectScope(summary, -1, -1, criterion.RiskType, criterion.ConsequenceTypeIndex);
                        double value = stream != null ? ExtractMeasure(stream, criterion.Measure) : double.NaN;
                        criterionOutputs[i] = value;
                        if (double.IsNaN(value)) continue;
                        double w = weights == null ? 1d : weights[i];
                        criterionWeight += w;
                        if (value > criterion.Threshold) exceedingWeight += w;
                    }
                    double baseline = criterionWeight > 0d ? exceedingWeight / criterionWeight : double.NaN;
                    var entryMovements = new double[inputs.Count];
                    for (int c = 0; c < inputs.Count; c++)
                    {
                        entryMovements[c] = ValueOfInformationEstimator.ExceedanceMovement(columns[c], criterionOutputs,
                            weights, GivenDataBins, criterion.Threshold);
                    }
                    movements.Add(new TolerableRiskConfidenceMovement(criterion.Measure.ToString(),
                        criterion.RiskType.ToString(), criterion.ConsequenceTypeIndex, criterion.Threshold, baseline,
                        double.IsNaN(baseline) ? double.NaN : 2d * baseline * (1d - baseline), entryMovements));
                }
            }

            string scopeLabel = componentIndex < 0
                ? "System"
                : failureModeIndex < 0 ? _components[componentIndex].Name : $"{_components[componentIndex].Name} mode {failureModeIndex + 1}";
            return new ValueOfInformationResults($"{outputMeasure} — {riskType} — {scopeLabel}", riskType, outputMeasure,
                consequenceType, GivenDataBins, valid, totalVariance, entries, groups, movements);
        }

        /// <summary>
        /// Answers a configuration-risk query — the risk of the system with named fault-tree
        /// house events held at specified states, such as a spillway gate out of service — as
        /// two deterministic mean-only re-quantifications over throwaway component clones: the
        /// unmodified baseline and the configured system, with system and per-component rows of
        /// annual failure probability and per-consequence-type expected annual consequences.
        /// Runtime-only: the authored model, its published results, and its estimated state are
        /// never touched, nothing is serialized or hashed, and no seed moves. Overrides address
        /// house events by function id and node id and reach nested targets — external
        /// transfers, tree-referenced responses, and composite-response children — through the
        /// same containment discipline the other runtime queries use; an override matching no
        /// house event is refused loudly rather than silently ignored. Every instance of one
        /// function id receives the override, so a stored function reused across components is
        /// reconfigured everywhere, which is the physical reading of one piece of equipment.
        /// The query is deliberately mean-only: a configured state is compute content, so a
        /// configured realization ensemble would re-roll every content-derived seed and mix
        /// stream noise into the difference.
        /// </summary>
        /// <param name="configuration">The house-event overrides defining the queried configuration.</param>
        /// <returns>The baseline and configured mean-only quantifications with their changes.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the configuration is null.</exception>
        /// <exception cref="ArgumentException">Thrown for an empty configuration or duplicate overrides.</exception>
        /// <exception cref="InvalidOperationException">Thrown when an override matches no house event, or when either mean-only run fails.</exception>
        public ConfigurationRiskResults MeasureConfigurationRisk(IReadOnlyList<HouseEventState> configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (configuration.Count == 0)
                throw new ArgumentException("The configuration-risk query requires at least one house-event override.", nameof(configuration));
            var seenOverrides = new HashSet<(Guid FunctionId, Guid NodeId)>();
            for (int i = 0; i < configuration.Count; i++)
            {
                if (!seenOverrides.Add((configuration[i].FunctionId, configuration[i].NodeId)))
                    throw new ArgumentException(
                        $"The configuration addresses house event '{configuration[i].NodeId:D}' of function '{configuration[i].FunctionId:D}' more than once.",
                        nameof(configuration));
            }

            List<SystemComponent> baselineComponents = CloneComponentsForConfiguration();
            List<SystemComponent> configuredComponents = CloneComponentsForConfiguration();
            IReadOnlyList<string> appliedOverrides =
                ApplyHouseEventStates(configuredComponents, configuration);

            SystemRealization baseline = RunConfigurationQuery(baselineComponents);
            SystemRealization configured = RunConfigurationQuery(configuredComponents);

            int typeCount = 1 + _additionalConsequenceTypes.Count;
            var componentEntries = new List<ConfigurationRiskEntry>(_authorComponents.Count);
            for (int i = 0; i < _authorComponents.Count; i++)
            {
                componentEntries.Add(CreateConfigurationEntry(_authorComponents[i].Name,
                    baseline.Components[i].Curves, baseline.Components[i].AdditionalCurves,
                    configured.Components[i].Curves, configured.Components[i].AdditionalCurves,
                    typeCount));
            }
            ConfigurationRiskEntry system = CreateConfigurationEntry("System",
                baseline.Curves, baseline.AdditionalCurves,
                configured.Curves, configured.AdditionalCurves, typeCount);

            var labels = new List<string>(typeCount) { SpecifiedConsequence };
            var units = new List<string>(typeCount) { ConsequenceUnit };
            for (int i = 0; i < _additionalConsequenceTypes.Count; i++)
            {
                labels.Add(_additionalConsequenceTypes[i].SpecifiedConsequence);
                units.Add(_additionalConsequenceTypes[i].ConsequenceUnit);
            }
            return new ConfigurationRiskResults(system, componentEntries, labels, units, appliedOverrides);
        }

        /// <summary>Builds one scope row from the baseline and configured mean curve sets.</summary>
        /// <param name="name">The scope display label.</param>
        /// <param name="baseline">The baseline primary curve set.</param>
        /// <param name="baselineAdditional">The baseline additional-type curve sets.</param>
        /// <param name="configured">The configured primary curve set.</param>
        /// <param name="configuredAdditional">The configured additional-type curve sets.</param>
        /// <param name="typeCount">The declared consequence-type count.</param>
        /// <returns>The scope row.</returns>
        private static ConfigurationRiskEntry CreateConfigurationEntry(string name, Curves baseline,
            IReadOnlyList<Curves> baselineAdditional, Curves configured,
            IReadOnlyList<Curves> configuredAdditional, int typeCount)
        {
            var baselineMeans = new double[typeCount];
            var configuredMeans = new double[typeCount];
            baselineMeans[0] = baseline.Total.Mean;
            configuredMeans[0] = configured.Total.Mean;
            for (int k = 1; k < typeCount; k++)
            {
                baselineMeans[k] = baselineAdditional[k - 1].Total.Mean;
                configuredMeans[k] = configuredAdditional[k - 1].Total.Mean;
            }
            return new ConfigurationRiskEntry(name, baseline.Fail.TotalProbability,
                configured.Fail.TotalProbability, baselineMeans, configuredMeans);
        }

        /// <summary>Clones the authored components onto throwaway query instances.</summary>
        /// <returns>The self-contained component clones in author order.</returns>
        private List<SystemComponent> CloneComponentsForConfiguration()
        {
            var clones = new List<SystemComponent>(_authorComponents.Count);
            for (int i = 0; i < _authorComponents.Count; i++)
                clones.Add(new SystemComponent(_authorComponents[i].ToXElement(RiskSerializationMode.SelfContained)));
            return clones;
        }

        /// <summary>
        /// Applies the house-event overrides across every fault tree the cloned components can
        /// reach — element-assigned functions, external transfer targets, tree-referenced
        /// responses, structural link targets, and composite-response children — and refuses any
        /// override that matched nothing.
        /// </summary>
        /// <param name="components">The cloned components to reconfigure.</param>
        /// <param name="configuration">The house-event overrides.</param>
        /// <returns>The display labels of the applied overrides, in configuration order.</returns>
        /// <exception cref="InvalidOperationException">Thrown when an override matches no house event.</exception>
        private static IReadOnlyList<string> ApplyHouseEventStates(
            IReadOnlyList<SystemComponent> components, IReadOnlyList<HouseEventState> configuration)
        {
            var labels = new string?[configuration.Count];
            var visited = new HashSet<IRiskFunction>(ReferenceEqualityComparer.Instance);

            void Walk(IRiskFunction? function)
            {
                if (function == null || !visited.Add(function)) return;
                if (function is FaultTreeResponse fault)
                {
                    foreach (FaultTreeNodeBase node in fault.FaultTree.Nodes)
                    {
                        if (node is FaultTreeHouseEventNode house)
                        {
                            for (int k = 0; k < configuration.Count; k++)
                            {
                                if (configuration[k].FunctionId != fault.Id
                                    || configuration[k].NodeId != house.Id) continue;
                                house.State = configuration[k].State;
                                labels[k] ??= $"'{fault.Name}' house event '{house.Name}' = {configuration[k].State}";
                            }
                        }
                        else if (node is FaultTreeBasicEventNode basic)
                        {
                            Walk(basic.ProbabilitySource.ResponseFunction);
                        }
                        else if (node is FaultTreeTransferNode transfer)
                        {
                            Walk(transfer.TargetFunction);
                        }
                    }
                }
                else if (function is EventTreeResponse tree)
                {
                    foreach (EventNodeBase node in tree.EventTree.Nodes)
                    {
                        if (node is ChanceNode chance) Walk(chance.ProbabilitySource.ResponseFunction);
                        else if (node is EventTreeLinkNode link) Walk(link.TargetFunction);
                    }
                }
                else if (function is CompositeResponse composite)
                {
                    foreach (WeightedResponseFunction child in composite.ResponseFunctions)
                        Walk(child.ResponseFunction);
                }
            }

            for (int i = 0; i < components.Count; i++)
            {
                foreach (IRiskFunction function in components[i].GetReferencedFunctions())
                    Walk(function);
            }

            var unmatched = new List<string>();
            for (int k = 0; k < configuration.Count; k++)
            {
                if (labels[k] == null)
                    unmatched.Add($"function '{configuration[k].FunctionId:D}' node '{configuration[k].NodeId:D}'");
            }
            if (unmatched.Count > 0)
            {
                throw new InvalidOperationException(
                    "The configuration-risk query addresses house events the system cannot reach: " +
                    $"{string.Join("; ", unmatched)}. Overrides are refused rather than silently ignored.");
            }
            var applied = new string[configuration.Count];
            for (int k = 0; k < configuration.Count; k++) applied[k] = labels[k]!;
            return applied;
        }

        /// <summary>Runs one mean-only quantification over a throwaway component set.</summary>
        /// <param name="components">The query components.</param>
        /// <returns>The published mean realization.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the query run does not publish mean results.</exception>
        private SystemRealization RunConfigurationQuery(IList<SystemComponent> components)
        {
            var query = new RiskAnalysis(components)
            {
                Name = Name,
                SpecifiedConsequence = SpecifiedConsequence,
                ConsequenceUnit = ConsequenceUnit,
                Options = new RiskAnalysisOptions(_authorOptions.ToXElement()),
            };
            foreach (ConsequenceTypeDescriptor descriptor in _additionalConsequenceTypes)
                query.AdditionalConsequenceTypes.Add(descriptor);
            query.Options.EstimateMeanRiskOnly = true;
            query.RunAsync().GetAwaiter().GetResult();
            return query.MeanRiskResults
                ?? throw new InvalidOperationException("The configuration-risk query run did not publish mean results.");
        }

        /// <summary>
        /// Computes the exposure-period and life-cycle conversions over the stored ensemble: the
        /// probability of at least one failure over the period and the cumulative, discounted,
        /// and equivalent-annual expected consequences, each converted per realization from the
        /// stored annual measures and reduced with the stored realization weights (post-run
        /// weight assignments are honored, the post-hoc query convention). The period failure
        /// probability reads the scope's Fail-stream annualized failure probability; the
        /// consequence conversions read the requested stream's unconditional mean. Pure
        /// post-processing: no re-simulation, nothing serialized, and the published results are
        /// untouched. See <see cref="ExposurePeriodRiskResults"/> for the conventions and the
        /// stationarity caveat.
        /// </summary>
        /// <param name="periodYears">The exposure period in years (at least one).</param>
        /// <param name="discountRate">The annual discount rate (0 = undiscounted; finite and non-negative).</param>
        /// <param name="riskType">The result stream the consequence conversions read.</param>
        /// <param name="componentIndex">The component scope (−1 = the system).</param>
        /// <param name="failureModeIndex">The failure-mode scope (−1 = the component).</param>
        /// <param name="consequenceType">The consequence-type position (0 = primary).</param>
        /// <returns>The conversions, or null when no full-uncertainty ensemble is stored.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown for a non-positive period, an invalid discount rate, or an out-of-range scope index.</exception>
        /// <exception cref="ArgumentException">Thrown for a failure-mode scope on a stream without mode resolution.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the stored realization weights are invalid.</exception>
        public ExposurePeriodRiskResults? MeasureExposurePeriodRisk(int periodYears, double discountRate = 0d,
            RiskType riskType = RiskType.Total, int componentIndex = -1, int failureModeIndex = -1, int consequenceType = 0)
        {
            if (periodYears < 1)
                throw new ArgumentOutOfRangeException(nameof(periodYears), "The exposure period must be at least one year.");
            if (!Tools.IsFinite(discountRate) || discountRate < 0d)
                throw new ArgumentOutOfRangeException(nameof(discountRate), "The discount rate must be finite and non-negative.");
            ValidateSensitivityScope(componentIndex, failureModeIndex, riskType, consequenceType);
            var results = RiskResults;
            if (!IsEstimated || results == null || results.Count < 2) return null;
            var weights = results.RealizationWeights;
            if (weights != null)
            {
                string? reason = EnsembleResults.DescribeInvalidWeights(weights, results.Count, out _);
                if (reason != null)
                {
                    throw new InvalidOperationException($"The stored realization weights are invalid. {reason}");
                }
            }

            // The annuity present-value factor for one unit per year: (1 − (1 + r)^−T)/r, with
            // the exact r → 0 limit T; evaluated in log space for precision.
            double annuityFactor = discountRate > 0d
                ? -Tools.Expm1(-periodYears * Tools.Log1p(discountRate)) / discountRate
                : periodYears;

            // The per-realization conversions, pairwise-filtered with their weights.
            int count = results.Count;
            var periodProbabilities = new List<double>(count);
            var cumulatives = new List<double>(count);
            var presentValues = new List<double>(count);
            var equivalentAnnuals = new List<double>(count);
            var validWeights = weights == null ? null : new List<double>(count);
            for (int i = 0; i < count; i++)
            {
                var summary = results[i];
                if (summary == null) continue;
                var failScope = SelectScope(summary, componentIndex, failureModeIndex, RiskType.Fail, 0);
                var consequenceScope = SelectScope(summary, componentIndex, failureModeIndex, riskType, consequenceType);
                if (failScope == null || consequenceScope == null) continue;
                double p = failScope.TotalProbability;
                double m = consequenceScope.Mean;
                if (double.IsNaN(p) || double.IsNaN(m)) continue;

                // P_T = 1 − (1 − p)^T, evaluated as −expm1(T·log1p(−p)) so small annual
                // probabilities keep full precision (the binomial convention; see the result's
                // remarks for the Poisson rate-form equivalence).
                periodProbabilities.Add(-Tools.Expm1(periodYears * Tools.Log1p(-p)));
                cumulatives.Add(periodYears * m);
                double presentValue = m * annuityFactor;
                presentValues.Add(presentValue);
                equivalentAnnuals.Add(presentValue / annuityFactor);
                validWeights?.Add(weights![i]);
            }

            double width = _options.ConfidenceIntervalWidth;
            string scopeLabel = componentIndex < 0
                ? "System"
                : failureModeIndex < 0 ? _components[componentIndex].Name : $"{_components[componentIndex].Name} mode {failureModeIndex + 1}";
            return new ExposurePeriodRiskResults($"{riskType} — {scopeLabel}", periodYears, discountRate,
                consequenceType, periodProbabilities.Count,
                ReduceExposureInterval(periodProbabilities, validWeights, width),
                ReduceExposureInterval(cumulatives, validWeights, width),
                ReduceExposureInterval(presentValues, validWeights, width),
                ReduceExposureInterval(equivalentAnnuals, validWeights, width));
        }

        /// <summary>
        /// Reduces one exposure-period quantity over the valid realizations: the weighted (or
        /// plain) mean and the symmetric lower/median/upper percentiles at the configured
        /// confidence width; the all-NaN interval when nothing survives.
        /// </summary>
        /// <param name="values">The valid per-realization values.</param>
        /// <param name="weights">The parallel weights, or null for equal weights.</param>
        /// <param name="width">The confidence-interval width.</param>
        /// <returns>The reduced interval.</returns>
        private static ExposurePeriodInterval ReduceExposureInterval(List<double> values, List<double>? weights, double width)
        {
            if (values.Count == 0)
            {
                return new ExposurePeriodInterval(double.NaN, double.NaN, double.NaN, double.NaN);
            }
            double lowerLevel = (1d - width) / 2d;
            var levels = new[] { lowerLevel, 0.5d, 1d - lowerLevel };
            double[] percentiles = weights == null
                ? Statistics.Percentile(values, levels)
                : Statistics.Percentile(values, levels, weights);
            double mean = weights == null ? Statistics.Mean(values) : Statistics.Mean(values, weights);
            return new ExposurePeriodInterval(percentiles[0], percentiles[1], mean, percentiles[2]);
        }

        /// <summary>
        /// Evaluates a life-cycle trajectory over the planning horizon: the epoch boundaries are
        /// the sorted distinct union of year zero, the definition's evaluation years, and its
        /// intervention years; each epoch quantifies the system mean-only on throwaway
        /// self-contained clones carrying the cumulative intervention state (house events
        /// last-wins, hazard replacements chained against the live assignment) with every
        /// deteriorating response evaluated at the epoch's start age. Annual risk is
        /// stepwise-constant within an epoch. The horizon aggregates follow the exposure-period
        /// conversions' exact conventions — the probability of at least one failure accumulated
        /// in log space, cumulative and per-epoch-annuity discounted expected consequences, the
        /// equivalent-annual amounts — plus the absorbing (first-failure-terminates)
        /// survival-weighted variants, each computed for the Total, Excess, and Fail
        /// consequence streams through one shared accumulation so a benefit stream can be
        /// selected without re-deriving the trajectory. When the definition retains epoch
        /// realizations, each epoch row also carries its mean realization with the per-sample
        /// workspace dropped, so every curve measure is readable per epoch and per stream.
        /// Deliberately mean-only, like the configuration-risk
        /// query: a configured state is compute content, so a full-uncertainty trajectory would
        /// re-roll every configured function's stream per epoch. Runtime-only: nothing is
        /// serialized, hashed, or seed-affecting, and the authored model is never touched.
        /// A future condition on intervention entries — exercise gated on the state observed at
        /// the entry's year — is the named extension seat for real-options decision rules.
        /// </summary>
        /// <param name="definition">The life-cycle definition (horizon, discount rate, evaluation years, schedule).</param>
        /// <returns>The trajectory.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the definition is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a house-event override or hazard-replacement target matches nothing the
        /// system can reach, when a replacement's arity mismatches the replaced hazard, or when
        /// an epoch's configured quantification fails — each wrapped with the epoch's start year.
        /// </exception>
        public LifeCycleRiskResults MeasureLifeCycleRisk(LifeCycleDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            int[] startYears = DeriveEpochStartYears(definition);
            var schedule = new List<LifeCycleIntervention>(definition.Interventions);
            schedule.Sort((a, b) => a.Year.CompareTo(b.Year));

            int typeCount = 1 + _additionalConsequenceTypes.Count;
            double discountRate = definition.DiscountRate;
            double discountBase = 1d / (1d + discountRate);
            double horizonAnnuity = AnnuityFactor(definition.PeriodYears, discountRate);

            var epochs = new List<LifeCycleEpochRisk>(startYears.Length);
            var cumulative = new double[typeCount];
            var presentValue = new double[typeCount];
            var absorbingCumulative = new double[typeCount];
            var absorbingPresentValue = new double[typeCount];
            var excessCumulative = new double[typeCount];
            var excessPresentValue = new double[typeCount];
            var absorbingExcessCumulative = new double[typeCount];
            var absorbingExcessPresentValue = new double[typeCount];
            var failCumulative = new double[typeCount];
            var failPresentValue = new double[typeCount];
            var absorbingFailCumulative = new double[typeCount];
            var absorbingFailPresentValue = new double[typeCount];
            var interventionLabels = new List<string>(schedule.Count);
            double logSurvival = 0d;

            for (int k = 0; k < startYears.Length; k++)
            {
                int startYear = startYears[k];
                int endYear = k + 1 < startYears.Length ? startYears[k + 1] : definition.PeriodYears;
                int span = endYear - startYear;

                var epoch = RunLifeCycleEpoch(schedule, startYear);
                var systemEntry = CreateLifeCycleEntry("System",
                    epoch.Realization.Curves, epoch.Realization.AdditionalCurves, typeCount);
                var componentEntries = new List<LifeCycleEpochEntry>(_authorComponents.Count);
                for (int i = 0; i < _authorComponents.Count; i++)
                {
                    componentEntries.Add(CreateLifeCycleEntry(_authorComponents[i].Name,
                        epoch.Realization.Components[i].Curves,
                        epoch.Realization.Components[i].AdditionalCurves, typeCount));
                }

                double p = systemEntry.FailureProbability;
                double logSurvivalAtStart = logSurvival;

                // The per-epoch scalars are stream-independent: the annuity segment carries the
                // non-absorbing (every-year-exposed) convention, and the survival terms carry
                // the absorbing (first-failure-terminates) geometric closed forms.
                double annuitySegment = AnnuityFactor(endYear, discountRate) - AnnuityFactor(startYear, discountRate);
                double survivalAtStart = Math.Exp(logSurvivalAtStart);
                double annualSurvival = 1d - p;
                double survivalYears = p > 0d ? -Tools.Expm1(span * Tools.Log1p(-p)) / p : span;
                double x = annualSurvival * discountBase;
                double geometric = x == 1d ? span : (1d - Math.Pow(x, span)) / (1d - x);
                double firstYearDiscount = DiscountingSupport.DiscountFactor(startYear + 1, discountRate);

                // One aggregation authority per stream over the same epoch realization.
                AccumulateStreamAggregates(systemEntry.ExpectedConsequences, span, annuitySegment,
                    survivalAtStart, survivalYears, firstYearDiscount, geometric,
                    cumulative, presentValue, absorbingCumulative, absorbingPresentValue);
                AccumulateStreamAggregates(systemEntry.ExcessExpectedConsequences, span, annuitySegment,
                    survivalAtStart, survivalYears, firstYearDiscount, geometric,
                    excessCumulative, excessPresentValue, absorbingExcessCumulative, absorbingExcessPresentValue);
                AccumulateStreamAggregates(systemEntry.FailExpectedConsequences, span, annuitySegment,
                    survivalAtStart, survivalYears, firstYearDiscount, geometric,
                    failCumulative, failPresentValue, absorbingFailCumulative, absorbingFailPresentValue);

                logSurvival += span * Tools.Log1p(-p);
                double cumulativeFailure = -Tools.Expm1(logSurvival);
                SystemRealization? retained = null;
                if (definition.RetainEpochRealizations)
                {
                    retained = epoch.Realization;
                    retained.DumpMemory();
                }
                epochs.Add(new LifeCycleEpochRisk(startYear, span, startYear, cumulativeFailure,
                    systemEntry, componentEntries, epoch.ActionLabels, retained));

                // The entry whose year opens this epoch echoes its own action labels.
                for (int s = 0; s < schedule.Count; s++)
                {
                    if (schedule[s].Year != startYear) continue;
                    interventionLabels.Add(DescribeIntervention(schedule[s], epoch));
                }
            }

            var equivalentAnnual = new double[typeCount];
            var excessEquivalentAnnual = new double[typeCount];
            var failEquivalentAnnual = new double[typeCount];
            for (int t = 0; t < typeCount; t++)
            {
                equivalentAnnual[t] = presentValue[t] / horizonAnnuity;
                excessEquivalentAnnual[t] = excessPresentValue[t] / horizonAnnuity;
                failEquivalentAnnual[t] = failPresentValue[t] / horizonAnnuity;
            }

            var labels = new List<string>(typeCount) { SpecifiedConsequence };
            var units = new List<string>(typeCount) { ConsequenceUnit };
            for (int i = 0; i < _additionalConsequenceTypes.Count; i++)
            {
                labels.Add(_additionalConsequenceTypes[i].SpecifiedConsequence);
                units.Add(_additionalConsequenceTypes[i].ConsequenceUnit);
            }

            return new LifeCycleRiskResults(definition.PeriodYears, discountRate, labels, units,
                epochs, -Tools.Expm1(logSurvival), cumulative, presentValue, equivalentAnnual,
                absorbingCumulative, absorbingPresentValue, interventionLabels,
                excessCumulative, excessPresentValue, excessEquivalentAnnual,
                absorbingExcessCumulative, absorbingExcessPresentValue,
                failCumulative, failPresentValue, failEquivalentAnnual,
                absorbingFailCumulative, absorbingFailPresentValue);
        }

        /// <summary>
        /// One epoch's configured quantification and its label bookkeeping.
        /// </summary>
        /// <param name="Realization">The epoch's published mean realization.</param>
        /// <param name="ActionLabels">Every applied action label, in application order.</param>
        /// <param name="EffectiveHouseEvents">The last-wins effective house states, parallel to their labels.</param>
        /// <param name="HouseLabels">The house-event labels, parallel to the effective states.</param>
        /// <param name="AppliedReplacements">The chained replacement actions, parallel to their labels.</param>
        /// <param name="ReplacementLabels">The replacement labels, parallel to the applied replacements.</param>
        private sealed record LifeCycleEpochRun(SystemRealization Realization,
            IReadOnlyList<string> ActionLabels,
            IReadOnlyList<HouseEventState> EffectiveHouseEvents,
            IReadOnlyList<string> HouseLabels,
            IReadOnlyList<HazardReplacement> AppliedReplacements,
            IReadOnlyList<string> ReplacementLabels);

        /// <summary>
        /// Derives the epoch start years: the sorted distinct union of year zero, the evaluation
        /// years, and the intervention years.
        /// </summary>
        /// <param name="definition">The life-cycle definition.</param>
        /// <returns>The ascending epoch start years, beginning at zero.</returns>
        private static int[] DeriveEpochStartYears(LifeCycleDefinition definition)
        {
            var years = new SortedSet<int> { 0 };
            for (int i = 0; i < definition.EvaluationYears.Count; i++)
            {
                years.Add(definition.EvaluationYears[i]);
            }
            for (int i = 0; i < definition.Interventions.Count; i++)
            {
                years.Add(definition.Interventions[i].Year);
            }
            var result = new int[years.Count];
            years.CopyTo(result);
            return result;
        }

        /// <summary>
        /// Flattens the schedule into the effective house-event states at an epoch start:
        /// entries with earlier or equal years apply in ascending order, a later state
        /// overriding an earlier one for the same house event (first-appearance order kept).
        /// </summary>
        /// <param name="schedule">The interventions in ascending year order.</param>
        /// <param name="epochStartYear">The epoch's start year.</param>
        /// <returns>The effective states.</returns>
        private static List<HouseEventState> AssembleEffectiveHouseEvents(
            IReadOnlyList<LifeCycleIntervention> schedule, int epochStartYear)
        {
            var order = new List<(Guid FunctionId, Guid NodeId)>();
            var effective = new Dictionary<(Guid FunctionId, Guid NodeId), HouseEventState>();
            for (int i = 0; i < schedule.Count; i++)
            {
                if (schedule[i].Year > epochStartYear) break;
                var states = schedule[i].HouseEvents;
                for (int j = 0; j < states.Count; j++)
                {
                    var key = (states[j].FunctionId, states[j].NodeId);
                    if (!effective.ContainsKey(key)) order.Add(key);
                    effective[key] = states[j];
                }
            }
            var result = new List<HouseEventState>(order.Count);
            for (int i = 0; i < order.Count; i++)
            {
                result.Add(effective[order[i]]);
            }
            return result;
        }

        /// <summary>
        /// Collects the hazard replacements in service at an epoch start, in ascending year
        /// order — the order they chain in, each matching the live assignment its predecessors
        /// left.
        /// </summary>
        /// <param name="schedule">The interventions in ascending year order.</param>
        /// <param name="epochStartYear">The epoch's start year.</param>
        /// <returns>The chained replacement actions.</returns>
        private static List<HazardReplacement> CollectHazardReplacements(
            IReadOnlyList<LifeCycleIntervention> schedule, int epochStartYear)
        {
            var result = new List<HazardReplacement>();
            for (int i = 0; i < schedule.Count; i++)
            {
                if (schedule[i].Year > epochStartYear) break;
                var replacements = schedule[i].HazardReplacements;
                for (int j = 0; j < replacements.Count; j++)
                {
                    result.Add(replacements[j]);
                }
            }
            return result;
        }

        /// <summary>
        /// Applies chained hazard replacements to the epoch's cloned components: each action
        /// factory-clones its replacement once (the authored instance is never wired into query
        /// structures) and reassigns every hazard element whose live function carries the target
        /// id, refusing arity mismatches and unmatched targets loudly.
        /// </summary>
        /// <param name="components">The cloned components.</param>
        /// <param name="replacements">The chained replacement actions.</param>
        /// <returns>The applied labels, parallel to the actions (first match each).</returns>
        /// <exception cref="InvalidOperationException">Thrown for an arity mismatch or an unmatched target.</exception>
        private static IReadOnlyList<string> ApplyHazardReplacements(
            IReadOnlyList<SystemComponent> components, IReadOnlyList<HazardReplacement> replacements)
        {
            if (replacements.Count == 0) return Array.Empty<string>();

            var labels = new string?[replacements.Count];
            for (int r = 0; r < replacements.Count; r++)
            {
                var replacement = replacements[r];
                var clone = RiskFunctionFactory.CreateHazardFunction(replacement.Replacement.ToXElement())
                    ?? throw new InvalidOperationException(
                        $"The replacement hazard function '{replacement.Replacement.Name}' could not be reconstructed from its serialized form.");
                for (int i = 0; i < components.Count; i++)
                {
                    foreach (HazardElement element in components[i].Graph.GetElements<HazardElement>())
                    {
                        if (element.Function == null || element.Function.Id != replacement.TargetFunctionId) continue;
                        if (element.Function is IBivariateHazardFunction != clone is IBivariateHazardFunction)
                        {
                            throw new InvalidOperationException(
                                $"The replacement hazard function '{clone.Name}' does not match the arity of the replaced hazard '{element.Function.Name}'; a univariate hazard replaces a univariate hazard and a bivariate hazard replaces a bivariate one.");
                        }
                        labels[r] ??= $"'{components[i].Name}' hazard '{element.Function.Name}' replaced by '{clone.Name}'";
                        element.Function = clone;
                    }
                }
            }

            var unmatched = new List<string>();
            for (int r = 0; r < replacements.Count; r++)
            {
                if (labels[r] == null)
                    unmatched.Add($"function '{replacements[r].TargetFunctionId:D}'");
            }
            if (unmatched.Count > 0)
            {
                throw new InvalidOperationException(
                    "The life-cycle query addresses hazard functions the system cannot reach: " +
                    $"{string.Join("; ", unmatched)}. Replacements are refused rather than silently ignored.");
            }
            var applied = new string[replacements.Count];
            for (int r = 0; r < replacements.Count; r++) applied[r] = labels[r]!;
            return applied;
        }

        /// <summary>
        /// Sets every deteriorating response's evaluation age on the epoch's cloned components.
        /// The wrapper's legal seat is element-assigned, so the referenced-function roots reach
        /// every instance.
        /// </summary>
        /// <param name="components">The cloned components.</param>
        /// <param name="evaluationAge">The epoch's evaluation age.</param>
        private static void ApplyEvaluationAges(IReadOnlyList<SystemComponent> components, double evaluationAge)
        {
            for (int i = 0; i < components.Count; i++)
            {
                foreach (IRiskFunction function in components[i].GetReferencedFunctions())
                {
                    if (function is DeterioratingResponse aging)
                    {
                        aging.EvaluationAge = evaluationAge;
                    }
                }
            }
        }

        /// <summary>
        /// Runs one epoch: fresh self-contained clones, the cumulative house states, the chained
        /// replacements, the epoch's evaluation age, and a mean-only quantification. Any failure
        /// is wrapped with the epoch's start year — the one fact the run's own diagnostics
        /// cannot know.
        /// </summary>
        /// <param name="schedule">The interventions in ascending year order.</param>
        /// <param name="epochStartYear">The epoch's start year.</param>
        /// <returns>The epoch run.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the epoch cannot be configured or quantified.</exception>
        private LifeCycleEpochRun RunLifeCycleEpoch(IReadOnlyList<LifeCycleIntervention> schedule, int epochStartYear)
        {
            try
            {
                List<SystemComponent> clones = CloneComponentsForConfiguration();
                List<HouseEventState> effectiveHouse = AssembleEffectiveHouseEvents(schedule, epochStartYear);
                IReadOnlyList<string> houseLabels = effectiveHouse.Count > 0
                    ? ApplyHouseEventStates(clones, effectiveHouse)
                    : Array.Empty<string>();
                List<HazardReplacement> replacements = CollectHazardReplacements(schedule, epochStartYear);
                IReadOnlyList<string> replacementLabels = ApplyHazardReplacements(clones, replacements);
                ApplyEvaluationAges(clones, epochStartYear);
                SystemRealization realization = RunConfigurationQuery(clones);

                var actionLabels = new List<string>(houseLabels.Count + replacementLabels.Count);
                actionLabels.AddRange(houseLabels);
                actionLabels.AddRange(replacementLabels);
                return new LifeCycleEpochRun(realization, actionLabels, effectiveHouse, houseLabels,
                    replacements, replacementLabels);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The life-cycle query epoch starting at year {epochStartYear} failed. {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Describes one intervention entry from the labels of the epoch its year opens: the
        /// entry's own states are the last-wins winners at that year, so its labels are read off
        /// the epoch's parallel label lists by reference.
        /// </summary>
        /// <param name="intervention">The intervention entry.</param>
        /// <param name="epoch">The epoch run its year opens.</param>
        /// <returns>The entry label.</returns>
        private static string DescribeIntervention(LifeCycleIntervention intervention, LifeCycleEpochRun epoch)
        {
            var parts = new List<string>(intervention.HouseEvents.Count + intervention.HazardReplacements.Count);
            for (int i = 0; i < intervention.HouseEvents.Count; i++)
            {
                for (int j = 0; j < epoch.EffectiveHouseEvents.Count; j++)
                {
                    if (ReferenceEquals(epoch.EffectiveHouseEvents[j], intervention.HouseEvents[i]))
                    {
                        parts.Add(epoch.HouseLabels[j]);
                        break;
                    }
                }
            }
            for (int i = 0; i < intervention.HazardReplacements.Count; i++)
            {
                for (int j = 0; j < epoch.AppliedReplacements.Count; j++)
                {
                    if (ReferenceEquals(epoch.AppliedReplacements[j], intervention.HazardReplacements[i]))
                    {
                        parts.Add(epoch.ReplacementLabels[j]);
                        break;
                    }
                }
            }
            return $"Year {intervention.Year}: {string.Join("; ", parts)}";
        }

        /// <summary>
        /// Accumulates one stream's per-type horizon aggregates for one epoch under both
        /// accounting conventions — the single aggregation authority every trajectory stream
        /// flows through, so the streams cannot drift. The scalar factors are epoch-level and
        /// stream-independent; the caller computes them once per epoch.
        /// </summary>
        /// <param name="means">The stream's per-type expected annual consequences for the epoch.</param>
        /// <param name="span">The epoch's exposure-year span.</param>
        /// <param name="annuitySegment">The epoch's annuity segment A(end) − A(start).</param>
        /// <param name="survivalAtStart">The probability every year before the epoch survived.</param>
        /// <param name="survivalYears">The expected surviving exposure years within the epoch.</param>
        /// <param name="firstYearDiscount">The discount factor for the epoch's first exposure year.</param>
        /// <param name="geometric">The epoch's discounted-survival geometric sum.</param>
        /// <param name="cumulative">The non-absorbing cumulative accumulator (updated in place).</param>
        /// <param name="presentValue">The non-absorbing present-value accumulator (updated in place).</param>
        /// <param name="absorbingCumulative">The absorbing cumulative accumulator (updated in place).</param>
        /// <param name="absorbingPresentValue">The absorbing present-value accumulator (updated in place).</param>
        private static void AccumulateStreamAggregates(IReadOnlyList<double> means, int span,
            double annuitySegment, double survivalAtStart, double survivalYears,
            double firstYearDiscount, double geometric,
            double[] cumulative, double[] presentValue,
            double[] absorbingCumulative, double[] absorbingPresentValue)
        {
            for (int t = 0; t < cumulative.Length; t++)
            {
                double mean = means[t];
                cumulative[t] += span * mean;
                presentValue[t] += mean * annuitySegment;
                absorbingCumulative[t] += mean * survivalAtStart * survivalYears;
                absorbingPresentValue[t] += mean * survivalAtStart * firstYearDiscount * geometric;
            }
        }

        /// <summary>
        /// Builds one life-cycle scope row from an epoch's mean curve sets, reading the Total,
        /// Excess, and Fail stream means per type from the same curve set — the one authority
        /// for every stream a trajectory carries.
        /// </summary>
        /// <param name="name">The scope display label.</param>
        /// <param name="curves">The primary curve set.</param>
        /// <param name="additionalCurves">The additional-type curve sets.</param>
        /// <param name="typeCount">The declared consequence-type count.</param>
        /// <returns>The scope row.</returns>
        private static LifeCycleEpochEntry CreateLifeCycleEntry(string name, Curves curves,
            IReadOnlyList<Curves> additionalCurves, int typeCount)
        {
            var means = new double[typeCount];
            var excessMeans = new double[typeCount];
            var failMeans = new double[typeCount];
            means[0] = curves.Total.Mean;
            excessMeans[0] = curves.Excess.Mean;
            failMeans[0] = curves.Fail.Mean;
            for (int t = 1; t < typeCount; t++)
            {
                means[t] = additionalCurves[t - 1].Total.Mean;
                excessMeans[t] = additionalCurves[t - 1].Excess.Mean;
                failMeans[t] = additionalCurves[t - 1].Fail.Mean;
            }
            return new LifeCycleEpochEntry(name, curves.Fail.TotalProbability, means,
                excessMeans, failMeans);
        }

        /// <summary>
        /// The annuity present-value factor for one unit per year over the given horizon —
        /// (1 − (1 + r)^−n)/r with the exact r → 0 limit n, evaluated in log space (the
        /// exposure-period conversions' exact expression shape, so stationary life-cycle
        /// aggregates reproduce them bit-for-bit). Delegates to the shared discounting
        /// arithmetic the cost-benefit layer prices with, so both layers evaluate one
        /// expression.
        /// </summary>
        /// <param name="years">The horizon in years.</param>
        /// <param name="discountRate">The annual discount rate.</param>
        /// <returns>The annuity factor.</returns>
        private static double AnnuityFactor(int years, double discountRate)
        {
            return DiscountingSupport.AnnuityFactor(years, discountRate);
        }

        /// <summary>
        /// Evaluates tolerable-risk criteria over a realization-summary ensemble: each entry's
        /// exceedance probability is the realization-weight fraction whose selected system-scope
        /// measure is strictly greater than the criterion threshold. NaN measures are filtered
        /// pairwise with their weights, null realization slots are skipped with theirs, and a
        /// zero surviving weight reports NaN — the all-NaN convention.
        /// </summary>
        /// <param name="criteria">The configured criteria, in declared order.</param>
        /// <param name="summaries">The per-realization summaries, by realization slot.</param>
        /// <param name="weights">The realization weights parallel to the slots, or null for equal weights.</param>
        /// <returns>One confidence entry per criterion.</returns>
        private static List<TolerableRiskConfidence> EvaluateTolerableRiskCriteria(
            IReadOnlyList<TolerableRiskCriterion> criteria, IReadOnlyList<SystemRiskResults?> summaries,
            double[]? weights)
        {
            var entries = new List<TolerableRiskConfidence>(criteria.Count);
            for (int c = 0; c < criteria.Count; c++)
            {
                var criterion = criteria[c];
                double exceedingWeight = 0d;
                double totalWeight = 0d;
                for (int i = 0; i < summaries.Count; i++)
                {
                    var summary = summaries[i];
                    if (summary == null) continue;
                    var stream = SelectScope(summary, -1, -1, criterion.RiskType, criterion.ConsequenceTypeIndex);
                    double value = stream != null ? ExtractMeasure(stream, criterion.Measure) : double.NaN;
                    if (double.IsNaN(value)) continue;
                    double weight = weights == null ? 1d : weights[i];
                    totalWeight += weight;
                    if (value > criterion.Threshold) exceedingWeight += weight;
                }
                entries.Add(new TolerableRiskConfidence
                {
                    Measure = criterion.Measure.ToString(),
                    RiskType = criterion.RiskType.ToString(),
                    ConsequenceTypeIndex = criterion.ConsequenceTypeIndex,
                    Threshold = criterion.Threshold,
                    ExceedanceProbability = totalWeight > 0d ? exceedingWeight / totalWeight : double.NaN,
                });
            }
            return entries;
        }

        /// <summary>
        /// Resolves one realization summary's scope stream, or null when the summary does not
        /// carry it (shape drift across a loaded ensemble). Internal so the cost-benefit layer
        /// resolves measures through the same switch pair the engine and the tolerable-risk
        /// confidence evaluation use.
        /// </summary>
        /// <param name="summary">The realization summary.</param>
        /// <param name="componentIndex">The component scope (−1 = system).</param>
        /// <param name="failureModeIndex">The mode narrowing (−1 = none).</param>
        /// <param name="riskType">The stream.</param>
        /// <param name="consequenceType">The consequence-type position.</param>
        /// <returns>The stream summary, or null.</returns>
        internal static SummaryRiskResults? SelectScope(SystemRiskResults summary, int componentIndex, int failureModeIndex,
            RiskType riskType, int consequenceType)
        {
            if (componentIndex < 0)
            {
                if (consequenceType == 0) return SystemStream(summary, riskType);
                int k = consequenceType - 1;
                return k < summary.AdditionalConsequences.Count ? TypeStream(summary.AdditionalConsequences[k], riskType) : null;
            }
            if (componentIndex >= summary.ComponentResults.Count) return null;
            var component = summary.ComponentResults[componentIndex];
            if (failureModeIndex < 0)
            {
                if (consequenceType == 0) return ComponentStream(component, riskType);
                int k = consequenceType - 1;
                return k < component.AdditionalConsequences.Count ? TypeStream(component.AdditionalConsequences[k], riskType) : null;
            }
            if (failureModeIndex >= component.FailureModeResults.Count) return null;
            var mode = component.FailureModeResults[failureModeIndex];
            if (consequenceType == 0)
            {
                return riskType == RiskType.Excess ? mode.Excess : mode.Fail;
            }
            int typeIndex = consequenceType - 1;
            if (typeIndex >= mode.AdditionalConsequences.Count) return null;
            return TypeStream(mode.AdditionalConsequences[typeIndex], riskType);
        }

        /// <summary>Selects a system-scope stream summary.</summary>
        /// <param name="summary">The system summary.</param>
        /// <param name="riskType">The stream.</param>
        /// <returns>The stream summary.</returns>
        private static SummaryRiskResults SystemStream(SystemRiskResults summary, RiskType riskType)
        {
            return riskType switch
            {
                RiskType.Excess => summary.Excess,
                RiskType.Background => summary.Background,
                RiskType.Total => summary.Total,
                RiskType.Fail => summary.Fail,
                _ => summary.NonFail,
            };
        }

        /// <summary>Selects a component-scope stream summary.</summary>
        /// <param name="component">The component summary.</param>
        /// <param name="riskType">The stream.</param>
        /// <returns>The stream summary.</returns>
        private static SummaryRiskResults ComponentStream(ComponentResults component, RiskType riskType)
        {
            return riskType switch
            {
                RiskType.Excess => component.Excess,
                RiskType.Background => component.Background,
                RiskType.Total => component.Total,
                RiskType.Fail => component.Fail,
                _ => component.NonFail,
            };
        }

        /// <summary>Selects a consequence-type stream summary.</summary>
        /// <param name="results">The type's summary set.</param>
        /// <param name="riskType">The stream.</param>
        /// <returns>The stream summary.</returns>
        private static SummaryRiskResults TypeStream(ConsequenceResults results, RiskType riskType)
        {
            return riskType switch
            {
                RiskType.Excess => results.Excess,
                RiskType.Background => results.Background,
                RiskType.Total => results.Total,
                RiskType.Fail => results.Fail,
                _ => results.NonFail,
            };
        }

        /// <summary>Extracts one scalar measure from a stream summary. Internal so the
        /// cost-benefit layer resolves measures through the same switch the engine uses.</summary>
        /// <param name="summary">The stream summary.</param>
        /// <param name="measure">The measure.</param>
        /// <returns>The value (possibly NaN).</returns>
        internal static double ExtractMeasure(SummaryRiskResults summary, RiskMeasure measure)
        {
            return measure switch
            {
                RiskMeasure.TotalProbability => summary.TotalProbability,
                RiskMeasure.ConditionalMean => summary.ConditionalMean,
                RiskMeasure.Mean => summary.Mean,
                RiskMeasure.StandardDeviation => summary.StandardDeviation,
                RiskMeasure.Skewness => summary.Skewness,
                RiskMeasure.Kurtosis => summary.Kurtosis,
                RiskMeasure.ConsequenceThresholdProbability => summary.ConsequenceThresholdProbability,
                RiskMeasure.HazardThresholdProbability => summary.HazardThresholdProbability,
                RiskMeasure.ValueAtRisk => summary.ValueAtRisk,
                _ => summary.ConditionalValueAtRisk,
            };
        }

        /// <summary>
        /// Correlates every input column against an output vector: pairwise NaN filtering on
        /// the output, the selected association per column (a non-finite association coerces to
        /// zero — the v1.0 convention), entries in walk order. The given-data measures route
        /// through the upstream global-sensitivity estimators under the documented bin
        /// convention and need at least as many valid pairs as bins.
        /// </summary>
        /// <param name="inputs">The labeled input columns.</param>
        /// <param name="outputs">The per-realization output values (NaN = unavailable).</param>
        /// <param name="measure">The association measure.</param>
        /// <param name="riskType">The output's stream (carried on the result).</param>
        /// <param name="outputLabel">The output's display label.</param>
        /// <returns>
        /// The result, or null when fewer than three valid pairs remain — fewer than the bin
        /// convention for a given-data measure.
        /// </returns>
        private static SensitivityResults? Correlate(List<SensitivityInput> inputs, double[] outputs,
            SensitivityMeasure measure, RiskType riskType, string outputLabel)
        {
            bool givenData = measure is SensitivityMeasure.FirstOrderSobol
                or SensitivityMeasure.PawnMedian or SensitivityMeasure.BorgonovoDelta;
            var validIndices = new List<int>(outputs.Length);
            for (int i = 0; i < outputs.Length; i++)
            {
                if (!double.IsNaN(outputs[i])) validIndices.Add(i);
            }
            if (validIndices.Count < (givenData ? GivenDataBins : 3)) return null;

            int count = validIndices.Count;
            var outputVector = new double[count];
            for (int i = 0; i < count; i++)
            {
                outputVector[i] = outputs[validIndices[i]];
            }

            var inputVector = new double[count];
            var entries = new List<SensitivityEntry>(inputs.Count);
            for (int c = 0; c < inputs.Count; c++)
            {
                var read = inputs[c].Read;
                for (int i = 0; i < count; i++)
                {
                    inputVector[i] = read(validIndices[i]);
                }
                double value = measure switch
                {
                    SensitivityMeasure.PearsonCorrelation => Correlation.Pearson(inputVector, outputVector),
                    SensitivityMeasure.SpearmanCorrelation => Correlation.Spearman(inputVector, outputVector),
                    SensitivityMeasure.FirstOrderSobol or SensitivityMeasure.PawnMedian or SensitivityMeasure.BorgonovoDelta
                        => GivenDataAssociation(measure, inputVector, outputVector),
                    _ => Tools.Sqr(Correlation.Pearson(inputVector, outputVector)),
                };
                if (!Tools.IsFinite(value)) value = 0d;
                entries.Add(new SensitivityEntry(inputs[c].Label, value));
            }
            return new SensitivityResults(outputLabel, riskType, measure, count, entries);
        }

        /// <summary>
        /// Evaluates one given-data association through the upstream global-sensitivity
        /// estimators: the first-order Sobol index, the PAWN median, or Borgonovo's delta,
        /// each under the documented bin convention. A non-finite value anywhere in either
        /// vector reports NaN (the caller's zero-coercion convention) rather than faulting the
        /// whole query on one corrupt column.
        /// </summary>
        /// <param name="measure">The given-data measure.</param>
        /// <param name="inputs">The column's valid-pair input values.</param>
        /// <param name="outputs">The valid-pair output values.</param>
        /// <returns>The association, or NaN for a non-finite sample.</returns>
        private static double GivenDataAssociation(SensitivityMeasure measure, double[] inputs, double[] outputs)
        {
            for (int i = 0; i < inputs.Length; i++)
            {
                if (!Tools.IsFinite(inputs[i]) || !Tools.IsFinite(outputs[i])) return double.NaN;
            }
            // A bit-constant input column (a pinned function's draws) carries no association by
            // definition; report exactly zero rather than an estimator noise floor.
            bool constant = true;
            for (int i = 1; i < inputs.Length; i++)
            {
                if (inputs[i] != inputs[0]) { constant = false; break; }
            }
            if (constant) return 0d;
            return measure switch
            {
                SensitivityMeasure.FirstOrderSobol => GlobalSensitivity.FirstOrderSobol(inputs, outputs, GivenDataBins),
                SensitivityMeasure.PawnMedian => GlobalSensitivity.PawnMedian(inputs, outputs, GivenDataBins),
                _ => GlobalSensitivity.BorgonovoDelta(inputs, outputs, GivenDataBins, GivenDataBins),
            };
        }

        #endregion

        #region Private Helpers — Realization Compute

        /// <summary>
        /// Computes one full realization. On the one-dimensional and additive paths each
        /// component gets its own adaptive Gauss–Kronrod pass over its hazard probability domain
        /// and the exact curves, profiles, and risk measures are built per component; a single
        /// component's curves are the system curves (v1.0 behavior), and multiple additive
        /// components aggregate by zero-inflated lattice convolution. The joint path instead
        /// integrates the correlated hazard hypercube with VEGAS, recording component and system
        /// points together.
        /// </summary>
        /// <param name="realizationIndex">The realization index, or −1 for the mean pass.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="token">The run cancellation token.</param>
        /// <returns>The computed realization.</returns>
        private SystemRealization ComputeRealization(int realizationIndex, RiskComputeFlags flags, CancellationToken token)
        {
            var componentRealizations = new List<ComponentRealization>(_components.Count);
            var sampledComponents = new SampledComponent[_components.Count];
            int additionalTypes = 0;
            for (int i = 0; i < _components.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                sampledComponents[i] = _components[i].Sample(realizationIndex);
                sampledComponents[i].RecordAdjustedModeCurves = _options.OutputAdjustedFailureModeCurves;
                if (sampledComponents[i].HasConditionalSecondary)
                {
                    // The conditional-quadrature discipline mirrors the primary axis's
                    // ensemble/mean split; reliability mode refines on failure probability
                    // alone (its consequence surrogate is identically zero).
                    bool ensembleRealization = realizationIndex >= 0;
                    sampledComponents[i].ConfigureConditionalQuadrature(
                        ensembleRealization ? _options.EnsembleTolerance : _options.Tolerance,
                        ensembleRealization ? _options.EnsembleMinDepth : 2,
                        _options.MaxDepth,
                        _options.Mode != RiskAnalysisMode.Reliability);
                }
                additionalTypes = Math.Max(additionalTypes, sampledComponents[i].ConsequenceTypeCount - 1);
                componentRealizations.Add(new ComponentRealization(sampledComponents[i].FailureModeCount)
                {
                    Name = _components[i].Name,
                });
            }
            var realization = new SystemRealization(componentRealizations);
            realization.EnsureAdditionalCurves(additionalTypes);
            StampConsequenceLabels(realization);
            StampModeLabels(realization);
            for (int i = 0; i < componentRealizations.Count; i++)
            {
                componentRealizations[i].SetMeasureOptions(_options.RiskMeasures);
                if (_options.OutputAdjustedFailureModeCurves)
                {
                    var modes = componentRealizations[i].FailureModes;
                    for (int j = 0; j < modes.Count; j++)
                    {
                        modes[j].EnableAdjustedCurves(additionalTypes);
                        modes[j].SetMeasureOptions(_options.RiskMeasures);
                    }
                }
            }

            if (_components.Count > 1 && _options.SystemRiskMethod == SystemRiskType.JointRiskMethod)
            {
                IntegrateJointSystem(sampledComponents, componentRealizations, realization, flags, realizationIndex, token);
                if (_runRetainedDetailIndex != realizationIndex) realization.DumpMemory();
                return realization;
            }

            for (int i = 0; i < _components.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                using var ledger = IntegrateComponent(sampledComponents[i], componentRealizations[i], realization, flags, realizationIndex, token);
                componentRealizations[i].ApplyRecordedMass(ledger);
                componentRealizations[i].FinalizeContributions(ledger);
                componentRealizations[i].CreateCurves(_options.LECOutputLength);
                if ((_options.RiskMeasures & RiskMeasureOptions.RiskProfiles) != 0)
                {
                    componentRealizations[i].CreateProfiles(includeFailureModes: realizationIndex < 0);
                }
                componentRealizations[i].ComputeRiskMeasures(_options.ConsequenceThreshold, _options.Alpha, _components[i].HazardThreshold, _runAdditionalThresholds);

                realization.MinN = Math.Min(realization.MinN, componentRealizations[i].MinN);
                realization.MaxN = Math.Max(realization.MaxN, componentRealizations[i].MaxN);
                for (int k = 0; k < componentRealizations[i].AdditionalCurves.Count; k++)
                {
                    realization.AdditionalMinN[k] = Math.Min(realization.AdditionalMinN[k], componentRealizations[i].AdditionalMinN[k]);
                    realization.AdditionalMaxN[k] = Math.Max(realization.AdditionalMaxN[k], componentRealizations[i].AdditionalMaxN[k]);
                }
                realization.MinH[i] = componentRealizations[i].MinH;
                realization.MaxH[i] = componentRealizations[i].MaxH;
            }

            if (_components.Count == 1)
            {
                // Single-component system results are the component results (v1.0 behavior at
                // the legacy clone site), cloned per consequence type. The component's system
                // contribution is its own totals — a 100% share of the % contribution diagnostic.
                realization.Curves = componentRealizations[0].Curves.Clone();
                for (int k = 0; k < componentRealizations[0].AdditionalCurves.Count; k++)
                {
                    realization.AdditionalCurves[k] = componentRealizations[0].AdditionalCurves[k].Clone();
                }
                AssignOwnSystemContribution(componentRealizations[0]);
            }
            else
            {
                AggregateAdditiveSystem(realization, componentRealizations, token);
            }

            if (_runRetainedDetailIndex != realizationIndex) realization.DumpMemory();
            return realization;
        }

        /// <summary>
        /// Aggregates the additive system realization (strictly independent components by
        /// definition): each risk-type stream's exact recorded pairs are zero-inflated and convolved
        /// on the shared consequence lattice — the exact enumeration of all component
        /// failure/non-failure combinations — and the defective stream probabilities are then
        /// restored to the v1.0 system-state semantics: the failure union for Fail and Excess,
        /// its complement for NonFail (the convolution's own recorded mass measures "any positive
        /// consequence", which quantizes zero-valued events into the atom). The convolved system
        /// mean equals the sum of the component means by construction — the v1.0 additive answer,
        /// now with the full curve v1.0 never produced. The failure union folds the component
        /// probabilities in the canonical-hash component order — the same association the
        /// convolution uses — so declaration order cannot move the union even at the last bit
        /// (a reproducibility-pin finding; declaration order previously reassociated the
        /// union product by one or two units in the last place).
        /// </summary>
        /// <param name="realization">The system realization to fill.</param>
        /// <param name="componentRealizations">The finished per-component realizations.</param>
        /// <param name="token">The run cancellation token.</param>
        private void AggregateAdditiveSystem(SystemRealization realization,
            IReadOnlyList<ComponentRealization> componentRealizations, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ConvolveSystemStreams(realization.Curves, componentRealizations, c => c.Curves);
            for (int k = 0; k < realization.AdditionalCurves.Count; k++)
            {
                int typeIndex = k;
                ConvolveSystemStreams(realization.AdditionalCurves[k], componentRealizations, c => c.AdditionalCurves[typeIndex]);
            }

            // The failure union is type-independent — every type's defective streams carry the
            // same system-state probabilities.
            var order = _additiveConvolutionOrder!;
            var failureProbabilities = new double[componentRealizations.Count];
            for (int i = 0; i < componentRealizations.Count; i++)
            {
                failureProbabilities[i] = Tools.Clamp(componentRealizations[order[i]].Curves.Fail.TotalProbability, 0d, 1d);
            }
            double failureUnion = Tools.Clamp(Probability.IndependentUnion(failureProbabilities), 0d, 1d);
            double nonFailureComplement = Tools.Clamp(1d - failureUnion, 0d, 1d);
            realization.Curves.Fail.TotalProbability = failureUnion;
            realization.Curves.Excess.TotalProbability = failureUnion;
            realization.Curves.NonFail.TotalProbability = nonFailureComplement;
            for (int k = 0; k < realization.AdditionalCurves.Count; k++)
            {
                realization.AdditionalCurves[k].Fail.TotalProbability = failureUnion;
                realization.AdditionalCurves[k].Excess.TotalProbability = failureUnion;
                realization.AdditionalCurves[k].NonFail.TotalProbability = nonFailureComplement;
            }

            realization.Curves.ComputeRiskMeasures(_options.ConsequenceThreshold, _options.Alpha);
            for (int k = 0; k < realization.AdditionalCurves.Count; k++)
            {
                realization.AdditionalCurves[k].ComputeRiskMeasures(AdditionalThresholdAt(k), _options.Alpha);
            }

            // The system support tops out at the sum of the component maxima — widen the
            // percentile grid extent to each type's convolved Total curve.
            if (realization.Curves.Total.LECConsequences.Length > 0)
            {
                realization.MaxN = Math.Max(realization.MaxN, realization.Curves.Total.LECConsequences[0]);
            }
            for (int k = 0; k < realization.AdditionalCurves.Count; k++)
            {
                if (realization.AdditionalCurves[k].Total.LECConsequences.Length > 0)
                {
                    realization.AdditionalMaxN[k] = Math.Max(realization.AdditionalMaxN[k], realization.AdditionalCurves[k].Total.LECConsequences[0]);
                }
            }

            // The per-component system contribution (the % contribution diagnostic): the failure
            // union of strictly independent components splits exactly by the Shapley value
            // φ_i = p_i · E[1/(1 + K_i)], with K_i the Poisson–binomial count of the OTHER
            // components failing — the equal split of every exclusive failure combination,
            // computed in closed form by the standard subset-distribution recursion instead of
            // a 2^D enumeration. Σφ ≡ the independent union to floating-point association.
            // The mean contributions are the component means themselves — means add exactly
            // under the convolution. The recursion folds in the canonical-hash component order
            // so declaration order cannot move the attribution even at the last bit (the 4b
            // reorder contract).
            var countDistribution = new double[componentRealizations.Count];
            var nextDistribution = new double[componentRealizations.Count];
            for (int i = 0; i < componentRealizations.Count; i++)
            {
                var component = componentRealizations[i];
                Array.Clear(countDistribution, 0, countDistribution.Length);
                countDistribution[0] = 1d;
                int occupied = 1;
                for (int j = 0; j < order.Length; j++)
                {
                    if (order[j] == i) continue;
                    double p = componentRealizations[order[j]].Curves.Fail.TotalProbability;
                    Array.Clear(nextDistribution, 0, occupied + 1);
                    for (int c = 0; c < occupied; c++)
                    {
                        nextDistribution[c] += countDistribution[c] * (1d - p);
                        nextDistribution[c + 1] += countDistribution[c] * p;
                    }
                    occupied++;
                    Array.Copy(nextDistribution, countDistribution, occupied);
                }
                double expectedReciprocal = 0d;
                for (int c = 0; c < occupied; c++)
                {
                    expectedReciprocal += countDistribution[c] / (c + 1);
                }
                double shapleyShare = component.Curves.Fail.TotalProbability * expectedReciprocal;

                component.SystemContribution = new RiskContribution
                {
                    FailureProbability = shapleyShare,
                    FailureMean = component.Curves.Fail.Mean,
                    ExcessMean = component.Curves.Excess.Mean,
                };
                for (int k = 0; k < component.AdditionalCurves.Count; k++)
                {
                    while (component.AdditionalSystemContributions.Count <= k)
                    {
                        component.AdditionalSystemContributions.Add(null);
                    }
                    component.AdditionalSystemContributions[k] = new RiskContribution
                    {
                        FailureProbability = shapleyShare,
                        FailureMean = component.AdditionalCurves[k].Fail.Mean,
                        ExcessMean = component.AdditionalCurves[k].Excess.Mean,
                    };
                }
            }
        }

        /// <summary>
        /// Assigns a single-component system's contribution: the component's own totals — a
        /// 100% share of the system it constitutes (the % contribution diagnostic).
        /// </summary>
        /// <param name="component">The finished component realization.</param>
        private static void AssignOwnSystemContribution(ComponentRealization component)
        {
            component.SystemContribution = new RiskContribution
            {
                FailureProbability = component.Curves.Fail.TotalProbability,
                FailureMean = component.Curves.Fail.Mean,
                ExcessMean = component.Curves.Excess.Mean,
            };
            for (int k = 0; k < component.AdditionalCurves.Count; k++)
            {
                while (component.AdditionalSystemContributions.Count <= k)
                {
                    component.AdditionalSystemContributions.Add(null);
                }
                component.AdditionalSystemContributions[k] = new RiskContribution
                {
                    FailureProbability = component.Curves.Fail.TotalProbability,
                    FailureMean = component.AdditionalCurves[k].Fail.Mean,
                    ExcessMean = component.AdditionalCurves[k].Excess.Mean,
                };
            }
        }

        /// <summary>
        /// Convolves all five risk-type streams of one consequence type across the components
        /// onto the system curve set.
        /// </summary>
        /// <param name="target">The system curve set to fill.</param>
        /// <param name="componentRealizations">The finished per-component realizations.</param>
        /// <param name="scope">Selects the consequence type's curve set from a component realization.</param>
        private void ConvolveSystemStreams(Curves target, IReadOnlyList<ComponentRealization> componentRealizations,
            Func<ComponentRealization, Curves> scope)
        {
            ConvolveSystemStream(target.Excess, componentRealizations, scope, c => c.Excess);
            ConvolveSystemStream(target.Background, componentRealizations, scope, c => c.Background);
            ConvolveSystemStream(target.Total, componentRealizations, scope, c => c.Total);
            ConvolveSystemStream(target.Fail, componentRealizations, scope, c => c.Fail);
            ConvolveSystemStream(target.NonFail, componentRealizations, scope, c => c.NonFail);
        }

        /// <summary>
        /// Convolves one risk-type stream across the components onto the system curve: the
        /// components' exact recorded pairs feed the shared lattice, the joint zero atom (lattice
        /// node zero) is kept only on an exhaustive stream (on a defective stream it is the
        /// no-event mass), and the exact curve construction runs on the lattice pairs. A stream
        /// with no positive-consequence mass anywhere stays empty.
        /// </summary>
        /// <param name="target">The system stream to fill.</param>
        /// <param name="componentRealizations">The finished per-component realizations.</param>
        /// <param name="scope">Selects the consequence type's curve set from a component realization.</param>
        /// <param name="stream">Selects the stream from a component's curve set.</param>
        /// <remarks>
        /// The components enter the sequential convolution in canonical-hash order (computed at
        /// run start), so the floating-point association is identical however the components are
        /// declared — component reordering stays bit-inert on the system curves.
        /// </remarks>
        private void ConvolveSystemStream(Curve target, IReadOnlyList<ComponentRealization> componentRealizations,
            Func<ComponentRealization, Curves> scope, Func<Curves, Curve> stream)
        {
            var order = _additiveConvolutionOrder!;
            var componentPairs = new List<IReadOnlyList<(double Mass, double Consequence)>>(componentRealizations.Count);
            for (int i = 0; i < componentRealizations.Count; i++)
            {
                componentPairs.Add(stream(scope(componentRealizations[order[i]])).CollectRecordedPairs());
            }

            var (pmf, step) = SystemConvolution.Convolve(componentPairs, _options.SystemConvolutionPoints);
            if (step <= 0d)
            {
                if (target.IsExhaustive)
                {
                    target.CreateCurve(new[] { (Mass: 1d, Consequence: 0d) }, _options.LECOutputLength);
                }
                return;
            }

            var pairs = new List<(double Mass, double Consequence)>(pmf.Length);
            for (int k = target.IsExhaustive ? 0 : 1; k < pmf.Length; k++)
            {
                if (pmf[k] > 0d)
                {
                    pairs.Add((pmf[k], k * step));
                }
            }
            if (pairs.Count == 0) return;
            target.CreateCurve(pairs, _options.LECOutputLength);
        }

        /// <summary>
        /// Appends the per-thread first-fault evaluation diagnostic — when one was recorded — to
        /// an integration-failure message, so a deliberate evaluation refusal (the Error
        /// extrapolation policy) survives the integrators' exception absorption with its full
        /// function/axis/value/range diagnostic instead of the generic failure text.
        /// </summary>
        /// <param name="message">The integration-failure message.</param>
        /// <returns>The message, with the recorded fault appended when present.</returns>
        private static string AppendEvaluationFault(string message)
        {
            string? fault = EvaluationFaultScope.Consume();
            return fault == null ? message : message + " First recorded evaluation fault: " + fault;
        }

        /// <summary>
        /// Runs the additive-path quadrature for one component: a bivariate component
        /// integrates its (primary, conditional) plane with the globally adaptive
        /// two-dimensional Gauss–Kronrod rule, everything else — univariate components and the
        /// discretization-error instrument's fixed-grid snapshots — runs the one-dimensional
        /// pass. Either way the recorded evaluations populate the risk points, the returned
        /// integral value is discarded, and the evaluation count and error estimate are kept as
        /// diagnostics.
        /// </summary>
        /// <returns>The sealed exhaustive mass ledger.</returns>
        /// <param name="sampled">The sampled component.</param>
        /// <param name="componentRealization">The component's realization sink.</param>
        /// <param name="realization">The system realization (diagnostics).</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="realizationIndex">
        /// The realization index, or −1 for the mean pass. Ensemble realizations (index ≥ 0)
        /// integrate at the relaxed <see cref="RiskAnalysisOptions.EnsembleTolerance"/> /
        /// <see cref="RiskAnalysisOptions.EnsembleMinDepth"/> discipline — the v1.0 philosophy:
        /// ensemble statistics average integration noise, so the ~4,200-evaluation forced floor
        /// of the full discipline is spent only where a single answer is published (the mean
        /// pass, mean-only runs, and the deterministic probes).
        /// </param>
        /// <param name="token">The active run cancellation token.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the integration reports failure — an integrand exception was absorbed by
        /// the integrator (<c>ReportFailure</c> is false), so the recorded risk points are
        /// truncated and no result may be published. Surfacing the failure here keeps a faulted
        /// evaluation from silently reading as zero risk (the VEGAS
        /// call sites carry the same guard).
        /// </exception>
        private QuadratureMassLedger IntegrateComponent(SampledComponent sampled, ComponentRealization componentRealization,
            SystemRealization realization, RiskComputeFlags flags, int realizationIndex, CancellationToken token)
        {
            return sampled.HasConditionalSecondary && !sampled.UsesFixedConditionalGrid
                ? IntegrateComponentAdaptive2D(sampled, componentRealization, realization, flags, realizationIndex, token)
                : IntegrateComponentConditionalGrid(sampled, componentRealization, realization, flags, realizationIndex, token);
        }

        /// <summary>
        /// The one-dimensional conditional-grid interior: the adaptive Gauss–Kronrod pass over
        /// the primary probability domain, with any conditional secondary dimension evaluated
        /// on its snapshot's grid per evaluation. Every univariate component integrates here,
        /// and so do the discretization-error instrument's fixed-grid snapshots — the
        /// Richardson ladder measures exactly this interior at reduced bin counts.
        /// </summary>
        /// <param name="sampled">The sampled component.</param>
        /// <param name="componentRealization">The component's realization sink.</param>
        /// <param name="realization">The system realization (diagnostics).</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="realizationIndex">The realization index, or −1 for the mean pass.</param>
        /// <param name="token">The active run cancellation token.</param>
        /// <returns>The sealed exhaustive mass ledger.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the integration reports failure.</exception>
        private QuadratureMassLedger IntegrateComponentConditionalGrid(SampledComponent sampled, ComponentRealization componentRealization,
            SystemRealization realization, RiskComputeFlags flags, int realizationIndex, CancellationToken token)
        {
            // One stratification build serves the balanced-objective scales and the integrator
            // seeding alike (the probes run before the integrator touches the list).
            var bins = BuildStratificationBins(sampled, flags);
            var support = HazardProbabilitySupport.Create(bins);
            var objective = BuildObjective(sampled, componentRealization, flags, bins);
            bool ensemble = realizationIndex >= 0;
            int expectedNodes = Math.Min(_options.MaxEvaluations + 2, Math.Max(256, _options.LECOutputLength * 16));
            var ledger = new QuadratureMassLedger(expectedNodes);

            int interiorEvaluations = 0;
            double standardError = 0d;
            if (support.Upper > support.Lower)
            {
                var integrator = new AdaptiveGaussKronrod(objective, support.Lower, support.Upper)
                {
                    ReportFailure = false,
                    MaxFunctionEvaluations = _options.MaxEvaluations,
                    MaxDepth = _options.MaxDepth,
                    RelativeTolerance = ensemble ? _options.EnsembleTolerance : _options.Tolerance,
                    MinDepth = ensemble ? _options.EnsembleMinDepth : 2,
                    Recorder = ledger.Record,
                };
                integrator.Integrate(bins);
                token.ThrowIfCancellationRequested();
                if (integrator.Status == IntegrationStatus.Failure)
                {

                    throw new InvalidOperationException(AppendEvaluationFault($"The risk integration failed for system component '{sampled.Name}': an integrand evaluation threw and the recorded curves are incomplete. The analysis cannot publish results for this run."));
                }
                interiorEvaluations = integrator.FunctionEvaluations;
                standardError = integrator.StandardError;
            }

            int endpointEvaluations = support.CompleteExhaustive(ledger.RunningTotalWeight,
                objective, ledger.Record, $"system component '{sampled.Name}'");
            ledger.SealExhaustive();
            realization.FunctionEvaluations += interiorEvaluations + endpointEvaluations;
            realization.StandardError += standardError / _components.Count;
            return ledger;
        }

        /// <summary>
        /// The two-dimensional adaptive interior of a bivariate component: per stratification
        /// strip, the globally adaptive tensor Gauss–Kronrod rule refines the failure-density
        /// surrogate over (primary non-exceedance, conditional probability) — the copula
        /// transform makes that plane exactly uniform, so the flushed weights are probability
        /// mass — and the flushed composite groups by exact primary abscissa into merged
        /// conditional columns the staged evaluation replays, committing ONE risk point per
        /// stream per distinct abscissa with the column's group mass recorded in the ledger.
        /// The strip edges keep the v1.0 stratification and the objective-discontinuity
        /// injections; the support-edge endpoint columns evaluate through the recording
        /// objective (the per-slice adaptive sweep) exactly like the one-dimensional path; and
        /// the sealed ledger's exhaustive gates hold unchanged. Where the refinement splits the
        /// primary axis at different conditional bands, neighboring columns cover complementary
        /// conditional slabs whose masses partition the area exactly — the documented
        /// output-granularity property of the two-dimensional rule.
        /// </summary>
        /// <param name="sampled">The sampled bivariate component.</param>
        /// <param name="componentRealization">The component's realization sink.</param>
        /// <param name="realization">The system realization (diagnostics: the surrogate evaluation count; the error estimate is the conservative per-strip |K − G| sum, not the one-dimensional root-sum-square).</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="realizationIndex">The realization index, or −1 for the mean pass (the ensemble/mean discipline split).</param>
        /// <param name="token">The active run cancellation token.</param>
        /// <returns>The sealed exhaustive mass ledger.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a strip's integration reports failure (a surrogate evaluation threw), or
        /// when a non-failed pass flushed no nodes — the recorder contract guarantees a
        /// complete flush on every non-throwing outcome, so an empty flush is an invariant
        /// break, never a silent zero.
        /// </exception>
        private QuadratureMassLedger IntegrateComponentAdaptive2D(SampledComponent sampled, ComponentRealization componentRealization,
            SystemRealization realization, RiskComputeFlags flags, int realizationIndex, CancellationToken token)
        {
            var bins = BuildStratificationBins(sampled, flags);

            // The evaluation budget is fair-shared across the positive-width strips: a
            // sequential hand-me-down lets early strips exhaust the budget and leaves the
            // tail strips at the 441-evaluation tensor floor — coarse columns whose
            // conditional error dwarfs the converged strips' (measured on the degenerate
            // seismic probe before this allocation was adopted). The endpoint columns get the
            // same share: an endpoint rectangle can carry most of a hazard's mass, and its
            // per-slice sweep runs once per pass, so the per-evaluation seat budget would
            // starve exactly the heaviest column. Configured before the balanced objective's
            // probe pass can build the sweep machinery.
            int activeStrips = 0;
            for (int i = 0; i < bins.Count; i++)
            {
                if (bins[i].UpperBound > bins[i].LowerBound) activeStrips++;
            }
            int stripBudget = Math.Max(441, _options.MaxEvaluations / Math.Max(1, activeStrips));
            sampled.ConfigureConditionalSweepBudget(stripBudget);

            var support = HazardProbabilitySupport.Create(bins);
            var objective = BuildObjective(sampled, componentRealization, flags, bins);
            bool ensemble = realizationIndex >= 0;
            int expectedNodes = Math.Min(_options.MaxEvaluations + 2, Math.Max(256, _options.LECOutputLength * 16));
            var ledger = new QuadratureMassLedger(expectedNodes);

            int surrogateEvaluations = 0;
            int replayPoints = 0;
            double standardError = 0d;
            if (support.Upper > support.Lower)
            {
                using var columns = new ConditionalColumnLedger(expectedNodes);

                for (int i = 0; i < bins.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    double lower = bins[i].LowerBound;
                    double upper = bins[i].UpperBound;
                    if (!(upper > lower)) continue;

                    // The recorder folds the probit Jacobian into each node's probability mass
                    // and tracks the strip's raw mass for the exact-width renormalization below.
                    int stripStart = columns.NodeCount;
                    double stripRawMass = 0d;
                    var integrator = new AdaptiveGaussKronrod2D(sampled.ConditionalDensityAt, lower, upper,
                        SampledComponent.ConditionalZFloor, SampledComponent.ConditionalZCeiling)
                    {
                        ReportFailure = false,
                        RelativeTolerance = ensemble ? _options.EnsembleTolerance : _options.Tolerance,
                        // The acceptance test is absolute OR relative; conditional risk mass
                        // can sit many orders below the inherited default, so the absolute
                        // branch is pinned at the framework floor.
                        AbsoluteTolerance = 1e-15,
                        MinDepth = ensemble ? _options.EnsembleMinDepth : 2,
                        MaxDepth = _options.MaxDepth,
                        MaxFunctionEvaluations = stripBudget,
                        Recorder = (x, z, weight, value) =>
                        {
                            double mass = weight * Normal.StandardPDF(z);
                            stripRawMass += mass;
                            columns.Record(x, z, mass, value);
                        },
                    };
                    integrator.Integrate();
                    if (integrator.Status == IntegrationStatus.Failure)
                    {
                        throw new InvalidOperationException(AppendEvaluationFault($"The conditional risk integration failed for system component '{sampled.Name}': a surrogate evaluation threw and the recorded columns are incomplete. The analysis cannot publish results for this run."));
                    }
                    surrogateEvaluations += integrator.FunctionEvaluations;
                    standardError += integrator.StandardError;

                    // The per-strip probit renormalization: the folded masses integrate φ to
                    // the refinement tolerance; scaling to the strip's exact probability width
                    // keeps every downstream exhaustive-mass gate exact while bounding the
                    // distortion by that tolerance. A material departure is structural mass
                    // loss, never rescaled silently.
                    double stripWidth = upper - lower;
                    if (!(stripRawMass > 0d) || Math.Abs(1d - stripRawMass / stripWidth) > 1e-3)
                    {
                        throw new InvalidOperationException($"The probit conditional mass of system component '{sampled.Name}' integrated to {stripRawMass:R} against a strip width of {stripWidth:R}.");
                    }
                    columns.ScaleRange(stripStart, columns.NodeCount - stripStart, stripWidth / stripRawMass);
                }

                columns.Seal();
                if (columns.GroupCount == 0)
                {
                    throw new InvalidOperationException($"The two-dimensional quadrature of system component '{sampled.Name}' flushed no accepted nodes.");
                }

                var tBuffer = new double[columns.MaxGroupNodeCount];
                var wBuffer = new double[columns.MaxGroupNodeCount];
                for (int g = 0; g < columns.GroupCount; g++)
                {
                    token.ThrowIfCancellationRequested();
                    double x = columns.GroupAbscissa(g);
                    int count = columns.FillGroupNormalized(g, tBuffer, wBuffer);
                    double hazardLevel = sampled.Hazard.InverseCDF(x);
                    sampled.ComputeRiskBivariateColumns(x, hazardLevel, flags, componentRealization, recordOutput: true,
                        null, x, count, tBuffer, wBuffer);
                    ledger.Record(x, columns.GroupMass(g), 0d);
                }
                replayPoints = columns.GroupCount;
            }

            int endpointEvaluations = support.CompleteExhaustive(ledger.RunningTotalWeight,
                objective, ledger.Record, $"system component '{sampled.Name}'");
            ledger.SealExhaustive();
            realization.FunctionEvaluations += surrogateEvaluations + replayPoints + endpointEvaluations;
            realization.StandardError += standardError / _components.Count;
            return ledger;
        }

        /// <summary>
        /// The effective adaptive-refinement objective: reliability mode always refines on the
        /// failure probability (its natural pairing — a consequence-free model's consequence
        /// objectives are identically zero, which would defeat the adaptivity); risk mode uses
        /// the configured objective.
        /// </summary>
        private RiskIntegrand EffectiveIntegrand => _options.Mode == RiskAnalysisMode.Reliability
            ? RiskIntegrand.TotalProbabilityOfFailure
            : _options.RiskIntegrand;

        /// <summary>
        /// Builds the integrand for the selected refinement objective
        /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.7).
        /// Every evaluation computes and records the full component risk regardless of the
        /// objective — the objective changes only where the adaptive refinement concentrates.
        /// </summary>
        /// <param name="sampled">The sampled component.</param>
        /// <param name="componentRealization">The component's realization sink.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="bins">The component's stratification bins (the balanced objective's scale probes sweep their edges).</param>
        /// <returns>The integrand over hazard non-exceedance probability.</returns>
        private Func<double, double> BuildObjective(SampledComponent sampled, ComponentRealization componentRealization,
            RiskComputeFlags flags, List<StratificationBin> bins)
        {
            if (EffectiveIntegrand == RiskIntegrand.Balanced)
            {
                var (meanScale, secondScale, tailScale) = ObjectiveScales(sampled, flags, bins);
                return p =>
                {
                    var output = Evaluate(sampled, componentRealization, flags, p, recordOutput: true);
                    return ObjectiveValue(output, p, RiskIntegrand.MeanTotalRisk) / meanScale
                        + ObjectiveValue(output, p, RiskIntegrand.SecondMoment) / secondScale
                        + ObjectiveValue(output, p, RiskIntegrand.TailConditionalRisk) / tailScale;
                };
            }

            var integrand = EffectiveIntegrand;
            return p =>
            {
                var output = Evaluate(sampled, componentRealization, flags, p, recordOutput: true);
                return ObjectiveValue(output, p, integrand);
            };
        }

        /// <summary>
        /// Evaluates the component risk at one hazard probability.
        /// </summary>
        /// <param name="sampled">The sampled component.</param>
        /// <param name="componentRealization">The component's realization sink.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="probability">The hazard non-exceedance probability.</param>
        /// <param name="recordOutput">True to record risk points.</param>
        /// <returns>The component risk output.</returns>
        private static ComponentRiskOutput Evaluate(SampledComponent sampled, ComponentRealization componentRealization,
            RiskComputeFlags flags, double probability, bool recordOutput)
        {
            double hazardLevel = sampled.Hazard.InverseCDF(probability);
            return sampled.ComputeRisk(probability, hazardLevel, flags, componentRealization, recordOutput,
                hazardNonExceedance: probability);
        }

        /// <summary>
        /// The refinement-objective value at one evaluation (the
        /// docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.7 table). Every member operates on
        /// the component output as delivered — for a bivariate component that output is already
        /// marginalized over the conditional secondary bins, with the entry lists enumerating
        /// the conditional mixture, and the tail objective's α boundary lies on the PRIMARY
        /// hazard's probability axis.
        /// </summary>
        /// <param name="output">The component risk output at the evaluation point.</param>
        /// <param name="probability">The hazard non-exceedance probability.</param>
        /// <param name="integrand">The objective member (never <see cref="RiskIntegrand.Balanced"/> here).</param>
        /// <returns>The objective value.</returns>
        private double ObjectiveValue(ComponentRiskOutput output, double probability, RiskIntegrand integrand)
        {
            switch (integrand)
            {
                case RiskIntegrand.MeanIncrementalRisk:
                    return output.ProbabilityOfFailure * output.MeanExcessConsequences;
                case RiskIntegrand.TotalProbabilityOfFailure:
                    return output.ProbabilityOfFailure;
                case RiskIntegrand.TailConditionalRisk:
                    return probability <= _options.Alpha
                        ? output.ProbabilityOfFailure * output.MeanFailureConsequences
                        : 0d;
                case RiskIntegrand.ThresholdExceedanceProbability:
                    {
                        double exceedance = 0d;
                        for (int i = 0; i < output.ResponseProbabilities.Count; i++)
                        {
                            if (output.FailureConsequences[i] > _options.ConsequenceThreshold) exceedance += output.ResponseProbabilities[i];
                        }
                        if (output.NonFailureConsequences > _options.ConsequenceThreshold) exceedance += output.ProbabilityOfNonFailure;
                        return exceedance;
                    }
                case RiskIntegrand.SecondMoment:
                    {
                        double secondMoment = 0d;
                        for (int i = 0; i < output.ResponseProbabilities.Count; i++)
                        {
                            secondMoment += output.ResponseProbabilities[i] * output.FailureConsequences[i] * output.FailureConsequences[i];
                        }
                        secondMoment += output.ProbabilityOfNonFailure * output.NonFailureConsequences * output.NonFailureConsequences;
                        return secondMoment;
                    }
                default:
                    return output.ProbabilityOfFailure * output.MeanFailureConsequences
                        + output.ProbabilityOfNonFailure * output.NonFailureConsequences;
            }
        }

        /// <summary>
        /// Estimates the normalization scales for the balanced objective's three members from
        /// ONE non-recording pre-pass over the stratification-bin edges — each evaluation feeds
        /// all three accumulations, so the pass costs a third of the per-member probes it
        /// replaces while producing the identical per-member sums (an earlier shape
        /// ran three separate sweeps and rebuilt the bins each time). A vanishing scale falls
        /// back to one so the balanced sum stays finite.
        /// </summary>
        /// <param name="sampled">The sampled component.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="bins">The component's stratification bins.</param>
        /// <returns>The positive normalization scales (mean, second moment, tail).</returns>
        private (double Mean, double Second, double Tail) ObjectiveScales(SampledComponent sampled, RiskComputeFlags flags,
            List<StratificationBin> bins)
        {
            var scratch = new ComponentRealization(sampled.FailureModeCount);
            double meanScale = 0d;
            double secondScale = 0d;
            double tailScale = 0d;
            for (int i = 0; i < bins.Count; i++)
            {
                var output = Evaluate(sampled, scratch, flags, bins[i].LowerBound, recordOutput: false);
                meanScale += Math.Abs(ObjectiveValue(output, bins[i].LowerBound, RiskIntegrand.MeanTotalRisk));
                secondScale += Math.Abs(ObjectiveValue(output, bins[i].LowerBound, RiskIntegrand.SecondMoment));
                tailScale += Math.Abs(ObjectiveValue(output, bins[i].LowerBound, RiskIntegrand.TailConditionalRisk));
            }
            var last = Evaluate(sampled, scratch, flags, bins[bins.Count - 1].UpperBound, recordOutput: false);
            meanScale += Math.Abs(ObjectiveValue(last, bins[bins.Count - 1].UpperBound, RiskIntegrand.MeanTotalRisk));
            secondScale += Math.Abs(ObjectiveValue(last, bins[bins.Count - 1].UpperBound, RiskIntegrand.SecondMoment));
            tailScale += Math.Abs(ObjectiveValue(last, bins[bins.Count - 1].UpperBound, RiskIntegrand.TailConditionalRisk));
            return (meanScale > 0d ? meanScale : 1d, secondScale > 0d ? secondScale : 1d, tailScale > 0d ? tailScale : 1d);
        }

        /// <summary>
        /// Builds the stratified hazard bins seeding the adaptive integrator (50 hazard-space
        /// bins mapped to probability — v1.0 constants), injecting the discontinuity of a
        /// discontinuous objective as a bin boundary so no quadrature panel spans the jump
        /// (§7.7): the tail objective switches at p = α, and the threshold objective crosses
        /// where the consequence passes the threshold (located by Brent's method on this
        /// realization's chain when a sign change brackets it).
        /// </summary>
        /// <param name="sampled">The sampled component.</param>
        /// <param name="flags">The realization's computational-warning flags (the threshold probe evaluates the chain).</param>
        /// <returns>The stratification bins in probability space.</returns>
        private List<StratificationBin> BuildStratificationBins(SampledComponent sampled, RiskComputeFlags flags)
        {
            var bins = BuildHazardBins(sampled);

            if (EffectiveIntegrand == RiskIntegrand.TailConditionalRisk)
            {
                InjectBoundary(bins, _options.Alpha);
            }
            else if (EffectiveIntegrand == RiskIntegrand.ThresholdExceedanceProbability)
            {
                var scratch = new ComponentRealization(sampled.FailureModeCount);
                double Crossing(double p)
                {
                    var output = Evaluate(sampled, scratch, flags, p, recordOutput: false);
                    return output.MeanFailureConsequences - _options.ConsequenceThreshold;
                }
                double lower = bins[0].LowerBound;
                double upper = bins[bins.Count - 1].UpperBound;
                double atLower = Crossing(lower);
                double atUpper = Crossing(upper);
                if (atLower * atUpper < 0d)
                {
                    double root = Brent.Solve(Crossing, lower, upper);
                    InjectBoundary(bins, root);
                }
            }
            return bins;
        }

        /// <summary>
        /// Builds the plain 50-bin hazard stratification in probability space (v1.0 constants) —
        /// shared by the risk integral's seeding and the tail-focus quadrature probe.
        /// </summary>
        /// <param name="sampled">The sampled component.</param>
        /// <returns>The stratification bins in probability space.</returns>
        private static List<StratificationBin> BuildHazardBins(SampledComponent sampled)
        {
            var bins = Stratify.XValues(new StratificationOptions(
                sampled.Hazard.InverseCDF(ProbabilityFloor), sampled.Hazard.InverseCDF(1d - ProbabilityFloor), HazardBinCount), true);
            return Stratify.XToProbability(bins, sampled.Hazard.CDF, false);
        }


        /// <summary>
        /// Splits the bin containing the boundary probability into two bins meeting at it; a
        /// boundary outside the bin range (or already on an edge) is a no-op.
        /// </summary>
        /// <param name="bins">The probability-space bins, in order.</param>
        /// <param name="boundary">The boundary probability to inject.</param>
        private static void InjectBoundary(List<StratificationBin> bins, double boundary)
        {
            for (int i = 0; i < bins.Count; i++)
            {
                if (boundary > bins[i].LowerBound && boundary < bins[i].UpperBound)
                {
                    var lower = new StratificationBin(bins[i].LowerBound, boundary);
                    var upper = new StratificationBin(boundary, bins[i].UpperBound);
                    bins.RemoveAt(i);
                    bins.Insert(i, upper);
                    bins.Insert(i, lower);
                    return;
                }
            }
        }

        #endregion

        #region Private Helpers — Joint System Integration

        /// <summary>
        /// Prepares the run-scoped joint-method state: the correlated-hazard latent structure,
        /// the 2^D component combination caches (all-zero row first — the v1.0 engine-level
        /// layout), and, under the automatic tail-focus mode, the deterministic per-component
        /// failure-probability quadrature probe that sets the VEGAS power-transform target. A
        /// no-op outside the multi-component joint method.
        /// </summary>
        /// <param name="token">The run cancellation token.</param>
        /// <remarks>
        /// An earlier design harvested the target from the VEGAS warm-up itself; two facts
        /// forced this probe instead: the
        /// automatic configuration resets the VEGAS bin count, which reallocates the importance
        /// grid — so γ must be set before the warm-up, not after it — and a γ = 1 Monte Carlo
        /// warm-up cannot observe the rare failure probabilities the target needs (that is the
        /// very problem the transform solves). The adaptive Gauss–Kronrod probe of
        /// AFP_i = ∫ P_F,i(p) dp on the mean sample is deterministic, costs about a thousand
        /// evaluations per component once per run, and measures the failure probabilities to
        /// quadrature accuracy.
        /// </remarks>
        private void PrepareJointSystem(CancellationToken token)
        {
            _jointCholeskyLower = null;
            _jointTailTargetProbability = 1e-2;
            if (_components.Count < 2 || _options.SystemRiskMethod != SystemRiskType.JointRiskMethod)
            {
                return;
            }

            int d = _components.Count;
            // Only the covariance is retained — the Cholesky factor below drives the integrand's
            // in-place latent transform, and the multivariate normal itself is never read.
            BuildHazardMultivariateNormal(d, out var covariance);

            // Extract the lower Cholesky factor once — the same construction the multivariate
            // normal performs internally on the same covariance, so the factor bits are
            // identical and the integrand's in-place latent transform reproduces
            // MultivariateNormal.InverseCDF exactly.
            var lower = new CholeskyDecomposition(new Matrix(covariance)).L;
            _jointCholeskyLower = new double[d, d];
            for (int i = 0; i < d; i++)
            {
                for (int j = 0; j < d; j++)
                {
                    _jointCholeskyLower[i, j] = lower[i, j];
                }
            }

            if (_options.VegasTailFocusMode == VegasTailFocusMode.Automatic)
            {
                double minimumFailureProbability = double.MaxValue;
                for (int i = 0; i < d; i++)
                {
                    token.ThrowIfCancellationRequested();
                    minimumFailureProbability = Math.Min(minimumFailureProbability, ProbeAnnualFailureProbability(_components[i]));
                }
                _jointTailTargetProbability = Tools.Clamp(minimumFailureProbability * _options.Alpha, 1e-12, 1e-2);
            }
        }

        /// <summary>
        /// Builds the correlated component-hazard latent structure per the dependency option,
        /// with the exact v1.0 off-diagonal constants: identity (independent), <c>1 − √εmach</c>
        /// (perfectly positive), <c>−1/(D − 1) + √εmach</c> (perfectly negative), or the
        /// analysis-validated user matrix.
        /// </summary>
        /// <param name="dimension">The component count D.</param>
        /// <param name="covariance">Receives the covariance the normal was built on (the Cholesky-extraction input).</param>
        /// <returns>The multivariate normal over the component hazard probabilities.</returns>
        private MultivariateNormal BuildHazardMultivariateNormal(int dimension, out double[,] covariance)
        {
            var mean = new double[dimension];
            covariance = new double[dimension, dimension];
            if (_options.ComponentHazardDependency == DependencyType.CorrelationMatrix)
            {
                var matrix = _options.HazardCorrelationMatrix!;
                for (int i = 0; i < dimension; i++)
                {
                    for (int j = 0; j < dimension; j++)
                    {
                        covariance[i, j] = matrix[i, j];
                    }
                }
                return new MultivariateNormal(mean, covariance);
            }

            DependencyMatrix.FillEquicorrelated(covariance, dimension,
                DependencyMatrix.AutomaticOffDiagonal(_options.ComponentHazardDependency, dimension));
            return new MultivariateNormal(mean, covariance);
        }

        /// <summary>
        /// The deterministic annual-failure-probability probe: integrates one component's
        /// combined failure probability over its hazard probability domain on the mean sample
        /// with adaptive Gauss–Kronrod — the one call site where the integral's returned value is
        /// the product. For a bivariate component each evaluation is the secondary-marginalized
        /// failure probability, so the probe returns the correct tail-focus target with no
        /// modification (each evaluation inherits the conditional-bin factor).
        /// </summary>
        /// <param name="component">The component to probe (its samplers are already set up).</param>
        /// <returns>The component's annualized failure probability on the mean sample.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the probe integration reports failure — a swallowed integrand exception
        /// would otherwise feed a truncated probability into the tail-focus target (the same
        /// guard as <see cref="IntegrateComponent"/>).
        /// </exception>
        private double ProbeAnnualFailureProbability(SystemComponent component)
        {
            var sampled = component.Sample(-1);
            var scratch = new ComponentRealization(sampled.FailureModeCount);
            var flags = new RiskComputeFlags();
            var bins = BuildHazardBins(sampled);
            var support = HazardProbabilitySupport.Create(bins);
            double FailureProbability(double probability)
            {
                return Tools.Clamp(sampled.ComputeRisk(probability, sampled.Hazard.InverseCDF(probability), flags, scratch,
                    hazardNonExceedance: probability).ProbabilityOfFailure, 0d, 1d);
            }

            double result = 0d;
            if (support.Upper > support.Lower)
            {
                var integrator = new AdaptiveGaussKronrod(FailureProbability, support.Lower, support.Upper)
                {
                    ReportFailure = false,
                    MaxFunctionEvaluations = _options.MaxEvaluations,
                    MaxDepth = _options.MaxDepth,
                    RelativeTolerance = _options.Tolerance,
                    MinDepth = 2,
                };
                integrator.Integrate(bins);
                if (integrator.Status == IntegrationStatus.Failure)
                {
                    throw new InvalidOperationException(AppendEvaluationFault($"The failure-probability probe failed for system component '{sampled.Name}': an integrand evaluation threw, so the tail-focus target cannot be derived."));
                }
                result = integrator.Result;
            }

            support.CompleteExhaustive(support.InteriorMass, FailureProbability,
                (_, mass, value) => result += mass * value,
                $"system component '{sampled.Name}' failure-probability probe");
            if (!double.IsFinite(result) || result < -1e-12 || result > 1d + 1e-12)
            {
                throw new InvalidOperationException($"The failure-probability probe produced the invalid exhaustive result {result:R} for system component '{sampled.Name}'.");
            }
            return Tools.Clamp(result, 0d, 1d);
        }

        /// <summary>
        /// Applies the VEGAS power-transform tail focus per the configured mode — always before
        /// the warm-up, because raising the bin count reallocates the importance grid (setting γ
        /// after the warm-up would discard it). Automatic uses the probe-derived target through
        /// <c>ConfigureForRareEvents</c>; manual applies the user's γ with the same bin-count and
        /// grid-damping adjustments when γ exceeds one; none leaves γ = 1 — sampling identical to
        /// v1.0.
        /// </summary>
        /// <param name="integrator">The VEGAS integrator to configure.</param>
        private void ConfigureTailFocus(Vegas integrator)
        {
            switch (_options.VegasTailFocusMode)
            {
                case VegasTailFocusMode.Manual:
                    integrator.TailFocusParameter = _options.VegasTailFocusParameter;
                    if (_options.VegasTailFocusParameter > 1d)
                    {
                        integrator.NumberOfBins = Math.Max(100, integrator.NumberOfBins);
                        integrator.Alpha = 1.8;
                    }
                    break;
                case VegasTailFocusMode.Automatic:
                    integrator.ConfigureForRareEvents(_jointTailTargetProbability);
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// The joint system-risk pass: VEGAS integrates over the D-dimensional hypercube of
        /// correlated hazard probabilities, and each evaluation computes every component's risk,
        /// enumerates the exclusive component failure/non-failure combinations, and — the v1.1
        /// correction of the documented v1.0 system-tail defect — crosses the failing components'
        /// recorded pathway/branch entries (conditional weights from the per-pathway lists)
        /// instead of collapsing each component to its conditional mean. Non-failing components
        /// contribute their branch-weighted mean non-failure consequence (the documented
        /// <see cref="ComponentRiskOutput"/> interim). The VEGAS weight is the recorded
        /// probability mass; recorded points accumulate across the recording passes and are
        /// self-normalized by the realized weight sum so the exhaustive budget is exactly one.
        /// </summary>
        /// <param name="sampledComponents">The sampled components for this realization.</param>
        /// <param name="componentRealizations">The per-component realization sinks.</param>
        /// <param name="realization">The system realization.</param>
        /// <param name="flags">The realization's computational-warning flags.</param>
        /// <param name="realizationIndex">The realization index, or −1 for the mean pass.</param>
        /// <param name="token">The run cancellation token.</param>
        /// <exception cref="InvalidOperationException">Thrown when the VEGAS integration fails.</exception>
        /// <remarks>
        /// The legacy engine's <c>tPF</c> accumulator (incremented twice per combination when
        /// recording) is not carried forward — the failure union is the Fail stream's recorded
        /// mass, and no scalar duplicates it. The exclusive-probability enumeration inherits the
        /// legacy convergence shortcut, which can truncate the deepest combinations of a
        /// high-dimensional system; the mass it drops is bounded by the enumeration tolerance
        /// and surfaces honestly through the mass-balance witness.
        /// </remarks>
        private void IntegrateJointSystem(SampledComponent[] sampledComponents,
            List<ComponentRealization> componentRealizations, SystemRealization realization,
            RiskComputeFlags flags, int realizationIndex, CancellationToken token)
        {
            int d = _components.Count;
            var lower = _jointCholeskyLower
                ?? throw new InvalidOperationException("The joint-method state was not prepared. The run sequence must call PrepareJointSystem before computing realizations.");
            int typeCount = 1 + realization.AdditionalCurves.Count;

            // The integration extents: the hazard probability hypercube (v1.0 constants).
            var minimums = new double[d];
            var maximums = new double[d];
            for (int i = 0; i < d; i++)
            {
                minimums[i] = ProbabilityFloor;
                maximums[i] = 1d - ProbabilityFloor;
            }

            // Pre-allocated evaluation buffers — the integrand allocates nothing it controls.
            // The primary consequence type drives the VEGAS objective; secondary types are
            // computed and recorded during the recording passes only.
            var hazardLevels = new double[d];
            var failureProbabilities = new double[d];
            var outputs = new ComponentRiskOutput[d];
            var outputsByType = new ComponentRiskOutput[d][];
            for (int i = 0; i < d; i++)
            {
                outputsByType[i] = new ComponentRiskOutput[typeCount];
            }
            var nonFailureValuesByType = new double[typeCount][];
            for (int k = 0; k < typeCount; k++)
            {
                nonFailureValuesByType[k] = new double[d];
            }
            var participating = new List<int>(d);
            var branchPick = new int[d];
            var failPoints = new RiskPoint?[typeCount];
            var excessPoints = new RiskPoint?[typeCount];
            var totalPoints = new RiskPoint?[typeCount];
            var nonFailPoints = new RiskPoint?[typeCount];
            var zBuffer = new double[d];
            var latentBuffer = new double[d];

            // The per-component system-contribution accumulators (the % contribution diagnostic),
            // one row per consequence type, filled through the recording passes in weight terms
            // and scaled by the self-normalization factor with the recorded masses.
            var systemContributionProbability = new double[typeCount][];
            var systemContributionFailure = new double[typeCount][];
            var systemContributionExcess = new double[typeCount][];
            for (int k = 0; k < typeCount; k++)
            {
                systemContributionProbability[k] = new double[d];
                systemContributionFailure[k] = new double[d];
                systemContributionExcess[k] = new double[d];
            }
            var tupleFailureValues = new double[d];
            var tupleExcessValues = new double[d];

            bool recording = false;
            double recordedWeightSum = 0d;

            // Reusable exclusive-combination outputs: the pooled overload clears and refills these,
            // reusing indicator rows in place, so the integrand allocates no outputs after its
            // first evaluation. Locals of this call, so the parallel loop shares nothing.
            var exclusiveProbabilities = new List<double>();
            var exclusiveIndicators = new List<int[]>();
            long cappedEvaluations = 0;
            double cappedResidual = 0d;

            double Integrand(double[] point, double weight)
            {
                if (token.IsCancellationRequested) return 0d;

                bool recordSecondary = recording && typeCount > 1;

                // Correlated hazard probabilities through the latent normal: the in-place
                // Cholesky transform replicating MultivariateNormal.InverseCDF exactly — the
                // full-row accumulation order of the Numerics matrix–vector product, with the
                // zero mean folded in — without its per-evaluation allocations.
                for (int j = 0; j < d; j++)
                {
                    zBuffer[j] = Normal.StandardZ(point[j]);
                }
                for (int i = 0; i < d; i++)
                {
                    double sum = 0.0d;
                    for (int j = 0; j < d; j++)
                    {
                        sum += lower[i, j] * zBuffer[j];
                    }
                    latentBuffer[i] = sum + 0d;
                }
                for (int i = 0; i < d; i++)
                {
                    double probability = Normal.StandardCDF(latentBuffer[i]);
                    probability = Tools.Clamp(probability, ProbabilityFloor, 1d - ProbabilityFloor);
                    hazardLevels[i] = sampledComponents[i].Hazard.InverseCDF(probability);

                    // The VEGAS weight is the recorded probability-mass coordinate (v1.0
                    // semantics — the component sinks record mass = weight directly), so a
                    // bivariate component takes its slice's non-exceedance probability through
                    // the dedicated parameter.
                    if (recordSecondary)
                    {
                        sampledComponents[i].ComputeRisk(weight, hazardLevels[i], flags, componentRealizations[i], recording, outputsByType[i],
                            hazardNonExceedance: probability);
                        outputs[i] = outputsByType[i][0];
                    }
                    else
                    {
                        outputs[i] = sampledComponents[i].ComputeRisk(weight, hazardLevels[i], flags, componentRealizations[i], recording,
                            hazardNonExceedance: probability);
                        outputsByType[i][0] = outputs[i];
                    }
                    failureProbabilities[i] = Tools.Clamp(outputs[i].ProbabilityOfFailure, 0d, 1d);
                    nonFailureValuesByType[0][i] = outputs[i].NonFailureConsequences;
                    for (int k = 1; k < (recordSecondary ? typeCount : 1); k++)
                    {
                        nonFailureValuesByType[k][i] = outputsByType[i][k].NonFailureConsequences;
                    }
                }

                // The background consequence combines every component's non-failure scalar.
                double background = CombineAll(nonFailureValuesByType[0], _options.JointConsequences);

                if (recording)
                {
                    recordedWeightSum += weight;
                    for (int k = 0; k < typeCount; k++)
                    {
                        var target = k == 0 ? realization.Curves : realization.AdditionalCurves[k - 1];
                        double typeBackground = k == 0 ? background : CombineAll(nonFailureValuesByType[k], _options.JointConsequences);
                        target.Background.AddRiskPoint(weight, 1d, typeBackground);
                        failPoints[k] = new RiskPoint { HazardProbability = weight, HazardProbabilityMass = weight };
                        excessPoints[k] = new RiskPoint { HazardProbability = weight, HazardProbabilityMass = weight };
                        totalPoints[k] = new RiskPoint { HazardProbability = weight, HazardProbabilityMass = weight };
                        nonFailPoints[k] = new RiskPoint { HazardProbability = weight, HazardProbabilityMass = weight };
                        target.Fail.RiskPoints.Add(failPoints[k]!);
                        target.Excess.RiskPoints.Add(excessPoints[k]!);
                        target.Total.RiskPoints.Add(totalPoints[k]!);
                        target.NonFail.RiskPoints.Add(nonFailPoints[k]!);
                    }
                }

                // The exclusive component failure/non-failure combinations. Conditional on the
                // hazard levels, component failures are independent — dependence enters only
                // through the correlated hazards (the v1.0 model). Combinations are generated on
                // demand, so nothing here is bounded by a 2^D matrix.
                var enumeration = Probability.IndependentExclusiveLazy(failureProbabilities,
                    exclusiveProbabilities, exclusiveIndicators, includeNoEventRow: true,
                    maxEmittedCombinations: _options.MaxSystemCombinations);
                ProbabilityPartitionBoundary.ClipInPlace(exclusiveProbabilities);
                if (enumeration == Probability.ExclusiveEnumerationStatus.Capped)
                {
                    double emitted = 0d;
                    for (int c = 0; c < exclusiveProbabilities.Count - 1; c++) emitted += exclusiveProbabilities[c];
                    cappedEvaluations++;
                    cappedResidual += Tools.Clamp(1d - emitted, 0d, 1d);
                }

                double expectedFailure = 0d;
                double expectedNonFailure = 0d;
                double secondaryDiscard = 0d;
                for (int c = 0; c < exclusiveIndicators.Count; c++)
                {
                    double combinationProbability = Tools.Clamp(exclusiveProbabilities[c], 0d, 1d);
                    if (combinationProbability <= 0d) continue;
                    var combination = exclusiveIndicators[c];

                    participating.Clear();
                    bool entriesAvailable = true;
                    for (int i = 0; i < d; i++)
                    {
                        if (combination[i] == 1)
                        {
                            participating.Add(i);
                            if (outputs[i].FailureConsequences.Count == 0) entriesAvailable = false;
                        }
                    }

                    double complementNonFailure = CombineComplement(nonFailureValuesByType[0], combination, _options.JointConsequences);
                    expectedNonFailure += combinationProbability * complementNonFailure;

                    if (participating.Count == 0)
                    {
                        // The no-failure combination: non-failure and total only (v1.0 layout).
                        if (recording)
                        {
                            for (int k = 0; k < typeCount; k++)
                            {
                                double typeComplement = k == 0
                                    ? complementNonFailure
                                    : CombineComplement(nonFailureValuesByType[k], combination, _options.JointConsequences);
                                nonFailPoints[k]!.Add(combinationProbability, typeComplement);
                                totalPoints[k]!.Add(combinationProbability, typeComplement);
                                WidenSystemExtents(realization, k, typeComplement, typeComplement);
                            }
                        }
                        continue;
                    }
                    if (!entriesAvailable) continue;

                    // The odometer over the failing components' recorded entries, per consequence
                    // type: the primary drives the VEGAS objective; secondary types record only.
                    AccumulateJointCombinationEntries(0, combinationProbability, complementNonFailure,
                        participating, branchPick, outputsByType, failureProbabilities, recording,
                        failPoints[0], excessPoints[0], totalPoints[0], realization, ref expectedFailure,
                        weight,
                        recording ? systemContributionProbability[0] : null,
                        recording ? systemContributionFailure[0] : null,
                        recording ? systemContributionExcess[0] : null,
                        tupleFailureValues, tupleExcessValues);
                    if (recordSecondary)
                    {
                        for (int k = 1; k < typeCount; k++)
                        {
                            double typeComplement = CombineComplement(nonFailureValuesByType[k], combination, _options.JointConsequences);
                            AccumulateJointCombinationEntries(k, combinationProbability, typeComplement,
                                participating, branchPick, outputsByType, failureProbabilities, recording,
                                failPoints[k], excessPoints[k], totalPoints[k], realization, ref secondaryDiscard,
                                weight, systemContributionProbability[k], systemContributionFailure[k], systemContributionExcess[k],
                                tupleFailureValues, tupleExcessValues);
                        }
                    }

                    // The non-failure stream records one entry per combination from the
                    // complement's scalars, excluding the all-fail combination whose complement
                    // is empty (v1.0 layout).
                    if (recording && participating.Count < d)
                    {
                        for (int k = 0; k < typeCount; k++)
                        {
                            double typeComplement = k == 0
                                ? complementNonFailure
                                : CombineComplement(nonFailureValuesByType[k], combination, _options.JointConsequences);
                            nonFailPoints[k]!.Add(combinationProbability, typeComplement);
                        }
                    }
                }

                return expectedFailure + expectedNonFailure;
            }

            int vegasSeed = SeedHelpers.ToPositiveSeed(SeedHelpers.HashCombine(_jointSeedBase, JointStreamSalt, realizationIndex));
            var integrator = new Vegas(Integrand, d, minimums, maximums)
            {
                Random = new MersenneTwister(vegasSeed),
                UseSobolSequence = false,
                ReportFailure = false,
                CheckConvergence = false,
                RelativeTolerance = 1e-3,
                MaxFunctionEvaluations = _options.MaxEvaluations,
                FunctionCalls = _options.WarmupEvaluations,
                IndependentEvaluations = _options.WarmupCycles,
                Initialize = 0,
            };
            if (_options.UseSobolJointSampling)
            {
                // The opt-in v1.0-style quasi-random driver, made content-seed reproducible by
                // the seeded scrambling: the same per-realization seed that drives the
                // pseudo-random generator scrambles the Sobol sequence (the generator the
                // integrator then ignores stays configured — harmless).
                integrator.UseSobolSequence = true;
                integrator.SobolSeed = vegasSeed;
            }
            ConfigureTailFocus(integrator);

            // The warm-up builds the importance grid; nothing is recorded.
            integrator.Integrate();
            realization.ChiSquared = integrator.ChiSquared;
            token.ThrowIfCancellationRequested();
            if (integrator.Status == IntegrationStatus.Failure)
            {
                throw new InvalidOperationException(AppendEvaluationFault("The VEGAS warm-up failed; the joint system risk integration cannot proceed."));
            }

            // The recording passes inherit the grid but not its answers; masses self-normalize.
            recording = true;
            integrator.Initialize = 1;
            integrator.FunctionCalls = _options.FinalEvaluations;
            integrator.IndependentEvaluations = VegasRecordingPasses;
            integrator.Integrate();
            token.ThrowIfCancellationRequested();
            if (integrator.Status == IntegrationStatus.Failure)
            {
                throw new InvalidOperationException(AppendEvaluationFault("The VEGAS recording pass failed; the joint system risk integration cannot proceed."));
            }
            realization.FunctionEvaluations += integrator.FunctionEvaluations;
            realization.StandardError = integrator.StandardError;

            // The certificate capture: the integrator recomputed its chi-squared for the
            // recording passes after the stored warm-up value, and the engine previously
            // discarded it; keep it with the recording relative standard error (index-owned
            // writes — the parallel realizations never share a slot).
            double recordingChiSquared = integrator.ChiSquared;
            double recordingRelativeSe = integrator.Result != 0d
                ? Math.Abs(integrator.StandardError / integrator.Result)
                : double.NaN;
            if (realizationIndex < 0)
            {
                _jointCertificateMeanPassCaptured = true;
                _jointCertificateMeanPassChiSquared = recordingChiSquared;
                _jointCertificateMeanPassRelativeSe = recordingRelativeSe;
            }
            else if (_jointCertificateChiSquared != null && realizationIndex < _jointCertificateChiSquared.Length)
            {
                _jointCertificateChiSquared[realizationIndex] = recordingChiSquared;
                _jointCertificateRelativeSe![realizationIndex] = recordingRelativeSe;
            }

            if (cappedEvaluations > 0)
            {
                double meanResidual = cappedResidual / cappedEvaluations;
                flags.HasTruncatedCombinationEnumeration = true;
                if (meanResidual > TruncatedCombinationResidualLimit) flags.HasExcessiveTruncatedMass = true;
            }

            if (recordedWeightSum > 0d)
            {
                double scale = 1d / recordedWeightSum;
                realization.Curves.ScaleRecordedMass(scale);
                for (int k = 0; k < realization.AdditionalCurves.Count; k++)
                {
                    realization.AdditionalCurves[k].ScaleRecordedMass(scale);
                }
                for (int i = 0; i < componentRealizations.Count; i++)
                {
                    componentRealizations[i].ScaleRecordedMass(scale);
                }

                // Finalize the % contribution attributions under the same self-normalization:
                // the failure-mode accumulators carry weights directly, and the
                // per-component system attributions scale with the recorded masses.
                for (int i = 0; i < componentRealizations.Count; i++)
                {
                    componentRealizations[i].FinalizeContributions(null, scale);
                    componentRealizations[i].SystemContribution = new RiskContribution
                    {
                        FailureProbability = systemContributionProbability[0][i] * scale,
                        FailureMean = systemContributionFailure[0][i] * scale,
                        ExcessMean = systemContributionExcess[0][i] * scale,
                    };
                    for (int k = 1; k < typeCount; k++)
                    {
                        while (componentRealizations[i].AdditionalSystemContributions.Count < k)
                        {
                            componentRealizations[i].AdditionalSystemContributions.Add(null);
                        }
                        componentRealizations[i].AdditionalSystemContributions[k - 1] = new RiskContribution
                        {
                            FailureProbability = systemContributionProbability[k][i] * scale,
                            FailureMean = systemContributionFailure[k][i] * scale,
                            ExcessMean = systemContributionExcess[k][i] * scale,
                        };
                    }
                }
            }

            // Build the component curves from the recorded masses (no mass re-derivation — the
            // VEGAS weights are the masses), then the system curves and measures, per
            // consequence type.
            for (int i = 0; i < componentRealizations.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                componentRealizations[i].CreateCurves(_options.LECOutputLength);
                if ((_options.RiskMeasures & RiskMeasureOptions.RiskProfiles) != 0)
                {
                    componentRealizations[i].CreateProfiles(includeFailureModes: realizationIndex < 0);
                }
                componentRealizations[i].ComputeRiskMeasures(_options.ConsequenceThreshold, _options.Alpha, _components[i].HazardThreshold, _runAdditionalThresholds);
                realization.MinN = Math.Min(realization.MinN, componentRealizations[i].MinN);
                realization.MaxN = Math.Max(realization.MaxN, componentRealizations[i].MaxN);
                for (int k = 0; k < componentRealizations[i].AdditionalCurves.Count; k++)
                {
                    realization.AdditionalMinN[k] = Math.Min(realization.AdditionalMinN[k], componentRealizations[i].AdditionalMinN[k]);
                    realization.AdditionalMaxN[k] = Math.Max(realization.AdditionalMaxN[k], componentRealizations[i].AdditionalMaxN[k]);
                }
                realization.MinH[i] = componentRealizations[i].MinH;
                realization.MaxH[i] = componentRealizations[i].MaxH;
            }
            realization.Curves.CreateCurves(_options.LECOutputLength);
            realization.Curves.ComputeRiskMeasures(_options.ConsequenceThreshold, _options.Alpha);
            for (int k = 0; k < realization.AdditionalCurves.Count; k++)
            {
                realization.AdditionalCurves[k].CreateCurves(_options.LECOutputLength);
                realization.AdditionalCurves[k].ComputeRiskMeasures(AdditionalThresholdAt(k), _options.Alpha);
            }
        }

        /// <summary>
        /// Runs the joint-combination odometer for one consequence type: the cross product over
        /// the failing components' recorded entries at that type (conditional branch weights
        /// multiply; consequences combine per the joint rule), accumulating the expected failure
        /// consequence in the caller's sequential chain and recording entries when requested.
        /// </summary>
        /// <param name="typeIndex">The consequence-type position (0 is the primary).</param>
        /// <param name="combinationProbability">The exclusive combination probability.</param>
        /// <param name="complementNonFailure">The combined non-failure consequence of the non-failing components at this type.</param>
        /// <param name="participating">The failing component indices.</param>
        /// <param name="branchPick">The shared odometer buffer (cleared here).</param>
        /// <param name="outputsByType">The per-component, per-type risk outputs.</param>
        /// <param name="failureProbabilities">The per-component failure probabilities.</param>
        /// <param name="recording">True to record entries on the supplied risk points.</param>
        /// <param name="failPoint">This type's Fail risk point (recording only).</param>
        /// <param name="excessPoint">This type's Excess risk point (recording only).</param>
        /// <param name="totalPoint">This type's Total risk point (recording only).</param>
        /// <param name="realization">The system realization (extent tracking).</param>
        /// <param name="expectedFailure">The caller's running expected-failure accumulator (kept sequential for bit-identity).</param>
        /// <param name="weight">The evaluation's VEGAS weight — the recorded mass the contribution shares carry.</param>
        /// <param name="contributionProbability">The optional per-component attributed-probability sink (the % contribution diagnostic); null skips attribution.</param>
        /// <param name="contributionFailure">The optional per-component attributed failure-value sink.</param>
        /// <param name="contributionExcess">The optional per-component attributed excess-value sink.</param>
        /// <param name="tupleFailureValues">The caller's per-participant failure-value scratch (attribution weights).</param>
        /// <param name="tupleExcessValues">The caller's per-participant excess-value scratch (attribution weights).</param>
        /// <remarks>
        /// The attribution scheme: within each exclusive component
        /// combination tuple, the probability splits equally (the Shapley value of the union
        /// game) and the combined failure and excess values split proportionally to the
        /// participants' own entry values (equal split when a value sum is zero). Attribution
        /// runs in separate accumulation chains — the expected-failure chain and the recorded
        /// entries are bit-untouched.
        /// </remarks>
        private void AccumulateJointCombinationEntries(int typeIndex, double combinationProbability, double complementNonFailure,
            List<int> participating, int[] branchPick, ComponentRiskOutput[][] outputsByType, double[] failureProbabilities,
            bool recording, RiskPoint? failPoint, RiskPoint? excessPoint, RiskPoint? totalPoint,
            SystemRealization realization, ref double expectedFailure,
            double weight = 0d, double[]? contributionProbability = null, double[]? contributionFailure = null,
            double[]? contributionExcess = null, double[]? tupleFailureValues = null, double[]? tupleExcessValues = null)
        {
            Array.Clear(branchPick, 0, participating.Count);
            while (true)
            {
                double tupleWeight = 1d;
                double combinedFailure = 0d;
                double combinedExcess = 0d;
                double tupleFailureSum = 0d;
                double tupleExcessSum = 0d;
                for (int p = 0; p < participating.Count; p++)
                {
                    var output = outputsByType[participating[p]][typeIndex];
                    double raw = failureProbabilities[participating[p]];
                    double entryWeight = raw > 0d
                        ? output.ResponseProbabilities[branchPick[p]] / raw
                        : (branchPick[p] == 0 ? 1d : 0d);
                    tupleWeight *= entryWeight;
                    double failureValue = output.FailureConsequences[branchPick[p]];
                    double excessValue = output.ExcessConsequences[branchPick[p]];
                    if (contributionProbability != null)
                    {
                        tupleFailureValues![p] = failureValue;
                        tupleExcessValues![p] = excessValue;
                        tupleFailureSum += failureValue;
                        tupleExcessSum += excessValue;
                    }
                    if (p == 0)
                    {
                        combinedFailure = failureValue;
                        combinedExcess = excessValue;
                    }
                    else
                    {
                        switch (_options.JointConsequences)
                        {
                            case JointConsequenceType.Additive:
                            case JointConsequenceType.Average:
                                combinedFailure += failureValue;
                                combinedExcess += excessValue;
                                break;
                            case JointConsequenceType.Maximum:
                                combinedFailure = Math.Max(combinedFailure, failureValue);
                                combinedExcess = Math.Max(combinedExcess, excessValue);
                                break;
                            case JointConsequenceType.Minimum:
                            default:
                                combinedFailure = Math.Min(combinedFailure, failureValue);
                                combinedExcess = Math.Min(combinedExcess, excessValue);
                                break;
                        }
                    }
                }
                if (_options.JointConsequences == JointConsequenceType.Average)
                {
                    combinedFailure /= participating.Count;
                    combinedExcess /= participating.Count;
                }

                if (tupleWeight > 0d)
                {
                    double entryProbability = Tools.Clamp(combinationProbability * tupleWeight, 0d, 1d);
                    expectedFailure += entryProbability * combinedFailure;
                    if (recording)
                    {
                        failPoint!.Add(entryProbability, combinedFailure);
                        excessPoint!.Add(entryProbability, combinedExcess);
                        totalPoint!.Add(entryProbability, combinedFailure + complementNonFailure);
                        WidenSystemExtents(realization, typeIndex, complementNonFailure, Math.Max(combinedFailure, complementNonFailure));
                    }

                    if (contributionProbability != null)
                    {
                        double weightedEntry = weight * entryProbability;
                        double equalShare = 1d / participating.Count;
                        for (int p = 0; p < participating.Count; p++)
                        {
                            int componentIndex = participating[p];
                            double failureShare = tupleFailureSum > 0d ? tupleFailureValues![p] / tupleFailureSum : equalShare;
                            double excessShare = tupleExcessSum > 0d ? tupleExcessValues![p] / tupleExcessSum : equalShare;
                            contributionProbability[componentIndex] += weightedEntry * equalShare;
                            contributionFailure![componentIndex] += weightedEntry * combinedFailure * failureShare;
                            contributionExcess![componentIndex] += weightedEntry * combinedExcess * excessShare;
                        }
                    }
                }

                // Advance the odometer.
                int digit = 0;
                while (digit < participating.Count)
                {
                    branchPick[digit]++;
                    if (branchPick[digit] < outputsByType[participating[digit]][typeIndex].FailureConsequences.Count) break;
                    branchPick[digit] = 0;
                    digit++;
                }
                if (digit == participating.Count) break;
            }
        }

        /// <summary>
        /// Widens one consequence type's system extent slots.
        /// </summary>
        /// <param name="realization">The system realization.</param>
        /// <param name="typeIndex">The consequence-type position (0 is the primary).</param>
        /// <param name="minCandidate">The candidate minimum consequence.</param>
        /// <param name="maxCandidate">The candidate maximum consequence.</param>
        private static void WidenSystemExtents(SystemRealization realization, int typeIndex, double minCandidate, double maxCandidate)
        {
            if (typeIndex == 0)
            {
                realization.MinN = Math.Min(realization.MinN, minCandidate);
                realization.MaxN = Math.Max(realization.MaxN, maxCandidate);
            }
            else
            {
                realization.AdditionalMinN[typeIndex - 1] = Math.Min(realization.AdditionalMinN[typeIndex - 1], minCandidate);
                realization.AdditionalMaxN[typeIndex - 1] = Math.Max(realization.AdditionalMaxN[typeIndex - 1], maxCandidate);
            }
        }

        /// <summary>
        /// Combines every component's value under the joint-consequence rule.
        /// </summary>
        /// <param name="values">The per-component values.</param>
        /// <param name="rule">The combination rule.</param>
        /// <returns>The combined value.</returns>
        private static double CombineAll(double[] values, JointConsequenceType rule)
        {
            double combined = values[0];
            for (int i = 1; i < values.Length; i++)
            {
                switch (rule)
                {
                    case JointConsequenceType.Additive:
                    case JointConsequenceType.Average:
                        combined += values[i];
                        break;
                    case JointConsequenceType.Maximum:
                        combined = Math.Max(combined, values[i]);
                        break;
                    case JointConsequenceType.Minimum:
                    default:
                        combined = Math.Min(combined, values[i]);
                        break;
                }
            }
            return rule == JointConsequenceType.Average ? combined / values.Length : combined;
        }

        /// <summary>
        /// Combines the non-participating (indicator zero) components' values under the
        /// joint-consequence rule; an empty complement yields zero (the v1.0 sentinel guard).
        /// </summary>
        /// <param name="values">The per-component values.</param>
        /// <param name="indicators">The combination's failure indicators.</param>
        /// <param name="rule">The combination rule.</param>
        /// <returns>The combined complement value.</returns>
        /// <remarks>
        /// Not delegated to the <c>Tools.Sum</c>/<c>Tools.Mean</c> indicator overloads: they take
        /// <c>IList</c>, which would make every element access in this per-evaluation kernel an
        /// interface dispatch, and Maximum/Minimum have no indicator overload.
        /// </remarks>
        private static double CombineComplement(double[] values, int[] indicators, JointConsequenceType rule)
        {
            double combined = 0d;
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (indicators[i] != 0) continue;
                if (count == 0)
                {
                    combined = values[i];
                }
                else
                {
                    switch (rule)
                    {
                        case JointConsequenceType.Additive:
                        case JointConsequenceType.Average:
                            combined += values[i];
                            break;
                        case JointConsequenceType.Maximum:
                            combined = Math.Max(combined, values[i]);
                            break;
                        case JointConsequenceType.Minimum:
                        default:
                            combined = Math.Min(combined, values[i]);
                            break;
                    }
                }
                count++;
            }
            if (count == 0) return 0d;
            return rule == JointConsequenceType.Average ? combined / count : combined;
        }

        #endregion

        #region Private Helpers — Percentile Post-Processing

        /// <summary>
        /// Builds the mean, median, and confidence-bound curve sets from the realization
        /// ensemble: loss exceedance percentiles on a shared consequence grid (the Total
        /// percentile curve is read from each realization's Total curve — the v1.0
        /// failure-plus-non-failure reconstruction is deleted), and hazard-profile percentiles
        /// on per-component hazard grids. Percentile assembly parallelizes over grid ordinates
        /// with index-owned writes; means are summed sequentially per ordinate.
        /// </summary>
        /// <param name="realizations">The realization ensemble.</param>
        /// <param name="realizationWeights">The run-input epistemic realization weights, or null for the unweighted assembly.</param>
        /// <param name="token">The run cancellation token.</param>
        /// <returns>
        /// The staged lower, upper, median, and mean realizations; null entries when no valid
        /// percentile grid can be formed.
        /// </returns>
        private (SystemRealization? Lower, SystemRealization? Upper, SystemRealization? Median, SystemRealization? Mean) PostProcessUncertainty(SystemRealization[] realizations, double[]? realizationWeights, CancellationToken token)
        {
            int realizationCount = realizations.Length;
            if (realizationCount == 0) return (null, null, null, null);
            int componentCount = _components.Count;
            int additionalTypes = realizations[0].AdditionalCurves.Count;
            double tail = (1d - _options.ConfidenceIntervalWidth) / 2d;

            // Sequential extent reduction across the ensemble, per consequence type.
            double minN = double.MaxValue;
            double maxN = double.MinValue;
            var additionalMinN = new double[additionalTypes];
            var additionalMaxN = new double[additionalTypes];
            for (int k = 0; k < additionalTypes; k++)
            {
                additionalMinN[k] = double.MaxValue;
                additionalMaxN[k] = double.MinValue;
            }
            var minH = new double[componentCount];
            var maxH = new double[componentCount];
            for (int d = 0; d < componentCount; d++)
            {
                minH[d] = double.MaxValue;
                maxH[d] = double.MinValue;
            }
            for (int i = 0; i < realizationCount; i++)
            {
                minN = Math.Min(minN, realizations[i].MinN);
                maxN = Math.Max(maxN, realizations[i].MaxN);
                for (int k = 0; k < additionalTypes; k++)
                {
                    additionalMinN[k] = Math.Min(additionalMinN[k], realizations[i].AdditionalMinN[k]);
                    additionalMaxN[k] = Math.Max(additionalMaxN[k], realizations[i].AdditionalMaxN[k]);
                }
                for (int d = 0; d < componentCount; d++)
                {
                    minH[d] = Math.Min(minH[d], realizations[i].MinH[d]);
                    maxH[d] = Math.Max(maxH[d], realizations[i].MaxH[d]);
                }
            }
            if (minN == double.MaxValue || maxN == double.MinValue
                || !double.IsFinite(minN) || !double.IsFinite(maxN)) return (null, null, null, null);

            var lower = CreatePercentileRealization("Lower", componentCount, additionalTypes, realizations[0]);
            var upper = CreatePercentileRealization("Upper", componentCount, additionalTypes, realizations[0]);
            var median = CreatePercentileRealization("Median", componentCount, additionalTypes, realizations[0]);
            var mean = CreatePercentileRealization("Mean", componentCount, additionalTypes, realizations[0]);
            var targets = new[] { lower, upper, median, mean };

            // The shared consequence grid, descending (v1.0 orientation), per consequence type —
            // types live on their own magnitude scales.
            double gridMin = minN < 1d ? 0d : minN;
            var consequenceGrid = maxN > gridMin ? RiskPercentileAssembler.BuildDescendingGrid(gridMin, maxN, _options.LECOutputLength)
                : new[] { maxN };

            // System and component LEC percentile curves for the five risk types, primary type.
            RiskPercentileAssembler.AssembleLecPercentiles(realizations, r => r.Curves, c => targets[c].Curves, consequenceGrid, tail, realizationWeights, token);
            for (int d = 0; d < componentCount; d++)
            {
                int componentIndex = d;
                RiskPercentileAssembler.AssembleLecPercentiles(realizations,
                    r => r.Components[componentIndex].Curves,
                    c => targets[c].Components[componentIndex].Curves,
                    consequenceGrid, tail, realizationWeights, token);

                int modeCount = realizations[0].Components[componentIndex].FailureModes.Count;
                for (int m = 0; m < modeCount; m++)
                {
                    int modeIndex = m;
                    RiskPercentileAssembler.AssembleLecPercentiles(realizations,
                        r => r.Components[componentIndex].FailureModes[modeIndex].Curves,
                        c => targets[c].Components[componentIndex].FailureModes[modeIndex].Curves,
                        consequenceGrid, tail, realizationWeights, token);
                }

                // Hazard-profile percentiles on the component's hazard grid, per consequence
                // type (the hazard grid is type-independent).
                if (maxH[d] > minH[d])
                {
                    var hazardGrid = RiskPercentileAssembler.BuildDescendingGrid(minH[d], maxH[d], _options.LECOutputLength);
                    RiskPercentileAssembler.AssembleProfilePercentiles(realizations, componentIndex, c => c.Curves, hazardGrid, tail, realizationWeights, targets,
                        primaryType: true, _options.LECOutputLength, token);
                    for (int k = 0; k < additionalTypes; k++)
                    {
                        int typeIndex = k;
                        RiskPercentileAssembler.AssembleProfilePercentiles(realizations, componentIndex, c => c.AdditionalCurves[typeIndex], hazardGrid, tail, realizationWeights, targets,
                            primaryType: false, _options.LECOutputLength, token);
                    }
                }
            }

            // The additional consequence types, each on its own grid.
            for (int k = 0; k < additionalTypes; k++)
            {
                int typeIndex = k;
                if (!double.IsFinite(additionalMinN[k]) || !double.IsFinite(additionalMaxN[k]))
                {
                    continue;
                }
                double typeGridMin = additionalMinN[k] < 1d ? 0d : additionalMinN[k];
                var typeGrid = additionalMaxN[k] > typeGridMin ? RiskPercentileAssembler.BuildDescendingGrid(typeGridMin, additionalMaxN[k], _options.LECOutputLength)
                    : new[] { additionalMaxN[k] };

                RiskPercentileAssembler.AssembleLecPercentiles(realizations, r => r.AdditionalCurves[typeIndex], c => targets[c].AdditionalCurves[typeIndex], typeGrid, tail, realizationWeights, token);
                for (int d = 0; d < componentCount; d++)
                {
                    int componentIndex = d;
                    RiskPercentileAssembler.AssembleLecPercentiles(realizations,
                        r => r.Components[componentIndex].AdditionalCurves[typeIndex],
                        c => targets[c].Components[componentIndex].AdditionalCurves[typeIndex],
                        typeGrid, tail, realizationWeights, token);

                    int modeCount = realizations[0].Components[componentIndex].FailureModes.Count;
                    for (int m = 0; m < modeCount; m++)
                    {
                        int modeIndex = m;
                        RiskPercentileAssembler.AssembleLecPercentiles(realizations,
                            r => r.Components[componentIndex].FailureModes[modeIndex].AdditionalCurves[typeIndex],
                            c => targets[c].Components[componentIndex].FailureModes[modeIndex].AdditionalCurves[typeIndex],
                            typeGrid, tail, realizationWeights, token);
                    }
                }
            }

            return (lower, upper, median, mean);
        }

        /// <summary>
        /// Creates an empty percentile realization shaped like the ensemble's realizations,
        /// including the additional consequence-type slots.
        /// </summary>
        /// <param name="name">The realization label.</param>
        /// <param name="componentCount">The component count.</param>
        /// <param name="additionalTypes">The number of additional consequence types.</param>
        /// <param name="template">A realization supplying the per-component failure-mode counts.</param>
        /// <returns>The shaped realization.</returns>
        private SystemRealization CreatePercentileRealization(string name, int componentCount, int additionalTypes, SystemRealization template)
        {
            var components = new List<ComponentRealization>(componentCount);
            for (int d = 0; d < componentCount; d++)
            {
                var componentRealization = new ComponentRealization(template.Components[d].FailureModes.Count)
                {
                    Name = template.Components[d].Name,
                };
                for (int m = 0; m < componentRealization.FailureModes.Count; m++)
                {
                    componentRealization.FailureModes[m].Name = template.Components[d].FailureModes[m].Name;
                    componentRealization.FailureModes[m].PathLabel = template.Components[d].FailureModes[m].PathLabel;
                }
                components.Add(componentRealization);
            }
            var realization = new SystemRealization(components) { Name = name };
            realization.EnsureAdditionalCurves(additionalTypes);
            StampConsequenceLabels(realization);
            return realization;
        }

        /// <summary>
        /// Stamps the declared consequence-type labels onto a realization — one entry per
        /// computed type including the primary (display metadata; positions past the declaration
        /// stamp blank).
        /// </summary>
        /// <param name="realization">The realization to stamp.</param>
        private void StampConsequenceLabels(SystemRealization realization)
        {
            realization.ConsequenceLabels.Clear();
            realization.ConsequenceUnits.Clear();
            int typeCount = 1 + realization.AdditionalCurves.Count;
            for (int k = 0; k < typeCount; k++)
            {
                if (k == 0)
                {
                    realization.ConsequenceLabels.Add(RunSpecifiedConsequence);
                    realization.ConsequenceUnits.Add(RunConsequenceUnit);
                }
                else if (k - 1 < RunAdditionalConsequenceTypes.Count)
                {
                    realization.ConsequenceLabels.Add(RunAdditionalConsequenceTypes[k - 1].SpecifiedConsequence);
                    realization.ConsequenceUnits.Add(RunAdditionalConsequenceTypes[k - 1].ConsequenceUnit);
                }
                else
                {
                    realization.ConsequenceLabels.Add(string.Empty);
                    realization.ConsequenceUnits.Add(string.Empty);
                }
            }
        }

        /// <summary>
        /// Stamps each component's end-state labels onto a realization:
        /// the mode name via the terminal-first label chain and the branch path
        /// descriptor, read from the frozen projection snapshot so labels can never drift from
        /// the sampled structure.
        /// </summary>
        /// <param name="realization">The realization to stamp.</param>
        private void StampModeLabels(SystemRealization realization)
        {
            for (int i = 0; i < _components.Count; i++)
            {
                var modes = _components[i].SampledProjection;
                if (modes == null) continue;
                var slots = realization.Components[i].FailureModes;
                int m = 0;
                for (int j = 0; j < modes.Count && m < slots.Count; j++)
                {
                    if (modes[j].IsNonFailureMode) continue;
                    slots[m].Name = SystemComponent.ModeLabel(modes[j], m);
                    slots[m].PathLabel = SystemComponent.ModePathLabel(modes[j]);
                    m++;
                }
            }
        }

        /// <summary>
        /// The declared consequence threshold for additional consequence type k, or
        /// NaN when none was declared — the run-scoped capture the measure sites read.
        /// </summary>
        /// <param name="typeIndex">The additional-type position (0 = the first additional type).</param>
        /// <returns>The declared threshold, or NaN.</returns>
        private double AdditionalThresholdAt(int typeIndex)
        {
            var thresholds = _runAdditionalThresholds;
            return thresholds != null && typeIndex < thresholds.Length ? thresholds[typeIndex] : double.NaN;
        }

        #endregion
    }
}
