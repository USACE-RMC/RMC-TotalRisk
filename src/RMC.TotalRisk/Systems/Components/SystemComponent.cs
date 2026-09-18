using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Numerics;
using Numerics.Distributions;
using Numerics.Mathematics.LinearAlgebra;
using Numerics.Mathematics.SpecialFunctions;
using Numerics.Sampling;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Transforms;
using RMC.TotalRisk.Systems.Components.Graph;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Systems.Components
{
    /// <summary>
    /// A system risk component: one hazard driving potential failure modes and their
    /// consequences, with the failure-mode combination options. The component owns its risk
    /// topology as a <see cref="ComponentGraph"/> — the formal DAG of hazard, transform,
    /// response, and consequence elements — and projects the engine-facing
    /// <see cref="FailureMode"/> chains from it deterministically.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>SystemComponent</c> with the option surface preserved (defaults,
    /// the CommonCause/MutuallyExclusive dependency coercion, the multivariate-normal off-diagonal
    /// constants <c>1 − √εmach</c> and <c>−1/(D − 1) + √εmach</c>, and the combination caches)
    /// and one deliberate v1.1 restructuring: v1.0 stored failure modes as
    /// a collection projected by the UI-side <c>RiskDiagram</c> from canvas topology; v1.1 makes
    /// the graph itself the model — <see cref="Graph"/> is the persisted truth, and
    /// <see cref="FailureModes"/> is a fresh, deterministic projection snapshot on every access
    /// (cheap at component scale; capture the list once per run). <see cref="AddFailureMode"/>
    /// keeps near-v1.0 ergonomics by expanding a chain-style mode into wired graph elements.
    /// </para>
    /// <para>
    /// <b>Identity vs persistence:</b> <see cref="ToXElement()"/> persists the element graph
    /// (whose link attributes carry Guids and names), which therefore cannot be the seed-identity
    /// surface. <see cref="CanonicalHash"/> instead hashes a deterministic internal identity form
    /// — the options, the hazard content, and the projected failure modes in path order —
    /// realizing the docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.5.3 recipe. Renaming or
    /// re-identifying elements, moving
    /// them on a canvas, or editing descriptions can never change the hash or re-roll Monte Carlo
    /// seeds; equal-content components hash identically and are disambiguated by
    /// <see cref="OccurrenceIndex"/> (§5.5.4).
    /// </para>
    /// <para>
    /// Improvements over v1.0, per the improve-on-port rule: <see cref="Clone"/> deep-copies via
    /// the serialization round-trip (v1.0 shared function references between clones); the
    /// correlation matrix serializes G17 row-major and actually round-trips (v1.0's read loop
    /// discarded every parsed value, so its persisted matrices were never restored); the matrix
    /// is serialized only under <see cref="DependencyType.CorrelationMatrix"/> (in the automatic
    /// dependency modes v1.0 lazily overwrote the field with the derived matrix, which would make
    /// the hash depend on whether the multivariate normal had been accessed); and the v1.0
    /// response-function-uniqueness error is dropped as obsolete under inline ownership and
    /// occurrence indexing. The v1.0 <c>ProfileHazardFunction</c> (a name-matched
    /// <c>IElement</c> reference) is ported as the structural
    /// <see cref="ProfileHazardElementId"/> element reference:
    /// resolved at the <see cref="SetupSamplers(int, int, SamplingScheme)"/> freeze point into the transform chain that
    /// remaps every recorded hazard level onto the selected axis, serialized append-only, and
    /// deliberately excluded from the identity form so a reporting-axis flip can never re-roll
    /// Monte Carlo seeds.
    /// </para>
    /// </remarks>
    public class SystemComponent : INotifyPropertyChanged
    {
        #region Construction

        /// <summary>
        /// Initializes a system component with the v1.0 defaults: joint failures, maximum joint
        /// consequences, independent failure modes, a zero hazard threshold, and an empty graph.
        /// </summary>
        public SystemComponent()
        {
            _graph = new ComponentGraph();
            SubscribeGraph();
        }

        /// <summary>
        /// Initializes a system component from a hazard function (the v1.0 constructor shape):
        /// creates the graph's hazard root element wrapping the function.
        /// </summary>
        /// <param name="hazardFunction">The hazard function; may be null (assigned later).</param>
        /// <param name="failureModeMethod">How multiple failure modes combine. Default: joint failures.</param>
        /// <param name="jointConsequences">How joint-failure consequences combine. Default: maximum.</param>
        /// <param name="hazardThreshold">The hazard level threshold for assurance estimates. Default: 0.</param>
        public SystemComponent(IHazardFunction? hazardFunction,
            FailureModeMethod failureModeMethod = FailureModeMethod.JointFailures,
            JointConsequenceType jointConsequences = JointConsequenceType.Maximum,
            double hazardThreshold = 0d)
        {
            _graph = new ComponentGraph();
            SubscribeGraph();

            _name = hazardFunction is null ? "System Component" : $"System Component - {hazardFunction.Name}";
            _failureModeMethod = failureModeMethod;
            _jointConsequences = jointConsequences;
            _hazardThreshold = hazardThreshold;
            if (hazardFunction != null)
            {
                var root = new HazardElement(_graph.GetUniqueName(string.IsNullOrEmpty(hazardFunction.Name) ? "Hazard" : hazardFunction.Name))
                {
                    Function = hazardFunction,
                };
                _graph.AddElement(root);
            }
        }

        /// <summary>
        /// Restores a system component from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement()"/>.</param>
        /// <param name="resolver">
        /// The function resolver, required only when the graph was written
        /// <see cref="RiskSerializationMode.ByReference"/>; null for self-contained forms.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the serialized graph is inconsistent (see <see cref="ComponentGraph"/>).</exception>
        public SystemComponent(XElement xElement, IRiskFunctionResolver? resolver = null)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            _name = SerializationUtilities.ReadString(xElement, nameof(Name), "System Component");
            _failureModeMethod = SerializationUtilities.ReadEnum(xElement, nameof(FailureModeMethod), FailureModeMethod.JointFailures);
            _jointConsequences = SerializationUtilities.ReadEnum(xElement, nameof(JointConsequences), JointConsequenceType.Maximum);
            _failureModeDependency = SerializationUtilities.ReadEnum(xElement, nameof(FailureModeDependency), DependencyType.Independent);
            _hazardThreshold = SerializationUtilities.ReadDouble(xElement, nameof(HazardThreshold));
            _correlationMatrix = SerializationUtilities.ParseMatrix(SerializationUtilities.ReadString(xElement, nameof(CorrelationMatrix)));

            // Conditional-presence child: absent on every factor-free form (and on every
            // form persisted outside the latent-factors dependency mode).
            var factorContainer = xElement.Element(nameof(LatentFactors));
            if (factorContainer != null)
            {
                foreach (XElement factorElement in factorContainer.Elements(nameof(LatentFactor)))
                {
                    AddLatentFactor(new LatentFactor(factorElement));
                }
            }

            // Append-only attribute; absent on earlier payloads, which load
            // forward as the primary-hazard default.
            string profileId = SerializationUtilities.ReadString(xElement, nameof(ProfileHazardElementId));
            _profileHazardElementId = Guid.TryParse(profileId, out Guid parsedProfileId) ? parsedProfileId : null;

            var graphElement = xElement.Element(nameof(ComponentGraph));
            _graph = graphElement != null ? new ComponentGraph(graphElement, resolver) : new ComponentGraph();
            SubscribeGraph();
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="Name"/>.
        /// </summary>
        private string _name = "System Component";

        /// <summary>
        /// The owned risk topology. Assigned once per constructor.
        /// </summary>
        private ComponentGraph _graph;

        /// <summary>
        /// Backing field for <see cref="FailureModeMethod"/>.
        /// </summary>
        private FailureModeMethod _failureModeMethod = FailureModeMethod.JointFailures;

        /// <summary>
        /// Backing field for <see cref="FailureModeDependency"/>.
        /// </summary>
        private DependencyType _failureModeDependency = DependencyType.Independent;

        /// <summary>
        /// Backing field for <see cref="JointConsequences"/>.
        /// </summary>
        private JointConsequenceType _jointConsequences = JointConsequenceType.Maximum;

        /// <summary>
        /// Backing field for <see cref="CorrelationMatrix"/>. In the automatic and latent-factors
        /// dependency modes the multivariate-normal update writes the derived matrix here (v1.0
        /// behavior); only the <see cref="DependencyType.CorrelationMatrix"/> mode treats it as
        /// user content.
        /// </summary>
        private double[,]? _correlationMatrix;

        /// <summary>
        /// The insertion-ordered latent factors of the
        /// <see cref="DependencyType.LatentFactors"/> dependency mode (see
        /// <see cref="LatentFactors"/>).
        /// </summary>
        private readonly List<LatentFactor> _latentFactors = new List<LatentFactor>();

        /// <summary>
        /// Backing field for <see cref="HazardThreshold"/>.
        /// </summary>
        private double _hazardThreshold;

        /// <summary>
        /// Backing field for <see cref="ProfileHazardElementId"/>.
        /// </summary>
        private Guid? _profileHazardElementId;

        /// <summary>
        /// The resolved profile transform chain — the run-time product of
        /// <see cref="SetupSamplers(int, int, SamplingScheme)"/>, never serialized, hashed, or cloned. Null when no
        /// profile element is selected or the selection does not resolve.
        /// </summary>
        private ITransformFunction[]? _profileTransformFunctions;

        /// <summary>
        /// The failure-path count the combination caches were computed for; −1 forces a rebuild.
        /// </summary>
        private int _combosForCount = -1;

        /// <summary>
        /// Cached failure on/off combinations (see <see cref="FailureModeIndicators"/>).
        /// </summary>
        private int[,]? _failureModeCombinations;

        /// <summary>
        /// The failure-path count the binomial cache was computed for; −1 forces a rebuild.
        /// </summary>
        private int _binomialForCount = -1;

        /// <summary>
        /// Cached binomial subset counts (see <see cref="FailureModeBinomialCombinations"/>).
        /// </summary>
        private int[]? _failureModeBinomialCombinations;

        /// <summary>
        /// The failure-path count the multivariate normal was built for; −1 forces a rebuild.
        /// </summary>
        private int _mvnForCount = -1;

        /// <summary>
        /// Marks the multivariate normal stale after a dependency or matrix edit.
        /// </summary>
        private bool _mvnStale = true;

        /// <summary>
        /// The cached multivariate normal for failure-mode dependence.
        /// </summary>
        private MultivariateNormal? _mvn;

        /// <summary>
        /// Whether the correlation matrix passed its last positive-definiteness check.
        /// </summary>
        private bool _matrixValid = true;

        /// <summary>
        /// Whether this run may share one competing-risks pre-processing across realizations
        /// (see <see cref="TryGetCompetingIncidence"/>). Set at the <c>SetupSamplers</c> freeze
        /// point; false outside a run.
        /// </summary>
        private bool _shareCompetingIncidence;

        /// <summary>
        /// The run-shared cumulative incidence DATA — hazard levels and incidence probabilities
        /// per combination unit — or null until the first realization publishes it. Arrays rather
        /// than distributions: see <see cref="TryGetCompetingIncidence"/>.
        /// </summary>
        private (double[] Hazards, double[] Probabilities)[]? _sharedIncidenceData;

        /// <summary>
        /// Guards publication of the run-shared competing-risks pre-processing.
        /// </summary>
        private readonly object _incidenceCacheLock = new object();

        /// <summary>
        /// This run's content-derived component seed, captured at <c>SetupSamplers</c>; it seeds
        /// the competing-risks quadrature randomizer. Zero outside a run.
        /// </summary>
        private int _runComponentSeed;

        /// <summary>
        /// The component's display name. Identity metadata — serialized, never hashed.
        /// </summary>
        public string Name
        {
            get { return _name; }
            set
            {
                string coerced = value ?? string.Empty;
                if (!string.Equals(_name, coerced, StringComparison.Ordinal))
                {
                    _name = coerced;
                    RaisePropertyChange(nameof(Name));
                }
            }
        }

        /// <summary>
        /// Gets the component's risk topology: the typed, validated DAG of hazard, transform,
        /// response, and consequence elements. The graph is the persisted truth;
        /// <see cref="FailureModes"/> projects from it.
        /// </summary>
        public ComponentGraph Graph
        {
            get { return _graph; }
        }

        /// <summary>
        /// The hazard function — a view over the graph's hazard root element (v1.0 API). Getting
        /// returns the root's wrapped function; setting assigns it, creating the root element
        /// when the graph has none, and applies the v1.0 auto-naming ("System Component - …")
        /// while the component still carries a default name.
        /// </summary>
        public IHazardFunction? HazardFunction
        {
            get { return RootHazardElement?.Function; }
            set
            {
                var root = RootHazardElement;
                if (root == null)
                {
                    root = new HazardElement(_graph.GetUniqueName(value is null || string.IsNullOrEmpty(value.Name) ? "Hazard" : value.Name));
                    _graph.AddElement(root);
                }
                root.Function = value;
                if (value != null && (string.IsNullOrEmpty(_name) || string.Equals(_name, "System Component", StringComparison.Ordinal)))
                {
                    Name = $"System Component - {value.Name}";
                }
                RaisePropertyChange(nameof(HazardFunction));
            }
        }

        /// <summary>
        /// Gets the failure modes projected from the graph topology: one per consequence element
        /// in graph declared order, each the chain of stage transforms, responses, trailing
        /// transforms, and consequence functions along the terminal's path. A fresh snapshot is
        /// built on every access — always consistent with the graph; capture the list once per
        /// run. Structurally unsound graphs (no single root, cycles) project empty; validation
        /// reports why.
        /// </summary>
        public IReadOnlyList<FailureMode> FailureModes
        {
            get { return ProjectFailureModes().AsReadOnly(); }
        }

        /// <summary>
        /// How multiple failure modes combine. Selecting the common-cause or mutually-exclusive
        /// method coerces <see cref="FailureModeDependency"/> to independent (v1.0 behavior —
        /// those methods have no dependence model). Compute-relevant — hashed.
        /// </summary>
        public FailureModeMethod FailureModeMethod
        {
            get { return _failureModeMethod; }
            set
            {
                if (_failureModeMethod != value)
                {
                    _failureModeMethod = value;
                    RaisePropertyChange(nameof(FailureModeMethod));
                    if (_failureModeMethod == FailureModeMethod.CommonCauseFailures || _failureModeMethod == FailureModeMethod.MutuallyExclusive)
                    {
                        FailureModeDependency = DependencyType.Independent;
                    }
                }
            }
        }

        /// <summary>
        /// The statistical dependence between failure modes, realized as a Gaussian copula over
        /// the response probabilities. Compute-relevant — hashed.
        /// </summary>
        public DependencyType FailureModeDependency
        {
            get { return _failureModeDependency; }
            set
            {
                if (_failureModeDependency != value)
                {
                    _failureModeDependency = value;
                    _mvnStale = true;
                    RaisePropertyChange(nameof(FailureModeDependency));
                }
            }
        }

        /// <summary>
        /// How the consequences of jointly failing modes combine. Compute-relevant — hashed.
        /// </summary>
        public JointConsequenceType JointConsequences
        {
            get { return _jointConsequences; }
            set
            {
                if (_jointConsequences != value)
                {
                    _jointConsequences = value;
                    RaisePropertyChange(nameof(JointConsequences));
                }
            }
        }

        /// <summary>
        /// The failure-mode correlation matrix. User content under
        /// <see cref="DependencyType.CorrelationMatrix"/> (must be positive definite with one row
        /// per failure path — validated); in the automatic and latent-factors modes it holds the
        /// derived effective matrix after the multivariate normal is built (v1.0 behavior).
        /// Serialized and hashed only in the correlation-matrix mode.
        /// </summary>
        public double[,]? CorrelationMatrix
        {
            get { return _correlationMatrix == null ? null : (double[,])_correlationMatrix.Clone(); }
            set
            {
                _correlationMatrix = value == null ? null : (double[,])value.Clone();
                _mvnStale = true;
                RaisePropertyChange(nameof(CorrelationMatrix));
            }
        }

        /// <summary>
        /// The named latent factors of the <see cref="DependencyType.LatentFactors"/> dependency
        /// mode, in declared order (order is semantic — it is the hashed content order). Each
        /// factor carries one loading per combination unit; the induced correlation
        /// ρij = Σf λif·λjf feeds the same combination kernels as a user matrix. Serialized and
        /// hashed as a conditional child only in the latent-factors mode, so every other
        /// component keeps a byte-identical form, canonical identity, and seed.
        /// </summary>
        public IReadOnlyList<LatentFactor> LatentFactors
        {
            get { return _latentFactors; }
        }

        /// <summary>
        /// Adds one latent factor. Configuring factors under the
        /// <see cref="DependencyType.LatentFactors"/> mode is deliberate compute content — the
        /// loadings enter the canonical identity and move seeds.
        /// </summary>
        /// <param name="factor">The factor.</param>
        /// <exception cref="ArgumentNullException">Thrown when the factor is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the same factor instance is already attached.</exception>
        public void AddLatentFactor(LatentFactor factor)
        {
            if (factor == null) throw new ArgumentNullException(nameof(factor));
            for (int i = 0; i < _latentFactors.Count; i++)
            {
                if (ReferenceEquals(_latentFactors[i], factor))
                {
                    throw new InvalidOperationException($"Latent factor '{factor.Name}' is already attached to this component.");
                }
            }
            _latentFactors.Add(factor);
            factor.PropertyChanged += LatentFactorPropertyChanged;
            _mvnStale = true;
            RaisePropertyChange(nameof(LatentFactors));
        }

        /// <summary>Removes one attached latent factor.</summary>
        /// <param name="factor">The attached factor.</param>
        /// <returns>True when the factor was attached and removed.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the factor is null.</exception>
        public bool RemoveLatentFactor(LatentFactor factor)
        {
            if (factor == null) throw new ArgumentNullException(nameof(factor));
            int index = _latentFactors.FindIndex(existing => ReferenceEquals(existing, factor));
            if (index < 0) return false;
            _latentFactors.RemoveAt(index);
            factor.PropertyChanged -= LatentFactorPropertyChanged;
            _mvnStale = true;
            RaisePropertyChange(nameof(LatentFactors));
            return true;
        }

        /// <summary>
        /// Invalidates the dependence machinery when an attached factor's content changes.
        /// </summary>
        /// <param name="sender">The factor.</param>
        /// <param name="e">The change.</param>
        private void LatentFactorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            _mvnStale = true;
            RaisePropertyChange(nameof(LatentFactors));
        }

        /// <summary>
        /// The hazard level threshold for assurance estimates (the probability of hazard levels
        /// exceeding the threshold). Compute-relevant — hashed.
        /// </summary>
        public double HazardThreshold
        {
            get { return _hazardThreshold; }
            set
            {
                if (_hazardThreshold != value)
                {
                    _hazardThreshold = value;
                    RaisePropertyChange(nameof(HazardThreshold));
                }
            }
        }

        /// <summary>
        /// The graph element whose output defines the profile hazard axis — the risk profiles
        /// (hazard frequency, conditional mean consequence, and the cumulative profiles) and the
        /// <see cref="HazardThreshold"/> are expressed in that element's output signal. Null (the
        /// default) selects the primary driving hazard. The referenced element must be a
        /// <see cref="TransformElement"/> in this component's own graph whose upstream path
        /// reaches the hazard root (validated by <see cref="Validate()"/>).
        /// </summary>
        /// <remarks>
        /// A reporting-axis binding, deliberately <b>seed-inert</b>: the id is
        /// serialized append-only on the persistence surface but excluded from the identity form
        /// behind <see cref="CanonicalHash"/>, so selecting or changing the profile axis can
        /// never re-roll Monte Carlo seeds — only the profile surfaces (and the hazard-threshold
        /// probability read from them) move. This is the same hashed-but-seed-inert posture the
        /// analysis options carry (§5.5.3: the options hash never feeds seeds); the asymmetry
        /// with <see cref="HazardThreshold"/>, which sits inside the identity form, is
        /// deliberate — removing an established identity attribute would move every component
        /// hash.
        /// </remarks>
        public Guid? ProfileHazardElementId
        {
            get { return _profileHazardElementId; }
            set
            {
                if (_profileHazardElementId != value)
                {
                    _profileHazardElementId = value;
                    RaisePropertyChange(nameof(ProfileHazardElementId));
                }
            }
        }

        /// <summary>
        /// Selects the profile hazard element by reference — the typed convenience over
        /// <see cref="ProfileHazardElementId"/> for graph editors.
        /// </summary>
        /// <param name="element">
        /// The transform element in this component's graph whose output defines the profile
        /// axis, or null to restore the primary driving hazard.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when the element is not a <see cref="TransformElement"/> or is not an element
        /// of this component's graph.
        /// </exception>
        public void SetProfileHazardElement(IRiskElement? element)
        {
            if (element == null)
            {
                ProfileHazardElementId = null;
                return;
            }
            if (element is not TransformElement)
            {
                throw new ArgumentException("The profile hazard element must be a transform element (the primary hazard is selected by clearing the id).", nameof(element));
            }
            if (!ReferenceEquals(_graph.GetElementById(element.Id), element))
            {
                throw new ArgumentException("The profile hazard element must belong to this component's graph.", nameof(element));
            }
            ProfileHazardElementId = element.Id;
        }

        /// <summary>
        /// The resolved profile transform chain (root-first) whose composition maps the driving
        /// hazard onto the selected profile axis, refreshed by <see cref="SetupSamplers(int, int, SamplingScheme)"/>; null
        /// when no profile element is selected. Run-time state — never serialized or hashed.
        /// </summary>
        internal ITransformFunction[]? ProfileTransformFunctions
        {
            get { return _profileTransformFunctions; }
        }

        /// <summary>
        /// Determines whether the hazard and every projected failure mode carry no knowledge
        /// uncertainty.
        /// </summary>
        public bool IsDeterministic
        {
            get
            {
                var hazard = HazardFunction;
                if (hazard is not null && !hazard.IsDeterministic) return false;
                var modes = ProjectFailureModes();
                for (int i = 0; i < modes.Count; i++)
                {
                    if (!modes[i].IsDeterministic) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// The compatibility inspection matrix of failure on/off combinations over the current
        /// combination units. The matrix is materialized only when this property is requested and
        /// returned as a defensive clone. The engine uses lazy enumeration and never reads or
        /// populates this dense cache. Null while the component has no failure paths.
        /// </summary>
        public int[,]? FailureModeIndicators
        {
            get
            {
                var indicators = FailureModeIndicatorsFor(CombinationUnitCount());
                return indicators == null ? null : (int[,])indicators.Clone();
            }
        }

        /// <summary>
        /// The seed for this run's competing-risks quadrature randomizer, derived from the
        /// component's content seed on a reserved ordinal so it cannot collide with the
        /// per-function sampler seeds. Carries no realization index: the shared pre-processing
        /// below is published by whichever realization finishes first.
        /// </summary>
        internal int CompetingRiskSeed => SeedHelpers.HashCombine(_runComponentSeed, Array.Empty<byte>(), CompetingRiskSeedOrdinal);

        /// <summary>
        /// The walk ordinal reserved for the competing-risks quadrature seed.
        /// </summary>
        private const int CompetingRiskSeedOrdinal = -101;

        /// <summary>
        /// Attempts to take this run's shared competing-risks pre-processing, so a deterministic
        /// component builds its cumulative incidence functions once instead of once per
        /// realization.
        /// </summary>
        /// <param name="incidenceData">Receives the shared incidence data, or null on a miss.</param>
        /// <returns>True when the run may share and a previous realization has already published.</returns>
        /// <remarks>
        /// Admissible only when <see cref="IsDeterministic"/> held at the freeze point, where every
        /// realization samples identical curves and so computes identical incidence data. Only the
        /// arrays are shared — callers rebuild their own distributions and bins, since a Numerics
        /// interpolator writes its <c>SearchStart</c> on every lookup and cannot be read
        /// concurrently.
        /// </remarks>
        internal bool TryGetCompetingIncidence(out (double[] Hazards, double[] Probabilities)[]? incidenceData)
        {
            if (!_shareCompetingIncidence)
            {
                incidenceData = null;
                return false;
            }
            incidenceData = Volatile.Read(ref _sharedIncidenceData);
            return incidenceData != null;
        }

        /// <summary>
        /// Publishes a realization's competing-risks pre-processing for the rest of the run.
        /// </summary>
        /// <param name="incidenceData">The cumulative incidence data to share.</param>
        /// <remarks>
        /// Two realizations may reach this before either publishes; both computed identical values,
        /// so either may win. The write is still locked and published with a release barrier so a
        /// reader cannot observe a partially constructed array.
        /// </remarks>
        internal void PublishCompetingIncidence((double[] Hazards, double[] Probabilities)[] incidenceData)
        {
            if (!_shareCompetingIncidence) return;
            lock (_incidenceCacheLock)
            {
                if (_sharedIncidenceData != null) return;
                Volatile.Write(ref _sharedIncidenceData, incidenceData);
            }
        }

        /// <summary>
        /// Materializes the legacy dense indicator matrix for explicit compatibility inspection.
        /// The engine does not call this accessor.
        /// </summary>
        /// <param name="unitCount">The combination-unit count.</param>
        /// <returns>The dense indicator matrix, or null when the component has no failure paths.</returns>
        /// <remarks>
        /// Callers should prefer the Numerics lazy enumeration APIs for wide components; this
        /// compatibility surface intentionally retains the dense allocation behavior of v1.0.
        /// </remarks>
        internal int[,]? FailureModeIndicatorsFor(int unitCount)
        {
            if (unitCount != _combosForCount)
            {
                _failureModeCombinations = unitCount > 0 ? Factorial.AllCombinations(unitCount) : null;
                _combosForCount = unitCount;
            }
            return _failureModeCombinations;
        }

        /// <summary>
        /// The compatibility inspection vector of binomial subset counts over the current
        /// combination units. It is materialized only when this property is requested and returned
        /// as a defensive clone. The engine uses lazy enumeration and never populates this cache.
        /// Null while the component has no failure paths.
        /// </summary>
        public int[]? FailureModeBinomialCombinations
        {
            get
            {
                var combinations = FailureModeBinomialCombinationsFor(CombinationUnitCount());
                return combinations == null ? null : (int[])combinations.Clone();
            }
        }

        /// <summary>
        /// Materializes the legacy binomial subset-count vector for explicit compatibility
        /// inspection. The engine does not call this accessor.
        /// </summary>
        /// <param name="unitCount">The combination-unit count.</param>
        /// <returns>The subset counts, or null when the component has no failure paths.</returns>
        internal int[]? FailureModeBinomialCombinationsFor(int unitCount)
        {
            if (unitCount != _binomialForCount)
            {
                if (unitCount > 0)
                {
                    var subsets = new int[unitCount];
                    for (int i = 1; i <= unitCount; i++)
                    {
                        subsets[i - 1] = (int)Math.Round(Factorial.BinomialCoefficient(unitCount, i));
                    }
                    _failureModeBinomialCombinations = subsets;
                }
                else
                {
                    _failureModeBinomialCombinations = null;
                }
                _binomialForCount = unitCount;
            }
            return _failureModeBinomialCombinations;
        }

        /// <summary>
        /// The multivariate normal realizing the failure-mode dependence (the Gaussian copula's
        /// latent distribution), built lazily per the dependency option with the exact v1.0
        /// off-diagonal constants: <c>1 − √εmach</c> (perfectly positive) and
        /// <c>−1/(D − 1) + √εmach</c> (perfectly negative — the most negative exchangeable
        /// equicorrelation that stays positive semi-definite) — or, in the latent-factors mode,
        /// the loadings-induced matrix. Null while the component has no
        /// failure paths or the correlation matrix is invalid.
        /// </summary>
        public MultivariateNormal? FailureModeMultivariateNormal
        {
            get
            {
                EnsureDependencyMatrixCurrent();
                return _matrixValid && CombinationUnitCount() > 0 ? _mvn : null;
            }
        }

        /// <summary>
        /// Ensures the failure-mode dependence machinery is current for the present failure-path
        /// count: rebuilds the multivariate normal when stale and — in the automatic and
        /// latent-factors dependency modes — back-fills <see cref="CorrelationMatrix"/> with the
        /// derived matrix. v1.0
        /// rebuilt eagerly on every ctor/setter/mode edit, so its compute paths always read a
        /// fresh matrix; v1.1 builds lazily, so the per-run freeze point
        /// (<see cref="SetupSamplers(int, int, SamplingScheme)"/>) calls this before any sampling. Without this call a
        /// derived-mode matrix never materializes on the compute path (the
        /// sampled component captures <see cref="CorrelationMatrix"/> at construction), which
        /// would silently zero the dependent combination kernels.
        /// </summary>
        internal void EnsureDependencyMatrixCurrent()
        {
            int count = CombinationUnitCount();
            if (_mvnStale || count != _mvnForCount)
            {
                UpdateMultivariateNormal(count);
            }
        }

        /// <summary>
        /// The component's occurrence index among identical-content components in the current
        /// analysis (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.5.4): the number of
        /// other components with the same
        /// canonical hash that precede it in the canonical ordering. Runtime-only — assigned by
        /// <see cref="AssignOccurrenceIndices"/> before each run; never serialized, never hashed.
        /// </summary>
        public int OccurrenceIndex { get; internal set; }

        /// <summary>
        /// The failure-mode projection captured by <see cref="SetupSamplers(int, int, SamplingScheme)"/> for the current
        /// run — <see cref="FailureModes"/> builds a fresh projection on every access, so the
        /// engine must sample against one frozen snapshot. Runtime sampler state: never
        /// serialized, never hashed, never cloned.
        /// </summary>
        private IReadOnlyList<FailureMode>? _sampledModes;

        /// <summary>
        /// The snapshot's non-failure mode; null when the component has none.
        /// </summary>
        private FailureMode? _sampledNonFailureMode;

        /// <summary>
        /// The frozen projection snapshot captured by <see cref="SetupSamplers(int, int, SamplingScheme)"/> — exposed for
        /// the engine's per-run end-state labeling; null before the samplers are set up.
        /// </summary>
        internal IReadOnlyList<FailureMode>? SampledProjection => _sampledModes;

        /// <summary>
        /// The snapshot's end-state group layout
        /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.9), frozen beside the projection
        /// by <see cref="SetupSamplers(int, int, SamplingScheme)"/> so every realization combines against one structure.
        /// Runtime sampler state: never serialized, never hashed, never cloned.
        /// </summary>
        private EndStateGroupLayout? _sampledLayout;

        /// <summary>
        /// Raised when a component property changes. Passive contract — headless callers need
        /// not subscribe.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        #endregion

        #region Methods

        /// <summary>
        /// Expands a chain-style failure mode into wired graph elements (the near-v1.0 authoring
        /// path): stage transforms and responses become elements chained from the hazard root
        /// (created if absent), trailing transforms follow, and a terminal consequence element
        /// carries the mode's consequence functions. An explicit non-default consequence hazard
        /// position becomes a hazard-source binding on the terminal.
        /// </summary>
        /// <param name="failureMode">The chain-style mode to expand. The graph takes ownership of its function instances.</param>
        /// <exception cref="ArgumentNullException">Thrown when the mode is null.</exception>
        /// <exception cref="NotSupportedException">
        /// Thrown when the mode carries bivariate topology — a Secondary hazard binding or
        /// consequence dimension, or a non-empty secondary-hazard chain. A linear chain
        /// expansion cannot wire the off-path secondary connections those require, and dropping
        /// them silently would break the expansion's bit-identical projection contract; author
        /// bivariate components through the graph surface instead.
        /// </exception>
        /// <remarks>
        /// The expansion is lossless for failure chains — projecting <see cref="FailureModes"/>
        /// afterwards reproduces the mode bit-identically (same canonical hash). A v1.0-style
        /// non-failure mode (transforms carried in <c>ResponseToConsequence</c>) normalizes to
        /// the canonical stage form on projection: identical math, canonicalized shape.
        /// <c>MultipleConsequences</c> re-derives from topology (a linear expansion is
        /// single-terminal).
        /// </remarks>
        public void AddFailureMode(FailureMode failureMode)
        {
            if (failureMode == null) throw new ArgumentNullException(nameof(failureMode));
            if (failureMode.HazardBinding == HazardDimension.Secondary
                || failureMode.ConsequenceHazardDimension == HazardDimension.Secondary
                || failureMode.SecondaryHazardToResponse.Count > 0)
            {
                throw new NotSupportedException(
                    "The chain-style expansion cannot wire bivariate topology (a Secondary hazard binding, a Secondary consequence dimension, or a secondary-hazard chain); author bivariate components through the graph surface.");
            }

            var root = RootHazardElement;
            if (root == null)
            {
                root = new HazardElement(_graph.GetUniqueName("Hazard"));
                _graph.AddElement(root);
            }

            IRiskElement upstream = root;
            // The port the next connection consumes from the current upstream: a response's
            // branch port after each stage (the stage's polarity), 0 otherwise,
            // so projecting the expansion reproduces the chain's polarities bit-identically.
            int upstreamPort = 0;
            Guid? upstreamBranchId = null;
            string? upstreamBranchName = null;
            var stageTransformElements = new List<TransformElement>();
            bool isNonFail = failureMode.IsNonFailureMode;
            for (int s = 0; s < failureMode.ResponseStages.Count; s++)
            {
                var stage = failureMode.ResponseStages[s];
                if (stage is null) continue;
                for (int t = 0; t < stage.Transforms.Count; t++)
                {
                    upstream = AddTransformElement(stage.Transforms[t], upstream,
                        stageTransformElements, upstreamPort, upstreamBranchId, upstreamBranchName);
                    upstreamPort = 0;
                    upstreamBranchId = null;
                    upstreamBranchName = null;
                }
                if (!isNonFail)
                {
                    var responseElement = new ResponseElement(_graph.GetUniqueName(ElementName(stage.Response?.Name, "Response")))
                    {
                        Function = stage.Response,
                        ExpandBranchOutputs = stage.SelectedBranchId.HasValue,
                        Input = new RiskConnection(upstream, upstreamPort,
                            upstreamBranchId, upstreamBranchName),
                    };
                    _graph.AddElement(responseElement);
                    upstream = responseElement;
                    if (stage.SelectedBranchId.HasValue)
                    {
                        ResponseBranchDescriptor branch = responseElement.RequireAvailableBranch(
                            stage.SelectedBranchId.Value);
                        upstreamPort = branch.OutputPort;
                        upstreamBranchId = branch.Id;
                        upstreamBranchName = branch.Name;
                    }
                    else
                    {
                        upstreamPort = (int)stage.BranchPolarity;
                        upstreamBranchId = null;
                        upstreamBranchName = null;
                    }
                }
            }
            for (int t = 0; t < failureMode.ResponseToConsequence.Count; t++)
            {
                upstream = AddTransformElement(failureMode.ResponseToConsequence[t], upstream,
                    null, upstreamPort, upstreamBranchId, upstreamBranchName);
                upstreamPort = 0;
                upstreamBranchId = null;
                upstreamBranchName = null;
            }

            var primary = failureMode.ConsequenceFunctions.Count > 0 ? failureMode.ConsequenceFunctions[0] : null;
            var terminal = new ConsequenceElement(_graph.GetUniqueName(ElementName(primary?.Name, "Consequence")))
            {
                Input = new RiskConnection(upstream, upstreamPort,
                    upstreamBranchId, upstreamBranchName),
                Functions = new ObservableCollection<IConsequenceFunction>(failureMode.ConsequenceFunctions),
            };

            if (failureMode.ConsequenceHazardPosition.HasValue &&
                failureMode.ConsequenceHazardPosition.Value != failureMode.TotalStageTransformCount)
            {
                int position = failureMode.ConsequenceHazardPosition.Value;
                IRiskElement? target = position == 0
                    ? root
                    : (position - 1 < stageTransformElements.Count ? stageTransformElements[position - 1] : null);
                if (target != null)
                {
                    terminal.HazardSource = new RiskConnection(target,
                        failureMode.ConsequenceHazardDimension == HazardDimension.Secondary ? 1 : 0);
                }
            }
            _graph.AddElement(terminal);
        }

        /// <summary>
        /// Determines whether the dependence matrix is valid for the current failure-path count:
        /// under <see cref="DependencyType.CorrelationMatrix"/> the stored user matrix (a matching
        /// dimension and positive definiteness via Cholesky decomposition); under
        /// <see cref="DependencyType.LatentFactors"/> the loadings' structural validity and the
        /// induced matrix's positive definiteness. Always true in the other modes.
        /// </summary>
        /// <returns>True when the matrix is usable.</returns>
        public bool IsCorrelationMatrixValid()
        {
            ValidateCorrelationMatrix(CombinationUnitCount());
            return _matrixValid;
        }

        /// <summary>
        /// Validates the component and reports any issues found.
        /// </summary>
        /// <returns>
        /// A tuple containing:
        /// <list type="bullet">
        /// <item>
        /// <description><c>IsValid</c>: <c>true</c> if the component passes all validation checks; otherwise <c>false</c>.</description>
        /// </item>
        /// <item>
        /// <description><c>ValidationMessages</c>: messages describing validation errors ("Error: …", invalidating) and warnings ("Warning: …", advisory).</description>
        /// </item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// Aggregates the graph's structural validation (which covers the hazard root, element
        /// functions, connectivity, path rules, bindings, and consequence alignment), the
        /// correlation-matrix check under the correlation-matrix dependency mode, and the
        /// projected failure modes' advisory label-continuity warnings (their errors are covered
        /// structurally by the graph; warnings are de-duplicated across modes sharing a chain
        /// prefix).
        /// </remarks>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            return Validate(RiskAnalysisMode.Risk);
        }

        /// <summary>
        /// Validates the component for the given analysis mode. Reliability mode
        /// threads through to the graph's and failure modes' relaxed consequence checks —
        /// consequence elements stay the structural terminals but need no functions; everything
        /// else is identical to <see cref="Validate()"/>.
        /// </summary>
        /// <param name="mode">The analysis mode the component is being validated for.</param>
        /// <returns>
        /// A tuple containing:
        /// <list type="bullet">
        /// <item>
        /// <description><c>IsValid</c>: <c>true</c> if the component passes all validation checks; otherwise <c>false</c>.</description>
        /// </item>
        /// <item>
        /// <description><c>ValidationMessages</c>: messages describing validation errors ("Error: …", invalidating) and warnings ("Warning: …", advisory).</description>
        /// </item>
        /// </list>
        /// </returns>
        public (bool IsValid, List<string> ValidationMessages) Validate(RiskAnalysisMode mode)
        {
            var messages = new List<string>();
            messages.AddRange(_graph.Validate(mode).ValidationMessages);

            if (_failureModeDependency == DependencyType.CorrelationMatrix && !IsCorrelationMatrixValid())
            {
                messages.Add($"Error: The failure mode correlation matrix is not positive definite or does not match the combination dimension (the state-group count) for system component '{Name}'.");
            }

            if (_failureModeDependency == DependencyType.LatentFactors)
            {
                int combinationDimension = CombinationUnitCount();
                var factorErrors = CollectLatentFactorConfigurationErrors(combinationDimension);
                messages.AddRange(factorErrors);
                if (factorErrors.Count == 0 && combinationDimension > 0 && !IsCorrelationMatrixValid())
                {
                    messages.Add($"Error: The latent-factor loadings of system component '{Name}' induce a correlation matrix that is not positive definite; reduce a unit's squared-loading sum below 1, or make the units' loading vectors less than proportional.");
                }
            }
            else if (_latentFactors.Count > 0)
            {
                messages.Add($"Warning: System component '{Name}' has latent factors configured but the failure-mode dependency is {_failureModeDependency}; the factors have no effect and do not persist outside the latent-factors mode.");
            }

            // The profile hazard selection: the id must resolve to a transform element in
            // this graph, carrying a function, whose upstream path reaches the hazard root. The
            // threshold advisory reminds the analyst that a selected profile re-expresses the
            // hazard threshold on the profile axis.
            if (_profileHazardElementId.HasValue)
            {
                var profileElement = _graph.GetElementById(_profileHazardElementId.Value);
                if (profileElement == null)
                {
                    messages.Add($"Error: The profile hazard element id does not resolve to an element of system component '{Name}'.");
                }
                else if (profileElement is not TransformElement profileTransform)
                {
                    messages.Add($"Error: The profile hazard element '{profileElement.Name}' of system component '{Name}' is not a transform element; select a transform, or clear the selection for the primary hazard.");
                }
                else
                {
                    if (profileTransform.Function == null)
                    {
                        messages.Add($"Error: The profile hazard element '{profileTransform.Name}' of system component '{Name}' has no transform function assigned.");
                    }
                    var path = _graph.GetUpstreamPath(profileTransform);
                    if (path.Count == 0 || path[0] is not HazardElement)
                    {
                        messages.Add($"Error: The profile hazard element '{profileTransform.Name}' of system component '{Name}' is not connected upstream to the hazard element.");
                    }
                    else if (ExitConnection(path[1], path[0])?.SourcePort == 1)
                    {
                        // A secondary-branch transform reaches the root and would pass the
                        // reachability test above, but profiles are a primary-axis surface.
                        messages.Add($"Error: The profile hazard element '{profileTransform.Name}' of system component '{Name}' consumes the hazard's secondary output (port 1); the profile axis must derive from the primary hazard dimension — select a primary-chain transform, or clear the selection.");
                    }
                    else if (_hazardThreshold != 0d)
                    {
                        messages.Add($"Warning: The hazard threshold ({SerializationUtilities.FormatDouble(_hazardThreshold)}) of system component '{Name}' is interpreted on the selected profile hazard axis ('{profileTransform.Name}').");
                    }
                }
            }

            var modes = ProjectFailureModes();
            for (int i = 0; i < modes.Count; i++)
            {
                foreach (string message in modes[i].Validate(mode).ValidationMessages)
                {
                    if (message.StartsWith("Warning:", StringComparison.Ordinal) && !messages.Contains(message))
                    {
                        messages.Add(message);
                    }
                }
            }

            // The cascade state-group rules (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md
            // §7.9): claimed non-failure states are
            // scoped to one state group per component (§7.9.5 — with two claiming groups the
            // exact complement decomposition needs a cross-product enumeration that is
            // deliberately deferred), and the competing method requires every failure state's
            // weight to be monotone in the hazard — guaranteed by an all-Fail signature, broken
            // by an else-chain (§7.9.6).
            var layout = EndStateGroupLayout.Build(modes);
            if (layout.ClaimingCascadeCount > 1)
            {
                messages.Add($"Error: System component '{Name}' wires Non-Fail branch consequences in {layout.ClaimingCascadeCount} state groups; branch-scoped non-failure consequences are supported for one state group per component — wire the other cascades' Non-Fail branches to the background path.");
            }
            if (_failureModeMethod == FailureModeMethod.CompetingFailures && layout.HasNonFailBranchFailureState)
            {
                messages.Add($"Error: The competing failure-mode method is undefined for system component '{Name}': a failure state rides a Non-Fail branch (an else-chain), so its probability is not monotone in the hazard and no weak-link ordering exists — use Joint, Common Cause, or Mutually Exclusive.");
            }

            // The bivariate competing-risks guard: the multi-unit weak-link machinery
            // pre-processes cumulative incidence functions over PRIMARY hazard levels, so it
            // cannot represent a response probability that depends on the secondary hazard.
            // Every failure state must therefore be a pure primary function — a Primary hazard
            // binding with no joint bivariate response and no bivariate stage transform.
            // Single-unit competing needs no pre-processing and stays legal for any binding.
            if (_failureModeMethod == FailureModeMethod.CompetingFailures && layout.CombinationUnitCount > 1
                && HazardFunction is IBivariateHazardFunction)
            {
                int stateIndex = 0;
                for (int i = 0; i < modes.Count; i++)
                {
                    if (modes[i].IsNonFailureMode) continue;
                    bool isFailureState = layout.IsFailureState[stateIndex++];
                    if (!isFailureState) continue;

                    bool secondaryDependent = modes[i].HazardBinding == HazardDimension.Secondary;
                    var stages = modes[i].ResponseStages;
                    for (int s = 0; s < stages.Count && !secondaryDependent; s++)
                    {
                        if (stages[s] is null) continue;
                        if (stages[s].Response is IBivariateResponseFunction) secondaryDependent = true;
                        var transforms = stages[s].Transforms;
                        for (int t = 0; t < transforms.Count && !secondaryDependent; t++)
                        {
                            if (transforms[t] is IBivariateTransformFunction) secondaryDependent = true;
                        }
                    }
                    if (secondaryDependent)
                    {
                        messages.Add($"Error: System component '{Name}' combines competing failure modes over a bivariate hazard, but failure mode '{modes[i].ResponseFunction?.Name}' has a secondary-dependent response probability (a Secondary hazard binding, a joint bivariate response, or a bivariate stage transform); the cumulative-incidence pre-processing over primary hazard levels cannot represent it — use Joint, Common Cause, or Mutually Exclusive, or bind every failure mode to the primary axis.");
                    }
                }
            }

            // The branch-explosion guardrail at the component level: joint failure pathways
            // take the cross product of the participating combination units' exposure branches
            // within one consequence type (per-type marginal compute — types never cross;
            // within a unit the exclusive states' branches ADD, across units they MULTIPLY), so
            // the worst type's product across units is bounded — warn above 64, error above
            // 1024. The per-mode combination methods never cross modes, so the check applies to
            // the joint method only.
            if (_failureModeMethod == FailureModeMethod.JointFailures)
            {
                long pathwayBranches = WorstCasePathwayBranches(modes, layout);
                if (pathwayBranches > 1024)
                {
                    messages.Add($"Error: The joint failure pathways' combined exposure branches exceed 1024 (the cross product of the combination units' branch counts within the worst consequence type) for system component '{Name}'; reduce the mixture branch counts.");
                }
                else if (pathwayBranches > 64)
                {
                    messages.Add($"Warning: The joint failure pathways' combined exposure branches ({pathwayBranches}) exceed 64 for system component '{Name}'; the branch cross product grows compute cost accordingly.");
                }
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <summary>
        /// Estimates the worst-case number of failure pathway/branch entries this component can
        /// record at one hazard evaluation — the joint system-risk path's per-component
        /// combination width. Entries are recorded per consequence type and types never cross
        /// (per-type marginal compute), so the estimate is the worst type's bound: for the joint
        /// failure-mode method <c>Π (1 + bᵢ) − 1</c> over the failure modes' branch counts at
        /// that type (every failure subset crossed with its branch tuples); for the per-mode
        /// methods <c>Σ bᵢ</c>. The running product is capped so the estimate never overflows.
        /// </summary>
        /// <returns>The worst-case entry count, at least one.</returns>
        internal long EstimateRecordedFailureEntries()
        {
            const long cap = 1L << 40;
            var modes = ProjectFailureModes();
            var layout = EndStateGroupLayout.Build(modes);
            var stateModes = CollectStateModes(modes);
            int typeCount = ConsequenceTypeCount(modes);
            long estimate = 1;
            for (int k = 0; k < typeCount; k++)
            {
                // Within a combination unit the exclusive states' branch entries ADD; across
                // units they MULTIPLY. Claimed non-failure states record
                // complement entries, so they ride the additive bound. Both reduce to the
                // plain per-mode arithmetic under a trivial layout.
                long perModeBound = 0;
                for (int s = 0; s < stateModes.Count; s++)
                {
                    perModeBound += BranchCountAt(stateModes[s], k);
                }
                long jointBound = 1;
                for (int u = 0; u < layout.CombinationUnitCount && jointBound < cap; u++)
                {
                    long unitBranches = 0;
                    var members = layout.CombinationUnitStates[u];
                    for (int m = 0; m < members.Length; m++)
                    {
                        unitBranches += BranchCountAt(stateModes[members[m]], k);
                    }
                    jointBound *= 1 + unitBranches;
                }
                long typeEstimate = _failureModeMethod == FailureModeMethod.JointFailures ? jointBound - 1 : perModeBound;
                estimate = Math.Max(estimate, typeEstimate);
            }

            // A bivariate hazard's recorded entries enumerate (node × pathway × branch): the
            // conditional sweep multiplies every combination width by its node count, priced at
            // the adaptive refinement budget's hard mesh bound (deliberately worst-case — a
            // converged sweep adopts far fewer nodes).
            if (HazardFunction is IBivariateHazardFunction bivariateHazard)
            {
                estimate = Math.Min(cap, estimate * SampledComponent.ConditionalMeshCapacity(bivariateHazard.SecondaryIntegrationBins));
            }
            return Math.Max(1, Math.Min(cap, estimate));
        }

        /// <summary>
        /// Collects the end states — the non-background projected modes in projection order,
        /// the index space the end-state group layout uses.
        /// </summary>
        /// <param name="modes">The projected modes.</param>
        /// <returns>The state modes.</returns>
        private static List<FailureMode> CollectStateModes(IReadOnlyList<FailureMode> modes)
        {
            var stateModes = new List<FailureMode>(modes.Count);
            for (int i = 0; i < modes.Count; i++)
            {
                if (!modes[i].IsNonFailureMode) stateModes.Add(modes[i]);
            }
            return stateModes;
        }

        /// <summary>
        /// The worst consequence type's joint-pathway branch cross product across the
        /// combination units (within a unit the exclusive states' branches add, across units
        /// they multiply — per-type marginal compute, types never cross), capped just past the
        /// error guardrail so the product never overflows.
        /// </summary>
        /// <param name="modes">The projected failure modes.</param>
        /// <param name="layout">The end-state group layout over the modes.</param>
        /// <returns>The worst type's cross product, at least one.</returns>
        private static long WorstCasePathwayBranches(IReadOnlyList<FailureMode> modes, EndStateGroupLayout layout)
        {
            var stateModes = CollectStateModes(modes);
            int typeCount = ConsequenceTypeCount(modes);
            long worst = 1;
            for (int k = 0; k < typeCount; k++)
            {
                long pathwayBranches = 1;
                for (int u = 0; u < layout.CombinationUnitCount && pathwayBranches <= 1024; u++)
                {
                    long unitBranches = 0;
                    var members = layout.CombinationUnitStates[u];
                    for (int m = 0; m < members.Length; m++)
                    {
                        unitBranches += BranchCountAt(stateModes[members[m]], k);
                    }
                    pathwayBranches *= unitBranches;
                }
                worst = Math.Max(worst, pathwayBranches);
            }
            return worst;
        }

        /// <summary>
        /// The number of consequence-type positions any failure path carries (misaligned counts
        /// are an analysis-level validation error; the guardrails bound whatever is present).
        /// </summary>
        /// <param name="modes">The projected failure modes.</param>
        /// <returns>The maximum consequence count across the failure paths, at least one.</returns>
        private static int ConsequenceTypeCount(IReadOnlyList<FailureMode> modes)
        {
            int typeCount = 1;
            for (int i = 0; i < modes.Count; i++)
            {
                if (!modes[i].IsNonFailureMode)
                {
                    typeCount = Math.Max(typeCount, modes[i].ConsequenceFunctions.Count);
                }
            }
            return typeCount;
        }

        /// <summary>
        /// A failure mode's exposure-branch count at one consequence-type position (one for a
        /// missing or null position — the mode then contributes no branching at that type).
        /// </summary>
        /// <param name="mode">The failure mode.</param>
        /// <param name="position">The consequence-type position.</param>
        /// <returns>The branch count, at least one.</returns>
        private static long BranchCountAt(FailureMode mode, int position)
        {
            var consequence = position < mode.ConsequenceFunctions.Count ? mode.ConsequenceFunctions[position] : null;
            return Math.Max(1, consequence?.CountExposureBranches() ?? 1);
        }

        /// <summary>
        /// Computes the component's canonical SHA-256 content hash — the stable, name-free
        /// identity that content-based Monte Carlo seeding derives from. Hashes the internal
        /// identity form (options + hazard content + projected failure modes in path order), NOT
        /// the persisted element graph, so element names, ids, positions, and link attributes
        /// can never perturb seeds.
        /// </summary>
        /// <returns>The 32-byte SHA-256 hash of the canonicalized identity form.</returns>
        public byte[] CanonicalHash()
        {
            return CanonicalContentHasher.Hash(BuildIdentityXElement(), CanonicalizationRules.ModelRules);
        }

        /// <summary>
        /// Assigns occurrence indices across an analysis's components — the
        /// docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.5.4
        /// reference implementation: sort by (canonical hash via
        /// <see cref="ByteArrayComparer.Instance"/>, declared index), then number each
        /// equal-hash bucket 0..n−1. Recomputed before every run; never persisted.
        /// </summary>
        /// <param name="components">The analysis's components in declared order.</param>
        /// <exception cref="ArgumentNullException">Thrown when the list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the list contains a null component.</exception>
        public static void AssignOccurrenceIndices(IReadOnlyList<SystemComponent> components)
        {
            if (components == null) throw new ArgumentNullException(nameof(components));

            int count = components.Count;
            var hashes = new byte[count][];
            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (components[i] == null) throw new ArgumentException("The component list contains a null entry.", nameof(components));
                hashes[i] = components[i].CanonicalHash();
                order[i] = i;
            }

            Array.Sort(order, (a, b) =>
            {
                int comparison = ByteArrayComparer.Instance.Compare(hashes[a], hashes[b]);
                return comparison != 0 ? comparison : a.CompareTo(b);
            });

            int occurrence = 0;
            for (int k = 0; k < count; k++)
            {
                occurrence = k > 0 && ByteArrayComparer.Instance.Compare(hashes[order[k - 1]], hashes[order[k]]) == 0
                    ? occurrence + 1
                    : 0;
                components[order[k]].OccurrenceIndex = occurrence;
            }
        }

        /// <summary>
        /// Sets up the component's samplers for a run: captures the failure-mode projection once
        /// (the frozen snapshot every realization samples against), then walks the hazard and
        /// every projected mode in order, seeding each distinct function instance exactly once
        /// with a content-derived seed
        /// (<c>SeedHelpers.HashCombine(componentSeed, function.CanonicalHash(), ordinal)</c>).
        /// </summary>
        /// <param name="sampleSize">The realization count N.</param>
        /// <param name="componentSeed">
        /// The component's content-derived seed —
        /// <c>SeedHelpers.HashCombine(analysisSeed, CanonicalHash(), OccurrenceIndex)</c>
        /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.5.4), computed by the analysis after
        /// <see cref="AssignOccurrenceIndices"/>.
        /// </param>
        /// <param name="scheme">The knowledge-uncertainty sampling scheme.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the sample size is not positive.</exception>
        /// <remarks>
        /// Ordinals advance per walk position (every encounter); a function instance referenced
        /// at several positions is seeded at its first canonical position only and draws the
        /// identical realizations everywhere it appears — one shared instance is one knowledge
        /// quantity (the single-owner rule; equal-content distinct instances get different
        /// ordinals and draw independently). Consequence functions are not walked: their shared
        /// knowledge percentiles come from each mode's coupling matrix. Forward rule for
        /// composite hazards/responses: the walk seeds cluster roots only, a root's
        /// own <c>SetupSampler</c> owns its subtree, and the dedup set must absorb subtree
        /// members. This is also the per-run freeze point that materializes the effective
        /// failure-mode dependency matrix (<see cref="EnsureDependencyMatrixCurrent"/>) so the
        /// automatic modes' derived matrices are current before the first sample. When this
        /// standalone overload finds no ambient sharing scope and the component's composites
        /// name shared epistemic variables, it enters a component-scope sharing scope derived
        /// from the component seed — so binders of one variable share within the component; an
        /// analysis run supplies its own run-scope columns instead, sharing across components.
        /// </remarks>
        public void SetupSamplers(int sampleSize, int componentSeed, SamplingScheme scheme)
        {
            if (!EpistemicSharingScope.IsActive)
            {
                var variables = new HashSet<string>(StringComparer.Ordinal);
                CollectEpistemicVariables(variables);
                if (variables.Count > 0)
                {
                    using (EpistemicSharingScope.Enter(EpistemicSharingScope.BuildColumns(variables, componentSeed, sampleSize, scheme)))
                    {
                        SetupSamplers(sampleSize, componentSeed, scheme, null);
                    }
                    return;
                }
            }
            SetupSamplers(sampleSize, componentSeed, scheme, null);
        }

        /// <summary>
        /// Accumulates the shared epistemic variables named by this component's composite
        /// functions, nested composites included — the sharing scope's discovery surface.
        /// </summary>
        /// <param name="sink">The accumulating distinct variable names.</param>
        /// <exception cref="ArgumentNullException">Thrown when the sink is null.</exception>
        internal void CollectEpistemicVariables(ISet<string> sink)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            foreach (var function in GetReferencedFunctions())
            {
                switch (function)
                {
                    case CompositeHazard hazard:
                        hazard.CollectEpistemicVariables(sink);
                        break;
                    case CompositeResponse response:
                        response.CollectEpistemicVariables(sink);
                        break;
                    case CompositeTransform transform:
                        transform.CollectEpistemicVariables(sink);
                        break;
                }
            }
        }

        /// <summary>
        /// Accumulates the logic-tree axes declared by this component's walked epistemic
        /// composites, nested composites included — the exact enumerator's discovery surface,
        /// aggregating through the clusters' own cycle-safe recursions.
        /// </summary>
        /// <param name="boundAxes">The bound axes, keyed by variable name.</param>
        /// <param name="unboundAxes">The unbound axes, in discovery order.</param>
        /// <param name="mismatchedVariables">The sink recording variables whose binders declare differing weight vectors.</param>
        /// <param name="epistemicFunctionIds">The sink recording every epistemic composite id.</param>
        /// <param name="visited">The functions already searched (reference identity, shared across components).</param>
        /// <exception cref="ArgumentNullException">Thrown when a sink is null.</exception>
        internal void CollectLogicTreeAxes(IDictionary<string, LogicTreeAxisSeed> boundAxes,
            IList<LogicTreeAxisSeed> unboundAxes, ISet<string> mismatchedVariables,
            ISet<Guid> epistemicFunctionIds, ISet<IRiskFunction> visited)
        {
            if (boundAxes == null) throw new ArgumentNullException(nameof(boundAxes));
            if (unboundAxes == null) throw new ArgumentNullException(nameof(unboundAxes));
            if (mismatchedVariables == null) throw new ArgumentNullException(nameof(mismatchedVariables));
            if (epistemicFunctionIds == null) throw new ArgumentNullException(nameof(epistemicFunctionIds));
            if (visited == null) throw new ArgumentNullException(nameof(visited));
            foreach (var function in GetReferencedFunctions())
            {
                switch (function)
                {
                    case CompositeHazard hazard:
                        hazard.CollectLogicTreeAxes(boundAxes, unboundAxes, mismatchedVariables, epistemicFunctionIds, visited);
                        break;
                    case CompositeResponse response:
                        response.CollectLogicTreeAxes(boundAxes, unboundAxes, mismatchedVariables, epistemicFunctionIds, visited);
                        break;
                    case CompositeTransform transform:
                        transform.CollectLogicTreeAxes(boundAxes, unboundAxes, mismatchedVariables, epistemicFunctionIds, visited);
                        break;
                }
            }
        }

        /// <summary>
        /// Sets up the component's samplers with a seed scribe threaded through the walk — the
        /// engine's capture/apply entry for the §5.5.8 seed-stable perturbation mode (the
        /// public overload passes no scribe).
        /// </summary>
        /// <param name="sampleSize">The realization count N.</param>
        /// <param name="componentSeed">The component's content-derived seed.</param>
        /// <param name="scheme">The knowledge-uncertainty sampling scheme.</param>
        /// <param name="scribe">The seed scribe (capture, and optionally apply), or null.</param>
        /// <param name="fractilePins">The epistemic conditioning pins by function id, or null — applied to walked functions after seeding, so the captured seed map is untouched.</param>
        /// <param name="appliedPins">The sink recording every pin id the walk applied, or null.</param>
        /// <returns>The captured effective seeds by walk ordinal (empty without a scribe).</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the sample size is not positive.</exception>
        /// <exception cref="InvalidOperationException">Thrown when a pinned scribe does not match the walk shape.</exception>
        internal int[] SetupSamplers(int sampleSize, int componentSeed, SamplingScheme scheme, SeedScribe? scribe,
            IReadOnlyDictionary<Guid, double>? fractilePins = null, ISet<Guid>? appliedPins = null)
        {
            if (sampleSize <= 0) throw new ArgumentOutOfRangeException(nameof(sampleSize), "The sample size must be positive.");

            // Materialize the effective failure-mode dependency matrix before any sampling: the
            // sampled component captures the raw correlation-matrix reference, and the automatic
            // modes derive theirs here (v1.0 rebuilt eagerly on every edit; v1.1 refreshes at
            // this single-threaded per-run freeze point instead).
            EnsureDependencyMatrixCurrent();

            var modes = ProjectFailureModes();
            _sampledModes = modes;
            _sampledLayout = EndStateGroupLayout.Build(modes);


            // Arm the competing-risks pre-processing cache for this run and drop the previous
            // run's, which the re-seeding has just invalidated.
            _runComponentSeed = componentSeed;
            Volatile.Write(ref _sharedIncidenceData, null);
            _shareCompetingIncidence = _failureModeMethod == FailureModeMethod.CompetingFailures
                && _sampledLayout.CombinationUnitCount > 1
                && IsDeterministic;

            _sampledNonFailureMode = null;
            for (int i = 0; i < modes.Count; i++)
            {
                if (modes[i].IsNonFailureMode)
                {
                    _sampledNonFailureMode = modes[i];
                    break;
                }
            }

            var seededFunctions = new HashSet<IRiskFunction>(ReferenceEqualityComparer.Instance);
            int ordinal = 0;
            var hazard = HazardFunction;
            if (hazard != null)
            {
                if (seededFunctions.Add(hazard))
                {
                    int seed = SeedHelpers.HashCombine(componentSeed, hazard.CanonicalHash(), ordinal);
                    if (scribe != null) seed = scribe.Resolve(ordinal, seed);
                    hazard.SetupSampler(sampleSize, seed, scheme);
                }
                FailureMode.ApplyFractilePin(hazard, fractilePins, appliedPins);
                ordinal++;
            }
            for (int i = 0; i < modes.Count; i++)
            {
                ordinal = modes[i].SetupSamplers(sampleSize, componentSeed, ordinal, scheme, seededFunctions, scribe, fractilePins, appliedPins);
            }

            // Resolve the profile transform chain at the same freeze point. The chain's
            // functions are normally the failure modes' own transforms and are already seeded
            // above (the dedup set makes this a no-op); a transform on a reporting-only branch
            // is seeded here at the walk positions AFTER every mode, so the mode streams are a
            // stable prefix and the profile selection can never perturb them.
            _profileTransformFunctions = ResolveProfileTransforms();
            if (_profileTransformFunctions != null)
            {
                for (int i = 0; i < _profileTransformFunctions.Length; i++)
                {
                    var transform = _profileTransformFunctions[i];
                    if (seededFunctions.Add(transform))
                    {
                        int seed = SeedHelpers.HashCombine(componentSeed, transform.CanonicalHash(), ordinal);
                        if (scribe != null) seed = scribe.Resolve(ordinal, seed);
                        transform.SetupSampler(sampleSize, seed, scheme);
                    }
                    FailureMode.ApplyFractilePin(transform, fractilePins, appliedPins);
                    ordinal++;
                }
            }

            return scribe != null ? scribe.Finish(ordinal) : Array.Empty<int>();
        }

        /// <summary>
        /// Samples the component for one realization against the snapshot captured by
        /// <see cref="SetupSamplers(int, int, SamplingScheme)"/>.
        /// </summary>
        /// <param name="realizationIndex">The realization index, or −1 for the mean functions.</param>
        /// <returns>The sampled component.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown before <see cref="SetupSamplers(int, int, SamplingScheme)"/> has run, or when the component has no
        /// hazard function.
        /// </exception>
        public SampledComponent Sample(int realizationIndex = -1)
        {
            if (_sampledModes == null || _sampledLayout == null)
            {
                throw new InvalidOperationException("SetupSamplers() must be called before sampling.");
            }
            return new SampledComponent(this, _sampledModes, _sampledNonFailureMode, _sampledLayout, realizationIndex);
        }

        /// <summary>
        /// Samples the mean component with its bivariate hazard's conditional discretization
        /// replaced by a diagnostic snapshot at an arbitrary bin count — the
        /// discretization-error diagnostic's entry point. The stored bin count, the canonical
        /// hash, and every seed are untouched; only the returned snapshot differs.
        /// </summary>
        /// <param name="bins">The diagnostic bin count.</param>
        /// <returns>The mean sampled component over the diagnostic conditional grid.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown before <see cref="SetupSamplers(int, int, SamplingScheme)"/> has run, or when
        /// the component's hazard is not a bivariate hazard.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the count is outside the supported range.</exception>
        internal SampledComponent SampleWithConditionalBins(int bins)
        {
            if (_sampledModes == null || _sampledLayout == null)
            {
                throw new InvalidOperationException("SetupSamplers() must be called before sampling.");
            }
            if (HazardFunction is not BivariateHazard bivariate)
            {
                throw new InvalidOperationException("The conditional-bin diagnostic requires a bivariate hazard.");
            }
            return new SampledComponent(this, _sampledModes, _sampledNonFailureMode, _sampledLayout, -1,
                bivariate.SampleBivariateAt(bins));
        }

        /// <summary>
        /// Collects this component's labeled knowledge-input columns for the sensitivity engine:
        /// one column per sampled function dimension plus each mode's consequence
        /// coupling columns, in the exact <see cref="SetupSamplers(int, int, SamplingScheme)"/> walk order with the same
        /// reference-identity dedup — one shared instance is one knowledge quantity, so labels
        /// and columns can never drift from the seeded streams. Deterministic functions
        /// contribute no column, and a coupling column appears only where an uncertain
        /// consequence actually consumes it (the mode's own function at that position, or —
        /// for failure modes — the paired non-failure function).
        /// </summary>
        /// <param name="sink">Receives the labeled columns, appended in walk order.</param>
        /// <param name="sharedEpistemicSeen">
        /// The shared-epistemic-variable dedup set spanning a multi-component collection, or null
        /// to dedup within this component alone. Every binder of one variable reads the same
        /// shared column, so the variable contributes exactly one column — labeled by the
        /// variable, at the first binder encountered.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the sink is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown before <see cref="SetupSamplers(int, int, SamplingScheme)"/> has run.</exception>
        internal void CollectSensitivityInputs(List<SensitivityInput> sink, ISet<string>? sharedEpistemicSeen = null)
        {
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            var modes = _sampledModes
                ?? throw new InvalidOperationException("SetupSamplers() must be called before collecting sensitivity inputs.");
            var nonFailureMode = _sampledNonFailureMode;
            sharedEpistemicSeen ??= new HashSet<string>(StringComparer.Ordinal);

            var seen = new HashSet<IRiskFunction>(ReferenceEqualityComparer.Instance);
            var usedLabels = new Dictionary<string, int>(StringComparer.Ordinal);

            void AddFunctionColumns(IRiskFunction? function, string fallbackRole)
            {
                // Deterministic functions allocate percentile rows their samplers never read —
                // an inert column would only add tornado noise bars.
                if (function == null || function.SamplingDimensions <= 0 || function.IsDeterministic) return;
                if (!seen.Add(function)) return;
                if (function is not RiskFunctionBase readable) return;

                // A bound epistemic composite reads a shared selector column: one variable is
                // one knowledge quantity, so the first binder contributes the column, labeled by
                // the variable rather than any single function.
                string? sharedVariable = function switch
                {
                    CompositeHazard h when h.CompositeCombinationType == CompositeCombinationType.EpistemicMixture
                        && h.EpistemicVariable.Length > 0 => h.EpistemicVariable,
                    CompositeResponse r when r.CompositeCombinationType == CompositeCombinationType.EpistemicMixture
                        && r.EpistemicVariable.Length > 0 => r.EpistemicVariable,
                    CompositeTransform t when t.CompositeFunctionType == CompositeFunctionType.EpistemicMixture
                        && t.EpistemicVariable.Length > 0 => t.EpistemicVariable,
                    _ => null,
                };
                if (sharedVariable != null)
                {
                    if (!sharedEpistemicSeen.Add(sharedVariable)) return;
                    string sharedLabel = DedupeLabel($"Epistemic Variable - {sharedVariable}", usedLabels);
                    sink.Add(new SensitivityInput(sharedLabel, index => readable.SampledPercentile(index, 0), sharedLabel));
                    return;
                }

                string baseName = string.IsNullOrEmpty(function.Name) ? fallbackRole : function.Name;
                string label = DedupeLabel($"{Name} - {baseName}", usedLabels);
                int dimensions = function.SamplingDimensions;
                for (int d = 0; d < dimensions; d++)
                {
                    int dimension = d;
                    string columnLabel = dimensions > 1 ? $"{label} [{dimension + 1}]" : label;
                    sink.Add(new SensitivityInput(columnLabel, index => readable.SampledPercentile(index, dimension), label));
                }
            }

            var hazard = HazardFunction;
            if (hazard is IBivariateHazardFunction bivariateHazard)
            {
                // A bivariate hazard samples through its linked marginals (its own sampling
                // dimension count is zero); the marginals are the knowledge inputs.
                AddFunctionColumns(bivariateHazard.MarginalX, "Marginal X Hazard");
                AddFunctionColumns(bivariateHazard.MarginalY, "Marginal Y Hazard");
            }
            else
            {
                AddFunctionColumns(hazard, "Hazard Function");
            }

            for (int i = 0; i < modes.Count; i++)
            {
                var mode = modes[i];

                // The coupling columns (the shared consequence draws), only where an
                // uncertain consequence consumes them.
                int positions = Math.Max(1, mode.ConsequenceFunctions.Count);
                for (int k = 0; k < positions; k++)
                {
                    var own = k < mode.ConsequenceFunctions.Count ? mode.ConsequenceFunctions[k] : null;
                    var paired = !mode.IsNonFailureMode && nonFailureMode != null && k < nonFailureMode.ConsequenceFunctions.Count
                        ? nonFailureMode.ConsequenceFunctions[k]
                        : null;
                    bool consumed = (own != null && !own.IsDeterministic) || (paired != null && !paired.IsDeterministic);
                    if (!consumed) continue;

                    string typeLabel = own != null && !string.IsNullOrEmpty(own.SpecifiedConsequence)
                        ? own.SpecifiedConsequence
                        : $"type {k + 1}";
                    string modeLabel = ModeLabel(mode, i);
                    string couplingLabel = positions > 1
                        ? $"{Name} - {modeLabel} - Consequence Knowledge [{typeLabel}]"
                        : $"{Name} - {modeLabel} - Consequence Knowledge";
                    couplingLabel = DedupeLabel(couplingLabel, usedLabels);
                    var owner = mode;
                    int position = k;
                    sink.Add(new SensitivityInput(couplingLabel, index => owner.CouplingPercentile(index, position)));
                }

                // The chain functions, in the walk order: stage transforms, stage responses,
                // trailing transforms, then the secondary-hazard chain.
                for (int s = 0; s < mode.ResponseStages.Count; s++)
                {
                    var stage = mode.ResponseStages[s];
                    if (stage is null) continue;
                    for (int t = 0; t < stage.Transforms.Count; t++)
                    {
                        AddFunctionColumns(stage.Transforms[t], "Transform");
                    }
                    AddFunctionColumns(stage.Response, "Response");
                }
                for (int t = 0; t < mode.ResponseToConsequence.Count; t++)
                {
                    AddFunctionColumns(mode.ResponseToConsequence[t], "Transform");
                }
                for (int t = 0; t < mode.SecondaryHazardToResponse.Count; t++)
                {
                    AddFunctionColumns(mode.SecondaryHazardToResponse[t], "Transform");
                }
            }
        }

        /// <summary>
        /// An end state's display label, resolved in a deliberate fixed order: the projected
        /// consequence terminal's element name (unique within a graph), then the primary
        /// consequence function's name, then the first response's name, then a positional
        /// fallback (the projected <c>FailureMode</c> carries no name of its own).
        /// </summary>
        /// <param name="mode">The mode.</param>
        /// <param name="index">The mode's projected position (the fallback ordinal).</param>
        /// <returns>The display label.</returns>
        internal static string ModeLabel(FailureMode mode, int index)
        {
            if (!string.IsNullOrEmpty(mode.ProjectedTerminalName)) return mode.ProjectedTerminalName;
            var primary = mode.ConsequenceFunctions.Count > 0 ? mode.ConsequenceFunctions[0] : null;
            if (primary != null && !string.IsNullOrEmpty(primary.Name)) return primary.Name;
            var response = mode.ResponseStages.Count > 0 ? mode.ResponseStages[0]?.Response : null;
            if (response != null && !string.IsNullOrEmpty(response.Name)) return response.Name;
            return mode.IsNonFailureMode ? "Non-Failure Mode" : $"Failure Mode {index + 1}";
        }

        /// <summary>
        /// An end state's branch path descriptor: each stage's response name with
        /// its branch polarity, joined along the chain — e.g.
        /// <c>"Initiation[Fail] → Progression[NonFail]"</c>; <c>"Non-Failure"</c> for the
        /// background mode.
        /// </summary>
        /// <param name="mode">The mode.</param>
        /// <returns>The path descriptor.</returns>
        internal static string ModePathLabel(FailureMode mode)
        {
            if (mode.IsNonFailureMode) return "Non-Failure";
            var builder = new StringBuilder();
            var stages = mode.ResponseStages;
            for (int s = 0; s < stages.Count; s++)
            {
                if (s > 0) builder.Append(" → ");
                var stage = stages[s];
                string name = stage?.Response != null && !string.IsNullOrEmpty(stage.Response.Name)
                    ? stage.Response.Name
                    : $"Response {s + 1}";
                builder.Append(name).Append('[').Append(stage?.BranchPolarity ?? BranchPolarity.Fail).Append(']');
            }
            return builder.ToString();
        }

        /// <summary>
        /// Makes a display label unique by appending an occurrence suffix on repeats (duplicate
        /// function or mode names are legal — a dictionary key must not throw on them, the
        /// latent v1.0 defect).
        /// </summary>
        /// <param name="label">The candidate label.</param>
        /// <param name="usedLabels">The labels already handed out, with their counts.</param>
        /// <returns>The unique label.</returns>
        private static string DedupeLabel(string label, Dictionary<string, int> usedLabels)
        {
            if (usedLabels.TryGetValue(label, out int count))
            {
                usedLabels[label] = count + 1;
                return $"{label} ({count + 1})";
            }
            usedLabels[label] = 1;
            return label;
        }

        /// <summary>
        /// Creates a deep, isolated copy via the serialization round-trip. An improvement over
        /// v1.0, which shared function references between clones. The occurrence index is
        /// runtime state and is not carried.
        /// </summary>
        /// <returns>The copied component.</returns>
        public SystemComponent Clone()
        {
            return new SystemComponent(ToXElement());
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the component: the option attributes and the owned element graph. This is
        /// the persistence surface; the canonical hash uses the internal identity form instead
        /// (see <see cref="CanonicalHash"/>). The correlation matrix serializes (G17 row-major,
        /// rows ';'-separated, values ','-separated) only under the correlation-matrix dependency
        /// mode — in the automatic and latent-factors modes the matrix is derived state, and
        /// serializing it would make the persisted form depend on whether the multivariate
        /// normal had been built. The latent-factor container likewise serializes only under the
        /// latent-factors mode (a conditional child, so every other component's form is
        /// byte-identical to its pre-latent-factor self).
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            return ToXElement(RiskSerializationMode.SelfContained);
        }

        /// <summary>
        /// Serializes the component in the given mode. The mode governs only how the graph's
        /// wrapped functions are written; the component's own options are unaffected, and so is
        /// <see cref="CanonicalHash"/>, which hashes the projected failure modes rather than the
        /// persisted graph. A component therefore seeds identically however it was persisted.
        /// </summary>
        /// <param name="mode">The serialization mode.</param>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement(RiskSerializationMode mode)
        {
            var element = new XElement(nameof(SystemComponent));
            element.SetAttributeValue(nameof(Name), _name);
            element.SetAttributeValue(nameof(FailureModeMethod), _failureModeMethod.ToString());
            element.SetAttributeValue(nameof(JointConsequences), _jointConsequences.ToString());
            element.SetAttributeValue(nameof(FailureModeDependency), _failureModeDependency.ToString());
            element.SetAttributeValue(nameof(HazardThreshold), SerializationUtilities.FormatDouble(_hazardThreshold));
            element.SetAttributeValue(nameof(CorrelationMatrix),
                _failureModeDependency == DependencyType.CorrelationMatrix ? SerializationUtilities.FormatMatrix(_correlationMatrix) : string.Empty);
            // Append-only attribute: persistence only — deliberately absent from the
            // identity form, so the profile selection can never re-roll seeds.
            element.SetAttributeValue(nameof(ProfileHazardElementId),
                _profileHazardElementId.HasValue ? _profileHazardElementId.Value.ToString("D") : string.Empty);
            element.Add(_graph.ToXElement(mode));
            // Conditional presence: the factor container exists only under the latent-factors
            // dependency mode with factors configured, so every other component keeps its
            // byte-identical serialized form (a new always-present attribute or child would
            // move every existing component's canonical bytes).
            if (_failureModeDependency == DependencyType.LatentFactors && _latentFactors.Count > 0)
            {
                var factors = new XElement(nameof(LatentFactors));
                for (int i = 0; i < _latentFactors.Count; i++)
                {
                    factors.Add(_latentFactors[i].ToXElement());
                }
                element.Add(factors);
            }
            return element;
        }

        /// <summary>
        /// Enumerates the distinct input functions this component's graph wraps, in declared
        /// element order. The dependency set a consuming layer persists alongside a by-reference
        /// component, and the answer to "what uses this function?" before one is deleted.
        /// </summary>
        /// <returns>The referenced functions, each appearing once.</returns>
        public IEnumerable<IRiskFunction> GetReferencedFunctions()
        {
            return _graph.GetReferencedFunctions();
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// The graph's hazard root element, or null while the graph has none.
        /// </summary>
        private HazardElement? RootHazardElement
        {
            get
            {
                foreach (var hazard in _graph.GetElements<HazardElement>())
                {
                    return hazard;
                }
                return null;
            }
        }

        /// <summary>
        /// Resolves the selected profile hazard element into the ordered transform chain that
        /// maps the driving hazard onto the profile axis. Null when nothing is selected or the
        /// selection does not resolve (validation reports why; the engine's validation gate
        /// keeps unresolved selections off the run path). Response elements along the path pass
        /// the hazard signal through unchanged — they consume it to produce a probability — so
        /// only the transform functions compose.
        /// </summary>
        /// <returns>The root-first transform chain, or null.</returns>
        private ITransformFunction[]? ResolveProfileTransforms()
        {
            if (!_profileHazardElementId.HasValue) return null;
            if (_graph.GetElementById(_profileHazardElementId.Value) is not TransformElement profileTransform) return null;

            var path = _graph.GetUpstreamPath(profileTransform);
            if (path.Count == 0 || path[0] is not HazardElement) return null;

            var chain = new List<ITransformFunction>(path.Count - 1);
            for (int i = 1; i < path.Count; i++)
            {
                if (path[i] is TransformElement transform)
                {
                    if (transform.Function == null) return null;
                    chain.Add(transform.Function);
                }
            }
            return chain.Count > 0 ? chain.ToArray() : null;
        }

        /// <summary>
        /// Builds the canonical identity form realizing the §5.5.3 recipe: the option attributes,
        /// the hazard content inline, and the projected failure modes in path order — each
        /// annotated with its <c>ResponseNodes</c> occurrence-ordinal sequence, so
        /// two terminals sharing one response element and two terminals on equal-content duplicate
        /// elements hash differently (shared elements share draws and form exclusive state groups;
        /// duplicates draw independently and combine as separate events). The annotation exists in
        /// the identity form only — <see cref="FailureMode.ToXElement"/> persistence never carries
        /// it. Identity metadata inside (names, labels) is stripped by the hasher; element ids,
        /// positions, and link attributes never appear at all. The profile hazard selection
        /// (<see cref="ProfileHazardElementId"/>) is deliberately excluded — a reporting-axis
        /// binding must never re-roll seeds.
        /// </summary>
        /// <returns>The identity form.</returns>
        private XElement BuildIdentityXElement()
        {
            var element = new XElement(nameof(SystemComponent));
            element.SetAttributeValue(nameof(FailureModeMethod), _failureModeMethod.ToString());
            element.SetAttributeValue(nameof(JointConsequences), _jointConsequences.ToString());
            element.SetAttributeValue(nameof(FailureModeDependency), _failureModeDependency.ToString());
            element.SetAttributeValue(nameof(HazardThreshold), SerializationUtilities.FormatDouble(_hazardThreshold));
            element.SetAttributeValue(nameof(CorrelationMatrix),
                _failureModeDependency == DependencyType.CorrelationMatrix ? SerializationUtilities.FormatMatrix(_correlationMatrix) : string.Empty);

            var hazard = HazardFunction;
            if (hazard != null)
            {
                element.Add(new XElement(nameof(HazardFunction), hazard.ToXElement()));
            }

            var modes = new XElement(nameof(FailureModes));
            var projected = ProjectFailureModes();
            for (int i = 0; i < projected.Count; i++)
            {
                var modeXml = projected[i].ToIdentityXElement();
                var ordinals = projected[i].ProjectedResponseOrdinals;
                modeXml.SetAttributeValue("ResponseNodes",
                    ordinals == null ? string.Empty : string.Join(",", ordinals));
                modes.Add(modeXml);
            }
            element.Add(modes);

            // Conditional presence mirrors the persisted form: the loadings are compute content
            // only under the latent-factors mode, and every other component's identity — and
            // therefore its seeds — is byte-identical to its pre-latent-factor self. Factor
            // names and descriptions inside are stripped by the hasher; declared factor order
            // is semantic.
            if (_failureModeDependency == DependencyType.LatentFactors && _latentFactors.Count > 0)
            {
                var factors = new XElement(nameof(LatentFactors));
                for (int i = 0; i < _latentFactors.Count; i++)
                {
                    factors.Add(_latentFactors[i].ToXElement());
                }
                element.Add(factors);
            }
            return element;
        }

        /// <summary>
        /// Projects the failure modes from the graph topology: one per consequence element in
        /// declared order, walking each terminal's root-first path and classifying transforms
        /// into stages around the responses (a response-free path becomes the canonical
        /// non-failure stage form). Structurally unsound graphs project empty.
        /// </summary>
        /// <returns>The projected modes, parent-wired.</returns>
        private List<FailureMode> ProjectFailureModes()
        {
            var modes = new List<FailureMode>();
            var root = RootHazardElement;
            if (root == null) return modes;
            int hazardCount = 0;
            foreach (var _ in _graph.GetElements<HazardElement>()) hazardCount++;
            if (hazardCount != 1) return modes;
            if (!_graph.TopologicalSort()) return modes;

            // Response-element occurrence ordinals in first-appearance order over the projected
            // mode list (reference identity): sibling end states sharing a response element share
            // its ordinal, equal-content duplicates get distinct ordinals. Feeds the end-state
            // group layout and the identity form's topology encoding.
            var responseOrdinals = new Dictionary<ResponseElement, int>();
            foreach (var terminal in _graph.GetElements<ConsequenceElement>())
            {
                var path = _graph.GetUpstreamPath(terminal);
                if (path.Count == 0 || !ReferenceEquals(path[0], root)) continue;
                modes.Add(BuildFailureMode(terminal, path, responseOrdinals));
            }
            return modes;
        }

        /// <summary>
        /// Builds one failure mode from a terminal's root-first path: transforms accumulate into
        /// the pending chain, each response element closes a stage whose branch polarity is read
        /// from the exit port the path uses (port 0 = Fail, port 1 = Non-Fail), transforms after
        /// the last response become the trailing chain, and the terminal's functions become the
        /// ordered consequence list. The mode's hazard binding is stamped from the root exit
        /// port (a path leaving the hazard's secondary output, port 1, is Secondary-bound), and
        /// the path's resolved secondary chain — the off-path univariate transforms feeding the
        /// first bivariate element's secondary input — projects into
        /// <see cref="FailureMode.SecondaryHazardToResponse"/> (unresolvable chains project
        /// empty; graph validation reports why). The terminal's hazard-source binding projects
        /// to a chain position and dimension: a primary-path target keeps the established
        /// position semantics, the hazard's port 1 is the raw secondary signal at position 0,
        /// and a secondary-chain transform is Secondary position k + 1. The
        /// multiple-consequences flag derives from the last response's fan-out on the mode's own
        /// exit port.
        /// </summary>
        /// <param name="terminal">The path's consequence element.</param>
        /// <param name="path">The root-first path ending with the terminal.</param>
        /// <param name="responseOrdinals">
        /// The shared response-element occurrence ordinal assignment, extended on first
        /// appearance.
        /// </param>
        /// <returns>The projected failure mode, parent-wired.</returns>
        private FailureMode BuildFailureMode(ConsequenceElement terminal, IReadOnlyList<IRiskElement> path,
            Dictionary<ResponseElement, int> responseOrdinals)
        {
            var stages = new List<ResponseStage>();
            var stageOrdinals = new List<int>();
            var pending = new List<ITransformFunction>();
            ResponseElement? lastResponseElement = null;
            RiskConnection? lastExitConnection = null;
            for (int i = 1; i < path.Count - 1; i++)
            {
                if (path[i] is TransformElement transformElement)
                {
                    pending.Add(transformElement.Function!);
                }
                else if (path[i] is ResponseElement responseElement)
                {
                    RiskConnection? exitConnection = ExitConnection(path[i + 1], responseElement);
                    int exitPort = exitConnection?.SourcePort ?? 0;
                    var polarity = exitPort == (int)BranchPolarity.NonFail ? BranchPolarity.NonFail : BranchPolarity.Fail;
                    if (exitConnection?.SourceBranchId is Guid branchId)
                    {
                        ResponseBranchDescriptor branch = responseElement.RequireAvailableBranch(branchId);
                        stages.Add(new ResponseStage(pending, responseElement.Function!, branch));
                    }
                    else
                    {
                        stages.Add(new ResponseStage(pending, responseElement.Function, polarity));
                    }
                    if (!responseOrdinals.TryGetValue(responseElement, out int ordinal))
                    {
                        ordinal = responseOrdinals.Count;
                        responseOrdinals.Add(responseElement, ordinal);
                    }
                    stageOrdinals.Add(ordinal);
                    pending = new List<ITransformFunction>();
                    lastResponseElement = responseElement;
                    lastExitConnection = exitConnection;
                }
            }

            List<ResponseStage> finalStages;
            List<ITransformFunction> trailing;
            if (lastResponseElement == null)
            {
                // The non-failure path: all transforms in the canonical stage form, so any
                // upstream signal position stays addressable by the consequence binding.
                finalStages = new List<ResponseStage> { new ResponseStage(pending, new NonFailResponse()) };
                trailing = new List<ITransformFunction>();
            }
            else
            {
                finalStages = stages;
                trailing = pending;
            }

            var mode = new FailureMode(finalStages, trailing, new List<IConsequenceFunction>(terminal.Functions))
            {
                Parent = this,
                ProjectedResponseOrdinals = lastResponseElement != null ? stageOrdinals.ToArray() : null,
                ProjectedTerminalName = terminal.Name,
                HazardBinding = ExitConnection(path[1], path[0])?.SourcePort == 1
                    ? HazardDimension.Secondary
                    : HazardDimension.Primary,
            };

            // The path's secondary chain: the univariate transforms feeding the first bivariate
            // element's secondary input, upstream → downstream. Unresolvable (or absent) chains
            // project empty — projection never throws; graph validation reports why.
            List<TransformElement>? secondaryChain = null;
            RiskConnection? anchor = ComponentGraph.FindSecondaryAnchor(path);
            if (anchor != null)
            {
                var chainElements = new List<TransformElement>();
                if (_graph.TryResolveSecondaryChain(anchor, chainElements, out _))
                {
                    secondaryChain = chainElements;
                    var chainFunctions = new List<ITransformFunction>(chainElements.Count);
                    for (int i = 0; i < chainElements.Count; i++)
                    {
                        if (chainElements[i].Function != null) chainFunctions.Add(chainElements[i].Function!);
                    }
                    mode.SecondaryHazardToResponse = chainFunctions;
                }
            }

            if (terminal.HazardSource != null)
            {
                var target = terminal.HazardSource.Source;
                int cursor = 0;
                int position = -1;
                for (int i = 0; i < path.Count - 1; i++)
                {
                    if (path[i] is HazardElement && ReferenceEquals(path[i], target))
                    {
                        position = 0;
                        break;
                    }
                    if (path[i] is TransformElement)
                    {
                        cursor++;
                        if (ReferenceEquals(path[i], target))
                        {
                            position = cursor;
                            break;
                        }
                    }
                }
                if (position >= 0)
                {
                    mode.ConsequenceHazardPosition = position;
                    mode.ConsequenceHazardDimension = terminal.HazardSource.SourcePort == 1
                        ? HazardDimension.Secondary
                        : HazardDimension.Primary;
                }
                else if (secondaryChain != null)
                {
                    // A secondary-chain transform: the consequences consume the secondary
                    // signal after the k-th chain transform (position 0 is the raw secondary
                    // signal, stamped by the hazard port-1 match above).
                    for (int k = 0; k < secondaryChain.Count; k++)
                    {
                        if (ReferenceEquals(secondaryChain[k], target))
                        {
                            mode.ConsequenceHazardPosition = k + 1;
                            mode.ConsequenceHazardDimension = HazardDimension.Secondary;
                            break;
                        }
                    }
                }
                // Other off-path bindings leave the default; graph validation reports them.
            }

            if (lastResponseElement != null)
            {
                // Same-port fan-out only: a Fail terminal and a Non-Fail
                // continuation are distinct end states, not "multiple consequences" of one
                // branch. Graphs authored before the cascade design wire port 0 exclusively,
                // so the derived value is unchanged for every legacy shape.
                int fanOut = 0;
                foreach (var consumer in _graph.GetDownstreamElements(lastResponseElement))
                {
                    RiskConnection? connection = ExitConnection(consumer, lastResponseElement);
                    if (connection != null && lastExitConnection != null
                        && connection.SourcePort == lastExitConnection.SourcePort
                        && connection.SourceBranchId == lastExitConnection.SourceBranchId)
                        fanOut++;
                }
                mode.MultipleConsequences = fanOut >= 2;
            }
            return mode;
        }

        /// <summary>Reads the first structural connection a consumer takes from a source element.</summary>
        /// <param name="consumer">The downstream element.</param>
        /// <param name="source">The upstream element.</param>
        /// <returns>The matching connection, or null.</returns>
        private static RiskConnection? ExitConnection(IRiskElement consumer, IRiskElement source)
        {
            foreach (RiskConnection connection in consumer.GetInputConnections())
            {
                if (ReferenceEquals(connection.Source, source)) return connection;
            }
            return null;
        }


        /// <summary>
        /// Reads the output port a consumer's structural connection takes from a source element:
        /// the first input connection referencing the source (the same first-connection rule the
        /// upstream path walk uses), defaulting to port 0 when none is found.
        /// </summary>
        /// <param name="consumer">The downstream element.</param>
        /// <param name="source">The upstream element whose exit port is wanted.</param>
        /// <returns>The connection's source port, or 0.</returns>
        private static int ExitPort(IRiskElement consumer, IRiskElement source)
        {
            return ExitConnection(consumer, source)?.SourcePort ?? 0;
        }

        /// <summary>
        /// The failure-mode combination dimension: the number of combination units in the
        /// current end-state layout — exclusive state groups plus standalone
        /// failure states, driving the combination caches, the multivariate normal, and the
        /// correlation-matrix dimension. Equals the v1.0 failure-path count when the layout is
        /// trivial. Built fresh from the projection (the engine's per-realization reads hit the
        /// count-keyed caches above, so the rebuild cost is a per-realization structural walk at
        /// component scale — the projection's documented cost profile).
        /// </summary>
        /// <returns>The combination-unit count.</returns>
        private int CombinationUnitCount()
        {
            return EndStateGroupLayout.Build(ProjectFailureModes()).CombinationUnitCount;
        }

        /// <summary>
        /// The combination-unit count for resource estimation, taken from the frozen layout when
        /// the component has been set up and derived from the projection otherwise.
        /// </summary>
        /// <returns>The combination-unit count.</returns>
        internal int CombinationUnitCountForEstimate()
        {
            return _sampledLayout != null ? _sampledLayout.CombinationUnitCount : CombinationUnitCount();
        }

        /// <summary>
        /// Rebuilds the multivariate normal for the failure-mode dependence — the exact v1.0
        /// construction: zero means; unit diagonal; off-diagonals per the dependency option
        /// (identity, <c>1 − √εmach</c>, <c>−1/(D − 1) + √εmach</c>, the user matrix, or the
        /// latent-factor-induced matrix). In the automatic and latent-factors modes the derived
        /// matrix is written back to the correlation-matrix field (v1.0 behavior); it never
        /// serializes from those modes.
        /// </summary>
        /// <param name="dimension">The combination-unit count D (the failure-path count under a trivial layout).</param>
        private void UpdateMultivariateNormal(int dimension)
        {
            _mvnForCount = dimension;
            _mvnStale = false;
            _mvn = null;
            if (dimension <= 0)
            {
                _matrixValid = true;
                return;
            }

            var mu = new double[dimension];
            var sigma = new double[dimension, dimension];
            if (_failureModeDependency == DependencyType.CorrelationMatrix)
            {
                if (_correlationMatrix == null || _correlationMatrix.GetLength(0) != dimension || _correlationMatrix.GetLength(1) != dimension)
                {
                    _matrixValid = false;
                    return;
                }
                for (int i = 0; i < dimension; i++)
                {
                    for (int j = 0; j < dimension; j++)
                    {
                        sigma[i, j] = _correlationMatrix[i, j];
                    }
                }
            }
            else if (_failureModeDependency == DependencyType.LatentFactors)
            {
                // The induced matrix is derived state exactly like the automatic modes'
                // (back-filled, never serialized from this mode); a structural configuration
                // error means no trustworthy matrix exists at this dimension.
                if (CollectLatentFactorConfigurationErrors(dimension).Count > 0)
                {
                    _matrixValid = false;
                    return;
                }
                FillLatentFactorMatrix(sigma, dimension);
                _correlationMatrix = sigma;
            }
            else
            {
                // The automatic modes write their derived matrix back to the correlation-matrix
                // field (v1.0 behavior; it never serializes from those modes).
                DependencyMatrix.FillEquicorrelated(sigma, dimension,
                    DependencyMatrix.AutomaticOffDiagonal(_failureModeDependency, dimension));
                _correlationMatrix = sigma;
            }

            ValidateCorrelationMatrix(dimension);
            if (!_matrixValid) return;

            _mvn = new MultivariateNormal(mu, sigma);
        }

        /// <summary>
        /// Validates the dependence matrix for the current dependency mode: under the
        /// correlation-matrix mode the stored user matrix must match the failure-path count and
        /// be positive definite (Cholesky); under the latent-factors mode the loadings must be
        /// structurally valid and the induced matrix (derived into a scratch copy — nothing
        /// stored is touched) must pass the same positive-definiteness gate. The automatic modes
        /// always pass (their matrices are constructed valid).
        /// </summary>
        /// <param name="dimension">The failure-path count D.</param>
        private void ValidateCorrelationMatrix(int dimension)
        {
            _matrixValid = true;
            if (_failureModeDependency == DependencyType.LatentFactors)
            {
                if (dimension <= 0) return;
                if (CollectLatentFactorConfigurationErrors(dimension).Count > 0)
                {
                    _matrixValid = false;
                    return;
                }
                var induced = new double[dimension, dimension];
                FillLatentFactorMatrix(induced, dimension);
                _matrixValid = IsPositiveDefinite(induced);
                return;
            }
            if (_failureModeDependency != DependencyType.CorrelationMatrix) return;

            if (_correlationMatrix == null || _correlationMatrix.GetLength(0) != dimension || _correlationMatrix.GetLength(1) != dimension)
            {
                _matrixValid = false;
                return;
            }
            _matrixValid = IsPositiveDefinite(_correlationMatrix);
        }

        /// <summary>
        /// Determines positive definiteness via Cholesky decomposition, treating a failed
        /// decomposition as not positive definite — the exact v1.0 treatment of the numerical
        /// failure path.
        /// </summary>
        /// <param name="matrix">The square matrix.</param>
        /// <returns>True when the decomposition succeeds and reports positive definiteness.</returns>
        private static bool IsPositiveDefinite(double[,] matrix)
        {
            try
            {
                var cholesky = new CholeskyDecomposition(new Matrix(matrix));
                return cholesky.IsPositiveDefinite;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Collects the structural configuration errors of the latent-factor loadings at the
        /// given combination dimension: at least one factor; every factor carrying one finite
        /// loading in [−1, 1] per combination unit; and every unit's squared-loading sum across
        /// factors at most one (the idiosyncratic-variance bound — at exactly one the unit has no
        /// idiosyncratic variance and the induced matrix may fail the positive-definiteness gate).
        /// </summary>
        /// <param name="dimension">The combination-unit count D.</param>
        /// <returns>The error messages; empty when the configuration is structurally valid.</returns>
        private List<string> CollectLatentFactorConfigurationErrors(int dimension)
        {
            var messages = new List<string>();
            if (_latentFactors.Count == 0)
            {
                messages.Add($"Error: The latent-factors dependency mode of system component '{Name}' requires at least one latent factor.");
                return messages;
            }

            for (int f = 0; f < _latentFactors.Count; f++)
            {
                var factor = _latentFactors[f];
                string label = string.IsNullOrEmpty(factor.Name) ? $"#{f + 1}" : $"'{factor.Name}'";
                var loadings = factor.Loadings;
                if (loadings.Count != dimension)
                {
                    messages.Add($"Error: Latent factor {label} of system component '{Name}' has {loadings.Count} loadings but the combination dimension (the state-group count) is {dimension}.");
                    continue;
                }
                for (int i = 0; i < loadings.Count; i++)
                {
                    double loading = loadings[i];
                    if (!Tools.IsFinite(loading) || loading < -1d || loading > 1d)
                    {
                        messages.Add($"Error: Latent factor {label} of system component '{Name}' has loading {SerializationUtilities.FormatDouble(loading)} at position {i}; loadings must be finite values within [-1, 1].");
                    }
                }
            }
            if (messages.Count > 0) return messages;

            for (int i = 0; i < dimension; i++)
            {
                double squaredSum = 0d;
                for (int f = 0; f < _latentFactors.Count; f++)
                {
                    double loading = _latentFactors[f].Loadings[i];
                    squaredSum += loading * loading;
                }
                if (squaredSum > 1d)
                {
                    messages.Add($"Error: The latent-factor loadings of system component '{Name}' give combination unit {i} a squared-loading sum of {SerializationUtilities.FormatDouble(squaredSum)}, exceeding 1 — the unit's idiosyncratic variance would be negative.");
                }
            }
            return messages;
        }

        /// <summary>
        /// Fills the latent-factor-induced correlation matrix: a unit diagonal assigned exactly,
        /// and off-diagonals ρij = Σf λif·λjf accumulated in declared factor order — the
        /// summation-order contract the dense-equivalence verification pins bit-exactly. Assumes
        /// the structural configuration checks have passed.
        /// </summary>
        /// <param name="target">The matrix to fill, at least <paramref name="dimension"/> square.</param>
        /// <param name="dimension">The combination-unit count D.</param>
        private void FillLatentFactorMatrix(double[,] target, int dimension)
        {
            for (int i = 0; i < dimension; i++)
            {
                target[i, i] = 1d;
                for (int j = i + 1; j < dimension; j++)
                {
                    double correlation = 0d;
                    for (int f = 0; f < _latentFactors.Count; f++)
                    {
                        var loadings = _latentFactors[f].Loadings;
                        correlation += loadings[i] * loadings[j];
                    }
                    target[i, j] = correlation;
                    target[j, i] = correlation;
                }
            }
        }

        /// <summary>
        /// Adds a transform element wrapping a function, wired from the current upstream element.
        /// </summary>
        /// <param name="function">The transform function to wrap; null entries are carried (validation reports them).</param>
        /// <param name="upstream">The element the new transform consumes.</param>
        /// <param name="stageRegistry">When non-null, collects the created element for binding-position mapping (stage transforms only).</param>
        /// <param name="upstreamPort">The upstream output port to consume — a response's branch port when the upstream element closed a stage; 0 otherwise.</param>
        /// <param name="upstreamBranchId">The stable expanded branch id, or null.</param>
        /// <param name="upstreamBranchName">The expanded branch-name fallback.</param>
        /// <returns>The created element (the new upstream).</returns>
        private TransformElement AddTransformElement(ITransformFunction? function,
            IRiskElement upstream, List<TransformElement>? stageRegistry, int upstreamPort = 0,
            Guid? upstreamBranchId = null, string? upstreamBranchName = null)
        {
            var element = new TransformElement(_graph.GetUniqueName(ElementName(function?.Name, "Transform")))
            {
                Function = function,
                Input = new RiskConnection(upstream, upstreamPort,
                    upstreamBranchId, upstreamBranchName),
            };
            _graph.AddElement(element);
            stageRegistry?.Add(element);
            return element;
        }

        /// <summary>
        /// Chooses an element display name: the wrapped function's name when present, otherwise
        /// the role fallback.
        /// </summary>
        /// <param name="functionName">The wrapped function's name; may be null or empty.</param>
        /// <param name="fallback">The role fallback ("Transform", "Response", "Consequence").</param>
        /// <returns>The chosen base name (uniqueness is applied by the graph).</returns>
        private static string ElementName(string? functionName, string fallback)
        {
            return string.IsNullOrEmpty(functionName) ? fallback : functionName!;
        }

        /// <summary>
        /// Subscribes to the owned graph's change notification so projection-dependent surfaces
        /// re-raise (the v1.0 collection-changed relay, re-homed onto the graph).
        /// </summary>
        private void SubscribeGraph()
        {
            _graph.PropertyChanged += GraphPropertyChanged;
        }

        /// <summary>
        /// Relays graph membership changes as a failure-mode change (the projection changed).
        /// </summary>
        /// <param name="sender">The owned graph.</param>
        /// <param name="e">The change description.</param>
        private void GraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, nameof(ComponentGraph.Elements), StringComparison.Ordinal))
            {
                RaisePropertyChange(nameof(FailureModes));
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
