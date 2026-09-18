using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Numerics;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Core.Support;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses
{
    /// <summary>
    /// An age-indexed deteriorating response function: wraps a base response (fragility) function
    /// together with an owned tabular deterioration law mapping age in years to a capacity-axis
    /// shift Δ(t), and evaluates the base at the shifted hazard —
    /// P<sub>f</sub>(h, t) = F<sub>base</sub>(h + Δ(t)). A positive shift weakens the wrapped
    /// response: capacity moves down, so the same hazard fails more.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// <b>The evaluation age is external state.</b> <see cref="EvaluationAge"/> is never
    /// serialized, never part of the canonical hash, and never an influence on sampling seeds —
    /// one authored function with one content-seeded stream evaluates at many ages, so epistemic
    /// realization i means the same state of knowledge at every age. The ordinary
    /// <see cref="IResponseFunction"/> members evaluate at the current <see cref="EvaluationAge"/>
    /// (zero by default — the undegraded base); the explicit-age <c>SampleFunctionAtAge</c>
    /// overloads are pure and leave the property untouched. A life-cycle evaluation layer sets the
    /// age on throwaway clones before a run; the age must not be mutated while a run is sampling.
    /// </para>
    /// <para>
    /// <b>The deterioration law</b> is an owned <see cref="Numerics.Data.UncertainOrderedPairedData"/>
    /// with strictly ascending non-negative ages and a shift distribution per ordinate, sampled
    /// co-monotonically (one percentile drives every ordinate — and the same percentile drives the
    /// base, the single-percentile convention of every wrapped pair). The shift at an age is the
    /// linear interpolation of the sampled law, holding the boundary ordinate outside the tabled
    /// age range. A law whose mean shift at age zero is non-zero draws a validation warning,
    /// because the age-zero response then differs from the base. A decreasing law (repair or
    /// strengthening) is deliberately legal.
    /// </para>
    /// <para>
    /// <b>Base restrictions.</b> The wrapped base must be a <see cref="TabularResponse"/> or an
    /// estimated <see cref="ParametricResponse"/>. Composite, tree, bivariate, non-failure, and
    /// nested deteriorating bases are refused: a composite hidden inside a wrapper would escape
    /// the epistemic discovery walks (shared-variable collection, logic-tree axis collection),
    /// and the other kinds have no meaningful capacity axis to shift. The wrapper itself is legal
    /// only as the single response stage of an ordinary failure mode under a univariate component
    /// hazard; composite membership, tree probability sources, multi-stage chains, and bivariate
    /// parents refuse it at validation, because those seats would silently evaluate the age-zero
    /// response.
    /// </para>
    /// <para>
    /// <b>Sampling and identity.</b> The wrapper consumes one knowledge dimension (the law's
    /// percentile column) and re-seeds the wrapped base with
    /// <c>SeedHelpers.HashCombine(seed, base.CanonicalHash(), 0)</c> — the composite forward rule,
    /// with ordinals 1 and above reserved. The canonical hash is a projected identity form: the
    /// law's serialized content plus the base's own canonical hash as a token attribute, so the
    /// serialization mode and every metadata edit (ids, names, labels — the base's included) are
    /// inert while any law or base content edit moves the hash. The base persists through the
    /// shared function-entry contract in both serialization modes; an unresolved reference marker
    /// is re-written verbatim on save so resolver-less round trips stay bit-equal.
    /// </para>
    /// <para>
    /// <b>Equivalence identity.</b> The wrapper at age t is exactly the base behind a
    /// deterministic <see cref="RMC.TotalRisk.RiskFunctions.Transforms.LinearTransform"/> with
    /// intercept Δ(t), slope one, and bounds spanning the domain: with a unit slope the transform
    /// computes Δ + h and the wrapper computes h + Δ, bit-identical in IEEE arithmetic. A zero
    /// shift reproduces the base bit-for-bit.
    /// </para>
    /// <para>
    /// A fractile pin on this function pins the LAW column; a pin addressed to the wrapped base's
    /// id refuses loudly at validation, exactly like a pin on a composite child. Sensitivity
    /// enumeration sees the wrapper's own law column; the base's knowledge draws ride its child
    /// stream unlabeled, the composite-child convention.
    /// </para>
    /// </remarks>
    public class DeterioratingResponse : ResponseFunctionBase
    {
        #region Construction

        /// <summary>
        /// Initializes a deteriorating response with no base response and the default zero law:
        /// {(0 → Deterministic(0))} — no deterioration authored yet, so a fresh instance is
        /// age-inert once a base is assigned.
        /// </summary>
        public DeterioratingResponse()
        {
        }

        /// <summary>
        /// Restores a deteriorating response from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement()"/>.</param>
        /// <param name="resolver">
        /// The function resolver re-attaching a by-reference base to the live stored instance;
        /// null when reading a self-contained form. A stale reference id throws; a name-only miss
        /// is recorded for <see cref="Validate"/> and the pending marker is re-written verbatim on
        /// the next save.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when inline base content cannot be reconstructed, when a serialized reference id
        /// is stale, or when a reference resolves to the wrong function cluster.
        /// </exception>
        public DeterioratingResponse(XElement xElement, IRiskFunctionResolver? resolver = null)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));

            var lawElement = xElement.Element(nameof(DeteriorationLaw))?.Element("UncertainOrderedPairedData");
            if (lawElement != null)
            {
                var law = new UncertainOrderedPairedData(lawElement)
                {
                    // Re-impose the law's ordering contract after the permissive parse (the
                    // tabular-function convention).
                    OrderX = SortOrder.Ascending,
                    OrderY = SortOrder.None,
                    StrictX = true,
                    StrictY = false,
                };
                law.Validate();
                _deteriorationLaw = law;
            }

            var baseChild = xElement.Element(nameof(BaseResponse))?.Elements().FirstOrDefault();
            if (baseChild != null)
            {
                if (baseChild.Name.LocalName == FunctionEntry.ReferenceElementName)
                {
                    // Capture the marker verbatim before resolution so an unresolved reference
                    // survives a resolver-less round trip bit-equal.
                    _pendingBaseIdText = baseChild.Attribute("Id")?.Value;
                    _pendingBaseName = baseChild.Attribute("Name")?.Value;
                }

                var resolved = FunctionEntry.Read<IResponseFunction>(
                    baseChild, resolver, c => RiskFunctionFactory.CreateFromXElement(c, resolver),
                    Name, $"The {nameof(DeterioratingResponse)} '{Name}'", "base response function",
                    _unresolvedFunctionReferences);
                if (resolved != null)
                {
                    BaseResponse = resolved;
                }
            }
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="BaseResponse"/>.
        /// </summary>
        private IResponseFunction? _baseResponse;

        /// <summary>
        /// Backing field for <see cref="DeteriorationLaw"/> — the default zero law.
        /// </summary>
        private UncertainOrderedPairedData _deteriorationLaw = new UncertainOrderedPairedData(
            new[] { new UncertainOrdinate(0d, new Deterministic(0d)) },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic);

        /// <summary>
        /// Backing field for <see cref="EvaluationAge"/> — runtime-only external state.
        /// </summary>
        private double _evaluationAge;

        /// <summary>
        /// The raw Id attribute text of an unresolved serialized base reference, re-written
        /// verbatim on save; null when the base is live or no reference is pending.
        /// </summary>
        private string? _pendingBaseIdText;

        /// <summary>
        /// The raw Name attribute text of an unresolved serialized base reference, re-written
        /// verbatim on save; null when the base is live or no reference is pending.
        /// </summary>
        private string? _pendingBaseName;

        /// <summary>
        /// Descriptions of serialized function references that could not be resolved, reported by
        /// <see cref="Validate"/> so the precise cause is visible instead of a generic message.
        /// </summary>
        private readonly List<string> _unresolvedFunctionReferences = new List<string>();

        /// <summary>
        /// True while <see cref="BasePropertyChanged"/> is re-raising, breaking the notification
        /// feedback loop an (illegal) cyclic wrapper graph would otherwise create — the wiring
        /// must stay crash-free so <see cref="Validate"/> can report the cause.
        /// </summary>
        private bool _raisingBaseChange;

        /// <summary>
        /// The wrapped base response function whose capacity axis the deterioration law shifts.
        /// </summary>
        /// <remarks>
        /// Referenced, not owned. The setter swaps a change subscription so edits to the wrapped
        /// function re-raise as <c>BaseResponse</c> (and edits to a swapped-out function raise
        /// nothing). Any assignment clears a pending unresolved serialized reference — an explicit
        /// assignment supersedes the stored link.
        /// </remarks>
        public IResponseFunction? BaseResponse
        {
            get { return _baseResponse; }
            set
            {
                if (ReferenceEquals(_baseResponse, value)) return;

                if (_baseResponse != null) _baseResponse.PropertyChanged -= BasePropertyChanged;
                _baseResponse = value;
                if (_baseResponse != null) _baseResponse.PropertyChanged += BasePropertyChanged;

                _pendingBaseIdText = null;
                _pendingBaseName = null;
                RaisePropertyChange(nameof(BaseResponse));
            }
        }

        /// <summary>
        /// The deterioration law: strictly ascending non-negative ages in years, with a
        /// capacity-shift distribution per ordinate. Positive shifts weaken the response.
        /// </summary>
        public UncertainOrderedPairedData DeteriorationLaw
        {
            get { return _deteriorationLaw; }
            set
            {
                if (!ReferenceEquals(_deteriorationLaw, value) && value is not null)
                {
                    _deteriorationLaw = value;
                    RaisePropertyChange(nameof(DeteriorationLaw));
                }
            }
        }

        /// <summary>
        /// The age in years at which the ordinary <see cref="IResponseFunction"/> members
        /// evaluate. Zero by default — the undegraded base.
        /// </summary>
        /// <remarks>
        /// Runtime-only external state: never serialized, never part of the canonical hash, and
        /// never an influence on sampling seeds — the same content-seeded stream serves every
        /// age. A run's isolated component snapshot carries this state onto its cloned instances
        /// by function id, so the configured age governs the run it precedes; a serialization
        /// round trip alone resets it to zero. Set only while no run is sampling this function.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the age is NaN or negative.</exception>
        public double EvaluationAge
        {
            get { return _evaluationAge; }
            set
            {
                if (double.IsNaN(value) || value < 0d)
                    throw new ArgumentOutOfRangeException(nameof(value), "The evaluation age must be non-negative.");
                if (_evaluationAge != value)
                {
                    _evaluationAge = value;
                    RaisePropertyChange(nameof(EvaluationAge));
                }
            }
        }

        /// <inheritdoc/>
        public override ResponseFunctionType FunctionType => ResponseFunctionType.Deteriorating;

        /// <inheritdoc/>
        public override bool IsDeterministic =>
            (_baseResponse?.IsDeterministic ?? true)
            && _deteriorationLaw.Distribution == UnivariateDistributionType.Deterministic;

        /// <inheritdoc/>
        /// <remarks>
        /// The law's percentile column. The wrapped base consumes no column here — it draws from
        /// its own child stream, seeded from this function's seed and the base's content hash.
        /// </remarks>
        public override int SamplingDimensions => 1;

        /// <inheritdoc/>
        public override bool SupportsOrderedCurveSampling => _baseResponse?.SupportsOrderedCurveSampling ?? false;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Allocates the law's one-column percentile matrix, then re-seeds the wrapped base with
        /// <c>SeedHelpers.HashCombine(seed, base.CanonicalHash(), 0)</c> — the composite forward
        /// rule (ordinals 1 and above reserved), so renaming the base can never change results
        /// while editing its content always re-rolls its stream.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid or a parametric base's posterior cannot serve the sample size.</exception>
        public override void SetupSampler(int sampleSize, int seed, SamplingScheme scheme)
        {
            ThrowIfUnusable();
            base.SetupSampler(sampleSize, seed, scheme);

            CompositeSupport.ThrowIfPosteriorCapacityTooSmall(_baseResponse!, sampleSize, Name);
            _baseResponse!.SetupSampler(sampleSize, SeedHelpers.HashCombine(seed, _baseResponse.CanonicalHash(), 0), scheme);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating): missing axis labels; unresolved base references; no base
        /// response; a base outside the tabular/parametric allow-list; an invalid base; an empty
        /// or invalid law; negative or non-finite law ages; missing or non-finite-mean shift
        /// distributions. Warnings (advisory): a non-zero mean shift at age zero (the age-zero
        /// response then differs from the base) and base axis-label mismatches.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The deteriorating response function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The deteriorating response function does not have a specified hazard unit.");

            foreach (string reference in _unresolvedFunctionReferences)
            {
                messages.Add($"Error: The deteriorating response function '{Name}' references {reference}, which was not found.");
            }

            if (_baseResponse == null)
            {
                if (_unresolvedFunctionReferences.Count == 0)
                    messages.Add("Error: The deteriorating response function has no base response function.");
            }
            else if (!BaseIsAllowed(_baseResponse))
            {
                messages.Add($"Error: The base response function '{_baseResponse.Name}' is a {_baseResponse.GetType().Name}; a deteriorating response function can wrap only a tabular or parametric response function.");
            }
            else
            {
                if (!_baseResponse.Validate().IsValid)
                    messages.Add($"Error: The selected base response function '{_baseResponse.Name}' is invalid.");

                if (_baseResponse.SpecifiedHazard != SpecifiedHazard)
                    messages.Add($"Warning: The base response function '{_baseResponse.Name}' does not match hazard type '{SpecifiedHazard}' of the deteriorating response function.");
                if (_baseResponse.HazardUnit != HazardUnit)
                    messages.Add($"Warning: The base response function '{_baseResponse.Name}' does not match hazard unit '{HazardUnit}' of the deteriorating response function.");
            }

            bool lawUsable = true;
            if (_deteriorationLaw is null || _deteriorationLaw.Count < 1)
            {
                messages.Add("Error: The deterioration law must have at least one ordinate.");
                lawUsable = false;
            }
            else if (!_deteriorationLaw.IsValid)
            {
                messages.Add("Error: Invalid deterioration law ordinates.");
                lawUsable = false;
            }
            else
            {
                for (int i = 0; i < _deteriorationLaw.Count; i++)
                {
                    var ordinate = _deteriorationLaw[i];
                    if (!Tools.IsFinite(ordinate.X) || ordinate.X < 0d)
                    {
                        messages.Add("Error: Deterioration law ages must be finite and non-negative.");
                        lawUsable = false;
                        break;
                    }
                    if (ordinate.Y is null || !Tools.IsFinite(ordinate.Y.Mean))
                    {
                        messages.Add("Error: Every deterioration law ordinate requires a shift distribution with a finite mean.");
                        lawUsable = false;
                        break;
                    }
                }
            }

            if (lawUsable)
            {
                double meanShiftAtZero = _deteriorationLaw!.CurveSample().GetYFromX(0d);
                if (meanShiftAtZero != 0d)
                {
                    messages.Add("Warning: The deterioration law's mean shift at age zero is "
                        + meanShiftAtZero.ToString("G6", CultureInfo.InvariantCulture)
                        + "; the age-zero response will not match the base response function.");
                }
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        public override IUnivariateDistribution SampleFunction()
        {
            return SampleFunctionAtAge(_evaluationAge);
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        public override IUnivariateDistribution SampleFunction(double percentile)
        {
            return SampleFunctionAtAge(percentile, _evaluationAge);
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid or the sampler has not been set up.</exception>
        public override IUnivariateDistribution SampleFunction(int realizationIndex)
        {
            return SampleFunctionAtAge(realizationIndex, _evaluationAge);
        }

        /// <summary>
        /// Samples the mean response at an explicit age: the base's mean product on the axis
        /// shifted by the mean law at that age. Pure — <see cref="EvaluationAge"/> is untouched.
        /// </summary>
        /// <param name="age">The age in years (non-negative; held at the law's boundary outside the tabled range).</param>
        /// <returns>The mean response distribution at the age.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the age is NaN or negative.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        public IUnivariateDistribution SampleFunctionAtAge(double age)
        {
            ThrowIfInvalidAge(age);
            ThrowIfUnusable();
            return WrapShifted(_baseResponse!.SampleFunction(), MeanShiftAt(age));
        }

        /// <summary>
        /// Samples the response at a knowledge-uncertainty percentile and an explicit age: one
        /// percentile drives the base AND the law co-monotonically. Pure —
        /// <see cref="EvaluationAge"/> is untouched.
        /// </summary>
        /// <param name="percentile">The percentile in (0, 1).</param>
        /// <param name="age">The age in years (non-negative; held at the law's boundary outside the tabled range).</param>
        /// <returns>The sampled response distribution at the age.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the age is NaN or negative.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        public IUnivariateDistribution SampleFunctionAtAge(double percentile, double age)
        {
            ThrowIfInvalidAge(age);
            ThrowIfUnusable();
            double shift = RequireFiniteShift(_deteriorationLaw.CurveSample(percentile).GetYFromX(age));
            return WrapShifted(_baseResponse!.SampleFunction(percentile), shift);
        }

        /// <summary>
        /// Samples the response for a realization at an explicit age: the base at its own child
        /// stream's realization, shifted by the law at this function's sampled percentile for the
        /// realization — so the SAME stream serves every age, and realization i carries one state
        /// of knowledge across the whole life cycle. Pure — <see cref="EvaluationAge"/> is
        /// untouched.
        /// </summary>
        /// <param name="realizationIndex">The realization index in [0, sample size).</param>
        /// <param name="age">The age in years (non-negative; held at the law's boundary outside the tabled range).</param>
        /// <returns>The sampled response distribution at the age.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the age is NaN or negative.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid or the sampler has not been set up.</exception>
        public IUnivariateDistribution SampleFunctionAtAge(int realizationIndex, double age)
        {
            ThrowIfInvalidAge(age);
            ThrowIfUnusable();
            double shift = RequireFiniteShift(_deteriorationLaw.CurveSample(Percentile(realizationIndex, 0)).GetYFromX(age));
            return WrapShifted(_baseResponse!.SampleFunction(realizationIndex), shift);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The base's mean curve with the hazard ordinates shifted down by the mean law at
        /// <see cref="EvaluationAge"/> — the curve form of evaluating the base at h + Δ. The
        /// engine's evaluation path is the distribution trio; the curve trio is a reporting
        /// surface.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        /// <exception cref="NotSupportedException">Thrown when the base emits no ordered curves.</exception>
        public override OrderedPairedData SampleResponseFunction()
        {
            ThrowIfUnusable();
            return ShiftCurve(_baseResponse!.SampleResponseFunction(), MeanShiftAt(_evaluationAge));
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        /// <exception cref="NotSupportedException">Thrown when the base emits no ordered curves.</exception>
        public override OrderedPairedData SampleResponseFunction(double percentile)
        {
            ThrowIfUnusable();
            double shift = RequireFiniteShift(_deteriorationLaw.CurveSample(percentile).GetYFromX(_evaluationAge));
            return ShiftCurve(_baseResponse!.SampleResponseFunction(percentile), shift);
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid or the sampler has not been set up.</exception>
        /// <exception cref="NotSupportedException">Thrown when the base emits no ordered curves.</exception>
        public override OrderedPairedData SampleResponseFunction(int realizationIndex)
        {
            ThrowIfUnusable();
            double shift = RequireFiniteShift(_deteriorationLaw.CurveSample(Percentile(realizationIndex, 0)).GetYFromX(_evaluationAge));
            return ShiftCurve(_baseResponse!.SampleResponseFunction(realizationIndex), shift);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Delegates to the base: an axis translation preserves monotonicity exactly.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        public override bool IsMonotonic()
        {
            ThrowIfUnusable();
            return _baseResponse!.IsMonotonic();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The base's minimum hazard minus the mean shift at <see cref="EvaluationAge"/> — the
        /// hazard where the shifted evaluation reaches the base's own lower table edge.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        public override double MinHazard()
        {
            ThrowIfUnusable();
            return _baseResponse!.MinHazard() - MeanShiftAt(_evaluationAge);
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        public override double MaxHazard()
        {
            ThrowIfUnusable();
            return _baseResponse!.MaxHazard() - MeanShiftAt(_evaluationAge);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Delegates to the base: an axis shift moves where probabilities occur, never their
        /// range.
        /// </remarks>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        public override double MinProbability()
        {
            ThrowIfUnusable();
            return _baseResponse!.MinProbability();
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        public override double MaxProbability()
        {
            ThrowIfUnusable();
            return _baseResponse!.MaxProbability();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Delegates to the wrapped base — the age-zero summary, matching the wrapper exactly
        /// when the law's mean shift at age zero is zero. Null while no base is assigned.
        /// </remarks>
        public override UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9)
        {
            return _baseResponse?.ComputeUncertaintyResults(confidenceIntervalWidth);
        }

        #endregion

        #region Serialization

        /// <inheritdoc/>
        public override XElement ToXElement()
        {
            return ToXElement(RiskSerializationMode.SelfContained);
        }

        /// <summary>
        /// Serializes the deteriorating response in the requested mode. Owned-child order is
        /// append-only contract: the law container first, the base container second.
        /// </summary>
        /// <param name="mode">The serialization mode governing the base container child.</param>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement(RiskSerializationMode mode)
        {
            var element = new XElement(nameof(DeterioratingResponse));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);

            var law = new XElement(nameof(DeteriorationLaw));
            law.Add(_deteriorationLaw.SaveToXElement());
            element.Add(law);

            var baseContainer = new XElement(nameof(BaseResponse));
            if (_baseResponse != null)
            {
                baseContainer.Add(FunctionEntry.Write(_baseResponse, mode));
            }
            else if (_pendingBaseIdText != null || _pendingBaseName != null)
            {
                // Re-write an unresolved reference marker verbatim so a resolver-less round trip
                // is bit-equal and the stored link is never silently dropped.
                var pending = new XElement(FunctionEntry.ReferenceElementName);
                if (_pendingBaseIdText != null) pending.SetAttributeValue("Id", _pendingBaseIdText);
                if (_pendingBaseName != null) pending.SetAttributeValue("Name", _pendingBaseName);
                baseContainer.Add(pending);
            }
            element.Add(baseContainer);
            return element;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Hashes a projected identity form, never the persisted form: the law's serialized
        /// content plus the base's own canonical hash as a token attribute. Consequences by
        /// construction: the serialization mode can never move the hash; the base's metadata
        /// edits (id, name, labels) are inert while its content edits move the hash;
        /// <see cref="EvaluationAge"/> never exists on any hashed surface; and a null base
        /// projects an empty token, so an unresolved reference does not alias a resolved one.
        /// </remarks>
        public override byte[] CanonicalHash()
        {
            var identity = new XElement(nameof(DeterioratingResponse));
            identity.SetAttributeValue("BaseHash", _baseResponse == null
                ? string.Empty
                : CanonicalContentHasher.ToTokenHex(_baseResponse.CanonicalHash()));
            identity.Add(_deteriorationLaw.SaveToXElement());
            return CanonicalContentHasher.Hash(identity, CanonicalizationRules.ModelRules);
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// The message reported when a compute surface is reached with an unusable configuration.
        /// </summary>
        private const string UnusableMessage =
            "The deteriorating response configuration is invalid. Call Validate() and correct the reported errors before sampling.";

        /// <summary>
        /// Determines whether a base response function is a wrappable kind: tabular or
        /// parametric. Composite, tree, bivariate, non-failure, and nested deteriorating bases
        /// are refused (see the class remarks for the rationale).
        /// </summary>
        /// <param name="baseResponse">The candidate base.</param>
        /// <returns>True when the base kind can be wrapped.</returns>
        private static bool BaseIsAllowed(IResponseFunction baseResponse)
        {
            return baseResponse is TabularResponse || baseResponse is ParametricResponse;
        }

        /// <summary>
        /// Determines whether the law can be sampled: at least one valid ordinate with finite
        /// non-negative ages and finite-mean shift distributions.
        /// </summary>
        /// <returns>True when the law is usable.</returns>
        private bool LawIsUsable()
        {
            if (_deteriorationLaw is null || _deteriorationLaw.Count < 1 || !_deteriorationLaw.IsValid)
                return false;
            for (int i = 0; i < _deteriorationLaw.Count; i++)
            {
                var ordinate = _deteriorationLaw[i];
                if (!Tools.IsFinite(ordinate.X) || ordinate.X < 0d) return false;
                if (ordinate.Y is null || !Tools.IsFinite(ordinate.Y.Mean)) return false;
            }
            return true;
        }

        /// <summary>
        /// Throws when a compute surface is reached with an unusable configuration (the
        /// sample-time mirror of <see cref="Validate"/>'s invalidating errors).
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        private void ThrowIfUnusable()
        {
            bool usable = _baseResponse != null && BaseIsAllowed(_baseResponse) && LawIsUsable();
            if (!usable) throw new InvalidOperationException(UnusableMessage);
        }

        /// <summary>
        /// Guards an explicit-age argument.
        /// </summary>
        /// <param name="age">The age in years.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the age is NaN or negative.</exception>
        private static void ThrowIfInvalidAge(double age)
        {
            if (double.IsNaN(age) || age < 0d)
                throw new ArgumentOutOfRangeException(nameof(age), "The evaluation age must be non-negative.");
        }

        /// <summary>
        /// Resolves the mean capacity shift at an age: linear interpolation of the mean law,
        /// holding the boundary ordinate outside the tabled range. Finite whenever the law is
        /// usable.
        /// </summary>
        /// <param name="age">The age in years.</param>
        /// <returns>The mean shift Δ(age).</returns>
        private double MeanShiftAt(double age)
        {
            return _deteriorationLaw.CurveSample().GetYFromX(age);
        }

        /// <summary>
        /// Guards a sampled capacity shift: an unbounded shift distribution sampled at an extreme
        /// percentile can produce an infinite shift, which cannot shift an axis.
        /// </summary>
        /// <param name="shift">The sampled shift.</param>
        /// <returns>The shift, when finite.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the shift is not finite.</exception>
        private static double RequireFiniteShift(double shift)
        {
            if (!Tools.IsFinite(shift))
                throw new InvalidOperationException(
                    "The deterioration law produced a non-finite capacity shift. Call Validate() and correct the reported errors before sampling.");
            return shift;
        }

        /// <summary>
        /// Wraps a sampled base product in the shifted-axis view.
        /// </summary>
        /// <param name="product">The base's sampled distribution product.</param>
        /// <param name="shift">The resolved capacity shift.</param>
        /// <returns>The shifted view.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the product is not a Numerics distribution.</exception>
        private ShiftedUnivariateDistribution WrapShifted(IUnivariateDistribution product, double shift)
        {
            if (product is not UnivariateDistributionBase inner)
            {
                throw new InvalidOperationException(
                    $"The base response function '{_baseResponse!.Name}' sampled to " +
                    (product == null ? "no distribution" : $"a {product.GetType().Name}") +
                    ", which the deteriorating response cannot shift. Call Validate() and correct the reported errors before sampling.");
            }
            return new ShiftedUnivariateDistribution(inner, shift);
        }

        /// <summary>
        /// Rebuilds a sampled base curve with its hazard ordinates shifted down by the resolved
        /// shift, preserving the source curve's ordering flags.
        /// </summary>
        /// <param name="source">The base's sampled curve.</param>
        /// <param name="shift">The resolved capacity shift.</param>
        /// <returns>The age-adjusted curve.</returns>
        private static OrderedPairedData ShiftCurve(OrderedPairedData source, double shift)
        {
            var shifted = new Ordinate[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                shifted[i] = new Ordinate(source[i].X - shift, source[i].Y);
            }
            return new OrderedPairedData(shifted, source.StrictX, source.OrderX, source.StrictY, source.OrderY);
        }

        /// <summary>
        /// Re-raises a wrapped base's change notification as a change of
        /// <see cref="BaseResponse"/>. Reentrant notifications are suppressed via
        /// <see cref="_raisingBaseChange"/> so an (illegal) cyclic wrapper graph degrades to a
        /// reportable validation error instead of unbounded recursion.
        /// </summary>
        /// <param name="sender">The wrapped base.</param>
        /// <param name="e">The originating change arguments.</param>
        private void BasePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_raisingBaseChange) return;
            _raisingBaseChange = true;
            try
            {
                RaisePropertyChange(nameof(BaseResponse));
            }
            finally
            {
                _raisingBaseChange = false;
            }
        }

        #endregion
    }
}
