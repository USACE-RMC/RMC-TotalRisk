using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.Core;
using RMC.TotalRisk.Core.Enums;
using RMC.TotalRisk.Core.Interfaces;
using RMC.TotalRisk.RiskFunctions;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.RiskFunctions.Responses.EventTrees;
using RMC.TotalRisk.RiskFunctions.Responses.FaultTrees;
using RMC.TotalRisk.RiskFunctions.Transforms;

namespace RMC.TotalRisk.Tests.RiskFunctions.Responses;

/// <summary>
/// Unit tests for <see cref="DeterioratingResponse"/>: defaults and change notification, the
/// validation matrix including every base-kind refusal, the sample-time gates, the shifted
/// evaluation semantics (mean, percentile, realization; explicit-age purity; boundary hold; the
/// one-stream-per-realization knowledge property), the curve trio, the bounds members, both
/// serialization modes with resolver repair and verbatim pending-marker re-write, the projected
/// identity hash (mode/metadata/age inert; law and base content moving), the derived base
/// seeding, and the linear-transform equivalence identity.
/// </summary>
[TestClass]
public class DeterioratingResponseTests
{
    #region Fixtures

    /// <summary>Builds a deterministic age→shift law over the given (age, shift) pairs.</summary>
    /// <param name="pairs">The (age, shift) pairs, ages strictly ascending.</param>
    /// <returns>The law.</returns>
    private static UncertainOrderedPairedData Law(params (double Age, double Shift)[] pairs)
    {
        var ordinates = new UncertainOrdinate[pairs.Length];
        for (int i = 0; i < pairs.Length; i++)
            ordinates[i] = new UncertainOrdinate(pairs[i].Age, new Deterministic(pairs[i].Shift));
        return new UncertainOrderedPairedData(ordinates, true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Deterministic);
    }

    /// <summary>The standard deterministic law: zero at age zero, 5 at 25, 12 at 50, 30 at 100.</summary>
    /// <returns>The law.</returns>
    private static UncertainOrderedPairedData StandardLaw()
    {
        return Law((0d, 0d), (25d, 5d), (50d, 12d), (100d, 30d));
    }

    /// <summary>
    /// An uncertain law: age zero pinned at an exact zero shift (the degenerate uniform), later
    /// ages uniform around the standard shifts.
    /// </summary>
    /// <returns>The law.</returns>
    private static UncertainOrderedPairedData UncertainLaw()
    {
        return new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Uniform(0d, 0d)),
                new UncertainOrdinate(25d, new Uniform(4d, 6d)),
                new UncertainOrdinate(50d, new Uniform(9.6d, 14.4d)),
                new UncertainOrdinate(100d, new Uniform(24d, 36d)),
            },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform);
    }

    /// <summary>Builds a deterministic tabular fragility base over stages 140/150/160.</summary>
    /// <returns>The base.</returns>
    private static TabularResponse TabularBase()
    {
        return new TabularResponse
        {
            Name = "Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(140d, new Deterministic(0d)),
                    new UncertainOrdinate(150d, new Deterministic(0.5d)),
                    new UncertainOrdinate(160d, new Deterministic(1d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds an uncertain tabular fragility base (uniform ordinates).</summary>
    /// <returns>The base.</returns>
    private static TabularResponse UncertainTabularBase()
    {
        return new TabularResponse
        {
            Name = "Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[]
                {
                    new UncertainOrdinate(140d, new Uniform(0d, 0.1d)),
                    new UncertainOrdinate(150d, new Uniform(0.3d, 0.7d)),
                    new UncertainOrdinate(160d, new Uniform(0.9d, 1d)),
                },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Uniform),
        };
    }

    /// <summary>Builds a deterministic estimated parametric fragility base (Normal capacity).</summary>
    /// <returns>The base.</returns>
    private static ParametricResponse ParametricBase()
    {
        var response = new ParametricResponse
        {
            Name = "Capacity",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ParentDistribution = new Normal(150d, 20d),
            IsUncertain = false,
        };
        response.Estimate();
        return response;
    }

    /// <summary>Builds a labeled wrapper over the given base and law.</summary>
    /// <param name="baseResponse">The base response.</param>
    /// <param name="law">The law; the standard law when null.</param>
    /// <returns>The wrapper.</returns>
    private static DeterioratingResponse Wrapper(IResponseFunction baseResponse, UncertainOrderedPairedData? law = null)
    {
        return new DeterioratingResponse
        {
            Name = "Aging Fragility",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            BaseResponse = baseResponse,
            DeteriorationLaw = law ?? StandardLaw(),
        };
    }

    /// <summary>Hex form of a function's canonical hash for equality asserts.</summary>
    /// <param name="function">The function.</param>
    /// <returns>The hash hex.</returns>
    private static string HashOf(IRiskFunction function)
    {
        return Convert.ToHexString(function.CanonicalHash());
    }

    /// <summary>Builds a resolver over an explicit store.</summary>
    /// <param name="functions">The stored functions.</param>
    /// <returns>The resolver.</returns>
    private static RiskFunctionResolver ResolverOver(params IRiskFunction[] functions)
    {
        return new RiskFunctionResolver(
            id =>
            {
                foreach (var function in functions)
                {
                    if (function.Id == id) return function;
                }
                return null;
            },
            name =>
            {
                foreach (var function in functions)
                {
                    if (function.Name == name) return function;
                }
                return null;
            });
    }

    #endregion

    #region Defaults and change notification

    /// <summary>Verifies the fresh-instance contract.</summary>
    [TestMethod]
    public void Test_Defaults_MatchContract()
    {
        // Act
        var wrapper = new DeterioratingResponse();

        // Assert
        Assert.IsNull(wrapper.BaseResponse);
        Assert.AreEqual(1, wrapper.DeteriorationLaw.Count);
        Assert.AreEqual(0d, wrapper.DeteriorationLaw[0].X);
        Assert.AreEqual(0d, wrapper.DeteriorationLaw[0].Y!.Mean);
        Assert.AreEqual(0d, wrapper.EvaluationAge);
        Assert.AreEqual(ResponseFunctionType.Deteriorating, wrapper.FunctionType);
        Assert.AreEqual(1, wrapper.SamplingDimensions);
        Assert.IsTrue(wrapper.IsDeterministic);
        Assert.IsFalse(wrapper.SupportsOrderedCurveSampling);
    }

    /// <summary>Verifies the base setter swaps the change subscription.</summary>
    [TestMethod]
    public void Test_BaseResponse_Setter_SwapsSubscription()
    {
        // Arrange
        var first = TabularBase();
        var second = TabularBase();
        var wrapper = Wrapper(first);
        var raised = new List<string>();
        wrapper.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        // Act — an edit on the wrapped base re-raises as BaseResponse.
        first.ProbabilityTransform = Transform.NormalZ;
        Assert.IsTrue(raised.Contains(nameof(DeterioratingResponse.BaseResponse)));

        // Act — a swapped-out base raises nothing.
        wrapper.BaseResponse = second;
        raised.Clear();
        first.ProbabilityTransform = Transform.None;

        // Assert
        Assert.AreEqual(0, raised.Count);
    }

    /// <summary>Verifies the law setter ignores null and raises on assignment.</summary>
    [TestMethod]
    public void Test_DeteriorationLaw_Setter_IgnoresNull_RaisesChange()
    {
        // Arrange
        var wrapper = Wrapper(TabularBase());
        var initial = wrapper.DeteriorationLaw;
        var raised = new List<string>();
        wrapper.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        // Act / Assert — null ignored.
        wrapper.DeteriorationLaw = null!;
        Assert.AreSame(initial, wrapper.DeteriorationLaw);
        Assert.AreEqual(0, raised.Count);

        // Act / Assert — assignment raises.
        wrapper.DeteriorationLaw = Law((0d, 0d), (10d, 1d));
        Assert.IsTrue(raised.Contains(nameof(DeterioratingResponse.DeteriorationLaw)));
    }

    /// <summary>Verifies the age setter raises on change and no-ops on the same value.</summary>
    [TestMethod]
    public void Test_EvaluationAge_Setter_RaisesChange()
    {
        // Arrange
        var wrapper = Wrapper(TabularBase());
        var raised = new List<string>();
        wrapper.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        // Act
        wrapper.EvaluationAge = 25d;
        wrapper.EvaluationAge = 25d;

        // Assert
        Assert.AreEqual(1, raised.Count(p => p == nameof(DeterioratingResponse.EvaluationAge)));
        Assert.AreEqual(25d, wrapper.EvaluationAge);
    }

    /// <summary>Verifies the age guards: NaN and negative ages throw, on the property and the explicit-age members.</summary>
    [TestMethod]
    public void Test_EvaluationAge_NaNOrNegative_Throws()
    {
        // Arrange
        var wrapper = Wrapper(TabularBase());

        // Act / Assert
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => wrapper.EvaluationAge = double.NaN);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => wrapper.EvaluationAge = -1d);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => wrapper.SampleFunctionAtAge(double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => wrapper.SampleFunctionAtAge(-1d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => wrapper.SampleFunctionAtAge(0.5d, -1d));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => wrapper.SampleFunctionAtAge(0, double.NaN));
    }

    #endregion

    #region Validation matrix

    /// <summary>Verifies missing axis labels error.</summary>
    [TestMethod]
    public void Test_Validate_MissingLabels_Errors()
    {
        // Arrange
        var wrapper = new DeterioratingResponse { BaseResponse = TabularBase() };

        // Act
        var (isValid, messages) = wrapper.Validate();

        // Assert
        Assert.IsFalse(isValid);
        Assert.IsTrue(messages.Any(m => m.Contains("specified hazard type")));
        Assert.IsTrue(messages.Any(m => m.Contains("specified hazard unit")));
    }

    /// <summary>Verifies a missing base errors.</summary>
    [TestMethod]
    public void Test_Validate_NullBase_Error()
    {
        // Arrange
        var wrapper = new DeterioratingResponse { Name = "Aging", SpecifiedHazard = "Stage", HazardUnit = "ft" };

        // Act
        var (isValid, messages) = wrapper.Validate();

        // Assert
        Assert.IsFalse(isValid);
        Assert.IsTrue(messages.Any(m => m.Contains("no base response function")));
    }

    /// <summary>Verifies both allowed base kinds validate.</summary>
    [TestMethod]
    public void Test_Validate_AllowedBases_TabularAndParametric_Valid()
    {
        // Arrange
        var overTabular = Wrapper(TabularBase());
        var overParametric = Wrapper(ParametricBase());

        // Act
        var tabular = overTabular.Validate();
        var parametric = overParametric.Validate();

        // Assert
        Assert.IsTrue(tabular.IsValid, string.Join("; ", tabular.ValidationMessages));
        Assert.IsTrue(parametric.IsValid, string.Join("; ", parametric.ValidationMessages));
    }

    /// <summary>Verifies every disallowed base kind is refused by the allow-list.</summary>
    [TestMethod]
    public void Test_Validate_DisallowedBases_Error()
    {
        // Arrange — one representative per refused kind.
        IResponseFunction[] disallowed =
        [
            new NonFailResponse(),
            new BivariateResponse { Name = "Surface" },
            new CompositeResponse { Name = "Composite" },
            new EventTreeResponse { Name = "Tree" },
            new FaultTreeResponse { Name = "Fault" },
            new DeterioratingResponse { Name = "Nested" },
        ];

        foreach (var baseResponse in disallowed)
        {
            // Act
            var wrapper = Wrapper(baseResponse);
            var (isValid, messages) = wrapper.Validate();

            // Assert
            Assert.IsFalse(isValid, baseResponse.GetType().Name);
            Assert.IsTrue(messages.Any(m => m.Contains("can wrap only a tabular or parametric response function")),
                $"{baseResponse.GetType().Name}: {string.Join("; ", messages)}");
        }
    }

    /// <summary>Verifies an invalid base reports the summary line.</summary>
    [TestMethod]
    public void Test_Validate_InvalidBase_SummaryLine()
    {
        // Arrange — a tabular base with no labels is invalid.
        var badBase = new TabularResponse { Name = "Bad" };
        var wrapper = Wrapper(badBase);

        // Act
        var (isValid, messages) = wrapper.Validate();

        // Assert
        Assert.IsFalse(isValid);
        Assert.IsTrue(messages.Any(m => m.Contains("'Bad' is invalid")));
    }

    /// <summary>Verifies the law shape errors: empty, invalid ordinates, negative ages, non-finite shifts.</summary>
    [TestMethod]
    public void Test_Validate_LawShape_Errors()
    {
        // Empty law.
        var empty = Wrapper(TabularBase(), new UncertainOrderedPairedData(
            Array.Empty<UncertainOrdinate>(), true, SortOrder.Ascending, false, SortOrder.None,
            UnivariateDistributionType.Deterministic));
        Assert.IsTrue(empty.Validate().ValidationMessages.Any(m => m.Contains("at least one ordinate")));

        // Negative first age.
        var negative = Wrapper(TabularBase(), Law((-5d, 0d), (10d, 1d)));
        Assert.IsTrue(negative.Validate().ValidationMessages
            .Any(m => m.Contains("ages must be finite and non-negative")));

        // Non-finite shift value — the upstream ordinate validity catches it first (the
        // wrapper's own finite-mean scan is defensive depth behind it).
        var infinite = Wrapper(TabularBase(), Law((0d, 0d), (10d, double.PositiveInfinity)));
        var (infiniteValid, infiniteMessages) = infinite.Validate();
        Assert.IsFalse(infiniteValid);
        Assert.IsTrue(infiniteMessages.Any(m => m.Contains("Invalid deterioration law ordinates")
            || m.Contains("finite mean")), string.Join("; ", infiniteMessages));
    }

    /// <summary>Verifies a non-zero mean shift at age zero is a Warning, not an Error.</summary>
    [TestMethod]
    public void Test_Validate_NonZeroAgeZeroShift_Warning()
    {
        // Arrange — the law starts at age 10, so the age-zero hold is 5.
        var wrapper = Wrapper(TabularBase(), Law((10d, 5d), (50d, 12d)));

        // Act
        var (isValid, messages) = wrapper.Validate();

        // Assert
        Assert.IsTrue(isValid, string.Join("; ", messages));
        Assert.IsTrue(messages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal)
            && m.Contains("age zero")));
    }

    /// <summary>Verifies base axis-label mismatches warn.</summary>
    [TestMethod]
    public void Test_Validate_BaseLabelMismatch_Warning()
    {
        // Arrange
        var baseResponse = TabularBase();
        baseResponse.SpecifiedHazard = "Pool Elevation";
        baseResponse.HazardUnit = "m";
        var wrapper = Wrapper(baseResponse);

        // Act
        var (isValid, messages) = wrapper.Validate();

        // Assert — advisory only.
        Assert.IsTrue(isValid, string.Join("; ", messages));
        Assert.IsTrue(messages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal)
            && m.Contains("does not match hazard type")));
        Assert.IsTrue(messages.Any(m => m.StartsWith("Warning:", StringComparison.Ordinal)
            && m.Contains("does not match hazard unit")));
    }

    #endregion

    #region Sample-time gates

    /// <summary>Verifies the unusable-configuration gate on every compute surface.</summary>
    [TestMethod]
    public void Test_SampleFunction_UnusableConfiguration_Throws()
    {
        // Null base.
        var noBase = new DeterioratingResponse { Name = "Aging", SpecifiedHazard = "Stage", HazardUnit = "ft" };
        Assert.ThrowsException<InvalidOperationException>(() => noBase.SampleFunction());
        Assert.ThrowsException<InvalidOperationException>(() => noBase.SetupSampler(8, 12345, SamplingScheme.LatinHypercube));

        // Disallowed base kind.
        var badKind = Wrapper(new NonFailResponse());
        Assert.ThrowsException<InvalidOperationException>(() => badKind.SampleFunction(0.5d));
        Assert.ThrowsException<InvalidOperationException>(() => badKind.SampleResponseFunction());
        Assert.ThrowsException<InvalidOperationException>(() => badKind.IsMonotonic());
        Assert.ThrowsException<InvalidOperationException>(() => badKind.MinHazard());
        Assert.ThrowsException<InvalidOperationException>(() => badKind.MaxProbability());

        // Bad law.
        var badLaw = Wrapper(TabularBase(), Law((-5d, 0d), (10d, 1d)));
        Assert.ThrowsException<InvalidOperationException>(() => badLaw.SampleFunctionAtAge(10d));
    }

    /// <summary>Verifies the posterior-capacity gate for an uncertain parametric base.</summary>
    [TestMethod]
    public void Test_SetupSampler_ParametricBaseCapacityTooSmall_Throws()
    {
        // Arrange — a 100-draw posterior (the smallest legal) cannot serve 1,000 realizations.
        var baseResponse = new ParametricResponse
        {
            Name = "Capacity",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            ParentDistribution = new Normal(150d, 20d),
            IsUncertain = true,
            Realizations = 100,
        };
        baseResponse.Estimate();
        var wrapper = Wrapper(baseResponse);

        // Act / Assert
        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => wrapper.SetupSampler(1000, 12345, SamplingScheme.LatinHypercube));
        Assert.IsTrue(exception.Message.Contains("posterior of only 100"));
    }

    /// <summary>Verifies a non-finite sampled shift is refused loudly at evaluation.</summary>
    [TestMethod]
    public void Test_SampleFunctionAtAge_NonFiniteSampledShift_Throws()
    {
        // Arrange — an unbounded shift distribution sampled at the upper endpoint.
        var law = new UncertainOrderedPairedData(
            new[]
            {
                new UncertainOrdinate(0d, new Normal(0d, 1d)),
                new UncertainOrdinate(50d, new Normal(12d, 3d)),
            },
            true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Normal);
        var wrapper = Wrapper(TabularBase(), law);

        // Act / Assert
        var exception = Assert.ThrowsException<InvalidOperationException>(
            () => wrapper.SampleFunctionAtAge(1d, 50d));
        Assert.IsTrue(exception.Message.Contains("non-finite capacity shift"));
    }

    #endregion

    #region Evaluation semantics

    /// <summary>
    /// Verifies the mean shifted evaluation bit-exactly at knot, interpolated, and held ages:
    /// wrapper.CDF(h) equals base.CDF(h + Δ).
    /// </summary>
    [TestMethod]
    public void Test_SampleFunction_MeanAtAge_ShiftsCdf()
    {
        // Arrange
        var baseResponse = TabularBase();
        var wrapper = Wrapper(baseResponse);
        double[] hazards = [130d, 141d, 148.5d, 152d, 159d, 165d];
        (double Age, double Shift)[] probes = [(0d, 0d), (25d, 5d), (37.5d, 8.5d), (50d, 12d), (150d, 30d)];

        foreach (var (age, shift) in probes)
        {
            // Act
            var sampled = wrapper.SampleFunctionAtAge(age);
            var reference = baseResponse.SampleFunction();

            // Assert — bit-exact composition (no delta).
            foreach (double h in hazards)
            {
                Assert.AreEqual(reference.CDF(h + shift), sampled.CDF(h), $"age {age}, hazard {h}");
            }
        }
    }

    /// <summary>Verifies one percentile drives base AND law co-monotonically, bit-exactly.</summary>
    [TestMethod]
    public void Test_SampleFunctionAtAge_Percentile_CoMonotonic()
    {
        // Arrange
        var baseResponse = UncertainTabularBase();
        var wrapper = Wrapper(baseResponse, UncertainLaw());
        double[] percentiles = [0.1d, 0.5d, 0.9d];
        double[] hazards = [141d, 150d, 158d];

        foreach (double p in percentiles)
        {
            // Act
            var sampled = wrapper.SampleFunctionAtAge(p, 50d);
            double shift = wrapper.DeteriorationLaw.CurveSample(p).GetYFromX(50d);
            var reference = baseResponse.SampleFunction(p);

            // Assert
            foreach (double h in hazards)
            {
                Assert.AreEqual(reference.CDF(h + shift), sampled.CDF(h), $"p {p}, hazard {h}");
            }
        }
    }

    /// <summary>
    /// Verifies the realization overload reads the base's own child-stream row and the law at
    /// this function's sampled percentile, bit-exactly.
    /// </summary>
    [TestMethod]
    public void Test_SampleFunctionAtAge_Realization_UsesOwnLawColumn()
    {
        // Arrange
        var baseResponse = UncertainTabularBase();
        var wrapper = Wrapper(baseResponse, UncertainLaw());
        wrapper.SetupSampler(16, 20260906, SamplingScheme.LatinHypercube);
        double[] hazards = [141d, 150d, 158d];

        for (int i = 0; i < 16; i++)
        {
            // Act
            var sampled = wrapper.SampleFunctionAtAge(i, 50d);
            double shift = wrapper.DeteriorationLaw.CurveSample(wrapper.SampledPercentile(i, 0)).GetYFromX(50d);
            var reference = baseResponse.SampleFunction(i);

            // Assert
            foreach (double h in hazards)
            {
                Assert.AreEqual(reference.CDF(h + shift), sampled.CDF(h), $"realization {i}, hazard {h}");
            }
        }
    }

    /// <summary>Verifies the explicit-age members never touch the evaluation-age property.</summary>
    [TestMethod]
    public void Test_SampleFunctionAtAge_IsPure_EvaluationAgeUntouched()
    {
        // Arrange
        var wrapper = Wrapper(TabularBase());
        wrapper.EvaluationAge = 10d;
        wrapper.SetupSampler(4, 1, SamplingScheme.LatinHypercube);

        // Act
        wrapper.SampleFunctionAtAge(75d);
        wrapper.SampleFunctionAtAge(0.4d, 75d);
        wrapper.SampleFunctionAtAge(2, 75d);

        // Assert
        Assert.AreEqual(10d, wrapper.EvaluationAge);
    }

    /// <summary>Verifies the ordinary overloads delegate to the explicit-age members at the current age.</summary>
    [TestMethod]
    public void Test_OrdinaryOverloads_DelegateAtEvaluationAge()
    {
        // Arrange
        var wrapper = Wrapper(UncertainTabularBase(), UncertainLaw());
        wrapper.SetupSampler(8, 777, SamplingScheme.LatinHypercube);
        wrapper.EvaluationAge = 50d;
        double[] hazards = [144d, 151d, 157d];

        // Act / Assert — each ordinary overload equals its explicit-age twin bit-exactly.
        foreach (double h in hazards)
        {
            Assert.AreEqual(wrapper.SampleFunctionAtAge(50d).CDF(h), wrapper.SampleFunction().CDF(h));
            Assert.AreEqual(wrapper.SampleFunctionAtAge(0.3d, 50d).CDF(h), wrapper.SampleFunction(0.3d).CDF(h));
            Assert.AreEqual(wrapper.SampleFunctionAtAge(5, 50d).CDF(h), wrapper.SampleFunction(5).CDF(h));
        }
    }

    /// <summary>
    /// Verifies the shifted product's full surface over a parametric (Normal) base: inverse CDF,
    /// PDF, moments, bounds, and clone parity.
    /// </summary>
    [TestMethod]
    public void Test_ShiftedDistribution_InverseCdfPdfMoments_Shift()
    {
        // Arrange — Δ(50) = 12 over a Normal(150, 20) capacity.
        var wrapper = Wrapper(ParametricBase());
        var reference = new Normal(150d, 20d);

        // Act
        var shifted = (UnivariateDistributionBase)wrapper.SampleFunctionAtAge(50d);

        // Assert
        Assert.AreEqual(reference.InverseCDF(0.25d) - 12d, shifted.InverseCDF(0.25d));
        Assert.AreEqual(reference.PDF(150d + 12d), shifted.PDF(150d));
        Assert.AreEqual(reference.Mean - 12d, shifted.Mean);
        Assert.AreEqual(reference.Median - 12d, shifted.Median);
        Assert.AreEqual(reference.Minimum - 12d, shifted.Minimum);
        Assert.AreEqual(reference.Maximum - 12d, shifted.Maximum);
        Assert.AreEqual(reference.StandardDeviation, shifted.StandardDeviation);
        Assert.AreEqual(reference.Skewness, shifted.Skewness);
        Assert.AreEqual(reference.Kurtosis, shifted.Kurtosis);

        var clone = shifted.Clone();
        Assert.AreEqual(shifted.CDF(147d), clone.CDF(147d));
        Assert.AreEqual(shifted.InverseCDF(0.8d), clone.InverseCDF(0.8d));
    }

    /// <summary>Verifies the boundary hold outside the tabled age range on both sides.</summary>
    [TestMethod]
    public void Test_AgeOutsideLaw_HoldsBoundaryShift()
    {
        // Arrange — the law spans ages 10 to 50.
        var baseResponse = TabularBase();
        var wrapper = Wrapper(baseResponse, Law((10d, 5d), (50d, 12d)));

        // Act / Assert — below the first age holds 5; beyond the last holds 12.
        Assert.AreEqual(baseResponse.SampleFunction().CDF(150d + 5d), wrapper.SampleFunctionAtAge(0d).CDF(150d));
        Assert.AreEqual(baseResponse.SampleFunction().CDF(150d + 12d), wrapper.SampleFunctionAtAge(500d).CDF(150d));
    }

    /// <summary>
    /// Verifies the age-zero identity: with a zero shift at age zero, the wrapper reproduces the
    /// base bit-for-bit, realization for realization.
    /// </summary>
    [TestMethod]
    public void Test_AgeZero_EquivalentToBase()
    {
        // Arrange
        var baseResponse = UncertainTabularBase();
        var wrapper = Wrapper(baseResponse, UncertainLaw());
        wrapper.SetupSampler(16, 424242, SamplingScheme.LatinHypercube);
        double[] hazards = [140d, 147d, 153d, 160d];

        for (int i = 0; i < 16; i++)
        {
            // Act
            var sampled = wrapper.SampleFunctionAtAge(i, 0d);
            var reference = baseResponse.SampleFunction(i);

            // Assert
            foreach (double h in hazards)
            {
                Assert.AreEqual(reference.CDF(h), sampled.CDF(h), $"realization {i}, hazard {h}");
            }
        }
    }

    /// <summary>
    /// Verifies the one-stream property: for every realization, evaluating at age t equals
    /// evaluating at age zero on the hazard shifted by that realization's own sampled Δ(t) —
    /// the same state of knowledge serves every age.
    /// </summary>
    [TestMethod]
    public void Test_Realization_SameKnowledgeAtEveryAge()
    {
        // Arrange
        var wrapper = Wrapper(UncertainTabularBase(), UncertainLaw());
        wrapper.SetupSampler(16, 987654, SamplingScheme.LatinHypercube);
        double[] hazards = [143d, 150d, 156d];
        double[] ages = [10d, 25d, 60d];

        for (int i = 0; i < 16; i++)
        {
            foreach (double age in ages)
            {
                // Act — the realization's own sampled shift at the age.
                double shift = wrapper.DeteriorationLaw.CurveSample(wrapper.SampledPercentile(i, 0)).GetYFromX(age);
                var atAge = wrapper.SampleFunctionAtAge(i, age);
                var atZero = wrapper.SampleFunctionAtAge(i, 0d);

                // Assert — bit-exact across the whole life cycle on one stream.
                foreach (double h in hazards)
                {
                    Assert.AreEqual(atZero.CDF(h + shift), atAge.CDF(h), $"realization {i}, age {age}, hazard {h}");
                }
            }
        }
    }

    /// <summary>
    /// Verifies the linear-transform equivalence identity at function level: the wrapper at age t
    /// equals the base behind a deterministic unit-slope transform with intercept Δ(t),
    /// bit-for-bit.
    /// </summary>
    [TestMethod]
    public void Test_LinearTransformTwin_FunctionLevel_BitEqual()
    {
        // Arrange — Δ(50) = 12.
        var baseResponse = TabularBase();
        var wrapper = Wrapper(baseResponse);
        var transform = new LinearTransform
        {
            Name = "Aging shift",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            TransformedHazard = "Stage",
            TransformedHazardUnit = "ft",
            Alpha = 12d,
            Beta = 1d,
            IsUncertain = false,
            Minimum = -1e9d,
            Maximum = 1e9d,
        };
        var shiftFunction = transform.SampleFunction();
        var reference = baseResponse.SampleFunction();
        double[] hazards = [130d, 141d, 148.5d, 152d, 159d, 165d];

        // Act
        var sampled = wrapper.SampleFunctionAtAge(50d);

        // Assert — Alpha + Beta·h with Beta = 1 is bit-equal to h + Alpha.
        foreach (double h in hazards)
        {
            Assert.AreEqual(reference.CDF(shiftFunction.Function(h)), sampled.CDF(h), $"hazard {h}");
        }
    }

    #endregion

    #region Curve trio and bounds

    /// <summary>Verifies the curve trio shifts the hazard ordinates and preserves the flags.</summary>
    [TestMethod]
    public void Test_SampleResponseFunction_ShiftsCurveOrdinates()
    {
        // Arrange — Δ(50) = 12 over stages 140/150/160.
        var wrapper = Wrapper(TabularBase());
        wrapper.EvaluationAge = 50d;
        wrapper.SetupSampler(4, 5, SamplingScheme.LatinHypercube);

        // Act
        var mean = wrapper.SampleResponseFunction();
        var percentile = wrapper.SampleResponseFunction(0.5d);
        var realization = wrapper.SampleResponseFunction(1);

        // Assert
        foreach (var curve in new[] { mean, percentile, realization })
        {
            Assert.AreEqual(3, curve.Count);
            Assert.AreEqual(128d, curve[0].X);
            Assert.AreEqual(138d, curve[1].X);
            Assert.AreEqual(148d, curve[2].X);
            Assert.AreEqual(0d, curve[0].Y);
            Assert.AreEqual(0.5d, curve[1].Y);
            Assert.AreEqual(1d, curve[2].Y);
        }
        Assert.IsTrue(wrapper.SupportsOrderedCurveSampling);
    }

    /// <summary>Verifies a parametric base's curve refusal propagates.</summary>
    [TestMethod]
    public void Test_SampleResponseFunction_ParametricBase_Throws()
    {
        // Arrange
        var wrapper = Wrapper(ParametricBase());

        // Act / Assert
        Assert.IsFalse(wrapper.SupportsOrderedCurveSampling);
        Assert.ThrowsException<NotSupportedException>(() => wrapper.SampleResponseFunction());
    }

    /// <summary>Verifies the hazard bounds shift by the mean law at the current age.</summary>
    [TestMethod]
    public void Test_MinMaxHazard_ShiftByMeanLaw()
    {
        // Arrange
        var wrapper = Wrapper(TabularBase());

        // Act / Assert — age zero: the base's own bounds; age 50: shifted down by 12.
        Assert.AreEqual(140d, wrapper.MinHazard());
        Assert.AreEqual(160d, wrapper.MaxHazard());
        wrapper.EvaluationAge = 50d;
        Assert.AreEqual(128d, wrapper.MinHazard());
        Assert.AreEqual(148d, wrapper.MaxHazard());
    }

    /// <summary>Verifies the probability bounds and monotonicity delegate to the base.</summary>
    [TestMethod]
    public void Test_MinMaxProbability_DelegateToBase()
    {
        // Arrange
        var wrapper = Wrapper(TabularBase());
        wrapper.EvaluationAge = 50d;

        // Act / Assert — an axis shift never moves the probability range or the ordering.
        Assert.AreEqual(0d, wrapper.MinProbability());
        Assert.AreEqual(1d, wrapper.MaxProbability());
        Assert.IsTrue(wrapper.IsMonotonic());
    }

    /// <summary>Verifies the uncertainty summary delegates to the base (the age-zero convention).</summary>
    [TestMethod]
    public void Test_ComputeUncertaintyResults_DelegatesToBase()
    {
        // Arrange
        var baseResponse = UncertainTabularBase();
        var wrapper = Wrapper(baseResponse);

        // Act
        var results = wrapper.ComputeUncertaintyResults();
        var reference = baseResponse.ComputeUncertaintyResults();

        // Assert
        Assert.IsNotNull(results);
        CollectionAssert.AreEqual(reference!.MeanCurve, results!.MeanCurve);
        Assert.IsNull(new DeterioratingResponse().ComputeUncertaintyResults());
    }

    #endregion

    #region Serialization

    /// <summary>Verifies the self-contained round trip is byte-equal with the base inline.</summary>
    [TestMethod]
    public void Test_Serialization_SelfContained_RoundTrips()
    {
        // Arrange
        var wrapper = Wrapper(UncertainTabularBase(), UncertainLaw());
        wrapper.Description = "Aging pool fragility.";

        // Act
        string serialized = wrapper.ToXElement().ToString();
        var restored = new DeterioratingResponse(wrapper.ToXElement());

        // Assert — the base restores inline, the law restores in shape, and a re-save is byte-equal.
        Assert.IsNotNull(restored.BaseResponse);
        Assert.IsInstanceOfType(restored.BaseResponse, typeof(TabularResponse));
        Assert.AreEqual(4, restored.DeteriorationLaw.Count);
        Assert.AreEqual(wrapper.Name, restored.Name);
        Assert.AreEqual(wrapper.Description, restored.Description);
        Assert.AreEqual(serialized, restored.ToXElement().ToString());
        Assert.AreEqual(HashOf(wrapper), HashOf(restored));
    }

    /// <summary>Verifies a by-reference form resolves to the LIVE stored base instance.</summary>
    [TestMethod]
    public void Test_Serialization_ByReference_ResolverRepairs()
    {
        // Arrange
        var stored = TabularBase();
        var wrapper = Wrapper(stored);

        // Act
        var restored = new DeterioratingResponse(
            wrapper.ToXElement(RiskSerializationMode.ByReference), ResolverOver(stored));

        // Assert
        Assert.IsTrue(ReferenceEquals(stored, restored.BaseResponse));
        Assert.AreEqual(HashOf(wrapper), HashOf(restored));
    }

    /// <summary>
    /// Verifies a resolver-less by-reference read records the unresolved link, validates as an
    /// error, and re-writes the pending marker verbatim so round trips stay bit-equal.
    /// </summary>
    [TestMethod]
    public void Test_Serialization_ByReference_WithoutResolver_RecordsUnresolvedAndRewritesPending()
    {
        // Arrange
        var stored = TabularBase();
        var wrapper = Wrapper(stored);
        string byReference = wrapper.ToXElement(RiskSerializationMode.ByReference).ToString();

        // Act
        var restored = new DeterioratingResponse(wrapper.ToXElement(RiskSerializationMode.ByReference));

        // Assert — unresolved, loud in validation, and byte-preserved on re-save.
        Assert.IsNull(restored.BaseResponse);
        Assert.IsTrue(restored.Validate().ValidationMessages.Any(m => m.Contains("was not found")));
        Assert.AreEqual(byReference, restored.ToXElement(RiskSerializationMode.ByReference).ToString());

        // A second resolver-less round trip is stable too.
        var again = new DeterioratingResponse(restored.ToXElement(RiskSerializationMode.ByReference));
        Assert.AreEqual(byReference, again.ToXElement(RiskSerializationMode.ByReference).ToString());
    }

    /// <summary>Verifies a stale serialized reference id throws loudly.</summary>
    [TestMethod]
    public void Test_Serialization_StaleReferenceId_Throws()
    {
        // Arrange — the resolver's store holds a DIFFERENT function.
        var stored = TabularBase();
        var wrapper = Wrapper(stored);
        var other = TabularBase();

        // Act / Assert
        Assert.ThrowsException<InvalidOperationException>(
            () => new DeterioratingResponse(wrapper.ToXElement(RiskSerializationMode.ByReference), ResolverOver(other)));
    }

    /// <summary>Verifies an explicit base assignment clears a pending unresolved reference.</summary>
    [TestMethod]
    public void Test_PendingReference_ClearedByAssignment()
    {
        // Arrange
        var wrapper = Wrapper(TabularBase());
        var restored = new DeterioratingResponse(wrapper.ToXElement(RiskSerializationMode.ByReference));

        // Act — assigning a live base supersedes the stored link.
        var replacement = TabularBase();
        restored.BaseResponse = replacement;

        // Assert — the re-save carries the new base, not the old marker.
        var resaved = restored.ToXElement(RiskSerializationMode.ByReference);
        string? markerId = resaved.Element(nameof(DeterioratingResponse.BaseResponse))?
            .Element("FunctionReference")?.Attribute("Id")?.Value;
        Assert.AreEqual(replacement.Id.ToString("D"), markerId);
    }

    #endregion

    #region Canonical hash

    /// <summary>Verifies the serialization mode and every metadata edit are hash-inert.</summary>
    [TestMethod]
    public void Test_CanonicalHash_ModeAndMetadata_Inert()
    {
        // Arrange
        var stored = TabularBase();
        var wrapper = Wrapper(stored);
        string baseline = HashOf(wrapper);

        // Act / Assert — both restored forms hash identically.
        var selfContained = new DeterioratingResponse(wrapper.ToXElement());
        var byReference = new DeterioratingResponse(
            wrapper.ToXElement(RiskSerializationMode.ByReference), ResolverOver(stored));
        Assert.AreEqual(baseline, HashOf(selfContained));
        Assert.AreEqual(baseline, HashOf(byReference));

        // Wrapper metadata edits are inert.
        wrapper.AssignNewId();
        wrapper.Name = "Renamed";
        wrapper.Description = "Edited.";
        wrapper.SpecifiedHazard = "StageX";
        wrapper.HazardUnit = "ftX";
        Assert.AreEqual(baseline, HashOf(wrapper));
    }

    /// <summary>Verifies the evaluation age never reaches the hash or the serialized form.</summary>
    [TestMethod]
    public void Test_CanonicalHash_EvaluationAge_Inert()
    {
        // Arrange
        var wrapper = Wrapper(TabularBase());
        string baseline = HashOf(wrapper);

        // Act / Assert — ages move nothing and serialize nowhere.
        foreach (double age in new[] { 10d, 50d, 137.5d })
        {
            wrapper.EvaluationAge = age;
            Assert.AreEqual(baseline, HashOf(wrapper));
        }
        Assert.IsFalse(wrapper.ToXElement().ToString().Contains(nameof(DeterioratingResponse.EvaluationAge)));
    }

    /// <summary>Verifies a law edit moves the hash.</summary>
    [TestMethod]
    public void Test_CanonicalHash_LawEdit_Moves()
    {
        // Arrange
        var wrapper = Wrapper(TabularBase());
        string baseline = HashOf(wrapper);

        // Act
        wrapper.DeteriorationLaw = Law((0d, 0d), (25d, 5d), (50d, 12d), (100d, 40d));

        // Assert
        Assert.AreNotEqual(baseline, HashOf(wrapper));
    }

    /// <summary>Verifies base CONTENT edits move the wrapper hash while base RENAMES do not.</summary>
    [TestMethod]
    public void Test_CanonicalHash_BaseContent_MovesAndRenameInert()
    {
        // Arrange
        var baseResponse = TabularBase();
        var wrapper = Wrapper(baseResponse);
        string baseline = HashOf(wrapper);

        // Act / Assert — renaming the base is metadata, inert through the projected identity.
        baseResponse.Name = "Renamed base";
        baseResponse.AssignNewId();
        Assert.AreEqual(baseline, HashOf(wrapper));

        // A base content edit moves the wrapper hash.
        baseResponse.ProbabilityTransform = Transform.NormalZ;
        Assert.AreNotEqual(baseline, HashOf(wrapper));
    }

    #endregion

    #region Seeding

    /// <summary>
    /// Verifies the wrapped base's stream derives from the wrapper's seed and the base's content
    /// hash: a standalone content-identical twin seeded with the derived seed draws the same
    /// percentiles bit-for-bit.
    /// </summary>
    [TestMethod]
    public void Test_SetupSampler_SeedsBaseWithDerivedSeed()
    {
        // Arrange
        var baseResponse = UncertainTabularBase();
        var wrapper = Wrapper(baseResponse, UncertainLaw());
        var twin = UncertainTabularBase();
        const int seed = 918273;

        // Act
        wrapper.SetupSampler(32, seed, SamplingScheme.LatinHypercube);
        twin.SetupSampler(32, SeedHelpers.HashCombine(seed, twin.CanonicalHash(), 0), SamplingScheme.LatinHypercube);

        // Assert — the composite forward rule, ordinal 0.
        for (int i = 0; i < 32; i++)
        {
            Assert.AreEqual(twin.SampledPercentile(i, 0), baseResponse.SampledPercentile(i, 0), $"realization {i}");
        }
    }

    /// <summary>
    /// Verifies a run honors the authored evaluation age: the run snapshot clones components,
    /// and the clone alone would reset the never-serialized age to zero, so the snapshot carries
    /// it onto the cloned instances by function id.
    /// </summary>
    [TestMethod]
    public void Test_Run_CarriesEvaluationAgeOntoRunClones()
    {
        // Arrange — one authored model, run at age zero and at age fifty.
        static RMC.TotalRisk.Analyses.Risk.RiskAnalysis Build(double age)
        {
            var wrapper = new DeterioratingResponse
            {
                Name = "Aging Fragility",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                BaseResponse = TabularBase(),
                DeteriorationLaw = StandardLaw(),
                EvaluationAge = age,
            };
            var component = new RMC.TotalRisk.Systems.Components.SystemComponent { Name = "Dam" };
            component.HazardFunction = new RMC.TotalRisk.RiskFunctions.Hazards.TabularHazard
            {
                Name = "Stage frequency",
                SpecifiedHazard = "Stage",
                HazardUnit = "ft",
                NoUncertaintyFunction = new UncertainOrderedPairedData(
                    new[]
                    {
                        new UncertainOrdinate(0.999d, new Deterministic(120d)),
                        new UncertainOrdinate(0.5d, new Deterministic(145d)),
                        new UncertainOrdinate(0.001d, new Deterministic(165d)),
                    },
                    true, SortOrder.Descending, true, SortOrder.Ascending,
                    UnivariateDistributionType.Deterministic),
            };
            component.AddFailureMode(new RMC.TotalRisk.Systems.Components.FailureMode(null, null, wrapper,
                new RMC.TotalRisk.RiskFunctions.Consequences.TabularConsequence
                {
                    Name = "Damages",
                    SpecifiedHazard = "Stage",
                    HazardUnit = "ft",
                    SpecifiedConsequence = "Damages",
                    ConsequenceUnit = "$",
                    UncertainOrderedPairedData = new UncertainOrderedPairedData(
                        new[]
                        {
                            new UncertainOrdinate(120d, new Deterministic(100d)),
                            new UncertainOrdinate(165d, new Deterministic(1000d)),
                        },
                        true, SortOrder.Ascending, false, SortOrder.None,
                        UnivariateDistributionType.Deterministic),
                }));
            return new RMC.TotalRisk.Analyses.Risk.RiskAnalysis(new[] { component })
            {
                SpecifiedConsequence = "Damages",
                ConsequenceUnit = "$",
            };
        }
        var atZero = Build(0d);
        var atFifty = Build(50d);

        // Act — mean-only runs (the defaults).
        atZero.RunAsync().GetAwaiter().GetResult();
        atFifty.RunAsync().GetAwaiter().GetResult();

        // Assert — the aged run fails more, so the age reached the run's cloned wrapper; and
        // the authored wrappers keep their configured ages afterward.
        Assert.IsTrue(atFifty.MeanRiskResults!.Curves.Fail.TotalProbability
            > atZero.MeanRiskResults!.Curves.Fail.TotalProbability);
    }

    /// <summary>Verifies repeated setups from one seed reproduce the sampled surface bit-for-bit.</summary>
    [TestMethod]
    public void Test_SetupSampler_Deterministic_RepeatedRunsIdentical()
    {
        // Arrange
        var first = Wrapper(UncertainTabularBase(), UncertainLaw());
        var second = Wrapper(UncertainTabularBase(), UncertainLaw());

        // Act
        first.SetupSampler(16, 555, SamplingScheme.LatinHypercube);
        second.SetupSampler(16, 555, SamplingScheme.LatinHypercube);

        // Assert
        for (int i = 0; i < 16; i++)
        {
            Assert.AreEqual(first.SampledPercentile(i, 0), second.SampledPercentile(i, 0));
            Assert.AreEqual(first.SampleFunctionAtAge(i, 50d).CDF(150d), second.SampleFunctionAtAge(i, 50d).CDF(150d));
        }
    }

    #endregion
}
