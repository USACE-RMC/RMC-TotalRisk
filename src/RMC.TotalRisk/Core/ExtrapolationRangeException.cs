using System;
using System.Globalization;
using RMC.TotalRisk.Core.Support;

namespace RMC.TotalRisk.Core
{
    /// <summary>
    /// Thrown when a function configured with <see cref="Enums.ExtrapolationPolicy.Error"/> is
    /// evaluated outside its table range.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The message carries the function name, the evaluated axis, the offending value, and the
    /// table range. Inside an adaptive integration the integrator absorbs thrown exceptions
    /// (<c>ReportFailure</c> is false) and reports failure status instead, so the guard also
    /// records this diagnostic in <see cref="EvaluationFaultScope"/> for the engine's
    /// integration-failure guards to surface; outside an integrator the exception propagates
    /// directly.
    /// </para>
    /// </remarks>
    public sealed class ExtrapolationRangeException : InvalidOperationException
    {
        /// <summary>
        /// Initializes the exception with the full out-of-range diagnostic.
        /// </summary>
        /// <param name="functionName">The name of the function that refused the evaluation.</param>
        /// <param name="axis">The evaluated axis (the function's hazard label and unit).</param>
        /// <param name="value">The offending evaluation value.</param>
        /// <param name="rangeMinimum">The smallest tabulated axis value.</param>
        /// <param name="rangeMaximum">The largest tabulated axis value.</param>
        public ExtrapolationRangeException(string functionName, string axis, double value, double rangeMinimum, double rangeMaximum)
            : base(ComposeMessage(functionName, axis, value, rangeMinimum, rangeMaximum))
        {
            FunctionName = functionName;
            Axis = axis;
            Value = value;
            RangeMinimum = rangeMinimum;
            RangeMaximum = rangeMaximum;
        }

        /// <summary>
        /// The name of the function that refused the evaluation.
        /// </summary>
        public string FunctionName { get; }

        /// <summary>
        /// The evaluated axis (the function's hazard label and unit).
        /// </summary>
        public string Axis { get; }

        /// <summary>
        /// The offending evaluation value.
        /// </summary>
        public double Value { get; }

        /// <summary>
        /// The smallest tabulated axis value.
        /// </summary>
        public double RangeMinimum { get; }

        /// <summary>
        /// The largest tabulated axis value.
        /// </summary>
        public double RangeMaximum { get; }

        /// <summary>
        /// Composes the diagnostic message.
        /// </summary>
        /// <param name="functionName">The function name.</param>
        /// <param name="axis">The evaluated axis.</param>
        /// <param name="value">The offending value.</param>
        /// <param name="rangeMinimum">The smallest tabulated axis value.</param>
        /// <param name="rangeMaximum">The largest tabulated axis value.</param>
        /// <returns>The composed message.</returns>
        private static string ComposeMessage(string functionName, string axis, double value, double rangeMinimum, double rangeMaximum)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "The extrapolation policy of function '{0}' is Error and the function was evaluated at {1} = {2:G17}, outside its table range [{3:G17}, {4:G17}]. Extend the table or select an extrapolation policy for the tail.",
                functionName, axis, value, rangeMinimum, rangeMaximum);
        }
    }
}
