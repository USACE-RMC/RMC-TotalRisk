using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses.Trees
{
    /// <summary>
    /// The discriminated conditional-probability value of a tree probability node: a fixed
    /// scalar, an uncertain table, or a referenced response — optionally evaluated on a derived
    /// hazard axis through an ordered hazard-transform chain.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Without a hazard-transform chain, an uncertain table is aligned ordinate-for-ordinate to
    /// the owning tree's hazard axis and a referenced response is evaluated at the caller hazard.
    /// With a chain, the caller hazard h is mapped through the transforms in order to t(h) and the
    /// table or referenced response is authored and evaluated on the transformed axis (for
    /// example, a node keyed on overtopping depth, duration, or warning time rather than on the
    /// driving hazard) — the table is then free of the tree axis and every lookup interpolates. A
    /// bivariate response surface becomes a legal source only with a declared
    /// <see cref="BivariateAxis"/> and a non-empty chain: the tree hazard drives the declared axis
    /// and the chain supplies the other surface coordinate.
    /// </para>
    /// <para>
    /// The chain and the axis are conditional serialized content: the <c>HazardTransforms</c>
    /// child and the <c>BivariateAxis</c> attribute are written only when configured, so every
    /// source without them keeps a byte-identical serialized form, canonical identity, and seed.
    /// Configuring either is deliberate compute content that moves the identity.
    /// </para>
    /// <para>
    /// Chain application is split by evaluation mode: the mean and percentile evaluators apply the
    /// live chain internally (mean curves, or one consistent percentile), while realization-mode
    /// callers apply their own sampler-bound transform clones and evaluate through the
    /// off-axis realization entry — the aligned realization lookup is undefined under a transform
    /// and throws.
    /// </para>
    /// </remarks>
    public sealed class ProbabilitySource
    {
        /// <summary>Initializes a deterministic scalar source.</summary>
        /// <param name="probability">The conditional probability.</param>
        public ProbabilitySource(double probability)
        {
            Kind = ProbabilitySourceKind.DeterministicScalar;
            ScalarProbability = probability;
        }

        /// <summary>Initializes an uncertain tabular source.</summary>
        /// <param name="table">The probability table aligned to the owning tree hazards.</param>
        /// <exception cref="ArgumentNullException">Thrown when the table is null.</exception>
        public ProbabilitySource(UncertainOrderedPairedData table)
        {
            Table = table ?? throw new ArgumentNullException(nameof(table));
            Kind = ProbabilitySourceKind.UncertainTabular;
        }

        /// <summary>Initializes a referenced-response source.</summary>
        /// <param name="responseFunction">The response evaluated at each tree hazard.</param>
        /// <exception cref="ArgumentNullException">Thrown when the function is null.</exception>
        public ProbabilitySource(IResponseFunction responseFunction)
        {
            ResponseFunction = responseFunction ?? throw new ArgumentNullException(nameof(responseFunction));
            Kind = ProbabilitySourceKind.ResponseFunctionReference;
        }

        /// <summary>Initializes an uncertain tabular source authored on a transformed hazard axis.</summary>
        /// <param name="table">The probability table authored at transformed-hazard ordinates.</param>
        /// <param name="hazardTransforms">The ordered transforms mapping the tree hazard onto the table's axis.</param>
        /// <exception cref="ArgumentNullException">Thrown when the table or the transform list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a transform entry is null.</exception>
        public ProbabilitySource(UncertainOrderedPairedData table, IReadOnlyList<ITransformFunction> hazardTransforms)
            : this(table)
        {
            _hazardTransforms = CopyTransforms(hazardTransforms);
        }

        /// <summary>Initializes a referenced-response source evaluated on a transformed hazard axis.</summary>
        /// <param name="responseFunction">The response evaluated at the transformed tree hazard.</param>
        /// <param name="hazardTransforms">The ordered transforms mapping the tree hazard onto the response's axis.</param>
        /// <exception cref="ArgumentNullException">Thrown when the function or the transform list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a transform entry is null.</exception>
        public ProbabilitySource(IResponseFunction responseFunction, IReadOnlyList<ITransformFunction> hazardTransforms)
            : this(responseFunction)
        {
            _hazardTransforms = CopyTransforms(hazardTransforms);
        }

        /// <summary>
        /// Initializes a bivariate-surface source: the tree hazard drives the declared surface
        /// axis and the transform chain supplies the other surface coordinate.
        /// </summary>
        /// <param name="responseFunction">The bivariate response whose surface is evaluated jointly.</param>
        /// <param name="bivariateAxis">The surface axis the tree hazard drives.</param>
        /// <param name="hazardTransforms">The ordered transforms supplying the other surface coordinate.</param>
        /// <exception cref="ArgumentNullException">Thrown when the function or the transform list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a transform entry is null.</exception>
        public ProbabilitySource(IResponseFunction responseFunction, BivariateSourceAxis bivariateAxis,
            IReadOnlyList<ITransformFunction> hazardTransforms)
            : this(responseFunction)
        {
            BivariateAxis = bivariateAxis;
            _hazardTransforms = CopyTransforms(hazardTransforms);
        }

        /// <summary>Restores a probability source from its serialized form.</summary>
        /// <param name="xElement">The serialized probability source.</param>
        /// <param name="resolver">The optional function resolver for by-reference content.</param>
        /// <param name="ownerName">The owning tree response name used in diagnostics.</param>
        /// <param name="treeKind">The hyphenated tree-kind diagnostic label (for example <c>event-tree</c>).</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the source kind or content is malformed.</exception>
        internal ProbabilitySource(XElement xElement, IRiskFunctionResolver? resolver, string ownerName, string treeKind)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            Kind = SerializationUtilities.ReadEnum(xElement, nameof(Kind), ProbabilitySourceKind.DeterministicScalar);
            switch (Kind)
            {
                case ProbabilitySourceKind.DeterministicScalar:
                    ScalarProbability = SerializationUtilities.ReadDouble(xElement, nameof(ScalarProbability));
                    break;
                case ProbabilitySourceKind.UncertainTabular:
                    var tableElement = xElement.Element(nameof(UncertainOrderedPairedData));
                    if (tableElement == null)
                        throw new InvalidOperationException($"An uncertain {treeKind} probability source has no serialized table.");
                    Table = new UncertainOrderedPairedData(tableElement)
                    {
                        OrderX = SortOrder.Ascending,
                        OrderY = SortOrder.None,
                        StrictX = true,
                        StrictY = false,
                    };
                    Table.Validate();
                    break;
                case ProbabilitySourceKind.ResponseFunctionReference:
                    var child = xElement.Element("Function")?.Elements().FirstOrDefault();
                    if (child == null)
                        throw new InvalidOperationException($"A response-backed {treeKind} probability source has no serialized function.");
                    ResponseFunction = FunctionEntry.Read<IResponseFunction>(
                        child, resolver, c => RiskFunctionFactory.CreateFromXElement(c, resolver), ownerName,
                        $"The {treeKind} response '{ownerName}'", "response function", _unresolvedReferences);
                    break;
                default:
                    throw new InvalidOperationException($"The {treeKind} probability source kind '{Kind}' is not supported.");
            }

            // The chain and the axis are read for every kind so a malformed pairing (for example
            // a chain on a scalar source) round-trips shape-preserving and fails Validate loudly
            // instead of being silently dropped.
            if (xElement.Attribute(nameof(BivariateAxis)) != null)
                BivariateAxis = SerializationUtilities.ReadEnum(xElement, nameof(BivariateAxis), BivariateSourceAxis.Primary);
            var chainElement = xElement.Element(nameof(HazardTransforms));
            if (chainElement != null)
            {
                var entries = new List<ITransformFunction?>();
                foreach (var entryElement in chainElement.Elements())
                {
                    entries.Add(FunctionEntry.Read<ITransformFunction>(
                        entryElement, resolver, c => RiskFunctionFactory.CreateFromXElement(c, resolver), ownerName,
                        $"The {treeKind} response '{ownerName}'", "hazard transform function", _unresolvedReferences));
                }
                _hazardTransforms = entries.ToArray();
            }
        }

        /// <summary>The represented source kind.</summary>
        public ProbabilitySourceKind Kind { get; }

        /// <summary>The scalar probability when <see cref="Kind"/> is deterministic scalar.</summary>
        public double? ScalarProbability { get; }

        /// <summary>The probability table when <see cref="Kind"/> is uncertain tabular — aligned to the owning tree hazards without a transform chain, authored on the transformed axis with one.</summary>
        public UncertainOrderedPairedData? Table { get; }

        /// <summary>The referenced response when <see cref="Kind"/> is response-function reference.</summary>
        public IResponseFunction? ResponseFunction { get; }

        /// <summary>
        /// The ordered hazard-transform chain mapping the tree hazard onto this source's
        /// evaluation axis; empty when the source evaluates on the tree axis directly. An entry is
        /// null only when a serialized by-reference transform could not be resolved (reported by
        /// <see cref="Validate"/>).
        /// </summary>
        public IReadOnlyList<ITransformFunction?> HazardTransforms => _hazardTransforms;

        /// <summary>
        /// The bivariate surface axis the tree hazard drives, or null for a univariate source.
        /// Legal only on a bivariate referenced response carrying a non-empty transform chain.
        /// </summary>
        public BivariateSourceAxis? BivariateAxis { get; }

        /// <summary>The stored hazard-transform chain; empty when the source has none.</summary>
        private readonly ITransformFunction?[] _hazardTransforms = Array.Empty<ITransformFunction?>();

        /// <summary>Unresolved serialized function references retained for validation.</summary>
        private readonly List<string> _unresolvedReferences = new List<string>();

        /// <summary>Whether a hazard-transform chain is configured.</summary>
        internal bool HasHazardTransforms => _hazardTransforms.Length > 0;

        /// <summary>The summed sampler dimensions of the hazard-transform chain entries.</summary>
        internal int HazardTransformDimensions
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _hazardTransforms.Length; i++)
                    total += _hazardTransforms[i]?.SamplingDimensions ?? 0;
                return total;
            }
        }

        /// <summary>
        /// Whether every hazard-transform chain entry is deterministic — the chain-only fold the
        /// plan compilers combine with a nested tree plan's own determinism, since a nested source
        /// must be inspected on the shared compile stack rather than through
        /// <see cref="IsDeterministic"/>.
        /// </summary>
        internal bool HazardTransformsAreDeterministic
        {
            get
            {
                for (int i = 0; i < _hazardTransforms.Length; i++)
                {
                    if (!(_hazardTransforms[i]?.IsDeterministic ?? false)) return false;
                }
                return true;
            }
        }

        /// <summary>Whether the source carries no knowledge uncertainty.</summary>
        internal bool IsDeterministic
        {
            get
            {
                bool deterministic = Kind switch
                {
                    ProbabilitySourceKind.DeterministicScalar => true,
                    ProbabilitySourceKind.UncertainTabular => Table!.Distribution == UnivariateDistributionType.Deterministic,
                    ProbabilitySourceKind.ResponseFunctionReference => ResponseFunction?.IsDeterministic ?? false,
                    _ => false,
                };
                if (!deterministic) return false;
                for (int i = 0; i < _hazardTransforms.Length; i++)
                {
                    if (!(_hazardTransforms[i]?.IsDeterministic ?? false)) return false;
                }
                return true;
            }
        }

        /// <summary>The local and recursively referenced sampler dimensions, including the hazard-transform chain.</summary>
        internal int SamplingDimensions
        {
            get
            {
                int dimensions = Kind switch
                {
                    ProbabilitySourceKind.DeterministicScalar => 0,
                    ProbabilitySourceKind.UncertainTabular => 1,
                    ProbabilitySourceKind.ResponseFunctionReference => ResponseFunction?.SamplingDimensions ?? 0,
                    _ => 0,
                };
                return dimensions + HazardTransformDimensions;
            }
        }

        /// <summary>Validates the source against the owning tree's hazard axis.</summary>
        /// <param name="hazards">The owning tree hazards.</param>
        /// <param name="nodeLabel">The node diagnostic label, including the node kind and quoted display name.</param>
        /// <param name="treeKind">The hyphenated tree-kind diagnostic label (for example <c>event-tree</c>).</param>
        /// <param name="ownerHazard">The owning tree's hazard-type label, for transform-chain continuity warnings; null skips the owner-side check.</param>
        /// <returns>Deterministically ordered validation messages.</returns>
        internal List<string> Validate(IReadOnlyList<double> hazards, string nodeLabel, string treeKind,
            string? ownerHazard = null)
        {
            var messages = new List<string>();
            if (Kind == ProbabilitySourceKind.DeterministicScalar)
            {
                double value = ScalarProbability!.Value;
                if (!double.IsFinite(value) || value < 0d || value > 1d)
                    messages.Add($"Error: {nodeLabel} has a scalar probability outside [0, 1].");
                if (HasHazardTransforms)
                    messages.Add($"Error: {nodeLabel} carries hazard transforms, which a scalar probability source cannot use.");
                if (BivariateAxis != null)
                    messages.Add($"Error: {nodeLabel} declares a bivariate surface axis, which only a bivariate response-function source can use.");
                return messages;
            }

            if (Kind == ProbabilitySourceKind.UncertainTabular)
            {
                if (BivariateAxis != null)
                    messages.Add($"Error: {nodeLabel} declares a bivariate surface axis, which only a bivariate response-function source can use.");
                if (!HasHazardTransforms)
                {
                    if (ReferenceEquals(Table, null) || !Table.IsValid || Table.Count != hazards.Count)
                    {
                        messages.Add($"Error: {nodeLabel} must have one valid probability-table ordinate per {treeKind} hazard level.");
                        return messages;
                    }
                    for (int i = 0; i < hazards.Count; i++)
                    {
                        if (Table[i].X != hazards[i])
                        {
                            messages.Add($"Error: {nodeLabel} probability-table hazards are not aligned to the {treeKind.Replace('-', ' ')}.");
                            break;
                        }
                        if (ReferenceEquals(Table[i].Y, null) || Table[i].Y!.Minimum < 0d || Table[i].Y!.Maximum > 1d)
                        {
                            messages.Add($"Error: {nodeLabel} probability distributions must remain within [0, 1].");
                            break;
                        }
                    }
                    return messages;
                }

                // A transformed table is authored on the transformed axis: the ordinate count is
                // free of the tree axis and every lookup interpolates, so only table validity and
                // the probability range are enforced.
                if (ReferenceEquals(Table, null) || !Table.IsValid || Table.Count == 0)
                {
                    messages.Add($"Error: {nodeLabel} must have a valid probability table.");
                    return messages;
                }
                for (int i = 0; i < Table.Count; i++)
                {
                    if (ReferenceEquals(Table[i].Y, null) || Table[i].Y!.Minimum < 0d || Table[i].Y!.Maximum > 1d)
                    {
                        messages.Add($"Error: {nodeLabel} probability distributions must remain within [0, 1].");
                        break;
                    }
                }
                ValidateHazardTransforms(messages, nodeLabel, ownerHazard, targetLabel: null);
                return messages;
            }

            foreach (string reference in _unresolvedReferences)
                messages.Add($"Error: {nodeLabel} references {reference}, which was not found.");
            if (ResponseFunction == null && _unresolvedReferences.Count == 0)
                messages.Add($"Error: {nodeLabel} has no referenced response function.");
            else if (ResponseFunction is IBivariateResponseFunction bivariateResponse)
            {
                if (BivariateAxis == null)
                {
                    // The scope guard: evaluating a bivariate response at a single tree hazard would
                    // silently collapse its secondary hazard through the stored weights.
                    messages.Add($"Error: {nodeLabel} references bivariate response function '{ResponseFunction.Name}'; tree probability sources cannot reference bivariate response functions.");
                }
                else if (!HasHazardTransforms)
                {
                    messages.Add($"Error: {nodeLabel} declares a bivariate surface axis but carries no hazard transforms to supply the other surface coordinate.");
                }
                else
                {
                    var validation = ResponseFunction.Validate();
                    foreach (string message in validation.ValidationMessages.Where(message =>
                        message.StartsWith("Error:", StringComparison.Ordinal)))
                    {
                        messages.Add(
                            $"Error: {nodeLabel} references invalid response function " +
                            $"'{ResponseFunction.Name}': {message.Substring("Error:".Length).Trim()}");
                    }
                    string drivenLabel = BivariateAxis == BivariateSourceAxis.Primary
                        ? bivariateResponse.SpecifiedHazard
                        : bivariateResponse.SecondarySpecifiedHazard;
                    AddContinuityWarning(messages,
                        $"{nodeLabel} bivariate surface axis expects hazard '{drivenLabel}' but the tree supplies",
                        ownerHazard, drivenLabel);
                }
            }
            else if (ResponseFunction is DeterioratingResponse)
            {
                // The scope guard: a tree source has no age surface to drive, so the reference
                // would silently evaluate the wrapper at its current evaluation age.
                messages.Add($"Error: {nodeLabel} references deteriorating response function '{ResponseFunction.Name}'; tree probability sources cannot reference deteriorating response functions.");
            }
            else if (ResponseFunction != null)
            {
                if (BivariateAxis != null)
                    messages.Add($"Error: {nodeLabel} declares a bivariate surface axis but references univariate response function '{ResponseFunction.Name}'.");
                var validation = ResponseFunction.Validate();
                foreach (string message in validation.ValidationMessages.Where(message =>
                    message.StartsWith("Error:", StringComparison.Ordinal)))
                {
                    messages.Add(
                        $"Error: {nodeLabel} references invalid response function " +
                        $"'{ResponseFunction.Name}': {message.Substring("Error:".Length).Trim()}");
                }
            }

            if (HasHazardTransforms)
            {
                string? targetLabel = null;
                if (ResponseFunction is IBivariateResponseFunction bivariateTarget && BivariateAxis != null)
                {
                    targetLabel = BivariateAxis == BivariateSourceAxis.Primary
                        ? bivariateTarget.SecondarySpecifiedHazard
                        : bivariateTarget.SpecifiedHazard;
                }
                else if (ResponseFunction != null && BivariateAxis == null)
                {
                    targetLabel = ResponseFunction.SpecifiedHazard;
                }
                ValidateHazardTransforms(messages, nodeLabel, ownerHazard, targetLabel);
            }
            return messages;
        }

        /// <summary>Validates the hazard-transform chain entries and their axis-label continuity.</summary>
        /// <param name="messages">The message sink.</param>
        /// <param name="nodeLabel">The node diagnostic label.</param>
        /// <param name="ownerHazard">The owning tree's hazard-type label, or null to skip the owner-side check.</param>
        /// <param name="targetLabel">The hazard-type label the chain output feeds, or null when the target carries no label.</param>
        private void ValidateHazardTransforms(List<string> messages, string nodeLabel, string? ownerHazard,
            string? targetLabel)
        {
            string? previousLabel = ownerHazard;
            for (int i = 0; i < _hazardTransforms.Length; i++)
            {
                var transform = _hazardTransforms[i];
                if (transform == null)
                {
                    // Unresolved by-reference entries already produced an unresolved-reference
                    // error; a null entry with no pending reference is a defect in the caller.
                    if (_unresolvedReferences.Count == 0)
                        messages.Add($"Error: {nodeLabel} hazard transform at position {i} has not been defined.");
                    previousLabel = null;
                    continue;
                }
                if (transform is IBivariateTransformFunction)
                {
                    messages.Add($"Error: {nodeLabel} hazard transform '{transform.Name}' is bivariate; a probability-source hazard-transform chain accepts univariate transforms only.");
                    previousLabel = transform.TransformedHazard;
                    continue;
                }
                var validation = transform.Validate();
                foreach (string message in validation.ValidationMessages.Where(message =>
                    message.StartsWith("Error:", StringComparison.Ordinal)))
                {
                    messages.Add(
                        $"Error: {nodeLabel} hazard transform '{transform.Name}' is invalid: " +
                        $"{message.Substring("Error:".Length).Trim()}");
                }
                AddContinuityWarning(messages,
                    $"{nodeLabel} hazard transform '{transform.Name}' expects hazard '{transform.SpecifiedHazard}' but receives",
                    transform.SpecifiedHazard, previousLabel);
                previousLabel = transform.TransformedHazard;
            }
            AddContinuityWarning(messages,
                $"{nodeLabel} hazard-transform chain produces '{previousLabel}' but the source expects",
                targetLabel, previousLabel);
        }

        /// <summary>Adds one axis-label continuity warning when both labels are present and differ.</summary>
        /// <param name="messages">The message sink.</param>
        /// <param name="prefix">The message prefix, ending just before the quoted actual label.</param>
        /// <param name="expected">The label the receiving side expects.</param>
        /// <param name="actual">The label the supplying side produces.</param>
        private static void AddContinuityWarning(List<string> messages, string prefix, string? expected, string? actual)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual)) return;
            if (string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            messages.Add($"Warning: {prefix} '{actual}'.");
        }

        /// <summary>Evaluates the source's mean probability.</summary>
        /// <param name="hazard">The current hazard value.</param>
        /// <param name="hazardIndex">The aligned tree-hazard index, ignored when a transform chain is configured.</param>
        /// <returns>The raw conditional probability.</returns>
        internal double EvaluateMean(double hazard, int hazardIndex)
        {
            if (HasHazardTransforms || BivariateAxis != null) return EvaluateTransformedMean(hazard);
            return Kind switch
            {
                ProbabilitySourceKind.DeterministicScalar => ScalarProbability!.Value,
                ProbabilitySourceKind.UncertainTabular => Table!.CurveSample()[hazardIndex].Y,
                ProbabilitySourceKind.ResponseFunctionReference => ResponseFunction!.SampleFunction().CDF(hazard),
                _ => throw new InvalidOperationException($"Unsupported probability source kind '{Kind}'."),
            };
        }

        /// <summary>Evaluates the source at one co-monotonic percentile.</summary>
        /// <param name="hazard">The current hazard value.</param>
        /// <param name="hazardIndex">The aligned tree-hazard index, ignored when a transform chain is configured.</param>
        /// <param name="percentile">The knowledge percentile.</param>
        /// <returns>The raw conditional probability.</returns>
        internal double EvaluatePercentile(double hazard, int hazardIndex, double percentile)
        {
            if (HasHazardTransforms || BivariateAxis != null) return EvaluateTransformedPercentile(hazard, percentile);
            return Kind switch
            {
                ProbabilitySourceKind.DeterministicScalar => ScalarProbability!.Value,
                ProbabilitySourceKind.UncertainTabular => Table!.CurveSample(percentile)[hazardIndex].Y,
                ProbabilitySourceKind.ResponseFunctionReference => ResponseFunction!.SampleFunction(percentile).CDF(hazard),
                _ => throw new InvalidOperationException($"Unsupported probability source kind '{Kind}'."),
            };
        }

        /// <summary>Evaluates the source for one sampler realization at an aligned tree hazard.</summary>
        /// <param name="hazard">The current hazard value.</param>
        /// <param name="hazardIndex">The aligned tree-hazard index.</param>
        /// <param name="realizationIndex">The realization index.</param>
        /// <param name="localPercentile">The local table percentile, or zero for a nonlocal source.</param>
        /// <returns>The raw conditional probability.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a transform chain or bivariate axis is configured: realization-mode chain
        /// values come from the caller's sampler-bound transform clones, so the caller transforms
        /// the hazard itself and evaluates through <see cref="EvaluateRealizationAtHazard"/>.
        /// </exception>
        internal double EvaluateRealization(double hazard, int hazardIndex, int realizationIndex, double localPercentile)
        {
            if (HasHazardTransforms || BivariateAxis != null)
            {
                throw new InvalidOperationException(
                    "A transformed probability source evaluates realizations at the caller-transformed " +
                    "hazard through EvaluateRealizationAtHazard; the aligned-index lookup is undefined " +
                    "under a hazard transform.");
            }
            return Kind switch
            {
                ProbabilitySourceKind.DeterministicScalar => ScalarProbability!.Value,
                ProbabilitySourceKind.UncertainTabular => Table!.CurveSample(localPercentile)[hazardIndex].Y,
                ProbabilitySourceKind.ResponseFunctionReference => ResponseFunction!.SampleFunction(realizationIndex).CDF(hazard),
                _ => throw new InvalidOperationException($"Unsupported probability source kind '{Kind}'."),
            };
        }

        /// <summary>Evaluates the mean source at a caller hazard not aligned to this source's owner axis.</summary>
        /// <param name="hazard">The caller hazard value.</param>
        /// <returns>The raw conditional probability.</returns>
        internal double EvaluateMeanAtHazard(double hazard)
        {
            if (HasHazardTransforms || BivariateAxis != null) return EvaluateTransformedMean(hazard);
            return Kind switch
            {
                ProbabilitySourceKind.DeterministicScalar => ScalarProbability!.Value,
                ProbabilitySourceKind.UncertainTabular => Table!.CurveSample().GetYFromX(hazard),
                ProbabilitySourceKind.ResponseFunctionReference => ResponseFunction!.SampleFunction().CDF(hazard),
                _ => throw new InvalidOperationException($"Unsupported probability source kind '{Kind}'."),
            };
        }

        /// <summary>Evaluates a percentile source at a caller hazard not aligned to this source's owner axis.</summary>
        /// <param name="hazard">The caller hazard value.</param>
        /// <param name="percentile">The knowledge percentile.</param>
        /// <returns>The raw conditional probability.</returns>
        internal double EvaluatePercentileAtHazard(double hazard, double percentile)
        {
            if (HasHazardTransforms || BivariateAxis != null) return EvaluateTransformedPercentile(hazard, percentile);
            return Kind switch
            {
                ProbabilitySourceKind.DeterministicScalar => ScalarProbability!.Value,
                ProbabilitySourceKind.UncertainTabular => Table!.CurveSample(percentile).GetYFromX(hazard),
                ProbabilitySourceKind.ResponseFunctionReference => ResponseFunction!.SampleFunction(percentile).CDF(hazard),
                _ => throw new InvalidOperationException($"Unsupported probability source kind '{Kind}'."),
            };
        }

        /// <summary>
        /// Evaluates a realization at a caller hazard not aligned to this source's owner axis. A
        /// realization-mode caller with a configured transform chain applies its sampler-bound
        /// transform clones first and passes the transformed hazard here.
        /// </summary>
        /// <param name="hazard">The caller hazard value — already transformed by the caller when a chain is configured.</param>
        /// <param name="realizationIndex">The realization index.</param>
        /// <param name="localPercentile">The local table percentile, or zero for a nonlocal source.</param>
        /// <returns>The raw conditional probability.</returns>
        internal double EvaluateRealizationAtHazard(double hazard, int realizationIndex, double localPercentile)
        {
            return Kind switch
            {
                ProbabilitySourceKind.DeterministicScalar => ScalarProbability!.Value,
                ProbabilitySourceKind.UncertainTabular => Table!.CurveSample(localPercentile).GetYFromX(hazard),
                ProbabilitySourceKind.ResponseFunctionReference => ResponseFunction!.SampleFunction(realizationIndex).CDF(hazard),
                _ => throw new InvalidOperationException($"Unsupported probability source kind '{Kind}'."),
            };
        }

        /// <summary>Evaluates the mean of a transformed or bivariate-surface source at a caller hazard.</summary>
        /// <param name="hazard">The caller hazard value.</param>
        /// <returns>The raw conditional probability.</returns>
        private double EvaluateTransformedMean(double hazard)
        {
            ThrowIfTransformStateInvalid();
            double transformed = ApplyHazardTransformsMean(hazard);
            if (BivariateAxis != null)
                return EvaluateBivariateSurfaceOn((IBivariateResponseFunction)ResponseFunction!, hazard, transformed);
            return Kind switch
            {
                ProbabilitySourceKind.UncertainTabular => Table!.CurveSample().GetYFromX(transformed),
                ProbabilitySourceKind.ResponseFunctionReference => ResponseFunction!.SampleFunction().CDF(transformed),
                _ => throw new InvalidOperationException($"Unsupported probability source kind '{Kind}'."),
            };
        }

        /// <summary>Evaluates one percentile of a transformed or bivariate-surface source at a caller hazard.</summary>
        /// <param name="hazard">The caller hazard value.</param>
        /// <param name="percentile">The knowledge percentile applied consistently to every chain entry and the target.</param>
        /// <returns>The raw conditional probability.</returns>
        private double EvaluateTransformedPercentile(double hazard, double percentile)
        {
            ThrowIfTransformStateInvalid();
            double transformed = ApplyHazardTransformsPercentile(hazard, percentile);
            if (BivariateAxis != null)
                return EvaluateBivariateSurfaceOn((IBivariateResponseFunction)ResponseFunction!, hazard, transformed);
            return Kind switch
            {
                ProbabilitySourceKind.UncertainTabular => Table!.CurveSample(percentile).GetYFromX(transformed),
                ProbabilitySourceKind.ResponseFunctionReference => ResponseFunction!.SampleFunction(percentile).CDF(transformed),
                _ => throw new InvalidOperationException($"Unsupported probability source kind '{Kind}'."),
            };
        }

        /// <summary>Throws when the transform chain or bivariate axis is configured on an unsupported shape.</summary>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid.</exception>
        private void ThrowIfTransformStateInvalid()
        {
            if (Kind == ProbabilitySourceKind.DeterministicScalar)
            {
                throw new InvalidOperationException(
                    "A scalar probability source cannot carry hazard transforms or a bivariate axis. " +
                    "Call Validate() and correct the reported errors before evaluating.");
            }
            if (BivariateAxis == null) return;
            if (Kind != ProbabilitySourceKind.ResponseFunctionReference
                || ResponseFunction is not IBivariateResponseFunction)
            {
                throw new InvalidOperationException(
                    "The probability source declares a bivariate surface axis but does not reference a " +
                    "bivariate response function. Call Validate() and correct the reported errors before evaluating.");
            }
            if (!HasHazardTransforms)
            {
                throw new InvalidOperationException(
                    "The probability source declares a bivariate surface axis but carries no hazard " +
                    "transforms to supply the other surface coordinate. Call Validate() and correct the " +
                    "reported errors before evaluating.");
            }
        }

        /// <summary>Maps a caller hazard through the live chain's mean transform curves.</summary>
        /// <param name="hazard">The caller hazard value.</param>
        /// <returns>The transformed hazard.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a chain entry is missing or unresolved.</exception>
        private double ApplyHazardTransformsMean(double hazard)
        {
            for (int i = 0; i < _hazardTransforms.Length; i++)
            {
                var transform = _hazardTransforms[i] ?? throw new InvalidOperationException(
                    $"The probability source hazard transform at position {i} is missing or unresolved. " +
                    "Call Validate() and correct the reported errors before evaluating.");
                hazard = transform.SampleFunction().Function(hazard);
            }
            return hazard;
        }

        /// <summary>Maps a caller hazard through the live chain at one consistent percentile.</summary>
        /// <param name="hazard">The caller hazard value.</param>
        /// <param name="percentile">The knowledge percentile.</param>
        /// <returns>The transformed hazard.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a chain entry is missing or unresolved.</exception>
        private double ApplyHazardTransformsPercentile(double hazard, double percentile)
        {
            for (int i = 0; i < _hazardTransforms.Length; i++)
            {
                var transform = _hazardTransforms[i] ?? throw new InvalidOperationException(
                    $"The probability source hazard transform at position {i} is missing or unresolved. " +
                    "Call Validate() and correct the reported errors before evaluating.");
                hazard = transform.SampleFunction(percentile).Function(hazard);
            }
            return hazard;
        }

        /// <summary>
        /// Maps a caller hazard through sampler-bound transform-clone curves for one realization —
        /// the realization-mode counterpart of the internal chain application, shared by both tree
        /// kinds' dispatch seats.
        /// </summary>
        /// <param name="transforms">The sampler-bound transform clones, in chain order.</param>
        /// <param name="hazard">The caller hazard value.</param>
        /// <param name="realizationIndex">The realization index.</param>
        /// <returns>The transformed hazard.</returns>
        internal static double ApplyTransformRealizations(IReadOnlyList<ITransformFunction> transforms,
            double hazard, int realizationIndex)
        {
            for (int i = 0; i < transforms.Count; i++)
                hazard = transforms[i].SampleFunction(realizationIndex).Function(hazard);
            return hazard;
        }

        /// <summary>
        /// Evaluates a bivariate response surface with the tree hazard on the declared axis and
        /// the transformed value on the other, through the surface's clamped evaluation.
        /// </summary>
        /// <param name="surface">The surface to evaluate — the live referenced response, or a caller's sampler-bound clone.</param>
        /// <param name="treeHazard">The tree hazard driving the declared axis.</param>
        /// <param name="transformedCoordinate">The chain-supplied value for the other axis.</param>
        /// <returns>The interpolated failure probability in [0, 1].</returns>
        internal double EvaluateBivariateSurfaceOn(IBivariateResponseFunction surface, double treeHazard,
            double transformedCoordinate)
        {
            return BivariateAxis == BivariateSourceAxis.Primary
                ? surface.SurfaceProbability(treeHazard, transformedCoordinate)
                : surface.SurfaceProbability(transformedCoordinate, treeHazard);
        }

        /// <summary>Serializes the discriminated source in the requested function-reference mode.</summary>
        /// <param name="mode">The serialization mode.</param>
        /// <returns>The serialized source.</returns>
        internal XElement ToXElement(RiskSerializationMode mode)
        {
            var element = new XElement(nameof(ProbabilitySource));
            element.SetAttributeValue(nameof(Kind), Kind.ToString());
            switch (Kind)
            {
                case ProbabilitySourceKind.DeterministicScalar:
                    element.SetAttributeValue(nameof(ScalarProbability), SerializationUtilities.FormatDouble(ScalarProbability!.Value));
                    break;
                case ProbabilitySourceKind.UncertainTabular:
                    if (!ReferenceEquals(Table, null)) element.Add(Table.SaveToXElement());
                    break;
                case ProbabilitySourceKind.ResponseFunctionReference:
                    if (ResponseFunction != null)
                        element.Add(new XElement("Function", FunctionEntry.Write(ResponseFunction, mode)));
                    break;
            }

            // Conditional presence: the axis attribute and the chain child exist only when
            // configured, so every source without them keeps its byte-identical serialized form.
            if (BivariateAxis != null)
                element.SetAttributeValue(nameof(BivariateAxis), BivariateAxis.Value.ToString());
            if (_hazardTransforms.Length > 0)
            {
                var chain = new XElement(nameof(HazardTransforms));
                foreach (var transform in _hazardTransforms)
                {
                    if (transform != null) chain.Add(FunctionEntry.Write(transform, mode));
                }
                element.Add(chain);
            }
            return element;
        }

        /// <summary>Builds the metadata-free projected identity of this source.</summary>
        /// <returns>The canonical identity element.</returns>
        /// <param name="recursiveResponseHash">
        /// The already-compiled identity hash of a nested event-tree response. Supplying it keeps
        /// cycle detection on the occurrence compiler's shared function/node stack.
        /// </param>
        internal XElement ToIdentityXElement(byte[]? recursiveResponseHash = null)
        {
            var element = new XElement(nameof(ProbabilitySource));
            element.SetAttributeValue(nameof(Kind), Kind.ToString());
            switch (Kind)
            {
                case ProbabilitySourceKind.DeterministicScalar:
                    element.SetAttributeValue(nameof(ScalarProbability), SerializationUtilities.FormatDouble(ScalarProbability!.Value));
                    break;
                case ProbabilitySourceKind.UncertainTabular:
                    if (!ReferenceEquals(Table, null)) element.Add(Table.SaveToXElement());
                    break;
                case ProbabilitySourceKind.ResponseFunctionReference:
                    element.SetAttributeValue("FunctionHash", ReferenceEquals(ResponseFunction, null)
                        ? string.Empty
                        : CanonicalContentHasher.ToTokenHex(recursiveResponseHash
                            ?? ResponseFunction.CanonicalHash()));
                    break;
            }

            // The chain enters identity as ordered transform content hashes and the axis as a
            // named attribute — conditional presence keeps every chainless identity byte-identical,
            // while configuring, editing, or reordering a chain deliberately moves it.
            if (BivariateAxis != null)
                element.SetAttributeValue(nameof(BivariateAxis), BivariateAxis.Value.ToString());
            if (_hazardTransforms.Length > 0)
            {
                var chain = new XElement(nameof(HazardTransforms));
                foreach (var transform in _hazardTransforms)
                {
                    var entry = new XElement("HazardTransform");
                    entry.SetAttributeValue("FunctionHash", ReferenceEquals(transform, null)
                        ? string.Empty
                        : CanonicalContentHasher.ToTokenHex(transform.CanonicalHash()));
                    chain.Add(entry);
                }
                element.Add(chain);
            }
            return element;
        }

        /// <summary>Returns a stable token for sampler ordering and occurrence seeding.</summary>
        /// <returns>The uppercase canonical SHA-256 token.</returns>
        internal string CanonicalToken()
        {
            return CanonicalContentHasher.ToTokenHex(
                CanonicalContentHasher.Hash(ToIdentityXElement(), CanonicalizationRules.ModelRules));
        }

        /// <summary>
        /// Creates an owned snapshot for a tree fragment. Local tabular content is deep-copied,
        /// while an ordinary response and every hazard-transform chain entry remain the same live
        /// external references.
        /// </summary>
        /// <returns>The independent probability-source snapshot.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a referenced response or chain transform is unresolved.</exception>
        internal ProbabilitySource CloneForFragment()
        {
            switch (Kind)
            {
                case ProbabilitySourceKind.DeterministicScalar:
                    return new ProbabilitySource(ScalarProbability!.Value);
                case ProbabilitySourceKind.UncertainTabular:
                {
                    var table = new UncertainOrderedPairedData(Table!.SaveToXElement())
                    {
                        OrderX = SortOrder.Ascending,
                        OrderY = SortOrder.None,
                        StrictX = true,
                        StrictY = false,
                    };
                    return HasHazardTransforms
                        ? new ProbabilitySource(table, SnapshotChainReferences())
                        : new ProbabilitySource(table);
                }
                case ProbabilitySourceKind.ResponseFunctionReference:
                {
                    var response = ResponseFunction ?? throw new InvalidOperationException(
                        "A tree fragment cannot snapshot an unresolved response-function probability source.");
                    if (BivariateAxis != null)
                        return new ProbabilitySource(response, BivariateAxis.Value, SnapshotChainReferences());
                    return HasHazardTransforms
                        ? new ProbabilitySource(response, SnapshotChainReferences())
                        : new ProbabilitySource(response);
                }
                default:
                    throw new InvalidOperationException($"Unsupported probability source kind '{Kind}'.");
            }
        }

        /// <summary>Returns the chain as live references for a fragment snapshot.</summary>
        /// <returns>The chain entries, in order.</returns>
        /// <exception cref="InvalidOperationException">Thrown when a chain entry is unresolved.</exception>
        private ITransformFunction[] SnapshotChainReferences()
        {
            var transforms = new ITransformFunction[_hazardTransforms.Length];
            for (int i = 0; i < _hazardTransforms.Length; i++)
            {
                transforms[i] = _hazardTransforms[i] ?? throw new InvalidOperationException(
                    "A tree fragment cannot snapshot a probability source with an unresolved hazard transform.");
            }
            return transforms;
        }

        /// <summary>Copies and null-checks a caller-supplied transform chain.</summary>
        /// <param name="hazardTransforms">The ordered transform chain.</param>
        /// <returns>The defensive copy.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when an entry is null.</exception>
        private static ITransformFunction?[] CopyTransforms(IReadOnlyList<ITransformFunction> hazardTransforms)
        {
            if (hazardTransforms == null) throw new ArgumentNullException(nameof(hazardTransforms));
            var transforms = new ITransformFunction?[hazardTransforms.Count];
            for (int i = 0; i < hazardTransforms.Count; i++)
            {
                transforms[i] = hazardTransforms[i] ?? throw new ArgumentException(
                    $"The hazard transform at position {i} is null.", nameof(hazardTransforms));
            }
            return transforms;
        }
    }
}
