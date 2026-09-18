using System;
using System.Collections.Generic;
using System.Linq;
using Numerics.Data;
using Numerics.Distributions;
using RMC.TotalRisk.RiskFunctions.Consequences;
using RMC.TotalRisk.RiskFunctions.Hazards;
using RMC.TotalRisk.RiskFunctions.Responses;
using RMC.TotalRisk.Systems.Components;
using RMC.TotalRisk.Systems.Components.Graph;

namespace RMC.TotalRisk.Verification.Analyses.Risk;

/// <summary>
/// Shared fixtures and exact reference math for the bivariate verification families — the
/// legacy seismic scenario (the PGA frequency curve, the stage-duration curve, the 4×6
/// bivariate response surface with the logarithmic probability transform, and the stage-driven
/// life-loss consequence, all transcribed verbatim from
/// <c>Test_TotalRisk\Test_BivariateRisk.vb</c>), the graph-wired engine components those
/// curves induce, and the closed-form conditional expectation
/// <c>E_Y[P(x, Y)]</c> evaluated exactly over the union grid.
/// </summary>
/// <remarks>
/// <para>
///     <b>Authors:</b>
///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
/// </para>
/// <para>
/// <b>Closed-form derivation.</b> The stage-duration marginal interpolates stages linearly in
/// normal-Z-transformed non-exceedance probability, so on each segment the stage is linear in
/// z = Φ⁻¹(t). The surface interpolates log₁₀-probability bilinearly, so at a fixed PGA the
/// log-probability is piecewise linear in stage and therefore piecewise linear in z on the
/// union grid (marginal knots ∪ surface secondary knots), giving P = e^{α+βz} per segment.
/// Substituting t = Φ(z) turns each segment of E_Y[P(x, Y)] = ∫₀¹ P(x, Y(t)) dt into the
/// lognormal partial expectation ∫ e^{α+βz} φ(z) dz = e^{α+β²/2}·[Φ(z_hi−β) − Φ(z_lo−β)].
/// The marginal saturates outside its tabulated probability range, which adds two exact atom
/// terms (mass 0.001 at stage 2529.8 and mass 2.14e-6 at stage 2633.5), and stages below the
/// surface's first secondary knot clamp to the first column (a constant-β = 0 segment).
/// Every closed-form value is cross-checked in the consuming tests against a dense Simpson
/// integral driven by the same <see cref="EmpiricalDistribution"/> the engine wraps, so a
/// transcription error in either path cannot survive.
/// </para>
/// </remarks>
internal static class BivariateOracleFixtures
{
    #region Pinned discretization allowances

    /// <summary>
    /// The pinned bins = 20 relative discretization error of the conditional trapezoid rule on
    /// the legacy seismic fixture (E_Y[P(0.8, Y)] against the exact union-grid closed form),
    /// measured at ≈ 0.353 by the convergence study in <c>CopulaDependenceVerification</c>
    /// (the pin carries ≈ 40% head-room). The error is large by mechanism, not by accident:
    /// the stage marginal's normal-Z probability transform puts P ~ e^{c·Φ⁻¹(t)} into the top
    /// conditional bins (the surface probability swings 0.35 → 0.81 inside t ∈ [0.999, 1]),
    /// so the uniform-t trapezoid is rate-limited by the Φ⁻¹ endpoint blow-up before the
    /// marginal saturates. Consumed as the default-bin discretization allowance by the
    /// legacy-oracle comparisons in <c>BivariateRiskVerification</c>, and documented in
    /// docs/verification/copula-dependence.md as the default-20 adequacy boundary: adequate
    /// for smooth moderate-variation surfaces (≈ 1.1e-3 relative on the study's smooth
    /// fixture), inadequate for tail-concentrated log-scale surfaces like this one.
    /// </summary>
    internal const double LegacySrpBins20RelativeError = 0.5d;

    /// <summary>
    /// The pinned bins = 1000 relative discretization error of the same study — measured at
    /// ≈ 1.98e-3 (the pin carries ≈ 2× head-room). The bins = 1000 engine runs on this
    /// fixture are therefore percent-scale comparators, not closed-form-scale ones; the
    /// per-assert tolerances in <c>BivariateRiskVerification</c> combine this figure with the
    /// Monte Carlo standard errors.
    /// </summary>
    internal const double LegacySrpBins1000RelativeError = 4e-3;

    #endregion

    #region Legacy arrays (Test_BivariateRisk.vb, verbatim)

    /// <summary>The legacy PGA hazard levels (g), ascending.</summary>
    internal static readonly double[] PgaLevels =
    {
        0.01d, 0.05d, 0.1d, 0.15d, 0.2d, 0.25d, 0.3d, 0.35d, 0.4d, 0.5d, 0.6d, 0.7d, 0.8d, 0.9d, 1d,
    };

    /// <summary>The legacy PGA annual exceedance probabilities, descending (log-interpolated).</summary>
    internal static readonly double[] PgaExceedance =
    {
        0.216475d, 0.023343d, 0.005343d, 0.002028d, 0.001024d, 0.000614d, 0.000407d, 0.000286d,
        0.000208d, 0.000117d, 0.0000692d, 0.0000421d, 0.0000264d, 0.0000168d, 0.0000107d,
    };

    /// <summary>The legacy stage-duration levels (ft), ascending.</summary>
    internal static readonly double[] StageLevels =
    {
        2529.8d, 2529.9d, 2530d, 2530.3d, 2536.1d, 2542.1d, 2544.9d, 2548.3d, 2550.9d, 2556.7d,
        2562.5d, 2569.3d, 2571.7d, 2575.5d, 2582.4d, 2586.7d, 2591.3d, 2598.5d, 2603.3d, 2604.5d,
        2605.3d, 2609.1d, 2610.5d, 2620.6d, 2629.2d, 2631.7d, 2632.8d, 2633.4d, 2633.5d,
    };

    /// <summary>The legacy stage-duration exceedance probabilities, descending (normal-Z interpolated).</summary>
    internal static readonly double[] StageExceedance =
    {
        0.999d, 0.998d, 0.995d, 0.99d, 0.98d, 0.95d, 0.9d, 0.85d, 0.8d, 0.7d, 0.6d, 0.5d, 0.4d,
        0.3d, 0.2d, 0.15d, 0.1d, 0.05d, 0.02d, 0.01d, 0.005d, 0.002d, 0.001d, 0.0000673d,
        0.00000676d, 0.00000347d, 0.00000258d, 0.0000022d, 0.00000214d,
    };

    /// <summary>The legacy surface primary (PGA) levels.</summary>
    internal static readonly double[] SurfacePrimaryLevels = { 0.2d, 0.4d, 0.6d, 0.8d };

    /// <summary>The legacy surface secondary (stage) levels.</summary>
    internal static readonly double[] SurfaceSecondaryLevels = { 2560d, 2585.5d, 2590d, 2605.5d, 2611d, 2633.5d };

    /// <summary>The legacy 4×6 system-response surface (rows = PGA, columns = stage; log-interpolated).</summary>
    internal static readonly double[,] SurfaceProbabilities =
    {
        { 0.00000054d, 0.000279d, 0.00101d, 0.00412d, 0.00648d, 0.00816d },
        { 0.0000144d, 0.00744d, 0.027d, 0.11d, 0.173d, 0.218d },
        { 0.0000421d, 0.0217d, 0.0789d, 0.321d, 0.505d, 0.636d },
        { 0.0000539d, 0.0278d, 0.101d, 0.411d, 0.647d, 0.814d },
    };

    /// <summary>The legacy life-loss consequence stages (ft), ascending.</summary>
    internal static readonly double[] LifeLossStages = { 2544.9d, 2591.3d, 2605.5d, 2633.5d, 2634.5d };

    /// <summary>The legacy life-loss values (lives), paired with <see cref="LifeLossStages"/>.</summary>
    internal static readonly double[] LifeLossValues = { 0d, 53.95d, 194.1d, 359.95d, 409.15d };

    #endregion

    #region Function builders

    /// <summary>
    /// Builds a deterministic tabular hazard from paired exceedance probabilities (descending)
    /// and hazard levels (ascending).
    /// </summary>
    /// <param name="name">The function name.</param>
    /// <param name="hazard">The hazard type label.</param>
    /// <param name="unit">The hazard unit label.</param>
    /// <param name="exceedance">The exceedance probabilities, descending.</param>
    /// <param name="levels">The hazard levels, ascending.</param>
    /// <param name="probabilityTransform">The probability interpolation transform.</param>
    internal static TabularHazard DeterministicHazard(string name, string hazard, string unit,
        double[] exceedance, double[] levels, Transform probabilityTransform)
    {
        var ordinates = new UncertainOrdinate[exceedance.Length];
        for (int i = 0; i < exceedance.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(exceedance[i], new Deterministic(levels[i]));
        }
        return new TabularHazard
        {
            Name = name,
            SpecifiedHazard = hazard,
            HazardUnit = unit,
            HazardTransform = Transform.None,
            ProbabilityTransform = probabilityTransform,
            NoUncertaintyFunction = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Descending, true, SortOrder.Ascending, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>Builds the legacy PGA frequency curve (log-interpolated probabilities).</summary>
    internal static TabularHazard PgaHazard()
        => DeterministicHazard("PGA Frequency", "PGA", "g", PgaExceedance, PgaLevels, Transform.Logarithmic);

    /// <summary>Builds the legacy stage-duration curve (normal-Z-interpolated probabilities).</summary>
    internal static TabularHazard StageHazard()
        => DeterministicHazard("Stage Duration", "Stage", "ft", StageExceedance, StageLevels, Transform.NormalZ);

    /// <summary>
    /// Builds the legacy 4×6 bivariate response surface with the logarithmic probability
    /// transform. The secondary weights are uniform placeholders — the joint engine mode never
    /// reads them, and the collapse-mode tests re-derive them explicitly.
    /// </summary>
    internal static BivariateResponse SurfaceResponse()
    {
        var response = new BivariateResponse
        {
            Name = "Seismic Surface",
            SpecifiedHazard = "PGA",
            HazardUnit = "g",
            SecondarySpecifiedHazard = "Stage",
            SecondaryHazardUnit = "ft",
            HazardTransform = Transform.None,
            SecondaryHazardTransform = Transform.None,
            ProbabilityTransform = Transform.Logarithmic,
        };
        response.PrimaryHazardLevels.Clear();
        foreach (double level in SurfacePrimaryLevels) response.PrimaryHazardLevels.Add(level);
        response.SecondaryHazardLevels.Clear();
        foreach (double level in SurfaceSecondaryLevels)
        {
            response.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = level, Weight = 1d / SurfaceSecondaryLevels.Length });
        }
        response.ProbabilityValues = (double[,])SurfaceProbabilities.Clone();
        return response;
    }

    /// <summary>
    /// Builds the TRANSPOSED legacy surface for the v1.0 arrangement: stage on the primary
    /// axis, PGA on the secondary axis, the 4×6 grid transposed to 6×4. Collapsing this
    /// response against the PGA frequency curve marginalizes PGA out through the legacy
    /// Voronoi weights, leaving a stage-driven fragility for a univariate stage component.
    /// The weights are left at the uniform default — callers derive them explicitly with
    /// <see cref="BivariateResponse.EstimateWeights(RMC.TotalRisk.Core.Interfaces.IHazardFunction)"/>.
    /// </summary>
    internal static BivariateResponse TransposedSurfaceResponse()
    {
        int rows = SurfaceSecondaryLevels.Length;
        int columns = SurfacePrimaryLevels.Length;
        var transposed = new double[rows, columns];
        for (int i = 0; i < rows; i++)
        {
            for (int j = 0; j < columns; j++) transposed[i, j] = SurfaceProbabilities[j, i];
        }

        var response = new BivariateResponse
        {
            Name = "Seismic Surface (stage-primary)",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SecondarySpecifiedHazard = "PGA",
            SecondaryHazardUnit = "g",
            HazardTransform = Transform.None,
            SecondaryHazardTransform = Transform.None,
            ProbabilityTransform = Transform.Logarithmic,
        };
        response.PrimaryHazardLevels.Clear();
        foreach (double level in SurfaceSecondaryLevels) response.PrimaryHazardLevels.Add(level);
        response.SecondaryHazardLevels.Clear();
        foreach (double level in SurfacePrimaryLevels)
        {
            response.SecondaryHazardLevels.Add(new WeightedHazardLevel { Level = level, Weight = 1d / columns });
        }
        response.ProbabilityValues = transposed;
        return response;
    }

    /// <summary>Builds the legacy stage-driven life-loss consequence.</summary>
    internal static TabularConsequence LifeLossConsequence()
    {
        var ordinates = new UncertainOrdinate[LifeLossStages.Length];
        for (int i = 0; i < LifeLossStages.Length; i++)
        {
            ordinates[i] = new UncertainOrdinate(LifeLossStages[i], new Deterministic(LifeLossValues[i]));
        }
        return new TabularConsequence
        {
            Name = "Life Loss",
            SpecifiedHazard = "Stage",
            HazardUnit = "ft",
            SpecifiedConsequence = "Life Loss",
            ConsequenceUnit = "lives",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(ordinates,
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds the graph-wired legacy engine scenario: an independence-copula bivariate hazard
    /// over the two legacy curves, the joint 4×6 surface response, and either the legacy
    /// life-loss consequence riding the failure path while bound to the raw secondary (stage)
    /// signal, or a linear primary-signal damage (the collapse-consistency comparison uses
    /// the latter so both engine paths integrate the same consequence).
    /// </summary>
    /// <param name="bins">The conditional-integration bin count.</param>
    /// <param name="marginalX">Optional replacement primary marginal (the SRP probe swaps in a degenerate curve).</param>
    /// <param name="primaryBoundConsequence">
    /// When true, replaces the stage-bound life loss with the primary-signal damage
    /// <see cref="PrimaryDamageConsequence"/>.
    /// </param>
    /// <param name="response">Optional replacement joint response (a refined surface).</param>
    internal static SystemComponent JointComponent(int bins, TabularHazard? marginalX = null,
        bool primaryBoundConsequence = false, BivariateResponse? response = null)
    {
        var joint = new BivariateHazard(marginalX ?? PgaHazard(), StageHazard())
        {
            Name = "Seismic Joint Hazard",
            SpecifiedHazard = "PGA",
            HazardUnit = "g",
            SecondarySpecifiedHazard = "Stage",
            SecondaryHazardUnit = "ft",
            SecondaryIntegrationBins = bins,
        };
        var component = new SystemComponent(joint) { Name = "Seismic Component" };
        var hazard = component.Graph.GetElements<HazardElement>().Single();
        var breach = new ResponseElement("Seismic Breach")
        {
            Function = response ?? SurfaceResponse(),
            Input = new RiskConnection(hazard),
            SecondaryInput = new RiskConnection(hazard, 1),
        };
        component.Graph.AddElement(breach);
        if (primaryBoundConsequence)
        {
            var damage = new ConsequenceElement("Primary Damage") { Input = new RiskConnection(breach) };
            damage.Functions.Add(PrimaryDamageConsequence());
            component.Graph.AddElement(damage);
        }
        else
        {
            var lifeLoss = new ConsequenceElement("Life Loss")
            {
                Input = new RiskConnection(breach),
                HazardSource = new RiskConnection(hazard, 1),
            };
            lifeLoss.Functions.Add(LifeLossConsequence());
            component.Graph.AddElement(lifeLoss);
        }
        return component;
    }

    /// <summary>Builds the linear primary-signal damage: 0 at PGA 0 rising to 100 at PGA 1.</summary>
    internal static TabularConsequence PrimaryDamageConsequence()
    {
        return new TabularConsequence
        {
            Name = "Primary Damage",
            SpecifiedHazard = "PGA",
            HazardUnit = "g",
            SpecifiedConsequence = "Damage",
            ConsequenceUnit = "$",
            UncertainOrderedPairedData = new UncertainOrderedPairedData(
                new[] { new UncertainOrdinate(0d, new Deterministic(0d)), new UncertainOrdinate(1d, new Deterministic(100d)) },
                true, SortOrder.Ascending, false, SortOrder.None, UnivariateDistributionType.Deterministic),
        };
    }

    /// <summary>
    /// Builds the degenerate primary marginal for the SRP probe: a two-knot curve bracketing
    /// the probed PGA symmetrically at ±<paramref name="halfWidth"/>, so the primary
    /// integration collapses onto the probed level with an O(halfWidth²) residual (the linear
    /// error terms cancel by symmetry, including the two 0.001-mass endpoint atoms).
    /// </summary>
    /// <param name="pga">The probed PGA level.</param>
    /// <param name="halfWidth">The bracket half-width.</param>
    internal static TabularHazard DegeneratePrimary(double pga, double halfWidth = 1e-6)
        => DeterministicHazard("Probe PGA", "PGA", "g",
            new[] { 0.999d, 0.001d }, new[] { pga - halfWidth, pga + halfWidth }, Transform.None);

    #endregion

    #region Exact reference math

    /// <summary>
    /// Returns the surface's log₁₀-probability column values at the given PGA — the exact
    /// bilinear-in-transformed-space row blend with the interpolator's native edge clamp.
    /// </summary>
    /// <param name="x">The PGA level.</param>
    internal static double[] SurfaceLogRow(double x)
    {
        int rows = SurfacePrimaryLevels.Length;
        int columns = SurfaceSecondaryLevels.Length;
        var logRow = new double[columns];
        if (x <= SurfacePrimaryLevels[0])
        {
            for (int j = 0; j < columns; j++) logRow[j] = Math.Log10(SurfaceProbabilities[0, j]);
            return logRow;
        }
        if (x >= SurfacePrimaryLevels[rows - 1])
        {
            for (int j = 0; j < columns; j++) logRow[j] = Math.Log10(SurfaceProbabilities[rows - 1, j]);
            return logRow;
        }
        int i = 0;
        while (x > SurfacePrimaryLevels[i + 1]) i++;
        double fraction = (x - SurfacePrimaryLevels[i]) / (SurfacePrimaryLevels[i + 1] - SurfacePrimaryLevels[i]);
        for (int j = 0; j < columns; j++)
        {
            double low = Math.Log10(SurfaceProbabilities[i, j]);
            double high = Math.Log10(SurfaceProbabilities[i + 1, j]);
            logRow[j] = low + fraction * (high - low);
        }
        return logRow;
    }

    /// <summary>
    /// Evaluates the surface probability at (x, y) exactly as the engine's interpolator does:
    /// log₁₀-bilinear with edge clamps on both axes.
    /// </summary>
    /// <param name="x">The PGA level.</param>
    /// <param name="y">The stage level.</param>
    internal static double SurfaceProbabilityAt(double x, double y)
    {
        var logRow = SurfaceLogRow(x);
        int columns = SurfaceSecondaryLevels.Length;
        if (y <= SurfaceSecondaryLevels[0]) return Math.Pow(10d, logRow[0]);
        if (y >= SurfaceSecondaryLevels[columns - 1]) return Math.Pow(10d, logRow[columns - 1]);
        int j = 0;
        while (y > SurfaceSecondaryLevels[j + 1]) j++;
        double fraction = (y - SurfaceSecondaryLevels[j]) / (SurfaceSecondaryLevels[j + 1] - SurfaceSecondaryLevels[j]);
        return Math.Pow(10d, logRow[j] + fraction * (logRow[j + 1] - logRow[j]));
    }

    /// <summary>
    /// Computes E_Y[P(x, Y)] exactly: the union-grid sum of lognormal partial expectations
    /// plus the marginal's two saturation atoms (see the class remarks for the derivation).
    /// </summary>
    /// <param name="x">The PGA level at which the surface row is taken.</param>
    internal static double ExactMarginalizedSurface(double x)
    {
        var logRow = SurfaceLogRow(x);
        int marginalCount = StageLevels.Length;

        // The marginal's non-exceedance knots and their z-values (stages ascend as
        // non-exceedance ascends because the exceedance column descends).
        var probabilities = new double[marginalCount];
        var zKnots = new double[marginalCount];
        for (int k = 0; k < marginalCount; k++)
        {
            probabilities[k] = 1d - StageExceedance[k];
            zKnots[k] = Normal.StandardZ(probabilities[k]);
        }

        // Atom terms: draws saturate to the end stages outside the tabulated range.
        double result = probabilities[0] * SurfaceProbabilityAt(x, StageLevels[0])
            + (1d - probabilities[marginalCount - 1]) * SurfaceProbabilityAt(x, StageLevels[marginalCount - 1]);

        // Union breakpoints in z: every marginal knot plus every surface knot that falls
        // strictly inside the marginal's stage range, mapped through the piecewise-linear
        // stage(z) relation.
        var breakpoints = new List<double>(zKnots);
        foreach (double knot in SurfaceSecondaryLevels)
        {
            if (knot <= StageLevels[0] || knot >= StageLevels[marginalCount - 1]) continue;
            int k = 0;
            while (knot > StageLevels[k + 1]) k++;
            double fraction = (knot - StageLevels[k]) / (StageLevels[k + 1] - StageLevels[k]);
            breakpoints.Add(zKnots[k] + fraction * (zKnots[k + 1] - zKnots[k]));
        }
        breakpoints.Sort();

        // Per-segment lognormal partial expectations of P = e^{α+βz}.
        double ln10 = Math.Log(10d);
        for (int s = 0; s < breakpoints.Count - 1; s++)
        {
            double zLow = breakpoints[s];
            double zHigh = breakpoints[s + 1];
            if (zHigh - zLow < 1e-14) continue;
            double zMid = 0.5d * (zLow + zHigh);

            // The stage is linear in z on this segment.
            int k = 0;
            while (zMid > zKnots[k + 1]) k++;
            double stageSlope = (StageLevels[k + 1] - StageLevels[k]) / (zKnots[k + 1] - zKnots[k]);
            double StageAt(double z) => StageLevels[k] + (z - zKnots[k]) * stageSlope;
            double stageMid = StageAt(zMid);

            // The log-probability is linear in stage on this segment (constant in the clamp
            // regions outside the surface's secondary knots).
            double logSlope;
            double logAtMid;
            int columns = SurfaceSecondaryLevels.Length;
            if (stageMid <= SurfaceSecondaryLevels[0])
            {
                logSlope = 0d;
                logAtMid = logRow[0];
            }
            else if (stageMid >= SurfaceSecondaryLevels[columns - 1])
            {
                logSlope = 0d;
                logAtMid = logRow[columns - 1];
            }
            else
            {
                int j = 0;
                while (stageMid > SurfaceSecondaryLevels[j + 1]) j++;
                logSlope = (logRow[j + 1] - logRow[j]) / (SurfaceSecondaryLevels[j + 1] - SurfaceSecondaryLevels[j]);
                logAtMid = logRow[j] + (stageMid - SurfaceSecondaryLevels[j]) * logSlope;
            }

            // P = e^{α+βz} with the coefficients anchored at the segment midpoint.
            double beta = logSlope * stageSlope * ln10;
            double alpha = logAtMid * ln10 - beta * zMid;
            result += Math.Exp(alpha + beta * beta / 2d)
                * (Normal.StandardCDF(zHigh - beta) - Normal.StandardCDF(zLow - beta));
        }
        return result;
    }

    /// <summary>
    /// Computes E_Y[P(x, Y)·C(Y)] exactly — the life-loss-weighted counterpart of
    /// <see cref="ExactMarginalizedSurface"/>. The union grid additionally carries the
    /// life-loss knots, so on every segment the surface is P = e^{α+βz} and the consequence
    /// is linear in z, C = c₀ + c₁z. The segment integral closes as
    /// e^{α+β²/2}·[c₀·ΔΦ + c₁·(β·ΔΦ + φ(z₁−β) − φ(z₂−β))] with ΔΦ = Φ(z₂−β) − Φ(z₁−β),
    /// from ∫z·e^{βz}φ(z)dz = e^{β²/2}[(φ(w₁) − φ(w₂)) + β(Φ(w₂) − Φ(w₁))], w = z − β. The
    /// marginal's two saturation atoms carry their own clamped consequence values. Passing a
    /// unit consequence must reproduce <see cref="ExactMarginalizedSurface"/> — the identity
    /// the consuming test asserts before using this reference.
    /// </summary>
    /// <param name="x">The PGA level at which the surface row is taken.</param>
    /// <param name="consequence">The consequence evaluated on the secondary (stage) axis.</param>
    internal static double ExactMarginalizedSurfaceWeighted(double x, Func<double, double> consequence)
    {
        var logRow = SurfaceLogRow(x);
        int marginalCount = StageLevels.Length;

        var probabilities = new double[marginalCount];
        var zKnots = new double[marginalCount];
        for (int k = 0; k < marginalCount; k++)
        {
            probabilities[k] = 1d - StageExceedance[k];
            zKnots[k] = Normal.StandardZ(probabilities[k]);
        }

        // Atom terms: draws saturate to the end stages outside the tabulated range.
        double result = probabilities[0] * SurfaceProbabilityAt(x, StageLevels[0]) * consequence(StageLevels[0])
            + (1d - probabilities[marginalCount - 1]) * SurfaceProbabilityAt(x, StageLevels[marginalCount - 1])
                * consequence(StageLevels[marginalCount - 1]);

        // Union breakpoints: marginal knots, surface secondary knots, and consequence knots.
        var breakpoints = new List<double>(zKnots);
        void AddInteriorKnot(double knot)
        {
            if (knot <= StageLevels[0] || knot >= StageLevels[marginalCount - 1]) return;
            int k = 0;
            while (knot > StageLevels[k + 1]) k++;
            double fraction = (knot - StageLevels[k]) / (StageLevels[k + 1] - StageLevels[k]);
            breakpoints.Add(zKnots[k] + fraction * (zKnots[k + 1] - zKnots[k]));
        }
        foreach (double knot in SurfaceSecondaryLevels) AddInteriorKnot(knot);
        foreach (double knot in LifeLossStages) AddInteriorKnot(knot);
        breakpoints.Sort();

        double ln10 = Math.Log(10d);
        for (int s = 0; s < breakpoints.Count - 1; s++)
        {
            double zLow = breakpoints[s];
            double zHigh = breakpoints[s + 1];
            if (zHigh - zLow < 1e-14) continue;
            double zMid = 0.5d * (zLow + zHigh);

            int k = 0;
            while (zMid > zKnots[k + 1]) k++;
            double stageSlope = (StageLevels[k + 1] - StageLevels[k]) / (zKnots[k + 1] - zKnots[k]);
            double StageAt(double z) => StageLevels[k] + (z - zKnots[k]) * stageSlope;
            double stageMid = StageAt(zMid);

            // The log-probability is linear in stage on this segment (flat in the clamp regions).
            double logSlope;
            double logAtMid;
            int columns = SurfaceSecondaryLevels.Length;
            if (stageMid <= SurfaceSecondaryLevels[0])
            {
                logSlope = 0d;
                logAtMid = logRow[0];
            }
            else if (stageMid >= SurfaceSecondaryLevels[columns - 1])
            {
                logSlope = 0d;
                logAtMid = logRow[columns - 1];
            }
            else
            {
                int j = 0;
                while (stageMid > SurfaceSecondaryLevels[j + 1]) j++;
                logSlope = (logRow[j + 1] - logRow[j]) / (SurfaceSecondaryLevels[j + 1] - SurfaceSecondaryLevels[j]);
                logAtMid = logRow[j] + (stageMid - SurfaceSecondaryLevels[j]) * logSlope;
            }

            double beta = logSlope * stageSlope * ln10;
            double alpha = logAtMid * ln10 - beta * zMid;

            // The consequence is linear in z on this segment: fit through the segment ends.
            double cLow = consequence(StageAt(zLow));
            double cHigh = consequence(StageAt(zHigh));
            double c1 = (cHigh - cLow) / (zHigh - zLow);
            double c0 = cLow - c1 * zLow;

            double wLow = zLow - beta;
            double wHigh = zHigh - beta;
            double deltaPhi = Normal.StandardCDF(wHigh) - Normal.StandardCDF(wLow);
            double densityDrop = StandardDensity(wLow) - StandardDensity(wHigh);
            result += Math.Exp(alpha + beta * beta / 2d)
                * (c0 * deltaPhi + c1 * (beta * deltaPhi + densityDrop));
        }
        return result;
    }

    /// <summary>The standard normal density.</summary>
    /// <param name="z">The standard normal deviate.</param>
    private static double StandardDensity(double z) => Math.Exp(-0.5d * z * z) / Math.Sqrt(2d * Math.PI);

    /// <summary>
    /// Computes E_Y[P(x, Y)] by composite Simpson integration over the non-exceedance axis,
    /// driving the same <see cref="EmpiricalDistribution"/> the engine's tabular hazard wraps —
    /// the semantic cross-check for <see cref="ExactMarginalizedSurface"/>.
    /// </summary>
    /// <param name="x">The PGA level at which the surface row is taken.</param>
    /// <param name="intervals">The (even) Simpson interval count.</param>
    internal static double DenseMarginalizedSurface(double x, int intervals = 1 << 21)
    {
        var marginal = new EmpiricalDistribution(StageLevels, StageExceedance, SortOrder.Ascending, SortOrder.Descending)
        {
            ProbabilityTransform = Transform.NormalZ,
        };
        double h = 1d / intervals;
        double sum = 0d;
        for (int i = 0; i <= intervals; i++)
        {
            double t = i == 0 ? 1e-15 : i == intervals ? 1d - 1e-15 : i * h;
            double value = SurfaceProbabilityAt(x, marginal.InverseCDF(t));
            int weight = i == 0 || i == intervals ? 1 : (i & 1) == 1 ? 4 : 2;
            sum += weight * value;
        }
        return sum * h / 3d;
    }

    /// <summary>
    /// Computes the dense reference of ∫₀¹ g(x(u)) du over the legacy PGA curve by composite
    /// Simpson integration, where x(u) is the curve's saturating inverse CDF.
    /// </summary>
    /// <param name="integrand">The per-PGA integrand g(x).</param>
    /// <param name="intervals">The (even) Simpson interval count.</param>
    internal static double DensePgaExpectation(Func<double, double> integrand, int intervals = 1 << 21)
    {
        var primary = new EmpiricalDistribution(PgaLevels, PgaExceedance, SortOrder.Ascending, SortOrder.Descending)
        {
            ProbabilityTransform = Transform.Logarithmic,
        };
        double h = 1d / intervals;
        double sum = 0d;
        for (int i = 0; i <= intervals; i++)
        {
            double u = i == 0 ? 1e-15 : i == intervals ? 1d - 1e-15 : i * h;
            double value = integrand(primary.InverseCDF(u));
            int weight = i == 0 || i == intervals ? 1 : (i & 1) == 1 ? 4 : 2;
            sum += weight * value;
        }
        return sum * h / 3d;
    }

    /// <summary>Evaluates the legacy life-loss curve at a stage (linear with end clamps).</summary>
    /// <param name="stage">The stage level.</param>
    internal static double LifeLossAt(double stage)
    {
        if (stage <= LifeLossStages[0]) return LifeLossValues[0];
        if (stage >= LifeLossStages[LifeLossStages.Length - 1]) return LifeLossValues[LifeLossValues.Length - 1];
        int i = 0;
        while (stage > LifeLossStages[i + 1]) i++;
        double fraction = (stage - LifeLossStages[i]) / (LifeLossStages[i + 1] - LifeLossStages[i]);
        return LifeLossValues[i] + fraction * (LifeLossValues[i + 1] - LifeLossValues[i]);
    }

    #endregion
}
