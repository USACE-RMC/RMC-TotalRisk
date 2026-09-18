using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Linq;
using Numerics.Sampling;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Results.Risk;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Responses;

namespace RMC.TotalRisk.Systems.Components
{
    /// <summary>
    /// A potential failure mode: one path from a system component's hazard to consequences —
    /// response stages in sequence (each a transform chain plus a response), trailing transforms,
    /// and the ordered consequence functions evaluated at the bound hazard position.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Ported from v1.0 <c>FailureMode</c> with the domain surface preserved and four deliberate
    /// v1.1 generalizations: (1) <b>response chains</b> — v1.0 fixed one
    /// response between two transform lists; v1.1 holds ordered <see cref="ResponseStages"/>
    /// (grammar <c>T* (R T*)* C</c>), and the v1.0 members <see cref="HazardToResponse"/> /
    /// <see cref="ResponseFunction"/> survive as views over stage 0; (2) <b>multiple
    /// consequences</b> — <see cref="ConsequenceFunctions"/> is an ordered list (index 0 is the
    /// primary used for risk integration; all are computed and tracked), with
    /// <see cref="ConsequenceFunction"/> as the v1.0 single-slot view; (3) <b>structural hazard
    /// binding</b> — <see cref="ConsequenceHazardPosition"/> lets the consequences consume the
    /// hazard signal at any chain position (0 = the raw hazard) instead of only the last
    /// response's input, with trailing <see cref="ResponseToConsequence"/> transforms folding
    /// from the bound position; (4) <b>the secondary hazard dimension</b> — under a bivariate
    /// component hazard the mode can originate at the secondary signal
    /// (<see cref="HazardBinding"/>), route its consequences to it
    /// (<see cref="ConsequenceHazardDimension"/>), and carry the path's secondary-chain
    /// transforms (<see cref="SecondaryHazardToResponse"/>, serialized only when non-empty so
    /// pre-bivariate modes' forms, hashes, and seeds are untouched).
    /// </para>
    /// <para>
    /// Serialization is self-contained: functions are owned inline children (v1.0's name-based
    /// project-collection resolution is not ported), so <see cref="ToXElement"/> doubles as the
    /// canonical-hash identity surface. Hazard-type label mismatches along the chain are
    /// advisory <c>Warning</c>s in v1.1 (v1.0 made them invalidating): labels are unhashed
    /// display metadata and can never gate or rewire compute. v1.0's function-change relay
    /// (re-raising <c>PropertyChanged</c> from owned functions) is UI dirty-tracking and is not
    /// ported; the UI layer re-adds it if needed.
    /// </para>
    /// </remarks>
    public class FailureMode : INotifyPropertyChanged
    {
        #region Construction

        /// <summary>
        /// Initializes a failure mode with the v1.0 defaults: one empty stage carrying the
        /// non-failure response sentinel, no trailing transforms, and no consequence functions.
        /// </summary>
        public FailureMode()
        {
            _responseStages.Add(new ResponseStage());
        }

        /// <summary>
        /// Initializes a failure mode from the v1.0 chain shape: one response bracketed by two
        /// transform lists, with a single consequence function.
        /// </summary>
        /// <param name="hazardToResponse">The transforms from the hazard to the response; null coerces to empty. Copied into stage 0.</param>
        /// <param name="responseToConsequence">The trailing transforms from the response to the consequences; null coerces to empty. Copied.</param>
        /// <param name="responseFunction">The response function; null coerces to a fresh <see cref="NonFailResponse"/> (v1.0 defaulting).</param>
        /// <param name="consequenceFunction">The consequence function; null leaves the consequence list empty.</param>
        public FailureMode(List<ITransformFunction>? hazardToResponse, List<ITransformFunction>? responseToConsequence,
            IResponseFunction? responseFunction, IConsequenceFunction? consequenceFunction)
        {
            _responseStages.Add(new ResponseStage(hazardToResponse ?? new List<ITransformFunction>(), responseFunction));
            if (responseToConsequence != null) _responseToConsequence.AddRange(responseToConsequence);
            if (consequenceFunction != null) _consequenceFunctions.Add(consequenceFunction);
        }

        /// <summary>
        /// Initializes a failure mode from the generalized staged shape.
        /// </summary>
        /// <param name="responseStages">The ordered response stages; an empty list coerces to one default stage. Copied.</param>
        /// <param name="responseToConsequence">The trailing transforms after the last response; null coerces to empty. Copied.</param>
        /// <param name="consequenceFunctions">The ordered consequence functions (index 0 is primary); null coerces to empty. Copied.</param>
        /// <exception cref="ArgumentNullException">Thrown when the stage list is null.</exception>
        public FailureMode(IList<ResponseStage> responseStages, IList<ITransformFunction>? responseToConsequence,
            IList<IConsequenceFunction>? consequenceFunctions)
        {
            if (responseStages == null) throw new ArgumentNullException(nameof(responseStages));

            _responseStages.AddRange(responseStages);
            if (_responseStages.Count == 0) _responseStages.Add(new ResponseStage());
            if (responseToConsequence != null) _responseToConsequence.AddRange(responseToConsequence);
            if (consequenceFunctions != null) _consequenceFunctions.AddRange(consequenceFunctions);
        }

        /// <summary>
        /// Restores a failure mode from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a serialized function child cannot be reconstructed — a compute chain must
        /// reconstruct faithfully, because silently dropping a function would change results.
        /// </exception>
        public FailureMode(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            _hazardBinding = SerializationUtilities.ReadEnum(xElement, nameof(HazardBinding), HazardDimension.Primary);
            _consequenceHazardDimension = SerializationUtilities.ReadEnum(xElement, nameof(ConsequenceHazardDimension), HazardDimension.Primary);
            _consequenceHazardPosition = xElement.Attribute(nameof(ConsequenceHazardPosition)) != null
                ? SerializationUtilities.ReadInt32(xElement, nameof(ConsequenceHazardPosition))
                : null;
            _multipleConsequences = SerializationUtilities.ReadBoolean(xElement, nameof(MultipleConsequences));

            var stagesElement = xElement.Element(nameof(ResponseStages));
            if (stagesElement != null)
            {
                foreach (var child in stagesElement.Elements(nameof(ResponseStage)))
                {
                    _responseStages.Add(new ResponseStage(child));
                }
            }
            if (_responseStages.Count == 0) _responseStages.Add(new ResponseStage());

            var trailingElement = xElement.Element(nameof(ResponseToConsequence));
            if (trailingElement != null)
            {
                foreach (var child in trailingElement.Elements())
                {
                    var transform = RiskFunctionFactory.CreateTransformFunction(child);
                    if (transform == null)
                    {
                        throw new InvalidOperationException(
                            $"Unrecognized transform function element '{child.Name.LocalName}' in a serialized failure mode. " +
                            "The failure mode cannot be reconstructed faithfully; the serialized form may come from a newer version.");
                    }
                    _responseToConsequence.Add(transform);
                }
            }

            var consequencesElement = xElement.Element(nameof(ConsequenceFunctions));
            if (consequencesElement != null)
            {
                foreach (var child in consequencesElement.Elements())
                {
                    var consequence = RiskFunctionFactory.CreateConsequenceFunction(child);
                    if (consequence == null)
                    {
                        throw new InvalidOperationException(
                            $"Unrecognized consequence function element '{child.Name.LocalName}' in a serialized failure mode. " +
                            "The failure mode cannot be reconstructed faithfully; the serialized form may come from a newer version.");
                    }
                    _consequenceFunctions.Add(consequence);
                }
            }

            // Written only when non-empty; a missing child loads as the empty chain, so every
            // pre-bivariate payload reads forward unchanged.
            var secondaryChainElement = xElement.Element(nameof(SecondaryHazardToResponse));
            if (secondaryChainElement != null)
            {
                foreach (var child in secondaryChainElement.Elements())
                {
                    var transform = RiskFunctionFactory.CreateTransformFunction(child);
                    if (transform == null)
                    {
                        throw new InvalidOperationException(
                            $"Unrecognized transform function element '{child.Name.LocalName}' in a serialized failure mode's secondary-hazard chain. " +
                            "The failure mode cannot be reconstructed faithfully; the serialized form may come from a newer version.");
                    }
                    _secondaryHazardToResponse.Add(transform);
                }
            }
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="Parent"/>.
        /// </summary>
        private SystemComponent? _parent;

        /// <summary>
        /// Backing list for <see cref="ResponseStages"/> — constructors guarantee at least one
        /// stage.
        /// </summary>
        private readonly List<ResponseStage> _responseStages = new List<ResponseStage>();

        /// <summary>
        /// Backing field for <see cref="ResponseToConsequence"/>.
        /// </summary>
        private List<ITransformFunction> _responseToConsequence = new List<ITransformFunction>();

        /// <summary>
        /// Backing field for <see cref="SecondaryHazardToResponse"/>.
        /// </summary>
        private List<ITransformFunction> _secondaryHazardToResponse = new List<ITransformFunction>();

        /// <summary>
        /// Backing field for <see cref="ConsequenceFunctions"/>.
        /// </summary>
        private List<IConsequenceFunction> _consequenceFunctions = new List<IConsequenceFunction>();

        /// <summary>
        /// Backing field for <see cref="HazardBinding"/>.
        /// </summary>
        private HazardDimension _hazardBinding = HazardDimension.Primary;

        /// <summary>
        /// Backing field for <see cref="ConsequenceHazardDimension"/>.
        /// </summary>
        private HazardDimension _consequenceHazardDimension = HazardDimension.Primary;

        /// <summary>
        /// Backing field for <see cref="ConsequenceHazardPosition"/> — null means the v1.0
        /// default (the last response's input).
        /// </summary>
        private int? _consequenceHazardPosition;

        /// <summary>
        /// Backing field for <see cref="MultipleConsequences"/>.
        /// </summary>
        private bool _multipleConsequences;

        /// <summary>
        /// The N×K consequence coupling matrix (K = one column per consequence position, at least
        /// one): the shared knowledge percentiles that drive each realization's paired
        /// failure/non-failure consequence samples co-monotonically (the v1.0
        /// engine drew one uniform for both sides of the pair). Runtime sampler state: allocated
        /// by <see cref="SetupSamplers"/>, never serialized, never hashed, never cloned.
        /// </summary>
        private double[,]? _couplingPercentiles;

        /// <summary>
        /// The owning system component — wired by the component's projection (v1.0 collection
        /// semantics) so label-continuity validation can compare the chain against the component
        /// hazard's labels. Not serialized; not copied by <see cref="Clone"/>.
        /// </summary>
        public SystemComponent? Parent
        {
            get { return _parent; }
            set { _parent = value; }
        }

        /// <summary>
        /// The response-element occurrence ordinals along the projected path, parallel to
        /// <see cref="ResponseStages"/> — stamped by the component projection in first-appearance
        /// order over the projected mode list, so sibling end states sharing a response element
        /// share its ordinal while equal-content duplicates get distinct ordinals. Consumed by
        /// the end-state group layout and the component identity form's topology encoding (arch
        /// doc §7.9); null for chain-authored modes and the projected non-failure path (both
        /// behave as singleton groups). Runtime projection state (the <see cref="Parent"/>
        /// precedent): never serialized, never hashed at mode level, never copied by
        /// <see cref="Clone"/>.
        /// </summary>
        public int[]? ProjectedResponseOrdinals { get; set; }

        /// <summary>
        /// The consequence terminal's element name, stamped by the component projection — the
        /// preferred end-state display label (terminal names are unique within a graph). Null for
        /// chain-authored modes. Runtime projection state (the <see cref="Parent"/> precedent):
        /// never serialized, never hashed, never copied by <see cref="Clone"/>.
        /// </summary>
        public string? ProjectedTerminalName { get; set; }

        /// <summary>
        /// The ordered response stages of the chain — always at least one. A single stage whose
        /// response is the <see cref="NonFailResponse"/> sentinel is the non-failure mode.
        /// </summary>
        public List<ResponseStage> ResponseStages
        {
            get { return _responseStages; }
        }

        /// <summary>
        /// The trailing transforms applied after the last response, from the bound hazard
        /// position toward the consequences (the v1.0 name and role). Assigning null coerces to
        /// an empty list; the failure mode takes ownership of an assigned list.
        /// </summary>
        public List<ITransformFunction> ResponseToConsequence
        {
            get { return _responseToConsequence; }
            set
            {
                if (!ReferenceEquals(_responseToConsequence, value))
                {
                    _responseToConsequence = value ?? new List<ITransformFunction>();
                    RaisePropertyChange(nameof(ResponseToConsequence));
                }
            }
        }

        /// <summary>
        /// The univariate transforms shaping the hazard's secondary signal on the way to this
        /// mode's bivariate elements — the path's secondary chain, upstream → downstream,
        /// projected from the graph (the transforms feeding the first bivariate element's
        /// secondary input). Empty for every univariate mode. Assigning null coerces to an empty
        /// list; the failure mode takes ownership of an assigned list.
        /// </summary>
        /// <remarks>
        /// Compute-relevant when present: the chain serializes (and therefore hashes) as a
        /// <c>SecondaryHazardToResponse</c> child written only when non-empty, so every
        /// pre-bivariate mode's serialized form, canonical hash, and seeds are untouched. The
        /// chain's samplers are seeded after the trailing transforms — appended walk positions —
        /// so existing modes' sampler ordinals never move. The engine evaluates the chain on the
        /// secondary axis: a Secondary-bound consequence consumes the secondary signal after
        /// <see cref="ConsequenceHazardPosition"/> chain transforms (position 0 is the raw
        /// secondary signal), and a joint-mode bivariate response receives the chain's full
        /// output as its secondary coordinate.
        /// </remarks>
        public List<ITransformFunction> SecondaryHazardToResponse
        {
            get { return _secondaryHazardToResponse; }
            set
            {
                if (!ReferenceEquals(_secondaryHazardToResponse, value))
                {
                    _secondaryHazardToResponse = value ?? new List<ITransformFunction>();
                    RaisePropertyChange(nameof(SecondaryHazardToResponse));
                }
            }
        }

        /// <summary>
        /// The ordered consequence functions: index 0 is the primary consequence used for risk
        /// integration; all entries are computed and tracked. Assigning null coerces to an empty
        /// list; the failure mode takes ownership of an assigned list.
        /// </summary>
        public List<IConsequenceFunction> ConsequenceFunctions
        {
            get { return _consequenceFunctions; }
            set
            {
                if (!ReferenceEquals(_consequenceFunctions, value))
                {
                    _consequenceFunctions = value ?? new List<IConsequenceFunction>();
                    RaisePropertyChange(nameof(ConsequenceFunctions));
                }
            }
        }

        /// <summary>
        /// Which hazard dimension the chain consumes as its signal origin:
        /// <see cref="HazardDimension.Primary"/> (the default, and the only legal value under a
        /// univariate component hazard), or <see cref="HazardDimension.Secondary"/> when the
        /// component hazard is bivariate and the mode's path leaves the hazard's secondary
        /// output — the projection stamps it from the root exit port. A Secondary-bound mode's
        /// stage chain runs with the secondary signal as its origin, through ordinary univariate
        /// machinery. Compute-relevant — hashed.
        /// </summary>
        public HazardDimension HazardBinding
        {
            get { return _hazardBinding; }
            set
            {
                if (_hazardBinding != value)
                {
                    _hazardBinding = value;
                    RaisePropertyChange(nameof(HazardBinding));
                }
            }
        }

        /// <summary>
        /// Which hazard dimension the consequence binding consumes at its bound position:
        /// <see cref="HazardDimension.Primary"/> (the default, and the only legal value under a
        /// univariate component hazard), or <see cref="HazardDimension.Secondary"/> when the
        /// consequences of a bivariate component consume the secondary signal — stamped by the
        /// projection from a binding onto the hazard's secondary output (position 0) or a
        /// secondary-chain transform (position k + 1). Compute-relevant — hashed.
        /// </summary>
        /// <remarks>
        /// The consequence input routing contract the engine implements: a univariate
        /// consequence with a Primary dimension consumes the transformed primary signal at
        /// <see cref="ConsequenceHazardPosition"/> (exact v1.0 behavior); with a Secondary
        /// dimension it consumes the secondary signal after
        /// <see cref="ConsequenceHazardPosition"/> secondary-chain transforms; the trailing
        /// <see cref="ResponseToConsequence"/> transforms fold after either (they are
        /// one-argument and axis-agnostic). A bivariate consequence evaluates its surface at
        /// both bound signals — this dimension is then inert, and trailing transforms under it
        /// are a validation error (a one-argument transform cannot precede a two-argument
        /// evaluation).
        /// </remarks>
        public HazardDimension ConsequenceHazardDimension
        {
            get { return _consequenceHazardDimension; }
            set
            {
                if (_consequenceHazardDimension != value)
                {
                    _consequenceHazardDimension = value;
                    RaisePropertyChange(nameof(ConsequenceHazardDimension));
                }
            }
        }

        /// <summary>
        /// The chain position whose hazard signal feeds the consequences. Under the Primary
        /// <see cref="ConsequenceHazardDimension"/>: 0 is the raw component hazard, k is the
        /// signal after the k-th stage transform, and null means the v1.0 default — the last
        /// response's input (<see cref="TotalStageTransformCount"/>). Under the Secondary
        /// dimension: 0 is the raw secondary signal and k is the signal after the k-th
        /// <see cref="SecondaryHazardToResponse"/> transform (always stamped explicitly by the
        /// projection). Trailing <see cref="ResponseToConsequence"/> transforms always apply,
        /// folding from the bound position. Compute-relevant — hashed; serialized resolved (see
        /// <see cref="ToXElement"/>).
        /// </summary>
        public int? ConsequenceHazardPosition
        {
            get { return _consequenceHazardPosition; }
            set
            {
                if (_consequenceHazardPosition != value)
                {
                    _consequenceHazardPosition = value;
                    RaisePropertyChange(nameof(ConsequenceHazardPosition));
                }
            }
        }

        /// <summary>
        /// Determines whether the last response's output fans out to multiple consequence paths
        /// (the v1.0 flag; the graph projection sets it when a response element feeds two or more
        /// terminal elements). Compute-relevant — hashed.
        /// </summary>
        public bool MultipleConsequences
        {
            get { return _multipleConsequences; }
            set
            {
                if (_multipleConsequences != value)
                {
                    _multipleConsequences = value;
                    RaisePropertyChange(nameof(MultipleConsequences));
                }
            }
        }

        /// <summary>
        /// Determines whether this is the component's non-failure mode: a single stage whose
        /// response is the <see cref="NonFailResponse"/> sentinel (identification is by type, not
        /// by reference — v1.1 has no singletons).
        /// </summary>
        public bool IsNonFailureMode
        {
            get { return _responseStages.Count == 1 && _responseStages[0] is not null && _responseStages[0].Response is NonFailResponse; }
        }

        /// <summary>
        /// Determines whether every stage, trailing transform, secondary-hazard-chain transform,
        /// and consequence function carries no knowledge uncertainty. Null entries are skipped
        /// (validation reports them).
        /// </summary>
        public bool IsDeterministic
        {
            get
            {
                for (int i = 0; i < _responseStages.Count; i++)
                {
                    if (_responseStages[i] is not null && !_responseStages[i].IsDeterministic) return false;
                }
                for (int i = 0; i < _responseToConsequence.Count; i++)
                {
                    if (_responseToConsequence[i] is not null && !_responseToConsequence[i].IsDeterministic) return false;
                }
                for (int i = 0; i < _secondaryHazardToResponse.Count; i++)
                {
                    if (_secondaryHazardToResponse[i] is not null && !_secondaryHazardToResponse[i].IsDeterministic) return false;
                }
                for (int i = 0; i < _consequenceFunctions.Count; i++)
                {
                    if (_consequenceFunctions[i] is not null && !_consequenceFunctions[i].IsDeterministic) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// The total number of stage transforms across the chain — the upper bound (and default
        /// value) of the consequence hazard position. Trailing transforms are not counted: they
        /// apply after the bound position.
        /// </summary>
        public int TotalStageTransformCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _responseStages.Count; i++)
                {
                    if (_responseStages[i] is not null) count += _responseStages[i].Transforms.Count;
                }
                return count;
            }
        }

        /// <summary>
        /// The consequence hazard position with the null default resolved: the explicit value
        /// when set, otherwise <see cref="TotalStageTransformCount"/> (the last response's input —
        /// exact v1.0 behavior).
        /// </summary>
        public int ResolvedConsequenceHazardPosition
        {
            get { return _consequenceHazardPosition ?? TotalStageTransformCount; }
        }

        /// <summary>
        /// The v1.0 view over stage 0's transform chain — the transforms from the hazard to the
        /// (first) response. Assigning replaces stage 0's list (null coerces to empty).
        /// </summary>
        public List<ITransformFunction> HazardToResponse
        {
            get { return StageZero.Transforms; }
            set
            {
                var stage = StageZero;
                if (!ReferenceEquals(stage.Transforms, value))
                {
                    stage.Transforms = value!;
                    RaisePropertyChange(nameof(HazardToResponse));
                }
            }
        }

        /// <summary>
        /// The v1.0 view over stage 0's response function. Assigning null coerces to a fresh
        /// <see cref="NonFailResponse"/> (v1.0 defaulting).
        /// </summary>
        public IResponseFunction ResponseFunction
        {
            get { return StageZero.Response; }
            set
            {
                var stage = StageZero;
                var before = stage.Response;
                stage.Response = value!;
                if (!ReferenceEquals(before, stage.Response))
                {
                    RaisePropertyChange(nameof(ResponseFunction));
                }
            }
        }

        /// <summary>
        /// The v1.0 view over the primary consequence — index 0 of
        /// <see cref="ConsequenceFunctions"/>, or null when the list is empty. Assigning replaces
        /// (or creates) index 0; assigning null removes index 0 (v1.0 single-slot semantics).
        /// </summary>
        public IConsequenceFunction? ConsequenceFunction
        {
            get { return _consequenceFunctions.Count > 0 ? _consequenceFunctions[0] : null; }
            set
            {
                if (value is null)
                {
                    if (_consequenceFunctions.Count == 0) return;
                    _consequenceFunctions.RemoveAt(0);
                }
                else if (_consequenceFunctions.Count == 0)
                {
                    _consequenceFunctions.Add(value);
                }
                else
                {
                    if (ReferenceEquals(_consequenceFunctions[0], value)) return;
                    _consequenceFunctions[0] = value;
                }
                RaisePropertyChange(nameof(ConsequenceFunction));
            }
        }

        /// <summary>
        /// Raised when a failure-mode property changes. Passive contract — headless callers need
        /// not subscribe.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        #endregion

        #region Methods

        /// <summary>
        /// Validates the failure mode and reports any issues found.
        /// </summary>
        /// <returns>
        /// A tuple containing:
        /// <list type="bullet">
        /// <item>
        /// <description><c>IsValid</c>: <c>true</c> if the failure mode passes all validation checks; otherwise <c>false</c>.</description>
        /// </item>
        /// <item>
        /// <description><c>ValidationMessages</c>: messages describing validation errors ("Error: …", invalidating) and warnings ("Warning: …", advisory).</description>
        /// </item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// Errors: no stages; null stage, trailing-transform, secondary-chain, or consequence
        /// entries; the non-failure sentinel inside a multi-stage chain; no consequence
        /// functions; a consequence hazard position outside its dimension's range (Primary:
        /// [0, <see cref="TotalStageTransformCount"/>]; Secondary: [0, the secondary-hazard
        /// chain length], explicit position required); the bivariate gate matrix — under a
        /// univariate or unknown component hazard, Secondary bindings, secondary chains, and
        /// bivariate transform/consequence functions are errors while a bivariate response is
        /// legal in collapse mode; under a bivariate hazard a bivariate response must be
        /// single-stage with at least two secondary levels; a bivariate transform inside the
        /// secondary chain; trailing transforms alongside a bivariate consequence; and every
        /// owned function's own errors (aggregated). Hazard-type label mismatches along the
        /// chain — including the secondary axis, which walks from the bound marginal's declared
        /// pair — are advisory warnings, a deliberate v1.1 divergence from v1.0, where label
        /// mismatches were invalidating (labels are unhashed display metadata).
        /// </remarks>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            return Validate(RiskAnalysisMode.Risk);
        }

        /// <summary>
        /// Validates the failure mode for the given analysis mode. Reliability mode
        /// relaxes the at-least-one-consequence requirement only — a consequence-free mode
        /// computes failure probability through the single zero-consequence branch; every other
        /// check (including validation of any consequences that are present) is identical to
        /// <see cref="Validate()"/>.
        /// </summary>
        /// <param name="mode">The analysis mode the failure mode is being validated for.</param>
        /// <returns>
        /// A tuple containing:
        /// <list type="bullet">
        /// <item>
        /// <description><c>IsValid</c>: <c>true</c> if the failure mode passes all validation checks; otherwise <c>false</c>.</description>
        /// </item>
        /// <item>
        /// <description><c>ValidationMessages</c>: messages describing validation errors ("Error: …", invalidating) and warnings ("Warning: …", advisory).</description>
        /// </item>
        /// </list>
        /// </returns>
        public (bool IsValid, List<string> ValidationMessages) Validate(RiskAnalysisMode mode)
        {
            var messages = new List<string>();

            if (_responseStages.Count == 0)
            {
                messages.Add("Error: The failure mode must have at least one response stage.");
            }
            for (int i = 0; i < _responseStages.Count; i++)
            {
                if (_responseStages[i] is null)
                {
                    messages.Add($"Error: The response stage at index {i} has not been defined.");
                    continue;
                }
                messages.AddRange(_responseStages[i].Validate().ValidationMessages);
                if (_responseStages.Count > 1 && _responseStages[i].Response is NonFailResponse)
                {
                    messages.Add($"Error: The non-failure response sentinel cannot appear in a response chain (stage index {i}); it identifies the sole stage of the non-failure mode.");
                }
                if (_responseStages.Count > 1 && _responseStages[i].Response is DeterioratingResponse)
                {
                    messages.Add($"Error: The deteriorating response '{_responseStages[i].Response.Name}' cannot appear in a response chain (stage index {i}); a deteriorating response is supported on the single response stage of an ordinary failure mode.");
                }
            }

            for (int i = 0; i < _responseToConsequence.Count; i++)
            {
                if (_responseToConsequence[i] is null)
                {
                    messages.Add($"Error: The response-to-consequence transform at index {i} has not been defined.");
                    continue;
                }
                messages.AddRange(_responseToConsequence[i].Validate().ValidationMessages);
            }

            // The secondary-hazard chain validates like the trailing transforms — and it is
            // univariate by construction: a bivariate transform inside it would need its own
            // secondary source, which the chain is.
            for (int i = 0; i < _secondaryHazardToResponse.Count; i++)
            {
                if (_secondaryHazardToResponse[i] is null)
                {
                    messages.Add($"Error: The secondary-hazard transform at index {i} has not been defined.");
                    continue;
                }
                messages.AddRange(_secondaryHazardToResponse[i].Validate().ValidationMessages);
                if (_secondaryHazardToResponse[i] is IBivariateTransformFunction)
                {
                    messages.Add($"Error: The secondary-hazard transform at index {i} is bivariate; the secondary chain shapes one signal and is univariate by construction.");
                }
            }

            if (_consequenceFunctions.Count == 0 && mode == RiskAnalysisMode.Risk)
            {
                messages.Add("Error: The failure mode must have at least one consequence function.");
            }
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                if (_consequenceFunctions[i] is null)
                {
                    messages.Add($"Error: The consequence function at index {i} has not been defined.");
                    continue;
                }
                messages.AddRange(_consequenceFunctions[i].Validate().ValidationMessages);
            }

            // Position bounds by dimension: a Primary position addresses the stage-transform
            // chain (null = the v1.0 last-response-input default); a Secondary position
            // addresses the secondary-hazard chain and is always stamped explicitly by the
            // projection.
            if (_consequenceHazardDimension == HazardDimension.Secondary)
            {
                if (!_consequenceHazardPosition.HasValue)
                {
                    messages.Add("Error: The Secondary consequence hazard dimension requires an explicit consequence hazard position (0 is the raw secondary signal, k is the signal after the k-th secondary-chain transform).");
                }
                else if (_consequenceHazardPosition.Value < 0 || _consequenceHazardPosition.Value > _secondaryHazardToResponse.Count)
                {
                    messages.Add($"Error: The consequence hazard position ({_consequenceHazardPosition.Value}) must be between 0 (the raw secondary signal) and the secondary-hazard chain length ({_secondaryHazardToResponse.Count}) under the Secondary consequence hazard dimension.");
                }
            }
            else if (_consequenceHazardPosition.HasValue &&
                (_consequenceHazardPosition.Value < 0 || _consequenceHazardPosition.Value > TotalStageTransformCount))
            {
                messages.Add($"Error: The consequence hazard position ({_consequenceHazardPosition.Value}) must be between 0 (the raw hazard) and the total stage transform count ({TotalStageTransformCount}).");
            }

            // The bivariate gate matrix, keyed on the owning component's hazard. Under a
            // univariate (or unknown — the mode is unparented) hazard no secondary dimension
            // exists: Secondary bindings, secondary chains, and bivariate transform/consequence
            // functions are errors, while a bivariate RESPONSE stays legal — it operates in
            // collapse mode, presenting its weighted mean collapse as an ordinary univariate
            // response, cascade stages included. Under a bivariate hazard a bivariate response
            // runs in joint mode: single-stage only, and its surface needs an interpolable
            // secondary axis (at least two secondary hazard levels).
            bool bivariateParent = _parent?.HazardFunction is IBivariateHazardFunction;
            if (!bivariateParent)
            {
                if (_hazardBinding == HazardDimension.Secondary)
                {
                    messages.Add("Error: The failure mode hazard binding is Secondary, which requires a bivariate component hazard.");
                }
                if (_consequenceHazardDimension == HazardDimension.Secondary)
                {
                    messages.Add("Error: The failure mode consequence hazard dimension is Secondary, which requires a bivariate component hazard.");
                }
                if (_secondaryHazardToResponse.Count > 0)
                {
                    messages.Add("Error: The failure mode carries a secondary-hazard chain, which requires a bivariate component hazard.");
                }
                for (int s = 0; s < _responseStages.Count; s++)
                {
                    var stage = _responseStages[s];
                    if (stage is null) continue;
                    for (int t = 0; t < stage.Transforms.Count; t++)
                    {
                        if (stage.Transforms[t] is IBivariateTransformFunction)
                        {
                            messages.Add($"Error: The stage transform '{stage.Transforms[t].Name}' is bivariate, which requires a bivariate component hazard; a bivariate transform has no secondary signal to consume and no collapse semantics.");
                        }
                    }
                }
                for (int i = 0; i < _responseToConsequence.Count; i++)
                {
                    if (_responseToConsequence[i] is IBivariateTransformFunction)
                    {
                        messages.Add($"Error: The response-to-consequence transform '{_responseToConsequence[i].Name}' is bivariate, which requires a bivariate component hazard; a bivariate transform has no secondary signal to consume and no collapse semantics.");
                    }
                }
                for (int i = 0; i < _consequenceFunctions.Count; i++)
                {
                    if (_consequenceFunctions[i] is IBivariateConsequenceFunction)
                    {
                        messages.Add($"Error: The consequence function '{_consequenceFunctions[i].Name}' is bivariate, which requires a bivariate component hazard; a bivariate consequence has no secondary signal to consume and no collapse semantics.");
                    }
                }
            }
            else
            {
                bool anyBivariateResponse = false;
                for (int s = 0; s < _responseStages.Count; s++)
                {
                    var stage = _responseStages[s];
                    if (stage?.Response is IBivariateResponseFunction jointResponse)
                    {
                        anyBivariateResponse = true;
                        if (jointResponse.SecondaryLevelCount < 2)
                        {
                            messages.Add($"Error: The bivariate response '{stage.Response.Name}' has {jointResponse.SecondaryLevelCount} secondary hazard level(s); joint evaluation under a bivariate hazard requires at least two for an interpolable secondary axis.");
                        }
                    }
                    if (stage?.Response is DeterioratingResponse)
                    {
                        messages.Add($"Error: The deteriorating response '{stage.Response.Name}' is not supported under a bivariate component hazard.");
                    }
                }
                if (anyBivariateResponse && _responseStages.Count > 1)
                {
                    messages.Add($"Error: The failure mode chains {_responseStages.Count} response stages through a bivariate response; a joint-mode bivariate response is supported on single-stage modes only.");
                }
            }

            // Trailing transforms cannot precede a bivariate consequence: they are one-argument
            // and the surface evaluates at both bound signals directly.
            bool anyBivariateConsequence = false;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                if (_consequenceFunctions[i] is IBivariateConsequenceFunction)
                {
                    anyBivariateConsequence = true;
                    break;
                }
            }
            if (anyBivariateConsequence && _responseToConsequence.Count > 0)
            {
                messages.Add("Error: The failure mode carries response-to-consequence transforms alongside a bivariate consequence; a bivariate consequence evaluates at both bound signals directly, so one-argument trailing transforms cannot precede it.");
            }

            // The branch-explosion guardrails under per-type marginal compute
            // (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §6.4.1 erratum): each
            // consequence position's exposure branches enumerate
            // separately for that type's results — types are never crossed — so the mode's
            // per-evaluation branch work is the SUM across its consequence positions, not a
            // cross product (which would model a cross-type coupling the engine never
            // performs). Warn above 64, error above 1024.
            long combinedBranches = 0;
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                if (_consequenceFunctions[i] is null) continue;
                combinedBranches += Math.Max(1, _consequenceFunctions[i].CountExposureBranches());
            }
            if (combinedBranches > 1024)
            {
                messages.Add("Error: The failure mode's combined consequence exposure branches exceed 1024 (the sum across consequence positions); reduce the mixture branch counts.");
            }
            else if (combinedBranches > 64)
            {
                messages.Add($"Warning: The failure mode's combined consequence exposure branches ({combinedBranches}) exceed 64; the branch enumeration grows compute cost accordingly.");
            }

            ValidateLabelContinuity(messages);

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <summary>
        /// Computes the failure mode's canonical SHA-256 content hash — the stable, name-free
        /// identity that content-based Monte Carlo seeding derives from.
        /// </summary>
        /// <returns>The 32-byte SHA-256 hash of the canonicalized <see cref="ToXElement"/> form.</returns>
        public byte[] CanonicalHash()
        {
            return CanonicalContentHasher.Hash(ToIdentityXElement(), CanonicalizationRules.ModelRules);
        }

        /// <summary>
        /// Sets up this mode's samplers for a run: allocates the consequence coupling matrix
        /// (this mode claims the first ordinal), then walks the chain — stage transforms, stage
        /// responses, trailing transforms, then the secondary-hazard chain, in declared order —
        /// seeding each function on its first encounter with a content-derived seed. The
        /// secondary-hazard chain sits at the end of the walk deliberately: its positions are
        /// appended, so every pre-bivariate mode's ordinals (and therefore seeds) are
        /// bit-identical to the walk without it. Consequence functions are deliberately not in
        /// the walk: they need no percentile matrices, because the coupling matrix supplies their
        /// shared knowledge percentile (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.8.7).
        /// </summary>
        /// <param name="sampleSize">The realization count N.</param>
        /// <param name="componentSeed">The owning component's content-derived seed.</param>
        /// <param name="ordinal">The next structural ordinal in the component's walk.</param>
        /// <param name="scheme">The knowledge-uncertainty sampling scheme.</param>
        /// <param name="seededFunctions">
        /// The functions already seeded in this component's walk (reference identity). The
        /// ordinal advances for every encounter so structural positions stay stable, but a shared
        /// function instance is seeded once — its first canonical owner wins, and everywhere it
        /// appears it draws the identical realizations (one instance = one knowledge quantity).
        /// Two distinct instances with equal content get different ordinals and draw
        /// independently.
        /// </param>
        /// <param name="scribe">The seed scribe (capture, and optionally apply), or null — the seed-stable perturbation mode (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.5.8).</param>
        /// <param name="fractilePins">The epistemic conditioning pins by function id, or null — applied to walked functions after seeding.</param>
        /// <param name="appliedPins">The sink recording every pin id the walk applied, or null.</param>
        /// <returns>The next unclaimed ordinal.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the seeded-function set is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the sample size is not positive.</exception>
        /// <exception cref="NotSupportedException">Thrown when the sampling scheme is unrecognized.</exception>
        internal int SetupSamplers(int sampleSize, int componentSeed, int ordinal, SamplingScheme scheme,
            ISet<IRiskFunction> seededFunctions, SeedScribe? scribe = null,
            IReadOnlyDictionary<Guid, double>? fractilePins = null, ISet<Guid>? appliedPins = null)
        {
            if (seededFunctions == null) throw new ArgumentNullException(nameof(seededFunctions));
            if (sampleSize <= 0) throw new ArgumentOutOfRangeException(nameof(sampleSize), "The sample size must be positive.");

            // The coupling matrix claims this mode's first ordinal so the pair-coupling stream is
            // as content-stable as any function's. The scribe sees the pre-fold seed — the fold
            // is deterministic, so capture/apply stays a pure seed substitution.
            int couplingBase = SeedHelpers.HashCombine(componentSeed, CanonicalHash(), ordinal);
            if (scribe != null) couplingBase = scribe.Resolve(ordinal, couplingBase);
            int couplingSeed = SeedHelpers.ToPositiveSeed(couplingBase);
            ordinal++;
            int columns = Math.Max(1, _consequenceFunctions.Count);
            _couplingPercentiles = scheme switch
            {
                SamplingScheme.LatinHypercube => LatinHypercube.Random(sampleSize, columns, couplingSeed),
                SamplingScheme.LatinHypercubeMedian => LatinHypercube.Median(sampleSize, columns, couplingSeed),
                SamplingScheme.MonteCarlo => SeedHelpers.IndependentUniform(sampleSize, columns, couplingSeed),
                SamplingScheme.ScrambledSobol => SeedHelpers.ScrambledSobol(sampleSize, columns, couplingSeed),
                _ => throw new NotSupportedException($"The sampling scheme '{scheme}' is not supported."),
            };

            for (int s = 0; s < _responseStages.Count; s++)
            {
                var stage = _responseStages[s];
                if (stage is null) continue;
                for (int i = 0; i < stage.Transforms.Count; i++)
                {
                    ordinal = SetupFunction(stage.Transforms[i], sampleSize, componentSeed, ordinal, scheme, seededFunctions, scribe, fractilePins, appliedPins);
                }
                ordinal = SetupFunction(stage.Response, sampleSize, componentSeed, ordinal, scheme, seededFunctions, scribe, fractilePins, appliedPins);
            }
            for (int i = 0; i < _responseToConsequence.Count; i++)
            {
                ordinal = SetupFunction(_responseToConsequence[i], sampleSize, componentSeed, ordinal, scheme, seededFunctions, scribe, fractilePins, appliedPins);
            }
            for (int i = 0; i < _secondaryHazardToResponse.Count; i++)
            {
                ordinal = SetupFunction(_secondaryHazardToResponse[i], sampleSize, componentSeed, ordinal, scheme, seededFunctions, scribe, fractilePins, appliedPins);
            }
            return ordinal;
        }

        /// <summary>
        /// Reads the shared knowledge percentile coupling a realization's paired consequences at
        /// a consequence position.
        /// </summary>
        /// <param name="realizationIndex">The realization index in [0, sample size).</param>
        /// <param name="position">The consequence position (0 is the primary).</param>
        /// <returns>The uniform (0, 1) coupling percentile.</returns>
        /// <exception cref="InvalidOperationException">Thrown before <see cref="SetupSamplers"/> has run.</exception>
        internal double CouplingPercentile(int realizationIndex, int position)
        {
            if (_couplingPercentiles == null)
            {
                throw new InvalidOperationException("SetupSamplers() must be called before sampling by realization index.");
            }
            return _couplingPercentiles[realizationIndex, position];
        }

        /// <summary>
        /// Samples this failure mode for one realization.
        /// </summary>
        /// <param name="nonFailureMode">
        /// The component's non-failure mode, paired-sampled at this mode's coupling percentile
        /// for the excess computation; null when the component has none.
        /// </param>
        /// <param name="realizationIndex">The realization index, or −1 for the mean functions.</param>
        /// <returns>The sampled failure mode.</returns>
        /// <exception cref="InvalidOperationException">Thrown when sampling by realization index before <see cref="SetupSamplers"/> has run.</exception>
        /// <remarks>
        /// Multi-stage chains sample every stage: the sampled
        /// mode's response probability is the polarity product over the stages.
        /// </remarks>
        public SampledFailureMode Sample(FailureMode? nonFailureMode, int realizationIndex = -1)
        {
            return new SampledFailureMode(this, nonFailureMode, realizationIndex);
        }

        /// <summary>
        /// Creates a deep copy of the failure mode via the serialization round-trip (exact by the
        /// round-trip contract). The parent component wiring is not copied.
        /// </summary>
        /// <returns>The copied failure mode.</returns>
        public FailureMode Clone()
        {
            return new FailureMode(ToXElement());
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the failure mode to an XElement — the persistence contract AND the
        /// canonical-hash identity surface: attribute names and owned-child order are
        /// append-only. The consequence hazard position is written resolved (the null default
        /// becomes the explicit last-response-input position), so a defaulted mode and an
        /// explicitly-equal mode serialize and hash identically; a restored mode therefore pins
        /// the position explicitly. The secondary-hazard chain is written only when non-empty
        /// (conditional presence — the shape every pre-bivariate mode serialized under). Null
        /// entries are skipped (validation reports them).
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(FailureMode));
            element.SetAttributeValue(nameof(HazardBinding), HazardBinding.ToString());
            element.SetAttributeValue(nameof(ConsequenceHazardDimension), ConsequenceHazardDimension.ToString());
            element.SetAttributeValue(nameof(ConsequenceHazardPosition), ResolvedConsequenceHazardPosition);
            element.SetAttributeValue(nameof(MultipleConsequences), MultipleConsequences);

            var stages = new XElement(nameof(ResponseStages));
            for (int i = 0; i < _responseStages.Count; i++)
            {
                if (_responseStages[i] is not null) stages.Add(_responseStages[i].ToXElement());
            }
            element.Add(stages);

            var trailing = new XElement(nameof(ResponseToConsequence));
            for (int i = 0; i < _responseToConsequence.Count; i++)
            {
                if (_responseToConsequence[i] is not null) trailing.Add(_responseToConsequence[i].ToXElement());
            }
            element.Add(trailing);

            var consequences = new XElement(nameof(ConsequenceFunctions));
            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                if (_consequenceFunctions[i] is not null) consequences.Add(_consequenceFunctions[i].ToXElement());
            }
            element.Add(consequences);

            // Conditional presence is the compatibility contract: an always-written empty
            // container would move every existing failure-mode hash and re-roll all seeds, so
            // the secondary-hazard chain appears only when it has content.
            if (_secondaryHazardToResponse.Count > 0)
            {
                var secondaryChain = new XElement(nameof(SecondaryHazardToResponse));
                for (int i = 0; i < _secondaryHazardToResponse.Count; i++)
                {
                    if (_secondaryHazardToResponse[i] is not null) secondaryChain.Add(_secondaryHazardToResponse[i].ToXElement());
                }
                element.Add(secondaryChain);
            }

            return element;
        }

        /// <summary>Builds canonical identity with projected response identities in place of persistence wrappers.</summary>
        /// <returns>The identity form.</returns>
        internal XElement ToIdentityXElement()
        {
            XElement element = ToXElement();
            XElement? stages = element.Element(nameof(ResponseStages));
            if (stages == null) return element;
            XElement[] persisted = stages.Elements(nameof(ResponseStage)).ToArray();
            int serializedIndex = 0;
            for (int i = 0; i < _responseStages.Count; i++)
            {
                if (_responseStages[i] == null) continue;
                persisted[serializedIndex++].ReplaceWith(
                    _responseStages[i].ToIdentityXElement());
            }
            return element;
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Seeds one walked function on first encounter and advances the structural ordinal for
        /// every encounter. Null entries are skipped without advancing — a null slot is a
        /// validation error the analysis gate rejects before any run.
        /// </summary>
        /// <param name="function">The function at this walk position, possibly null.</param>
        /// <param name="sampleSize">The realization count N.</param>
        /// <param name="componentSeed">The owning component's content-derived seed.</param>
        /// <param name="ordinal">This walk position's ordinal.</param>
        /// <param name="scheme">The knowledge-uncertainty sampling scheme.</param>
        /// <param name="seededFunctions">The functions already seeded (reference identity).</param>
        /// <param name="scribe">The seed scribe capturing (and, when pinned, overriding) the resolved seed at this ordinal, or null outside a scribed walk.</param>
        /// <param name="fractilePins">The epistemic conditioning pins by function id, or null — applied after seeding, so the captured seed map is untouched.</param>
        /// <param name="appliedPins">The sink recording every pin id the walk applied, or null.</param>
        /// <returns>The next unclaimed ordinal.</returns>
        private static int SetupFunction(IRiskFunction? function, int sampleSize, int componentSeed, int ordinal,
            SamplingScheme scheme, ISet<IRiskFunction> seededFunctions, SeedScribe? scribe = null,
            IReadOnlyDictionary<Guid, double>? fractilePins = null, ISet<Guid>? appliedPins = null)
        {
            if (function is null) return ordinal;
            if (seededFunctions.Add(function))
            {
                int seed = SeedHelpers.HashCombine(componentSeed, function.CanonicalHash(), ordinal);
                if (scribe != null) seed = scribe.Resolve(ordinal, seed);
                function.SetupSampler(sampleSize, seed, scheme);
            }
            ApplyFractilePin(function, fractilePins, appliedPins);
            return ordinal + 1;
        }

        /// <summary>
        /// Applies a matching epistemic conditioning pin to a walked function by overwriting its
        /// pre-allocated percentile matrix (idempotent, so shared instances re-apply harmlessly
        /// at every encounter). Only functions that own a matrix are pinnable — a function with
        /// no sampling dimensions is a validated no-effect pin and is skipped here.
        /// </summary>
        /// <param name="function">The walked function.</param>
        /// <param name="fractilePins">The pins by function id, or null.</param>
        /// <param name="appliedPins">The sink recording applied pin ids, or null.</param>
        internal static void ApplyFractilePin(IRiskFunction function,
            IReadOnlyDictionary<Guid, double>? fractilePins, ISet<Guid>? appliedPins)
        {
            if (fractilePins == null || fractilePins.Count == 0) return;
            if (function.SamplingDimensions <= 0) return;
            if (function is RiskFunctionBase pinnable && fractilePins.TryGetValue(function.Id, out double percentile))
            {
                pinnable.OverrideSampledPercentiles(percentile);
                appliedPins?.Add(function.Id);
            }
        }

        /// <summary>
        /// Stage 0, self-healing: if the stage list was emptied through the exposed list, a
        /// default stage is re-added so the v1.0 views never fault. Validation still reports an
        /// emptied list when the views are not touched first.
        /// </summary>
        private ResponseStage StageZero
        {
            get
            {
                if (_responseStages.Count == 0) _responseStages.Add(new ResponseStage());
                return _responseStages[0];
            }
        }

        /// <summary>
        /// Walks the chain comparing hazard-type labels and units at each function's input
        /// position, appending advisory warnings on mismatches. Transforms advance the signal
        /// cursor; responses compare at their input without advancing (the hazard signal passes
        /// through a response); consequences compare at the bound position after folding the
        /// trailing transforms. The secondary axis walks in parallel: the secondary-hazard chain
        /// compares from the bivariate component hazard's declared secondary pair, bivariate
        /// stage functions compare their secondary labels against the chain's end signal, and a
        /// Secondary-dimension consequence binding reads its expected pair from the secondary
        /// checkpoints. Comparisons are skipped when either side is empty (the v1.0
        /// unknown-context fallback) and are case-insensitive (v1.0 behavior).
        /// </summary>
        /// <param name="messages">The message sink.</param>
        private void ValidateLabelContinuity(List<string> messages)
        {
            // Checkpoints of the secondary signal: index 0 is the bivariate component hazard's
            // declared secondary pair (empty for standalone or univariate-parent modes, which
            // silences every secondary comparison), index k is the signal after the k-th
            // secondary-chain transform.
            var bivariateParent = _parent?.HazardFunction as IBivariateHazardFunction;
            var secondaryLabels = new List<string> { bivariateParent?.SecondarySpecifiedHazard ?? string.Empty };
            var secondaryUnits = new List<string> { bivariateParent?.SecondaryHazardUnit ?? string.Empty };
            for (int i = 0; i < _secondaryHazardToResponse.Count; i++)
            {
                var transform = _secondaryHazardToResponse[i];
                if (transform is null) continue;
                CompareLabels(messages, secondaryLabels[secondaryLabels.Count - 1], secondaryUnits[secondaryUnits.Count - 1],
                    transform.SpecifiedHazard, transform.HazardUnit, $"secondary-hazard transform function '{transform.Name}'");
                secondaryLabels.Add(transform.TransformedHazard);
                secondaryUnits.Add(transform.TransformedHazardUnit);
            }
            string secondaryEndLabel = secondaryLabels[secondaryLabels.Count - 1];
            string secondaryEndUnit = secondaryUnits[secondaryUnits.Count - 1];

            // Checkpoints of the hazard signal: index 0 is the incoming component hazard
            // (unknown for a standalone failure mode; a Secondary-bound mode originates at the
            // bound marginal's secondary pair), index k is the signal after the k-th
            // stage transform.
            var labels = new List<string> { IncomingHazardLabel() };
            var units = new List<string> { IncomingHazardUnit() };

            for (int s = 0; s < _responseStages.Count; s++)
            {
                var stage = _responseStages[s];
                if (stage is null) continue;

                for (int i = 0; i < stage.Transforms.Count; i++)
                {
                    var transform = stage.Transforms[i];
                    if (transform is null) continue;
                    CompareLabels(messages, labels[labels.Count - 1], units[units.Count - 1],
                        transform.SpecifiedHazard, transform.HazardUnit, $"transform function '{transform.Name}'");
                    if (transform is IBivariateTransformFunction bivariateTransform)
                    {
                        CompareLabels(messages, secondaryEndLabel, secondaryEndUnit,
                            bivariateTransform.SecondarySpecifiedHazard, bivariateTransform.SecondaryHazardUnit,
                            $"bivariate transform function '{transform.Name}' (secondary axis)");
                    }
                    labels.Add(transform.TransformedHazard);
                    units.Add(transform.TransformedHazardUnit);
                }

                if (stage.Response is not null && stage.Response is not NonFailResponse)
                {
                    CompareLabels(messages, labels[labels.Count - 1], units[units.Count - 1],
                        stage.Response.SpecifiedHazard, stage.Response.HazardUnit, $"response function '{stage.Response.Name}'");
                    if (stage.Response is IBivariateResponseFunction bivariateResponse && bivariateParent != null)
                    {
                        // Joint mode only: in collapse mode (univariate parent) the secondary
                        // axis is internal to the response and no graph signal reaches it —
                        // the empty secondary checkpoint silences the comparison anyway.
                        CompareLabels(messages, secondaryEndLabel, secondaryEndUnit,
                            bivariateResponse.SecondarySpecifiedHazard, bivariateResponse.SecondaryHazardUnit,
                            $"bivariate response function '{stage.Response.Name}' (secondary axis)");
                    }
                }
            }

            // The consequence input: the signal at the bound position on the bound dimension,
            // folded through the trailing transforms. An out-of-range explicit position is
            // already an error; clamp for the advisory walk.
            string label;
            string unit;
            if (_consequenceHazardDimension == HazardDimension.Secondary)
            {
                int secondaryPosition = Math.Min(_consequenceHazardPosition ?? 0, secondaryLabels.Count - 1);
                secondaryPosition = Math.Max(secondaryPosition, 0);
                label = secondaryLabels[secondaryPosition];
                unit = secondaryUnits[secondaryPosition];
            }
            else
            {
                int position = Math.Min(ResolvedConsequenceHazardPosition, labels.Count - 1);
                position = Math.Max(position, 0);
                label = labels[position];
                unit = units[position];
            }

            for (int i = 0; i < _responseToConsequence.Count; i++)
            {
                var transform = _responseToConsequence[i];
                if (transform is null) continue;
                CompareLabels(messages, label, unit, transform.SpecifiedHazard, transform.HazardUnit,
                    $"response-to-consequence transform function '{transform.Name}'");
                label = transform.TransformedHazard;
                unit = transform.TransformedHazardUnit;
            }

            for (int i = 0; i < _consequenceFunctions.Count; i++)
            {
                var consequence = _consequenceFunctions[i];
                if (consequence is null) continue;
                if (consequence is IBivariateConsequenceFunction bivariateConsequence)
                {
                    // A bivariate consequence consumes both axes directly: its primary labels
                    // compare against the primary bound signal and its secondary labels against
                    // the secondary chain's end signal.
                    CompareLabels(messages, label, unit, consequence.SpecifiedHazard, consequence.HazardUnit,
                        $"bivariate consequence function '{consequence.Name}'");
                    CompareLabels(messages, secondaryEndLabel, secondaryEndUnit,
                        bivariateConsequence.SecondarySpecifiedHazard, bivariateConsequence.SecondaryHazardUnit,
                        $"bivariate consequence function '{consequence.Name}' (secondary axis)");
                    continue;
                }
                CompareLabels(messages, label, unit, consequence.SpecifiedHazard, consequence.HazardUnit,
                    $"consequence function '{consequence.Name}'");
            }
        }

        /// <summary>
        /// The hazard-type label entering the chain: the owning component hazard's label at the
        /// bound dimension when the mode is parented (a Secondary binding originates at a
        /// bivariate hazard's declared secondary label), otherwise empty (standalone modes skip
        /// the incoming comparison — the v1.0 unknown-context fallback).
        /// </summary>
        /// <returns>The incoming hazard-type label, or empty when unknown.</returns>
        private string IncomingHazardLabel()
        {
            var hazard = _parent?.HazardFunction;
            if (hazard is IBivariateHazardFunction bivariate && _hazardBinding == HazardDimension.Secondary)
            {
                return bivariate.SecondarySpecifiedHazard ?? string.Empty;
            }
            return hazard?.SpecifiedHazard ?? string.Empty;
        }

        /// <summary>
        /// The hazard-unit label entering the chain: the owning component hazard's unit at the
        /// bound dimension when the mode is parented, otherwise empty.
        /// </summary>
        /// <returns>The incoming hazard-unit label, or empty when unknown.</returns>
        private string IncomingHazardUnit()
        {
            var hazard = _parent?.HazardFunction;
            if (hazard is IBivariateHazardFunction bivariate && _hazardBinding == HazardDimension.Secondary)
            {
                return bivariate.SecondaryHazardUnit ?? string.Empty;
            }
            return hazard?.HazardUnit ?? string.Empty;
        }

        /// <summary>
        /// Compares an expected hazard label/unit pair against a function's declared input pair,
        /// appending advisory warnings on mismatches. Skipped when either side is empty.
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <param name="expectedLabel">The hazard-type label of the signal at the function's input.</param>
        /// <param name="expectedUnit">The hazard-unit label of the signal at the function's input.</param>
        /// <param name="declaredLabel">The function's declared input hazard-type label.</param>
        /// <param name="declaredUnit">The function's declared input hazard-unit label.</param>
        /// <param name="functionDescription">A short description naming the function for the message.</param>
        private static void CompareLabels(List<string> messages, string expectedLabel, string expectedUnit,
            string declaredLabel, string declaredUnit, string functionDescription)
        {
            if (!string.IsNullOrEmpty(expectedLabel) && !string.IsNullOrEmpty(declaredLabel) &&
                !string.Equals(expectedLabel, declaredLabel, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add($"Warning: The hazard type '{expectedLabel}' does not match the specified hazard '{declaredLabel}' of the {functionDescription}.");
            }
            if (!string.IsNullOrEmpty(expectedUnit) && !string.IsNullOrEmpty(declaredUnit) &&
                !string.Equals(expectedUnit, declaredUnit, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add($"Warning: The hazard unit '{expectedUnit}' does not match the specified hazard unit '{declaredUnit}' of the {functionDescription}.");
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
