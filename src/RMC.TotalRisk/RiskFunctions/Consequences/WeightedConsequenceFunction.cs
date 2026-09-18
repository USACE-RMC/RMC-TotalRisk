using System.ComponentModel;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.RiskFunctions.Consequences
{
    /// <summary>
    /// A consequence function paired with a weight — one entry in a
    /// <see cref="CompositeConsequence"/>'s weighted child list.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Mirrors the sibling BestFit <c>WeightedUnivariateAnalysis</c> support type: the wrapped
    /// function is referenced, not owned (a consuming layer may store one function and use it in
    /// several composites), the setter swaps a change subscription, and the wrapped function's own
    /// change notifications are forwarded verbatim so owners can filter by the child's property
    /// name. Unlike BestFit, a composite child may itself be a <see cref="CompositeConsequence"/>
    /// — nesting is well-defined for pointwise consequence combines, and
    /// <see cref="CompositeConsequence.Validate"/> rejects circular references.
    /// </para>
    /// <para>
    /// The entry does not serialize itself: the owning composite writes the weight and the child
    /// entry (inline content or a <c>FunctionReference</c> marker) through the shared
    /// <c>FunctionEntry</c> mechanics, so the reference format cannot drift per container.
    /// </para>
    /// </remarks>
    public class WeightedConsequenceFunction : INotifyPropertyChanged
    {
        #region Construction

        /// <summary>
        /// Initializes an empty entry (no function, weight zero — the v1.0 defaults).
        /// </summary>
        public WeightedConsequenceFunction()
        {
        }

        /// <summary>
        /// Initializes an entry with the specified function and weight.
        /// </summary>
        /// <param name="consequenceFunction">The consequence function; may be null while unconfigured.</param>
        /// <param name="weight">The weight, typically in [0, 1] with weights summing to one across the composite.</param>
        public WeightedConsequenceFunction(IConsequenceFunction? consequenceFunction, double weight)
        {
            ConsequenceFunction = consequenceFunction;
            Weight = weight;
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="Weight"/>.
        /// </summary>
        private double _weight;

        /// <summary>
        /// Backing field for <see cref="ConsequenceFunction"/>.
        /// </summary>
        private IConsequenceFunction? _consequenceFunction;

        /// <summary>
        /// Occurs when the weight, the wrapped function, or any property of the wrapped function
        /// changes (the latter forwarded verbatim).
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// The weight of this entry in the composite. Ignored (treated as one) under
        /// <see cref="Core.Enums.CompositeFunctionType.Additive"/>; the selection probability under
        /// <see cref="Core.Enums.CompositeFunctionType.Mixture"/>.
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
        /// The wrapped consequence function — referenced, not owned. Null while unconfigured
        /// (reported by the owning composite's <c>Validate()</c>).
        /// </summary>
        public IConsequenceFunction? ConsequenceFunction
        {
            get { return _consequenceFunction; }
            set
            {
                if (ReferenceEquals(_consequenceFunction, value)) return;

                if (_consequenceFunction != null) _consequenceFunction.PropertyChanged -= WrappedFunctionChanged;
                _consequenceFunction = value;
                if (_consequenceFunction != null) _consequenceFunction.PropertyChanged += WrappedFunctionChanged;

                RaisePropertyChange(nameof(ConsequenceFunction));
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
