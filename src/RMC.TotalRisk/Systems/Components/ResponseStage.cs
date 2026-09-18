using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Xml.Linq;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees;
using RMC.TotalRisk.RiskFunctions.Responses.Trees.Support;

namespace RMC.TotalRisk.Systems.Components
{
    /// <summary>
    /// One response stage of a failure-mode chain: the ordered transform functions that convert
    /// the incoming hazard, followed by the response function evaluated at the transformed hazard.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// New in v1.1: v1.0 fixed every failure mode to exactly one response bracketed by two
    /// transform lists; v1.1 generalizes the chain to one or more stages in sequence
    /// (<c>T* (R T*)* C</c>), where each stage is this type. A single-stage failure mode
    /// reproduces the v1.0 shape exactly — the v1.0 <c>HazardToResponse</c> and
    /// <c>ResponseFunction</c> members survive on <c>FailureMode</c> as views over stage 0.
    /// </para>
    /// <para>
    /// <see cref="Response"/> is never null: assigning null coerces to a fresh
    /// <see cref="NonFailResponse"/>, preserving the v1.0 defaulting behavior. The serialized
    /// child order (<c>Transforms</c> then <c>Response</c>) is append-only contract — the stage
    /// XML participates in the failure mode's canonical-hash identity surface.
    /// </para>
    /// <para>
    /// The cascade end-state design (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.9):
    /// <see cref="BranchPolarity"/> records which branch of the
    /// stage's response chance node the failure mode's path follows — <c>Fail</c> contributes
    /// <c>p(h)</c> and <c>NonFail</c> contributes <c>1 − p(h)</c> to the mode's polarity-product
    /// system response probability. The attribute is always written resolved (the
    /// <c>ConsequenceHazardPosition</c> precedent), a deliberate, documented hash/re-pin event:
    /// every stage hash predating the attribute moved once, and a missing attribute loads
    /// forward as <c>Fail</c> — the v1.0-implied branch.
    /// </para>
    /// </remarks>
    public sealed class ResponseStage : INotifyPropertyChanged
    {
        #region Construction

        /// <summary>
        /// Initializes an empty response stage: no transforms, with the non-failure response
        /// sentinel (the v1.0 failure-mode default).
        /// </summary>
        public ResponseStage()
        {
        }

        /// <summary>
        /// Initializes a response stage from a transform chain and a response.
        /// </summary>
        /// <param name="transforms">
        /// The ordered transforms applied to the incoming hazard before the response; copied into
        /// the stage.
        /// </param>
        /// <param name="response">
        /// The response function; null coerces to a fresh <see cref="NonFailResponse"/> (v1.0
        /// defaulting).
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the transform list is null.</exception>
        public ResponseStage(IList<ITransformFunction> transforms, IResponseFunction? response)
        {
            if (transforms == null) throw new ArgumentNullException(nameof(transforms));

            _transforms = new List<ITransformFunction>(transforms);
            _response = response ?? new NonFailResponse();
        }

        /// <summary>
        /// Initializes a response stage from a transform chain, a response, and a branch polarity.
        /// </summary>
        /// <param name="transforms">
        /// The ordered transforms applied to the incoming hazard before the response; copied into
        /// the stage.
        /// </param>
        /// <param name="response">
        /// The response function; null coerces to a fresh <see cref="NonFailResponse"/> (v1.0
        /// defaulting).
        /// </param>
        /// <param name="branchPolarity">
        /// The branch of the response chance node this stage follows.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when the transform list is null.</exception>
        public ResponseStage(IList<ITransformFunction> transforms, IResponseFunction? response, BranchPolarity branchPolarity)
            : this(transforms, response)
        {
            _branchPolarity = branchPolarity;
        }

        /// <summary>Initializes a response stage that selects one expanded response branch.</summary>
        /// <param name="transforms">The ordered transforms applied before the response.</param>
        /// <param name="response">The branching response.</param>
        /// <param name="branch">The selected stable branch descriptor.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public ResponseStage(IList<ITransformFunction> transforms, IResponseFunction response,
            ResponseBranchDescriptor branch)
            : this(transforms, response,
                (branch ?? throw new ArgumentNullException(nameof(branch))).IsFailure
                    ? BranchPolarity.Fail
                    : BranchPolarity.NonFail)
        {
            _selectedBranchId = branch.Id;
            _selectedBranchName = branch.Name;
        }

        /// <summary>
        /// Restores a response stage from its serialized form.
        /// </summary>
        /// <param name="xElement">The serialized form produced by <see cref="ToXElement"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when the element is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a serialized transform or response child cannot be reconstructed. Unlike
        /// graph-level deserialization (which skips unknown elements gracefully), a compute chain
        /// must reconstruct faithfully — silently dropping a function would change results.
        /// </exception>
        public ResponseStage(XElement xElement)
        {
            if (xElement == null) throw new ArgumentNullException(nameof(xElement));

            // A missing attribute loads an earlier payload forward as the v1.0-implied Fail branch.
            _branchPolarity = SerializationUtilities.ReadEnum(xElement, nameof(BranchPolarity), BranchPolarity.Fail);
            if (Guid.TryParse(xElement.Attribute(nameof(SelectedBranchId))?.Value, out Guid branchId))
                _selectedBranchId = branchId;
            _selectedBranchName = xElement.Attribute(nameof(SelectedBranchName))?.Value;

            var transformsElement = xElement.Element(nameof(Transforms));
            if (transformsElement != null)
            {
                foreach (var child in transformsElement.Elements())
                {
                    var transform = RiskFunctionFactory.CreateTransformFunction(child);
                    if (transform == null)
                    {
                        throw new InvalidOperationException(
                            $"Unrecognized transform function element '{child.Name.LocalName}' in a serialized response stage. " +
                            "The stage cannot be reconstructed faithfully; the serialized form may come from a newer version.");
                    }
                    _transforms.Add(transform);
                }
            }

            var responseChild = xElement.Element(nameof(Response))?.Elements().FirstOrDefault();
            if (responseChild != null)
            {
                var response = RiskFunctionFactory.CreateResponseFunction(responseChild);
                _response = response ?? throw new InvalidOperationException(
                    $"Unrecognized response function element '{responseChild.Name.LocalName}' in a serialized response stage. " +
                    "The stage cannot be reconstructed faithfully; the serialized form may come from a newer version.");
            }

            if (!_selectedBranchId.HasValue && !string.IsNullOrEmpty(_selectedBranchName))
            {
                if (_response is not IBranchingResponseFunction branching)
                    throw new InvalidOperationException(
                        "A serialized response stage carries a branch-name fallback, but its response is not branching.");
                ResponseBranchDescriptor[] matches = branching.GetBranches()
                    .Where(branch => string.Equals(branch.Name, _selectedBranchName,
                        StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException(
                        $"Serialized response-stage branch-name fallback '{_selectedBranchName}' is {(matches.Length == 0 ? "missing" : "ambiguous")}.");
                _selectedBranchId = matches[0].Id;
            }
        }

        #endregion

        #region Members

        /// <summary>
        /// Backing field for <see cref="Transforms"/>.
        /// </summary>
        private List<ITransformFunction> _transforms = new List<ITransformFunction>();

        /// <summary>
        /// Backing field for <see cref="Response"/> — the v1.0 non-failure default.
        /// </summary>
        private IResponseFunction _response = new NonFailResponse();

        /// <summary>
        /// Backing field for <see cref="BranchPolarity"/> — the v1.0-implied Fail branch.
        /// </summary>
        private BranchPolarity _branchPolarity = BranchPolarity.Fail;

        /// <summary>The selected expanded branch id, or null for aggregate Fail/Non-Fail selection.</summary>
        private Guid? _selectedBranchId;

        /// <summary>The selected expanded branch-name fallback.</summary>
        private string? _selectedBranchName;

        /// <summary>
        /// The ordered transform functions applied to the incoming hazard before the response.
        /// Assigning null coerces to an empty list; the stage takes ownership of an assigned list.
        /// </summary>
        public List<ITransformFunction> Transforms
        {
            get { return _transforms; }
            set
            {
                if (!ReferenceEquals(_transforms, value))
                {
                    _transforms = value ?? new List<ITransformFunction>();
                    RaisePropertyChange(nameof(Transforms));
                }
            }
        }

        /// <summary>
        /// The response function evaluated at the stage's transformed hazard. Never null:
        /// assigning null coerces to a fresh <see cref="NonFailResponse"/> (v1.0 defaulting).
        /// </summary>
        public IResponseFunction Response
        {
            get { return _response; }
            set
            {
                var coerced = value ?? new NonFailResponse();
                if (!ReferenceEquals(_response, coerced))
                {
                    _response = coerced;
                    RaisePropertyChange(nameof(Response));
                }
            }
        }

        /// <summary>
        /// The branch of the stage's response chance node this failure mode's path follows:
        /// <see cref="BranchPolarity.Fail"/> (output port 0 — the v1.0-implied default)
        /// contributes <c>p(h)</c> to the mode's polarity product,
        /// <see cref="BranchPolarity.NonFail"/> (output port 1) contributes <c>1 − p(h)</c>.
        /// Serialized always (resolved-on-write) and part of the canonical-hash identity surface —
        /// flipping the polarity is a compute edit that moves seeds.
        /// </summary>
        public BranchPolarity BranchPolarity
        {
            get { return _branchPolarity; }
            set
            {
                if (_branchPolarity != value)
                {
                    _branchPolarity = value;
                    RaisePropertyChange(nameof(BranchPolarity));
                }
            }
        }

        /// <summary>The selected expanded branch id, or null for aggregate Fail/Non-Fail selection.</summary>
        public Guid? SelectedBranchId => _selectedBranchId;

        /// <summary>The selected expanded branch-name migration fallback.</summary>
        public string? SelectedBranchName => _selectedBranchName;

        /// <summary>Gets the selected expanded branch descriptor.</summary>
        /// <returns>The matching branch, or null when this stage uses aggregate Fail/Non-Fail selection.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the stored branch is stale or the response is not branching.</exception>
        public ResponseBranchDescriptor? GetSelectedBranch()
        {
            if (!_selectedBranchId.HasValue) return null;
            if (_response is not IBranchingResponseFunction branching)
                throw new InvalidOperationException(
                    "A response stage selects an expanded branch, but its response function is not branching.");
            ResponseBranchDescriptor? branch = branching.GetBranches()
                .FirstOrDefault(item => item.Id == _selectedBranchId.Value);
            return branch ?? throw new InvalidOperationException(
                $"The response stage references stale branch id '{_selectedBranchId.Value:D}' on response '{_response.Name}'.");
        }

        /// <summary>
        /// Determines whether every transform and the response carry no knowledge uncertainty.
        /// Null transform entries are skipped (validation reports them).
        /// </summary>
        public bool IsDeterministic
        {
            get
            {
                for (int i = 0; i < _transforms.Count; i++)
                {
                    if (_transforms[i] is not null && !_transforms[i].IsDeterministic) return false;
                }
                return _response.IsDeterministic;
            }
        }

        /// <summary>
        /// Raised when a stage property changes. Passive contract — headless callers need not
        /// subscribe.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        #endregion

        #region Methods

        /// <summary>
        /// Validates the stage and reports any issues found.
        /// </summary>
        /// <returns>
        /// A tuple containing:
        /// <list type="bullet">
        /// <item>
        /// <description><c>IsValid</c>: <c>true</c> if the stage passes all validation checks; otherwise <c>false</c>.</description>
        /// </item>
        /// <item>
        /// <description><c>ValidationMessages</c>: messages describing validation errors ("Error: …", invalidating) and warnings ("Warning: …", advisory).</description>
        /// </item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// Null transform entries are errors; the transforms' and response's own validation
        /// messages are aggregated. Hazard-type continuity along the chain is validated by the
        /// owning failure mode, which sees the whole path.
        /// </remarks>
        public (bool IsValid, List<string> ValidationMessages) Validate()
        {
            var messages = new List<string>();

            for (int i = 0; i < _transforms.Count; i++)
            {
                if (_transforms[i] is null)
                {
                    messages.Add($"Error: The response stage transform at index {i} has not been defined.");
                    continue;
                }
                messages.AddRange(_transforms[i].Validate().ValidationMessages);
            }

            messages.AddRange(_response.Validate().ValidationMessages);
            if (_selectedBranchId.HasValue)
            {
                try
                {
                    ResponseBranchDescriptor branch = GetSelectedBranch()!;
                    BranchPolarity expected = branch.IsFailure ? BranchPolarity.Fail : BranchPolarity.NonFail;
                    if (_branchPolarity != expected)
                        messages.Add($"Error: The response stage's branch polarity does not match selected branch '{branch.Name}'.");
                }
                catch (InvalidOperationException ex)
                {
                    messages.Add($"Error: {ex.Message}");
                }
            }

            return (messages.FindIndex(m => m.StartsWith("Error:", StringComparison.Ordinal)) < 0, messages);
        }

        #endregion

        #region Serialization

        /// <summary>
        /// Serializes the stage to an XElement: the <c>BranchPolarity</c> attribute (always
        /// written resolved — the documented one-time hash event; an earlier payload without it
        /// loads forward as <c>Fail</c>), then the transform chain in order under <c>Transforms</c>,
        /// then the response under <c>Response</c>. Attribute names and child order are
        /// append-only contract (the stage XML feeds the failure mode's canonical hash). Null
        /// transform entries are skipped (validation reports them).
        /// </summary>
        /// <returns>The serialized form.</returns>
        public XElement ToXElement()
        {
            var element = new XElement(nameof(ResponseStage));
            element.SetAttributeValue(nameof(BranchPolarity), _branchPolarity.ToString());
            if (_selectedBranchId.HasValue)
            {
                if (_response is not EventTreeResponse eventTree)
                    throw new InvalidOperationException(
                        "Expanded branch identity is currently defined only for EventTreeResponse.");
                ResponseBranchDescriptor selected = GetSelectedBranch()!;
                element.SetAttributeValue("SelectedBranchIdentity",
                    eventTree.GetBranchIdentityToken(selected.Id));
                element.SetAttributeValue(nameof(SelectedBranchId), selected.Id.ToString("D"));
                element.SetAttributeValue(nameof(SelectedBranchName), selected.Name);
            }

            var transforms = new XElement(nameof(Transforms));
            for (int i = 0; i < _transforms.Count; i++)
            {
                if (_transforms[i] is not null) transforms.Add(_transforms[i].ToXElement());
            }
            element.Add(transforms);

            element.Add(new XElement(nameof(Response), _response.ToXElement()));
            return element;
        }

        /// <summary>Builds the projected identity form used by failure-mode and component hashing.</summary>
        /// <returns>The identity form.</returns>
        internal XElement ToIdentityXElement()
        {
            XElement element = ToXElement();
            if (_response is IProjectedIdentityResponse projected)
            {
                XElement? persisted = element.Element(nameof(Response))?.Elements().SingleOrDefault();
                persisted?.ReplaceWith(projected.ToIdentityXElement());
            }
            return element;
        }


        #endregion

        #region Private Helpers

        /// <summary>
        /// Raises the <see cref="PropertyChanged"/> event.
        /// </summary>
        /// <param name="propertyName">The name of the property that changed.</param>
        private void RaisePropertyChange(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
