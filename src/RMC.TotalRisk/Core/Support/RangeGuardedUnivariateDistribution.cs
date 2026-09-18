using System;
using System.Collections.Generic;
using Numerics.Distributions;

namespace RMC.TotalRisk.Core.Support
{
    /// <summary>
    /// A delegating distribution wrapper enforcing the
    /// <see cref="Enums.ExtrapolationPolicy.Error"/> mode on sampled hazard and response curves:
    /// forward (hazard-axis) evaluation outside the sampled table's span throws
    /// <see cref="ExtrapolationRangeException"/> with a full diagnostic.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The guard is created only when a sampled function's policy is Error, so every other model
    /// keeps the raw wrapper and the unchanged code path. Deriving from
    /// <see cref="UnivariateDistributionBase"/> keeps the derived lookup family
    /// (<c>CCDF</c>, <c>LogCDF</c>, <c>LogCCDF</c>, <c>HF</c>) routed through the guarded
    /// <see cref="CDF(double)"/> by virtual dispatch. Only the forward direction is guarded:
    /// <see cref="InverseCDF(double)"/> retains the endpoint hold, because the engine's domain
    /// derivation legitimately probes the far tails to locate the sampled curve's own span, and
    /// <see cref="PDF(double)"/> keeps its table-span support. The guard range is the inner
    /// sampled curve's own span, exact per realization under hazard-uncertainty tables. The
    /// inner wrapper is configured with the endpoint-hold extrapolation sides, so a bypassed
    /// guard degrades to the historical hold rather than to a silent extension. Before throwing,
    /// the guard records its diagnostic in <see cref="EvaluationFaultScope"/> so the engine can
    /// surface it across the adaptive integrators' exception absorption. Like its inner wrapper,
    /// an instance is realization-owned and never shared across threads.
    /// </para>
    /// </remarks>
    internal sealed class RangeGuardedUnivariateDistribution : UnivariateDistributionBase
    {
        /// <summary>
        /// Initializes the guard around a sampled distribution product.
        /// </summary>
        /// <param name="inner">The sampled distribution to delegate to; its
        /// <see cref="UnivariateDistributionBase.Minimum"/>/<see cref="UnivariateDistributionBase.Maximum"/>
        /// span is the guarded range.</param>
        /// <param name="functionName">The owning model function's name, for the diagnostic.</param>
        /// <param name="axis">The hazard-axis label (hazard label and unit), for the diagnostic.</param>
        /// <exception cref="ArgumentNullException">Thrown when the inner distribution is null.</exception>
        internal RangeGuardedUnivariateDistribution(UnivariateDistributionBase inner, string functionName, string axis)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _functionName = functionName;
            _axis = axis;
        }

        /// <summary>
        /// The sampled distribution being guarded.
        /// </summary>
        private readonly UnivariateDistributionBase _inner;

        /// <summary>
        /// The owning model function's name, for the diagnostic.
        /// </summary>
        private readonly string _functionName;

        /// <summary>
        /// The hazard-axis label, for the diagnostic.
        /// </summary>
        private readonly string _axis;

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
        public override double Mean => _inner.Mean;

        /// <inheritdoc/>
        public override double Median => _inner.Median;

        /// <inheritdoc/>
        public override double Mode => _inner.Mode;

        /// <inheritdoc/>
        public override double StandardDeviation => _inner.StandardDeviation;

        /// <inheritdoc/>
        public override double Skewness => _inner.Skewness;

        /// <inheritdoc/>
        public override double Kurtosis => _inner.Kurtosis;

        /// <inheritdoc/>
        public override double Minimum => _inner.Minimum;

        /// <inheritdoc/>
        public override double Maximum => _inner.Maximum;

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
        /// <remarks>
        /// Deliberately unguarded: the density keeps its table-span support.
        /// </remarks>
        public override double PDF(double x)
        {
            return _inner.PDF(x);
        }

        /// <inheritdoc/>
        /// <exception cref="ExtrapolationRangeException">Thrown when x lies outside the sampled
        /// table's span.</exception>
        public override double CDF(double x)
        {
            if (x < _inner.Minimum || x > _inner.Maximum)
            {
                var fault = new ExtrapolationRangeException(_functionName, _axis, x, _inner.Minimum, _inner.Maximum);
                EvaluationFaultScope.TryCapture(fault.Message);
                throw fault;
            }
            return _inner.CDF(x);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Deliberately unguarded: inverse lookups retain the endpoint hold (see the class
        /// remarks).
        /// </remarks>
        public override double InverseCDF(double probability)
        {
            return _inner.InverseCDF(probability);
        }

        /// <inheritdoc/>
        public override UnivariateDistributionBase Clone()
        {
            return new RangeGuardedUnivariateDistribution(_inner.Clone(), _functionName, _axis);
        }
    }
}
