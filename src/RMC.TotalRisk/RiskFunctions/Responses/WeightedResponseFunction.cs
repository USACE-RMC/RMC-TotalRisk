using System.ComponentModel;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.RiskFunctions.Responses
{
    /// <summary>
    /// A response function paired with a weight — one entry in a <see cref="CompositeResponse"/>'s
    /// weighted child list.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The sibling of <c>WeightedHazardFunction</c> and <c>WeightedConsequenceFunction</c>, with the
    /// same contract: the wrapped function is referenced, not owned (a consuming layer may store one
    /// function and use it in several composites), the setter swaps a change subscription, and the
    /// wrapped function's own change notifications are forwarded verbatim so owners can filter by
    /// the child's property name. A child may itself be a <see cref="CompositeResponse"/> — nesting
    /// is well-defined, and <see cref="CompositeResponse.Validate"/> rejects circular references.
    /// </para>
    /// <para>
    /// Improved over the v1.0 <c>WeightedResponseFunction</c>, whose <c>ToXElement()</c> wrote a
    /// <b>name-based</b> reference to the child's <c>NameOnDisk</c>: the entry does not serialize
    /// itself at all. The owning composite writes the weight and the child entry (inline content or
    /// an id-bearing <c>FunctionReference</c> marker) through the shared <c>FunctionEntry</c>
    /// mechanics, so the reference format cannot drift per container and a rename cannot break a
    /// link.
    /// </para>
    /// </remarks>
    public class WeightedResponseFunction : INotifyPropertyChanged
    {
        #region Construction

        /// <summary>
        /// Initializes an empty entry (no function, weight zero — the v1.0 defaults).
        /// </summary>
        public WeightedResponseFunction()
        {
        }

        /// <summary>
        /// Initializes an entry with the specified function and weight.
        /// </summary>
        /// <param name="responseFunction">The response function; may be null while unconfigured.</param>
        /// <param name="weight">The weight, in [0, 1] with weights summing to one across the composite.</param>
        public WeightedResponseFunction(IResponseFunction? responseFunction, double weight)
        {
            ResponseFunction = responseFunction;
            Weight = weight;
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="Weight"/>.
        /// </summary>
        private double _weight;

        /// <summary>
        /// Backing field for <see cref="ResponseFunction"/>.
        /// </summary>
        private IResponseFunction? _responseFunction;

        /// <summary>
        /// Occurs when the weight, the wrapped function, or any property of the wrapped function
        /// changes (the latter forwarded verbatim).
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// The weight of this entry in the composite: the mixture proportion under
        /// <see cref="Core.Enums.CompositeCombinationType.Mixture"/>. Inert (and coerced to one in
        /// the composite's canonical hash) under
        /// <see cref="Core.Enums.CompositeCombinationType.CompetingRisks"/>, where the combination
        /// is governed by the weakest-link rule and the configured dependence rather than by
        /// weights.
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
        /// The wrapped response function — referenced, not owned. Null while unconfigured (reported
        /// by the owning composite's <c>Validate()</c>).
        /// </summary>
        public IResponseFunction? ResponseFunction
        {
            get { return _responseFunction; }
            set
            {
                if (ReferenceEquals(_responseFunction, value)) return;

                if (_responseFunction != null) _responseFunction.PropertyChanged -= WrappedFunctionChanged;
                _responseFunction = value;
                if (_responseFunction != null) _responseFunction.PropertyChanged += WrappedFunctionChanged;

                RaisePropertyChange(nameof(ResponseFunction));
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
