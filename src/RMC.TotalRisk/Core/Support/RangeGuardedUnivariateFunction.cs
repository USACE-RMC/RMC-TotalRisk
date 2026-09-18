using System;
using System.Collections.Generic;
using Numerics.Functions;

namespace RMC.TotalRisk.Core.Support
{
    /// <summary>
    /// A delegating univariate-function wrapper enforcing the
    /// <see cref="Enums.ExtrapolationPolicy.Error"/> mode: forward evaluation outside the table's
    /// input range throws <see cref="ExtrapolationRangeException"/> with a full diagnostic.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The guard is created only when a sampled function's policy is Error, so every other model
    /// keeps the raw wrapper and the unchanged code path. Only the forward direction is guarded:
    /// <see cref="InverseFunction(double)"/> retains the endpoint hold, because inverse lookups
    /// are domain-defining query paths rather than chain evaluations. The inner wrapper is
    /// configured with the endpoint-hold extrapolation sides, so a bypassed guard degrades to
    /// the historical hold rather than to a silent extension. Before throwing, the guard records
    /// its diagnostic in <see cref="EvaluationFaultScope"/> so the engine can surface it across
    /// the adaptive integrators' exception absorption. Like its inner wrapper, an instance is
    /// realization-owned and never shared across threads.
    /// </para>
    /// </remarks>
    internal sealed class RangeGuardedUnivariateFunction : IUnivariateFunction
    {
        /// <summary>
        /// Initializes the guard around a sampled function product.
        /// </summary>
        /// <param name="inner">The sampled function to delegate to.</param>
        /// <param name="functionName">The owning model function's name, for the diagnostic.</param>
        /// <param name="axis">The input-axis label (hazard label and unit), for the diagnostic.</param>
        /// <param name="rangeMinimum">The smallest tabulated input value.</param>
        /// <param name="rangeMaximum">The largest tabulated input value.</param>
        /// <exception cref="ArgumentNullException">Thrown when the inner function is null.</exception>
        internal RangeGuardedUnivariateFunction(IUnivariateFunction inner, string functionName, string axis, double rangeMinimum, double rangeMaximum)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _functionName = functionName;
            _axis = axis;
            _rangeMinimum = rangeMinimum;
            _rangeMaximum = rangeMaximum;
        }

        /// <summary>
        /// The sampled function being guarded.
        /// </summary>
        private readonly IUnivariateFunction _inner;

        /// <summary>
        /// The owning model function's name, for the diagnostic.
        /// </summary>
        private readonly string _functionName;

        /// <summary>
        /// The input-axis label, for the diagnostic.
        /// </summary>
        private readonly string _axis;

        /// <summary>
        /// The smallest tabulated input value.
        /// </summary>
        private readonly double _rangeMinimum;

        /// <summary>
        /// The largest tabulated input value.
        /// </summary>
        private readonly double _rangeMaximum;

        /// <inheritdoc/>
        public int NumberOfParameters => _inner.NumberOfParameters;

        /// <inheritdoc/>
        public bool ParametersValid => _inner.ParametersValid;

        /// <inheritdoc/>
        public double Minimum
        {
            get { return _inner.Minimum; }
            set { _inner.Minimum = value; }
        }

        /// <inheritdoc/>
        public double Maximum
        {
            get { return _inner.Maximum; }
            set { _inner.Maximum = value; }
        }

        /// <inheritdoc/>
        public double[] MinimumOfParameters => _inner.MinimumOfParameters;

        /// <inheritdoc/>
        public double[] MaximumOfParameters => _inner.MaximumOfParameters;

        /// <inheritdoc/>
        public bool IsDeterministic
        {
            get { return _inner.IsDeterministic; }
            set { _inner.IsDeterministic = value; }
        }

        /// <inheritdoc/>
        public double ConfidenceLevel
        {
            get { return _inner.ConfidenceLevel; }
            set { _inner.ConfidenceLevel = value; }
        }

        /// <inheritdoc/>
        public void SetParameters(IList<double> parameters)
        {
            _inner.SetParameters(parameters);
        }

        /// <inheritdoc/>
        public ArgumentOutOfRangeException? ValidateParameters(IList<double> parameters, bool throwException)
        {
            return _inner.ValidateParameters(parameters, throwException);
        }

        /// <inheritdoc/>
        /// <exception cref="ExtrapolationRangeException">Thrown when x lies outside the table's
        /// input range.</exception>
        public double Function(double x)
        {
            if (x < _rangeMinimum || x > _rangeMaximum)
            {
                var fault = new ExtrapolationRangeException(_functionName, _axis, x, _rangeMinimum, _rangeMaximum);
                EvaluationFaultScope.TryCapture(fault.Message);
                throw fault;
            }
            return _inner.Function(x);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Deliberately unguarded: inverse lookups retain the endpoint hold (see the class
        /// remarks).
        /// </remarks>
        public double InverseFunction(double y)
        {
            return _inner.InverseFunction(y);
        }
    }
}
