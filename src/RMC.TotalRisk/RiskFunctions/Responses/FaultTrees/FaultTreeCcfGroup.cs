using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees.Nodes;

namespace RMC.TotalRisk.RiskFunctions.Responses.FaultTrees
{
    /// <summary>
    /// One parametric common-cause failure group over a fault tree's basic events: a named,
    /// ordered set of exchangeable member events whose shared total failure probability is split
    /// across independent and common-cause derived events by a parametric model. The owning tree
    /// expands the group at compile time into derived Boolean events the exact decision diagram
    /// evaluates unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Every model maps onto one per-multiplicity factor kernel: with group size n and total
    /// member probability Q, the derived event of multiplicity k carries probability f_k · Q,
    /// each member owning one independent event (k = 1) and one derived event existing per
    /// member combination of each multiplicity k ≥ 2. All three models satisfy the exact
    /// per-member identity Σ C(n−1, k−1) · f_k = 1, so the rare-event sum of a member's derived
    /// events recovers Q exactly; the exact engine evaluates the exact Boolean union of the
    /// derived events rather than the rare-event approximation. The alpha-factor mapping is the
    /// non-staggered convention f_k = k·α_k / (C(n−1, k−1)·α_t) with α_t = Σ k·α_k; beta-factor
    /// uses f_1 = 1 − β and f_n = β; the multiple Greek letter model uses
    /// f_k = (Π ρ_i, i ≤ k)·(1 − ρ_{k+1}) / C(n−1, k−1) with ρ_1 = 1 and ρ_{n+1} = 0.
    /// </para>
    /// <para>
    /// Declaring a group is the statement that its members are one exchangeable population:
    /// member sources must be content-identical, and the expansion samples ONE shared
    /// total-probability basis stream per group per independent context — the state-of-knowledge
    /// correlation of one population parameter — with the deterministic factors applied to it.
    /// Configuring a group is deliberate compute content: groups serialize as a conditional
    /// child of the fault tree, so every group-free tree keeps a byte-identical form, canonical
    /// identity, and seed.
    /// </para>
    /// </remarks>
    public sealed class FaultTreeCcfGroup : INotifyPropertyChanged
    {
        /// <summary>The largest supported group size; the expansion creates 2^n − n − 1 combination events.</summary>
        public const int MaximumGroupSize = 6;

        /// <summary>Initializes an empty beta-factor group.</summary>
        public FaultTreeCcfGroup()
        {
        }

        /// <summary>Initializes a configured group.</summary>
        /// <param name="name">The group name.</param>
        /// <param name="model">The parametric model.</param>
        /// <param name="parameters">The model parameters.</param>
        /// <param name="memberNodeIds">The ordered member basic-event node ids.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public FaultTreeCcfGroup(string name, FaultTreeCcfModel model, IReadOnlyList<double> parameters,
            IReadOnlyList<Guid> memberNodeIds)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _model = model;
            _parameters = (parameters ?? throw new ArgumentNullException(nameof(parameters))).ToArray();
            _memberNodeIds = (memberNodeIds ?? throw new ArgumentNullException(nameof(memberNodeIds))).ToArray();
        }

        /// <summary>Restores a group from its serialized form. Reads are permissive; malformed content fails validation.</summary>
        /// <param name="xElement">The serialized group.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        internal FaultTreeCcfGroup(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));
            _name = SerializationUtilities.ReadString(xElement, nameof(Name));
            _description = SerializationUtilities.ReadString(xElement, nameof(Description));
            _model = SerializationUtilities.ReadEnum(xElement, nameof(Model), FaultTreeCcfModel.BetaFactor);
            string parameters = SerializationUtilities.ReadString(xElement, nameof(Parameters));
            _parameters = parameters.Length == 0
                ? Array.Empty<double>()
                : parameters.Split('|').Select(token => SerializationUtilities.ParseDouble(token, double.NaN)).ToArray();
            string members = SerializationUtilities.ReadString(xElement, nameof(MemberNodeIds));
            _memberNodeIds = members.Length == 0
                ? Array.Empty<Guid>()
                : members.Split('|').Select(token => Guid.TryParse(token, out Guid id) ? id : Guid.Empty).ToArray();
        }

        /// <summary>Backing field for <see cref="Name"/>.</summary>
        private string _name = string.Empty;

        /// <summary>Backing field for <see cref="Description"/>.</summary>
        private string _description = string.Empty;

        /// <summary>Backing field for <see cref="Model"/>.</summary>
        private FaultTreeCcfModel _model = FaultTreeCcfModel.BetaFactor;

        /// <summary>Backing field for <see cref="Parameters"/>.</summary>
        private double[] _parameters = Array.Empty<double>();

        /// <summary>Backing field for <see cref="MemberNodeIds"/>.</summary>
        private Guid[] _memberNodeIds = Array.Empty<Guid>();

        /// <inheritdoc/>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>The group display name. Metadata — serialized, never hashed.</summary>
        public string Name
        {
            get { return _name; }
            set
            {
                if (_name == value) return;
                _name = value ?? string.Empty;
                RaisePropertyChanged(nameof(Name));
            }
        }

        /// <summary>The group description. Metadata — serialized, never hashed.</summary>
        public string Description
        {
            get { return _description; }
            set
            {
                if (_description == value) return;
                _description = value ?? string.Empty;
                RaisePropertyChanged(nameof(Description));
            }
        }

        /// <summary>The parametric model. Compute-relevant.</summary>
        public FaultTreeCcfModel Model
        {
            get { return _model; }
            set
            {
                if (_model == value) return;
                _model = value;
                RaisePropertyChanged(nameof(Model));
            }
        }

        /// <summary>
        /// The model parameters, in the model's published order: beta-factor takes [β]; the
        /// multiple Greek letter model takes [β, γ, δ, …] (one conditional parameter per
        /// multiplicity two through the group size); the alpha-factor model takes [α_1 … α_n].
        /// Compute-relevant.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when set to null.</exception>
        public IReadOnlyList<double> Parameters
        {
            get { return _parameters; }
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                _parameters = value.ToArray();
                RaisePropertyChanged(nameof(Parameters));
            }
        }

        /// <summary>The ordered member basic-event node ids. Compute-relevant through the members' expansion positions.</summary>
        /// <exception cref="ArgumentNullException">Thrown when set to null.</exception>
        public IReadOnlyList<Guid> MemberNodeIds
        {
            get { return _memberNodeIds; }
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                _memberNodeIds = value.ToArray();
                RaisePropertyChanged(nameof(MemberNodeIds));
            }
        }

        /// <summary>Serializes the group.</summary>
        /// <returns>The serialized group.</returns>
        internal XElement ToXElement()
        {
            var element = new XElement(nameof(FaultTreeCcfGroup));
            element.SetAttributeValue(nameof(Name), _name);
            element.SetAttributeValue(nameof(Description), _description);
            element.SetAttributeValue(nameof(Model), _model.ToString());
            element.SetAttributeValue(nameof(Parameters),
                string.Join("|", _parameters.Select(SerializationUtilities.FormatDouble)));
            element.SetAttributeValue(nameof(MemberNodeIds),
                string.Join("|", _memberNodeIds.Select(id => id.ToString("D"))));
            return element;
        }

        /// <summary>
        /// Computes the per-multiplicity factor vector f_1 … f_n, where a derived event of
        /// multiplicity k carries probability f_k times the shared member total.
        /// </summary>
        /// <returns>The factors, indexed by multiplicity minus one.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid for the model.</exception>
        internal double[] ComputeFactors()
        {
            int size = _memberNodeIds.Length;
            if (size < 2 || size > MaximumGroupSize)
                throw new InvalidOperationException(FactorError("has an unsupported member count"));
            var factors = new double[size];
            switch (_model)
            {
                case FaultTreeCcfModel.BetaFactor:
                {
                    if (_parameters.Length != 1 || !IsUnitInterval(_parameters[0]))
                        throw new InvalidOperationException(FactorError("requires one beta parameter in [0, 1]"));
                    double beta = _parameters[0];
                    factors[0] = 1d - beta;
                    factors[size - 1] = beta;
                    return factors;
                }
                case FaultTreeCcfModel.MultipleGreekLetter:
                {
                    if (_parameters.Length != size - 1 || _parameters.Any(value => !IsUnitInterval(value)))
                        throw new InvalidOperationException(FactorError("requires one conditional parameter in [0, 1] per multiplicity two through the group size"));
                    double running = 1d;
                    for (int k = 1; k <= size; k++)
                    {
                        double next = k < size ? _parameters[k - 1] : 0d;
                        factors[k - 1] = running * (1d - next) / Binomial(size - 1, k - 1);
                        running *= next;
                    }
                    return factors;
                }
                case FaultTreeCcfModel.AlphaFactor:
                {
                    if (_parameters.Length != size || _parameters.Any(value => !double.IsFinite(value) || value < 0d))
                        throw new InvalidOperationException(FactorError("requires one non-negative alpha fraction per multiplicity"));
                    double total = 0d;
                    double weighted = 0d;
                    for (int k = 1; k <= size; k++)
                    {
                        total += _parameters[k - 1];
                        weighted += k * _parameters[k - 1];
                    }
                    if (Math.Abs(total - 1d) > 1e-8 || weighted <= 0d)
                        throw new InvalidOperationException(FactorError("requires alpha fractions summing to one"));
                    for (int k = 1; k <= size; k++)
                        factors[k - 1] = k * _parameters[k - 1] / (Binomial(size - 1, k - 1) * weighted);
                    return factors;
                }
                default:
                    throw new InvalidOperationException(FactorError($"has unsupported model '{_model}'"));
            }
        }

        /// <summary>
        /// Collects the group's configuration errors against its owning tree: member count and
        /// resolution, member kinds, duplicate membership, model parameters, and the
        /// content-identical member-source requirement.
        /// </summary>
        /// <param name="tree">The owning fault tree.</param>
        /// <returns>Deterministically ordered error messages; empty when the group is expandable.</returns>
        internal List<string> GetConfigurationErrors(FaultTree tree)
        {
            var messages = new List<string>();
            string label = $"Fault-tree CCF group '{_name}'";
            int size = _memberNodeIds.Length;
            if (size < 2)
                messages.Add($"Error: {label} requires at least two member basic events.");
            if (size > MaximumGroupSize)
                messages.Add($"Error: {label} exceeds the supported group size of {MaximumGroupSize}.");
            if (_memberNodeIds.Distinct().Count() != size)
                messages.Add($"Error: {label} lists a member basic event more than once.");

            var members = new List<FaultTreeBasicEventNode>(size);
            foreach (Guid id in _memberNodeIds)
            {
                FaultTreeNodeBase? node = tree.FindNode(id);
                if (node == null)
                {
                    messages.Add($"Error: {label} references member node '{id:D}', which was not found.");
                }
                else if (node is not FaultTreeBasicEventNode basic)
                {
                    messages.Add($"Error: {label} member '{node.Name}' is not a basic event.");
                }
                else
                {
                    members.Add(basic);
                }
            }

            switch (_model)
            {
                case FaultTreeCcfModel.BetaFactor:
                    if (_parameters.Length != 1 || !IsUnitInterval(_parameters[0]))
                        messages.Add($"Error: {label} requires one beta parameter in [0, 1].");
                    break;
                case FaultTreeCcfModel.MultipleGreekLetter:
                    if (size >= 2 && (_parameters.Length != size - 1 || _parameters.Any(value => !IsUnitInterval(value))))
                        messages.Add($"Error: {label} requires one conditional parameter in [0, 1] per multiplicity two through the group size.");
                    break;
                case FaultTreeCcfModel.AlphaFactor:
                    if (size >= 2)
                    {
                        if (_parameters.Length != size || _parameters.Any(value => !double.IsFinite(value) || value < 0d))
                            messages.Add($"Error: {label} requires one non-negative alpha fraction per multiplicity.");
                        else if (Math.Abs(_parameters.Sum() - 1d) > 1e-8)
                            messages.Add($"Error: {label} requires alpha fractions summing to one.");
                        else if (Enumerable.Range(1, size).Sum(k => k * _parameters[k - 1]) <= 0d)
                            messages.Add($"Error: {label} requires a positive expected failure multiplicity.");
                    }
                    break;
                default:
                    messages.Add($"Error: {label} has unsupported model '{_model}'.");
                    break;
            }

            if (members.Count == size && size >= 2)
            {
                try
                {
                    string basis = members[0].ProbabilitySource.CanonicalToken();
                    for (int i = 1; i < members.Count; i++)
                    {
                        if (!string.Equals(basis, members[i].ProbabilitySource.CanonicalToken(), StringComparison.Ordinal))
                        {
                            messages.Add($"Error: {label} member '{members[i].Name}' does not have the same probability-source content as member '{members[0].Name}'; an exchangeable group shares one total failure probability.");
                        }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException
                    || ex is InvalidOperationException || ex is NotSupportedException)
                {
                    messages.Add($"Error: {label} member sources cannot be hashed for the exchangeability check: {ex.Message}");
                }
            }
            return messages;
        }

        /// <summary>Computes the binomial coefficient for the small group sizes the expansion supports.</summary>
        /// <param name="n">The set size.</param>
        /// <param name="k">The selection size.</param>
        /// <returns>The coefficient.</returns>
        internal static double Binomial(int n, int k)
        {
            if (k < 0 || k > n) return 0d;
            double result = 1d;
            for (int i = 0; i < k; i++) result = result * (n - i) / (i + 1);
            return Math.Round(result);
        }

        /// <summary>Tests a parameter for membership in the closed unit interval.</summary>
        /// <param name="value">The parameter.</param>
        /// <returns>True when finite and within [0, 1].</returns>
        private static bool IsUnitInterval(double value)
        {
            return double.IsFinite(value) && value >= 0d && value <= 1d;
        }

        /// <summary>Builds one factor-computation error message.</summary>
        /// <param name="reason">The failure reason.</param>
        /// <returns>The message.</returns>
        private string FactorError(string reason)
        {
            return $"Fault-tree CCF group '{_name}' {reason}. Call Validate() and correct the reported errors.";
        }

        /// <summary>Raises one property-change notification.</summary>
        /// <param name="propertyName">The property name.</param>
        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
