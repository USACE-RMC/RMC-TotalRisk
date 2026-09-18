using System.ComponentModel;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.RiskFunctions.Transforms
{
    /// <summary>
    /// A transform function paired with a weight — one entry in a <see cref="CompositeTransform"/>'s
    /// weighted child list.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// New in v1.1 — v1.0 had no composite transform, so this type has no legacy counterpart. It
    /// follows the shape of its hazard, response, and consequence siblings exactly: the wrapped
    /// function is referenced, not owned, the setter swaps a change subscription, the wrapped
    /// function's own change notifications are forwarded verbatim, and the entry does not serialize
    /// itself (the owning composite writes the weight and the child entry through the shared
    /// <c>FunctionEntry</c> mechanics).
    /// </para>
    /// </remarks>
    public class WeightedTransformFunction : INotifyPropertyChanged
    {
        #region Construction

        /// <summary>
        /// Initializes an empty entry (no function, weight zero).
        /// </summary>
        public WeightedTransformFunction()
        {
        }

        /// <summary>
        /// Initializes an entry with the specified function and weight.
        /// </summary>
        /// <param name="transformFunction">The transform function; may be null while unconfigured.</param>
        /// <param name="weight">The weight, in [0, 1] with weights summing to one across the composite.</param>
        public WeightedTransformFunction(ITransformFunction? transformFunction, double weight)
        {
            TransformFunction = transformFunction;
            Weight = weight;
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="Weight"/>.
        /// </summary>
        private double _weight;

        /// <summary>
        /// Backing field for <see cref="TransformFunction"/>.
        /// </summary>
        private ITransformFunction? _transformFunction;

        /// <summary>
        /// Occurs when the weight, the wrapped function, or any property of the wrapped function
        /// changes (the latter forwarded verbatim).
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// The weight of this entry in the composite — the credibility assigned to this candidate
        /// transform, with weights summing to one across the composite.
        /// </summary>
        public double Weight
        {
            get { return _weight; }
            set
            {
                if (_weight != value)
                {
                    _weight = value;
                    RaisePropertyChange(nameof(Weight));
                }
            }
        }

        /// <summary>
        /// The wrapped transform function — referenced, not owned. Null while unconfigured
        /// (reported by the owning composite's <c>Validate()</c>).
        /// </summary>
        public ITransformFunction? TransformFunction
        {
            get { return _transformFunction; }
            set
            {
                if (ReferenceEquals(_transformFunction, value)) return;

                if (_transformFunction != null) _transformFunction.PropertyChanged -= WrappedFunctionChanged;
                _transformFunction = value;
                if (_transformFunction != null) _transformFunction.PropertyChanged += WrappedFunctionChanged;

                RaisePropertyChange(nameof(TransformFunction));
            }
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Forwards a wrapped function's change notification verbatim, so owners can distinguish
        /// child content edits from entry-level changes by property name.
        /// </summary>
        /// <param name="sender">The wrapped function.</param>
        /// <param name="e">The originating change arguments.</param>
        private void WrappedFunctionChanged(object? sender, PropertyChangedEventArgs e)
        {
            RaisePropertyChange(e.PropertyName ?? string.Empty);
        }

        /// <summary>
        /// Raises the <see cref="PropertyChanged"/> event.
        /// </summary>
        /// <param name="propertyName">The name of the property that changed.</param>
        protected virtual void RaisePropertyChange(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
