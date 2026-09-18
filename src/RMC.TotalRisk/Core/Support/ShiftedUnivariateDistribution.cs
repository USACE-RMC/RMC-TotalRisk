using System;
using System.Collections.Generic;
using Numerics;
using Numerics.Distributions;

namespace RMC.TotalRisk.Core.Support
{
    /// <summary>
    /// A delegating distribution wrapper evaluating an inner sampled distribution on a shifted
    /// axis: the wrapped variable is the inner variable minus a constant shift, so
    /// <c>CDF(x) = inner.CDF(x + shift)</c> and <c>InverseCDF(p) = inner.InverseCDF(p) − shift</c>.
    /// A deteriorating response wraps its base response's sampled product this way — a positive
    /// capacity shift moves the fragility left, so the same hazard fails more.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Deriving from <see cref="UnivariateDistributionBase"/> keeps the derived lookup family
    /// (<c>CCDF</c>, <c>LogCDF</c>, <c>LogCCDF</c>, <c>HF</c>) routed through the shifted
    /// <see cref="CDF(double)"/> and <see cref="PDF(double)"/> by virtual dispatch. Location
    /// measures (mean, median, mode, minimum, maximum) shift with the axis; dispersion and shape
    /// measures (standard deviation, skewness, kurtosis) are shift-invariant and delegate
    /// unchanged. A zero shift reproduces the inner distribution's lookups bit-for-bit
    /// (<c>x + 0.0 == x</c> in IEEE arithmetic). Like the inner product it wraps, an instance is
    /// realization-owned and never shared across threads.
    /// </para>
    /// </remarks>
    internal sealed class ShiftedUnivariateDistribution : UnivariateDistributionBase
    {
        /// <summary>
        /// Initializes the shifted view over a sampled distribution product.
        /// </summary>
        /// <param name="inner">The sampled distribution to delegate to.</param>
        /// <param name="shift">The constant axis shift subtracted from the inner variable.</param>
        /// <exception cref="ArgumentNullException">Thrown when the inner distribution is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the shift is not finite.</exception>
        internal ShiftedUnivariateDistribution(UnivariateDistributionBase inner, double shift)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (!Tools.IsFinite(shift))
                throw new ArgumentOutOfRangeException(nameof(shift), "The axis shift must be finite.");
            _shift = shift;
        }

        /// <summary>
        /// The sampled distribution being shifted.
        /// </summary>
        private readonly UnivariateDistributionBase _inner;

        /// <summary>
        /// The constant axis shift subtracted from the inner variable.
        /// </summary>
        private readonly double _shift;

        /// <inheritdoc/>
        public override UnivariateDistributionType Type => _inner.Type;

        /// <inheritdoc/>
        public override string DisplayName => _inner.DisplayName;

        /// <inheritdoc/>
        public override string ShortDisplayName => _inner.ShortDisplayName;

        /// <inheritdoc/>
        public override int NumberOfParameters => _inner.NumberOfParameters;

        /// <inheritdoc/>
        public override string[,] ParametersToString => _inner.ParametersToString;

        /// <inheritdoc/>
        public override string[] ParameterNamesShortForm => _inner.ParameterNamesShortForm;

        /// <inheritdoc/>
        public override double[] GetParameters => _inner.GetParameters;

        /// <inheritdoc/>
        public override string[] GetParameterPropertyNames => _inner.GetParameterPropertyNames;

        /// <inheritdoc/>
        public override double Mean => _inner.Mean - _shift;

        /// <inheritdoc/>
        public override double Median => _inner.Median - _shift;

        /// <inheritdoc/>
        public override double Mode => _inner.Mode - _shift;

        /// <inheritdoc/>
        public override double StandardDeviation => _inner.StandardDeviation;

        /// <inheritdoc/>
        public override double Skewness => _inner.Skewness;

        /// <inheritdoc/>
        public override double Kurtosis => _inner.Kurtosis;

        /// <inheritdoc/>
        public override double Minimum => _inner.Minimum - _shift;

        /// <inheritdoc/>
        public override double Maximum => _inner.Maximum - _shift;

        /// <inheritdoc/>
        public override double[] MinimumOfParameters => _inner.MinimumOfParameters;

        /// <inheritdoc/>
        public override double[] MaximumOfParameters => _inner.MaximumOfParameters;

        /// <inheritdoc/>
        public override void SetParameters(IList<double> parameters)
        {
            _inner.SetParameters(parameters);
        }

        /// <inheritdoc/>
        public override ArgumentOutOfRangeException? ValidateParameters(IList<double> parameters, bool throwException)
        {
            return _inner.ValidateParameters(parameters, throwException);
        }

        /// <inheritdoc/>
        public override double PDF(double x)
        {
            return _inner.PDF(x + _shift);
        }

        /// <inheritdoc/>
        public override double CDF(double x)
        {
            return _inner.CDF(x + _shift);
        }

        /// <inheritdoc/>
        public override double InverseCDF(double probability)
        {
            return _inner.InverseCDF(probability) - _shift;
        }

        /// <inheritdoc/>
        public override UnivariateDistributionBase Clone()
        {
            return new ShiftedUnivariateDistribution(_inner.Clone(), _shift);
        }
    }
}
