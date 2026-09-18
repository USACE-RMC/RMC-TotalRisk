using System.ComponentModel;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions.Support;

namespace RMC.TotalRisk.RiskFunctions.Hazards
{
    /// <summary>
    /// A hazard function paired with a weight — one entry in a <see cref="CompositeHazard"/>'s
    /// weighted child list.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// The sibling of <c>WeightedConsequenceFunction</c>, with the same contract: the wrapped
    /// function is referenced, not owned (a consuming layer may store one function and use it in
    /// several composites), the setter swaps a change subscription, and the wrapped function's own
    /// change notifications are forwarded verbatim so owners can filter by the child's property
    /// name. A child may itself be a <see cref="CompositeHazard"/> — nesting is well-defined, and
    /// <see cref="CompositeHazard.Validate"/> rejects circular references.
    /// </para>
    /// <para>
    /// Improved over the v1.0 <c>WeightedHazardFunction</c>, whose <c>ToXElement()</c> wrote a
    /// <b>name-based</b> reference to the child's <c>NameOnDisk</c>: the entry does not serialize
    /// itself at all. The owning composite writes the weight and the child entry (inline content or
    /// an id-bearing <c>FunctionReference</c> marker) through the shared <c>FunctionEntry</c>
    /// mechanics, so the reference format cannot drift per container and a rename cannot break a
    /// link.
    /// </para>
    /// </remarks>
    public class WeightedHazardFunction : INotifyPropertyChanged
    {
        #region Construction

        /// <summary>
        /// Initializes an empty entry (no function, weight zero — the v1.0 defaults).
        /// </summary>
        public WeightedHazardFunction()
        {
        }

        /// <summary>
        /// Initializes an entry with the specified function and weight.
        /// </summary>
        /// <param name="hazardFunction">The hazard function; may be null while unconfigured.</param>
        /// <param name="weight">The weight, in [0, 1] with weights summing to one across the composite.</param>
        public WeightedHazardFunction(IHazardFunction? hazardFunction, double weight)
        {
            HazardFunction = hazardFunction;
            Weight = weight;
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="Weight"/>.
        /// </summary>
        private double _weight;

        /// <summary>
        /// Backing field for <see cref="HazardFunction"/>.
        /// </summary>
        private IHazardFunction? _hazardFunction;

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
        /// is governed by the maximum rule and the configured dependence rather than by weights.
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
        /// The wrapped hazard function — referenced, not owned. Null while unconfigured (reported
        /// by the owning composite's <c>Validate()</c>).
        /// </summary>
        public IHazardFunction? HazardFunction
        {
            get { return _hazardFunction; }
            set
            {
                if (ReferenceEquals(_hazardFunction, value)) return;

                if (_hazardFunction != null) _hazardFunction.PropertyChanged -= WrappedFunctionChanged;
                _hazardFunction = value;
                if (_hazardFunction != null) _hazardFunction.PropertyChanged += WrappedFunctionChanged;

                RaisePropertyChange(nameof(HazardFunction));
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
