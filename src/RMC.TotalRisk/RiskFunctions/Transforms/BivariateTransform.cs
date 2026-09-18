using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Numerics.Data;
using Numerics.Distributions;
using Numerics.Functions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.RiskFunctions.Transforms
{
    /// <summary>
    /// A bivariate transform function: a deterministic two-way table z = f(x, y) mapping a
    /// primary and a secondary hazard onto a transformed hazard through Numerics bilinear
    /// interpolation, with optional per-axis interpolation transforms.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The surface is stored on the <see cref="Bilinear"/> convention
    /// <c>ZValues[i, j] = z(X1Values[i], X2Values[j])</c>: strictly ascending primary (X1) and
    /// secondary (X2) axes with one finite output cell per axis pair. The surface is
    /// deterministic (no knowledge uncertainty; sampling dimension D = 0), so the transformed
    /// output is identical across realizations and the inherited univariate sampling surface
    /// throws <see cref="NotSupportedException"/> — a bivariate transform is evaluated jointly
    /// through <see cref="Evaluate"/> or <see cref="CreateInterpolator"/>, never collapsed onto a
    /// single hazard axis.
    /// </para>
    /// <para>
    /// <b>Extrapolation policy (deliberate — the native <see cref="Bilinear"/> behavior the
    /// legacy evaluator shipped):</b> when both coordinates fall outside their axes, the nearest
    /// corner cell is returned exactly; when one coordinate falls outside, it clamps to its
    /// nearest edge row or column and the result is one-dimensional linear interpolation along
    /// the in-range axis (in transform space). Boundary equality interpolates normally.
    /// Re-clamping or gradient extrapolation would break parity with the legacy two-way tables.
    /// </para>
    /// <para>
    /// A normal-Z axis or output permits values of exactly 0 and 1: Numerics clamps 0 to a finite
    /// extreme z-score while 1 transforms to positive infinity, so a saturated cell recovers its
    /// exact value at interior queries — native legacy evaluator behavior, preserved.
    /// </para>
    /// </remarks>
    public class BivariateTransform : TransformFunctionBase, IBivariateTransformFunction
    {
        #region Construction

        /// <summary>
        /// Initializes a bivariate transform with the default unit-square zero surface:
        /// X1 = X2 = {0, 1}, all cells zero.
        /// </summary>
        public BivariateTransform()
        {
        }

        /// <summary>
        /// Restores a bivariate transform from its serialized form. Reads are permissive and
        /// shape-preserving: a ragged or partially unparseable surface payload reconstructs to
        /// exactly the parsed shape (absent cells NaN) and fails <see cref="Validate"/> — it is
        /// never silently truncated.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        public BivariateTransform(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            ReadIdentityAttributes(xElement);
            SpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SpecifiedHazard));
            HazardUnit = SerializationUtilities.ReadString(xElement, nameof(HazardUnit));
            SecondarySpecifiedHazard = SerializationUtilities.ReadString(xElement, nameof(SecondarySpecifiedHazard));
            SecondaryHazardUnit = SerializationUtilities.ReadString(xElement, nameof(SecondaryHazardUnit));
            TransformedHazard = SerializationUtilities.ReadString(xElement, nameof(TransformedHazard));
            TransformedHazardUnit = SerializationUtilities.ReadString(xElement, nameof(TransformedHazardUnit));
            _hazardTransform = SerializationUtilities.ReadEnum(xElement, nameof(HazardTransform), Transform.None);
            _secondaryHazardTransform = SerializationUtilities.ReadEnum(xElement, nameof(SecondaryHazardTransform), Transform.None);
            _transformTransform = SerializationUtilities.ReadEnum(xElement, nameof(TransformTransform), Transform.None);
            _x1Values = BivariateTableSupport.ReadAxis(xElement, nameof(X1Values));
            _x2Values = BivariateTableSupport.ReadAxis(xElement, nameof(X2Values));
            _zValues = BivariateTableSupport.ReadGrid(xElement, nameof(ZValues), "Row");
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="SecondarySpecifiedHazard"/>.
        /// </summary>
        private string _secondarySpecifiedHazard = string.Empty;

        /// <summary>
        /// Backing field for <see cref="SecondaryHazardUnit"/>.
        /// </summary>
        private string _secondaryHazardUnit = string.Empty;

        /// <summary>
        /// Backing field for <see cref="X1Values"/> — the default unit axis.
        /// </summary>
        private double[] _x1Values = new[] { 0d, 1d };

        /// <summary>
        /// Backing field for <see cref="X2Values"/> — the default unit axis.
        /// </summary>
        private double[] _x2Values = new[] { 0d, 1d };

        /// <summary>
        /// Backing field for <see cref="ZValues"/> — the default zero surface.
        /// </summary>
        private double[,] _zValues = new double[2, 2];

        /// <summary>
        /// Backing field for <see cref="HazardTransform"/>.
        /// </summary>
        private Transform _hazardTransform = Transform.None;

        /// <summary>
        /// Backing field for <see cref="SecondaryHazardTransform"/>.
        /// </summary>
        private Transform _secondaryHazardTransform = Transform.None;

        /// <summary>
        /// Backing field for <see cref="TransformTransform"/>.
        /// </summary>
        private Transform _transformTransform = Transform.None;

        /// <inheritdoc/>
        public string SecondarySpecifiedHazard
        {
            get { return _secondarySpecifiedHazard; }
            set
            {
                if (_secondarySpecifiedHazard != value)
                {
                    _secondarySpecifiedHazard = value;
                    RaisePropertyChange(nameof(SecondarySpecifiedHazard));
                }
            }
        }

        /// <inheritdoc/>
        public string SecondaryHazardUnit
        {
            get { return _secondaryHazardUnit; }
            set
            {
                if (_secondaryHazardUnit != value)
                {
                    _secondaryHazardUnit = value;
                    RaisePropertyChange(nameof(SecondaryHazardUnit));
                }
            }
        }

        /// <summary>
        /// The strictly ascending primary hazard (X1) axis values. Assigning swaps the whole
        /// array; null assignments are ignored.
        /// </summary>
        public double[] X1Values
        {
            get { return _x1Values; }
            set
            {
                if (!ReferenceEquals(_x1Values, value) && value is not null)
                {
                    _x1Values = value;
                    RaisePropertyChange(nameof(X1Values));
                }
            }
        }

        /// <summary>
        /// The strictly ascending secondary hazard (X2) axis values. Assigning swaps the whole
        /// array; null assignments are ignored.
        /// </summary>
        public double[] X2Values
        {
            get { return _x2Values; }
            set
            {
                if (!ReferenceEquals(_x2Values, value) && value is not null)
                {
                    _x2Values = value;
                    RaisePropertyChange(nameof(X2Values));
                }
            }
        }

        /// <summary>
        /// The transformed-hazard surface on the bilinear convention
        /// <c>ZValues[i, j] = z(X1Values[i], X2Values[j])</c> — one row per primary value, one
        /// column per secondary value. Assigning swaps the whole array; null assignments are
        /// ignored.
        /// </summary>
        public double[,] ZValues
        {
            get { return _zValues; }
            set
            {
                if (!ReferenceEquals(_zValues, value) && value is not null)
                {
                    _zValues = value;
                    RaisePropertyChange(nameof(ZValues));
                }
            }
        }

        /// <summary>
        /// The interpolation transform applied to the primary hazard (X1) axis.
        /// </summary>
        public Transform HazardTransform
        {
            get { return _hazardTransform; }
            set
            {
                if (_hazardTransform != value)
                {
                    _hazardTransform = value;
                    RaisePropertyChange(nameof(HazardTransform));
                }
            }
        }

        /// <summary>
        /// The interpolation transform applied to the secondary hazard (X2) axis.
        /// </summary>
        public Transform SecondaryHazardTransform
        {
            get { return _secondaryHazardTransform; }
            set
            {
                if (_secondaryHazardTransform != value)
                {
                    _secondaryHazardTransform = value;
                    RaisePropertyChange(nameof(SecondaryHazardTransform));
                }
            }
        }

        /// <summary>
        /// The interpolation transform applied to the transformed-hazard (Z) output.
        /// </summary>
        public Transform TransformTransform
        {
            get { return _transformTransform; }
            set
            {
                if (_transformTransform != value)
                {
                    _transformTransform = value;
                    RaisePropertyChange(nameof(TransformTransform));
                }
            }
        }

        /// <inheritdoc/>
        public override TransformFunctionType FunctionType => TransformFunctionType.Bivariate;

        /// <inheritdoc/>
        /// <remarks>Always true — the surface carries no knowledge uncertainty.</remarks>
        public override bool IsDeterministic => true;

        /// <inheritdoc/>
        /// <remarks>Zero — deterministic; the base sampler records the sample size only.</remarks>
        public override int SamplingDimensions => 0;

        #endregion

        #region IRiskFunction Methods

        /// <inheritdoc/>
        /// <remarks>
        /// Errors (invalidating): missing axis labels (all six — the primary, secondary, and
        /// transformed-hazard pairs); an axis with fewer than two, non-finite, or
        /// non-strictly-ascending values; a surface not dimensioned one row per primary value by
        /// one column per secondary value; a non-finite cell; a logarithmic axis or output over
        /// values below zero; a normal-Z axis or output over values outside [0, 1]. The transform
        /// guards run only when the structure is usable.
        /// </remarks>
        public override (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            if (string.IsNullOrEmpty(SpecifiedHazard))
                messages.Add("Error: The bivariate transform function does not have a specified hazard type.");
            if (string.IsNullOrEmpty(HazardUnit))
                messages.Add("Error: The bivariate transform function does not have a specified hazard unit.");
            if (string.IsNullOrEmpty(SecondarySpecifiedHazard))
                messages.Add("Error: The bivariate transform function does not have a specified secondary hazard type.");
            if (string.IsNullOrEmpty(SecondaryHazardUnit))
                messages.Add("Error: The bivariate transform function does not have a specified secondary hazard unit.");
            if (string.IsNullOrEmpty(TransformedHazard))
                messages.Add("Error: The bivariate transform function does not have a transformed hazard type.");
            if (string.IsNullOrEmpty(TransformedHazardUnit))
                messages.Add("Error: The bivariate transform function does not have a specified transformed hazard unit.");

            bool structureUsable = BivariateTableSupport.ValidateStructure(
                messages, "bivariate transform function", _x1Values, _x2Values, _zValues);
            if (structureUsable)
            {
                BivariateTableSupport.ValidateAxisTransform(messages, "hazard", _hazardTransform, _x1Values);
                BivariateTableSupport.ValidateAxisTransform(messages, "secondary hazard", _secondaryHazardTransform, _x2Values);
                BivariateTableSupport.ValidateSurfaceTransform(messages, "transform", _transformTransform, _zValues);
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Not supported by contract — a bivariate transform has no univariate form, and a
        /// fabricated bridge would silently evaluate the surface at a meaningless secondary
        /// value. Graph arity and validation keep bivariate functions out of univariate chains.
        /// </remarks>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public override IUnivariateFunction SampleFunction()
        {
            throw NoUnivariateSurface();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Not supported by contract — see <see cref="SampleFunction()"/>.
        /// </remarks>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public override IUnivariateFunction SampleFunction(double percentile)
        {
            throw NoUnivariateSurface();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Not supported by contract — see <see cref="SampleFunction()"/>.
        /// </remarks>
        /// <exception cref="NotSupportedException">Always thrown.</exception>
        public override IUnivariateFunction SampleFunction(int realizationIndex)
        {
            throw NoUnivariateSurface();
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override double MinHazard()
        {
            ThrowIfTableUnusable();
            return _x1Values[0];
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override double MaxHazard()
        {
            ThrowIfTableUnusable();
            return _x1Values[_x1Values.Length - 1];
        }

        /// <inheritdoc/>
        /// <remarks>The surface minimum — deterministic, so the mean-only flag is irrelevant.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override double MinTransformedHazard(bool meanOnly)
        {
            ThrowIfTableUnusable();
            return BivariateTableSupport.GridBounds(_zValues).Min;
        }

        /// <inheritdoc/>
        /// <remarks>The surface maximum — deterministic, so the mean-only flag is irrelevant.</remarks>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public override double MaxTransformedHazard(bool meanOnly)
        {
            ThrowIfTableUnusable();
            return BivariateTableSupport.GridBounds(_zValues).Max;
        }

        /// <inheritdoc/>
        /// <remarks>Not applicable — the surface is deterministic; returns null.</remarks>
        public override UncertaintyAnalysisResults? ComputeUncertaintyResults(double confidenceIntervalWidth = 0.9)
        {
            return null;
        }

        #endregion

        #region IBivariateTransformFunction Methods

        /// <inheritdoc/>
        public double Evaluate(double x, double y)
        {
            return CreateInterpolator().Interpolate(x, y);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The interpolator shares the function's axis and surface arrays (arrays are only ever
        /// swapped whole, never mutated in place) and owns its private mutable search state —
        /// exactly the share-the-arrays, rebuild-the-wrapper discipline the per-realization loop
        /// requires. Do not add defensive copies here; they would put an allocation of the whole
        /// table into every realization.
        /// </remarks>
        public Bilinear CreateInterpolator()
        {
            ThrowIfTableUnusable();
            return new Bilinear(_x1Values, _x2Values, _zValues, SortOrder.Ascending)
            {
                X1Transform = _hazardTransform,
                X2Transform = _secondaryHazardTransform,
                YTransform = _transformTransform,
            };
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public double MinSecondaryHazard()
        {
            ThrowIfTableUnusable();
            return _x2Values[0];
        }

        /// <inheritdoc/>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        public double MaxSecondaryHazard()
        {
            ThrowIfTableUnusable();
            return _x2Values[_x2Values.Length - 1];
        }

        #endregion

        #region Serialization

        /// <inheritdoc/>
        public override XElement ToXElement()
        {
            var element = new XElement(nameof(BivariateTransform));
            WriteIdentityAttributes(element);
            element.SetAttributeValue(nameof(SpecifiedHazard), SpecifiedHazard);
            element.SetAttributeValue(nameof(HazardUnit), HazardUnit);
            element.SetAttributeValue(nameof(SecondarySpecifiedHazard), SecondarySpecifiedHazard);
            element.SetAttributeValue(nameof(SecondaryHazardUnit), SecondaryHazardUnit);
            element.SetAttributeValue(nameof(TransformedHazard), TransformedHazard);
            element.SetAttributeValue(nameof(TransformedHazardUnit), TransformedHazardUnit);
            element.SetAttributeValue(nameof(HazardTransform), HazardTransform.ToString());
            element.SetAttributeValue(nameof(SecondaryHazardTransform), SecondaryHazardTransform.ToString());
            element.SetAttributeValue(nameof(TransformTransform), TransformTransform.ToString());
            element.Add(BivariateTableSupport.WriteAxis(nameof(X1Values), _x1Values));
            element.Add(BivariateTableSupport.WriteAxis(nameof(X2Values), _x2Values));
            element.Add(BivariateTableSupport.WriteGrid(nameof(ZValues), "Row", _zValues));
            return element;
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Builds the univariate-surface rejection: a bivariate transform is evaluated jointly.
        /// </summary>
        /// <returns>The exception to throw.</returns>
        private static NotSupportedException NoUnivariateSurface()
        {
            return new NotSupportedException(
                "A bivariate transform function has no univariate sampling surface. Evaluate it at a (primary, secondary) hazard pair via Evaluate(x, y) or CreateInterpolator().");
        }

        /// <summary>
        /// The evaluation usability gate (the cluster's invalid-configuration throw): both axes
        /// with at least two finite strictly ascending values, matching surface dimensions, and
        /// finite cells.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the table is invalid.</exception>
        private void ThrowIfTableUnusable()
        {
            if (!BivariateTableSupport.IsUsable(_x1Values, _x2Values, _zValues))
                throw new InvalidOperationException("The bivariate transform table is invalid. Call Validate() and correct the reported errors before evaluating.");
        }

        #endregion
    }
}
