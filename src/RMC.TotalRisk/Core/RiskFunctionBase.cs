using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Linq;
using Numerics.Distributions;
using Numerics.Sampling;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Core
{
    /// <summary>
    /// The shared implementation base for all risk input functions: property-change notification,
    /// identity-metadata backing, the canonical-hash pipeline, and the per-function percentile
    /// sampler machinery.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Each function owns its sampler (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §5.8):
    /// <see cref="SetupSampler"/>
    /// pre-allocates an N×D percentile matrix, where D is <see cref="SamplingDimensions"/>, and
    /// per-realization sampling reads row <c>realizationIndex</c> via
    /// <see cref="Percentile(int, int)"/>. Functions with D = 0 (deterministic, or posterior-indexed
    /// parametric functions) allocate no matrix but still record <see cref="SampleSize"/> for
    /// index-bound checks.
    /// </para>
    /// <para>
    /// <see cref="INotifyPropertyChanged"/> is a passive contract: headless callers never
    /// subscribe; the consuming UI layer data-binds to it.
    /// </para>
    /// </remarks>
    public abstract class RiskFunctionBase : IRiskFunction
    {
        #region Members

        /// <summary>
        /// Backing field for <see cref="Id"/>.
        /// </summary>
        private Guid _id = Guid.NewGuid();

        /// <summary>
        /// Backing field for <see cref="Name"/>.
        /// </summary>
        private string _name = string.Empty;

        /// <summary>
        /// Backing field for <see cref="Description"/>.
        /// </summary>
        private string _description = string.Empty;

        /// <summary>
        /// Backing field for <see cref="SpecifiedHazard"/>.
        /// </summary>
        private string _specifiedHazard = string.Empty;

        /// <summary>
        /// Backing field for <see cref="HazardUnit"/>.
        /// </summary>
        private string _hazardUnit = string.Empty;

        /// <summary>
        /// The pre-allocated N×D percentile matrix; null until <see cref="SetupSampler"/> runs, and
        /// null when <see cref="SamplingDimensions"/> is zero.
        /// </summary>
        protected double[,]? _percentiles;

        /// <inheritdoc/>
        public Guid Id
        {
            get { return _id; }
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public string SpecifiedHazard
        {
            get { return _specifiedHazard; }
            set
            {
                if (_specifiedHazard != value)
                {
                    _specifiedHazard = value;
                    RaisePropertyChange(nameof(SpecifiedHazard));
                }
            }
        }

        /// <inheritdoc/>
        public string HazardUnit
        {
            get { return _hazardUnit; }
            set
            {
                if (_hazardUnit != value)
                {
                    _hazardUnit = value;
                    RaisePropertyChange(nameof(HazardUnit));
                }
            }
        }

        /// <inheritdoc/>
        public abstract bool IsDeterministic { get; }

        /// <inheritdoc/>
        public abstract int SamplingDimensions { get; }

        /// <summary>
        /// The realization count recorded by the last <see cref="SetupSampler"/> call — the row
        /// count of the percentile matrix, and the index bound for D = 0 functions. Zero until the
        /// sampler has been set up.
        /// </summary>
        public int SampleSize { get; protected set; }

        /// <summary>
        /// Occurs when a property of the function changes. Passive: headless callers never
        /// subscribe; the consuming UI layer data-binds.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the sample size is not positive.</exception>
        /// <exception cref="NotSupportedException">Thrown when the sampling scheme is unrecognized.</exception>
        public virtual void SetupSampler(int sampleSize, int seed, SamplingScheme scheme)
        {
            if (sampleSize <= 0) throw new ArgumentOutOfRangeException(nameof(sampleSize), "The sample size must be positive.");

            SampleSize = sampleSize;
            int dimensions = SamplingDimensions;
            if (dimensions == 0)
            {
                _percentiles = null;
                return;
            }

            int positiveSeed = ToPositiveSeed(seed);
            _percentiles = scheme switch
            {
                SamplingScheme.LatinHypercube => LatinHypercube.Random(sampleSize, dimensions, positiveSeed),
                SamplingScheme.LatinHypercubeMedian => LatinHypercube.Median(sampleSize, dimensions, positiveSeed),
                SamplingScheme.MonteCarlo => SeedHelpers.IndependentUniform(sampleSize, dimensions, positiveSeed),
                SamplingScheme.ScrambledSobol => SeedHelpers.ScrambledSobol(sampleSize, dimensions, positiveSeed),
                _ => throw new NotSupportedException($"The sampling scheme '{scheme}' is not supported."),
            };
        }

        /// <summary>
        /// Overwrites every entry of the pre-allocated percentile matrix with one fixed
        /// percentile — the epistemic conditioning (fractile pinning) primitive. Overwriting the
        /// matrix after seeding is what keeps a pin seed-inert: every function's matrix is
        /// generated from its own dedicated stream, so replacing this function's draws cannot
        /// move any other function's, and the captured seed map is untouched.
        /// </summary>
        /// <param name="percentile">The percentile to hold every realization and dimension at,
        /// strictly inside (0, 1).</param>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="SetupSampler"/> has
        /// not populated the matrix (call order, or a function with no sampling dimensions).</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the percentile is not
        /// strictly inside (0, 1).</exception>
        internal void OverrideSampledPercentiles(double percentile)
        {
            if (_percentiles == null)
                throw new InvalidOperationException("SetupSampler() must be called before pinning percentiles.");
            if (!(percentile > 0d) || !(percentile < 1d))
                throw new ArgumentOutOfRangeException(nameof(percentile), "The pinned percentile must lie strictly inside (0, 1).");
            int rows = _percentiles.GetLength(0);
            int columns = _percentiles.GetLength(1);
            for (int i = 0; i < rows; i++)
            {
                for (int d = 0; d < columns; d++)
                {
                    _percentiles[i, d] = percentile;
                }
            }
        }

        /// <summary>
        /// Overwrites the first (selector) column of the pre-allocated percentile matrix with a
        /// shared per-realization draw — the shared-epistemic-variable primitive behind
        /// <see cref="EpistemicSharingScope"/>. Like <see cref="OverrideSampledPercentiles"/>,
        /// overwriting after seeding is what keeps the share seed-inert: every other function's
        /// draws are untouched and the captured seed map does not move.
        /// </summary>
        /// <param name="column">The shared uniform (0, 1) draws, one per realization.</param>
        /// <exception cref="ArgumentNullException">Thrown when the column is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when <see cref="SetupSampler"/> has
        /// not populated the matrix, or when the column length does not match the sample size.</exception>
        internal void OverrideSelectorColumn(double[] column)
        {
            if (column == null) throw new ArgumentNullException(nameof(column));
            if (_percentiles == null)
                throw new InvalidOperationException("SetupSampler() must be called before applying a shared selector column.");
            if (_percentiles.GetLength(0) != column.Length)
                throw new InvalidOperationException("The shared selector column length does not match the sampler's sample size.");
            for (int i = 0; i < column.Length; i++)
            {
                _percentiles[i, 0] = column[i];
            }
        }

        /// <inheritdoc/>
        public abstract UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9);

        /// <inheritdoc/>
        public abstract (bool IsValid, List<string> ValidationMessages) Validate();

        /// <inheritdoc/>
        public void AssignNewId()
        {
            _id = Guid.NewGuid();
            RaisePropertyChange(nameof(Id));
        }

        /// <inheritdoc/>
        public abstract XElement ToXElement();

        /// <inheritdoc/>
        /// <remarks>
        /// Virtual for container function types whose persisted form is not their identity
        /// surface: a composite function hashes a projected identity form (mode, weights, child
        /// content hashes) so serialization mode and child metadata can never move its hash — the
        /// same identity-projection exception <c>SystemComponent</c> established.
        /// </remarks>
        public virtual byte[] CanonicalHash()
        {
            return CanonicalContentHasher.Hash(ToXElement(), CanonicalizationRules.ModelRules);
        }

        #endregion

        #region Protected Helpers

        /// <summary>
        /// Writes the shared identity attributes — <see cref="Id"/> ("D" format),
        /// <see cref="Name"/>, and <see cref="Description"/> — onto a serialized form. Every
        /// concrete function calls this first from its <c>ToXElement()</c> so identity is written
        /// in exactly one place.
        /// </summary>
        /// <param name="xElement">The serialized form under construction.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <remarks>
        /// All three attributes are stripped by <see cref="CanonicalizationRules.ModelRules"/>, so
        /// this method contributes nothing to the canonical hash by construction.
        /// </remarks>
        protected void WriteIdentityAttributes(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            xElement.SetAttributeValue(nameof(Id), _id.ToString("D"));
            xElement.SetAttributeValue(nameof(Name), _name);
            xElement.SetAttributeValue(nameof(Description), _description);
        }

        /// <summary>
        /// Reads the shared identity attributes from a serialized form. A missing or unparseable
        /// id yields a fresh one, so forms written before ids existed still load.
        /// </summary>
        /// <param name="xElement">The serialized form.</param>
        /// <param name="defaultName">The name to use when the attribute is absent.</param>
        /// <param name="defaultDescription">The description to use when the attribute is absent.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        protected void ReadIdentityAttributes(XElement xElement, string defaultName = "", string defaultDescription = "")
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            _id = Guid.TryParse(xElement.Attribute(nameof(Id))?.Value, out var id) ? id : Guid.NewGuid();
            _name = SerializationUtilities.ReadString(xElement, nameof(Name), defaultName);
            _description = SerializationUtilities.ReadString(xElement, nameof(Description), defaultDescription);
        }

        /// <summary>
        /// Raises the <see cref="PropertyChanged"/> event.
        /// </summary>
        /// <param name="propertyName">The name of the property that changed.</param>
        protected virtual void RaisePropertyChange(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Reads the pre-allocated percentile for a realization and sampling dimension.
        /// </summary>
        /// <param name="realizationIndex">The realization row, in [0, <see cref="SampleSize"/>).</param>
        /// <param name="dimension">The sampling dimension column, in [0, <see cref="SamplingDimensions"/>).</param>
        /// <returns>The uniform (0, 1) percentile driving this function's draw.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="SetupSampler"/> has not been called (or the function has no
        /// sampling dimensions).
        /// </exception>
        protected double Percentile(int realizationIndex, int dimension)
        {
            if (_percentiles == null)
                throw new InvalidOperationException("SetupSampler() must be called before sampling by realization index.");
            return _percentiles[realizationIndex, dimension];
        }

        /// <summary>
        /// Reads the pre-allocated percentile for a realization and sampling dimension — the
        /// public read the sensitivity engine correlates against: the percentile row IS the
        /// function's knowledge draw, re-derivable bit-exactly from the content seeds.
        /// </summary>
        /// <param name="realizationIndex">The realization row, in [0, <see cref="SampleSize"/>).</param>
        /// <param name="dimension">The sampling dimension column, in [0, <see cref="SamplingDimensions"/>).</param>
        /// <returns>The uniform (0, 1) percentile driving this function's draw.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="SetupSampler"/> has not been called (or the function has no
        /// sampling dimensions).
        /// </exception>
        public double SampledPercentile(int realizationIndex, int dimension)
        {
            return Percentile(realizationIndex, dimension);
        }

        /// <summary>
        /// Maps any 32-bit seed onto [1, int.MaxValue] deterministically.
        /// </summary>
        /// <param name="seed">The seed, possibly zero or negative (content-derived seeds span the full int range).</param>
        /// <returns>An equivalent strictly positive seed.</returns>
        /// <remarks>
        /// Delegates to <see cref="SeedHelpers.ToPositiveSeed(int)"/> — the fold moved to the seed
        /// kernel when the risk engine gained non-function consumers (the failure-mode coupling
        /// matrix and the per-realization VEGAS seeds). Kept protected here so existing function
        /// implementations keep their call sites unchanged.
        /// </remarks>
        protected static int ToPositiveSeed(int seed)
        {
            return SeedHelpers.ToPositiveSeed(seed);
        }

        #endregion
    }
}
