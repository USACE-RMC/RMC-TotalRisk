using System;
using System.Collections.Generic;
using Numerics;
using Numerics.Mathematics;

namespace RMC.TotalRisk.Analyses.Risk
{
    /// <summary>
    /// Exact lattice convolution of independent component loss distributions — the additive
    /// system-risk aggregation kernel: each component's weighted (mass, consequence) pairs are
    /// binned onto a shared consequence lattice with a moment-preserving split and a zero atom
    /// for the unrecorded remainder, and the lattice mass vectors are convolved by fast Fourier
    /// transform, enumerating all component failure/non-failure combinations in
    /// O(n log n) instead of 2^D.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// <b>Why the atoms force a lattice, not <c>EmpiricalDistribution.Convolve</c></b>
    /// (docs/requirements/MODEL_LIBRARY_ARCHITECTURE.md §7.8;
    /// <c>docs/technical-reference/loss-exceedance-curves.md</c>): the
    /// zero-inflation that makes convolution equal to full combination enumeration puts a point
    /// mass at zero consequence — "this component did not fail, so it contributes nothing".
    /// <c>EmpiricalDistribution.Convolve</c> samples continuous <c>PDF</c>s on a uniform grid,
    /// and a distribution function jump has no finite density: any ramp-width approximation
    /// either loses the atom or corrupts the sampled density and its renormalization. Binning
    /// the discrete pairs directly onto a shared lattice keeps every atom exact, and because all
    /// components share one lattice step the fast-Fourier-transform convolution is the exact
    /// discrete convolution of the binned distributions — no probability-density sampling, no
    /// per-stage regridding. The transform itself is Numerics (<c>Fourier.FFT</c>); only the
    /// domain-specific lattice assembly lives here.
    /// </para>
    /// <para>
    /// <b>Why this is not <c>EmpiricalDistribution.ConvolveDiscrete</c>.</b> The atom-aware
    /// upstream kernel is pairwise: it derives its lattice step
    /// from the two operands' spans, so an N-way fold through it re-bins the running result at a
    /// step that changes on every fold. Its deposit splits each atom across two nodes, and that
    /// smearing compounds once per fold. Binning every component **once** onto a single lattice
    /// sized to the summed support and then folding by integer shift is exact by comparison, which
    /// is why the local kernel stays. <c>ConvolveDiscrete</c> remains the right choice for a
    /// genuine two-operand convolution.
    /// </para>
    /// <para>
    /// <b>Accuracy:</b> the two-node split preserves each pair's probability mass and first
    /// moment exactly, so the convolved mean equals the sum of the component means to
    /// floating-point roundoff — the additive method's v1.0 mean-parity gate holds by
    /// construction. Second and higher moments carry a quantization error bounded by the square
    /// of the lattice step (the split widens a point mass by at most one step), which the
    /// minimum of 4,096 lattice points keeps far below sampling error. Values quantized into
    /// lattice node zero merge with the zero atom; the engine restores the exact stream
    /// probability from the component curves afterward.
    /// </para>
    /// </remarks>
    public static class SystemConvolution
    {
        /// <summary>
        /// Convolves independent component loss distributions on a shared consequence lattice.
        /// </summary>
        /// <param name="components">
        /// One weighted pair list per component — the exact recorded (mass, consequence) pairs of
        /// one risk-type stream (<see cref="Results.Risk.Curve.CollectRecordedPairs"/>). Masses must be
        /// non-negative and consequences non-negative; each component's recorded mass may be less
        /// than one (a defective stream), and the shortfall becomes its zero-consequence atom.
        /// </param>
        /// <param name="convolutionPoints">
        /// The lattice resolution over the combined consequence range (the engine passes
        /// <c>RiskAnalysisOptions.SystemConvolutionPoints</c>, minimum 4,096). Must be at least two.
        /// </param>
        /// <returns>
        /// The system lattice: <c>Pmf[k]</c> is the probability mass at consequence
        /// <c>k · Step</c>, with index zero holding the joint zero atom (no component contributed).
        /// When every component is a pure zero atom the result is a unit mass at index zero with a
        /// step of zero.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when the component list or an entry is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the component list is empty, or a pair carries a negative mass or negative consequence.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the lattice resolution is less than two.</exception>
        public static (double[] Pmf, double Step) Convolve(
            IReadOnlyList<IReadOnlyList<(double Mass, double Consequence)>> components, int convolutionPoints)
        {
            if (components == null) throw new ArgumentNullException(nameof(components));
            if (components.Count == 0) throw new ArgumentException("At least one component is required.", nameof(components));
            if (convolutionPoints < 2) throw new ArgumentOutOfRangeException(nameof(convolutionPoints), "The lattice resolution must be at least two.");

            // The shared lattice spans the combined range [0, Σ component maxima].
            double totalMaximum = 0d;
            for (int i = 0; i < components.Count; i++)
            {
                totalMaximum += ComponentMaximum(components[i], i);
            }
            if (totalMaximum <= 0d)
            {
                return (new[] { 1d }, 0d);
            }
            double step = totalMaximum / (convolutionPoints - 1);

            double[] result = BinToLattice(components[0], step);
            for (int i = 1; i < components.Count; i++)
            {
                result = ConvolvePair(result, BinToLattice(components[i], step));
            }
            return (result, step);
        }

        /// <summary>
        /// Finds one component's largest recorded consequence, validating the pair contract.
        /// </summary>
        /// <param name="pairs">The component's weighted pairs.</param>
        /// <param name="componentIndex">The component index, for the exception text.</param>
        /// <returns>The largest consequence carrying positive mass, or zero when none does.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the pair list is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a pair carries a negative mass or negative consequence.</exception>
        private static double ComponentMaximum(IReadOnlyList<(double Mass, double Consequence)> pairs, int componentIndex)
        {
            if (pairs == null) throw new ArgumentNullException(nameof(pairs), $"The pair list of component {componentIndex} is null.");
            double maximum = 0d;
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Mass < 0d)
                    throw new ArgumentException($"Component {componentIndex} carries a negative probability mass ({pairs[i].Mass:R}).", nameof(pairs));
                if (pairs[i].Consequence < 0d)
                    throw new ArgumentException($"Component {componentIndex} carries a negative consequence ({pairs[i].Consequence:R}); the engine clamps consequences to zero upstream.", nameof(pairs));
                if (pairs[i].Mass > 0d && pairs[i].Consequence > maximum)
                {
                    maximum = pairs[i].Consequence;
                }
            }
            return maximum;
        }

        /// <summary>
        /// Bins one component's pairs onto the lattice with the moment-preserving two-node split
        /// (mass divides between the bracketing nodes so the pair's first moment is preserved
        /// exactly), then adds the zero atom carrying any unrecorded remainder so the vector is a
        /// proper probability mass function.
        /// </summary>
        /// <param name="pairs">The component's weighted pairs (validated by <see cref="ComponentMaximum"/>).</param>
        /// <param name="step">The lattice step.</param>
        /// <returns>The component's lattice mass vector.</returns>
        private static double[] BinToLattice(IReadOnlyList<(double Mass, double Consequence)> pairs, double step)
        {
            double maximum = 0d;
            double recorded = 0d;
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Mass > 0d)
                {
                    recorded += pairs[i].Mass;
                    if (pairs[i].Consequence > maximum) maximum = pairs[i].Consequence;
                }
            }

            var pmf = new double[(int)(maximum / step) + 2];
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Mass <= 0d) continue;
                double position = pairs[i].Consequence / step;
                int node = (int)position;
                double fraction = position - node;
                pmf[node] += pairs[i].Mass * (1d - fraction);
                pmf[node + 1] += pairs[i].Mass * fraction;
            }

            // The zero atom: with probability 1 − recorded the component contributes nothing.
            // A recorded mass marginally above one (floating-point accumulation) adds no atom and
            // is left unnormalized — the mass-balance witness downstream stays honest.
            if (recorded < 1d)
            {
                pmf[0] += 1d - recorded;
            }
            return pmf;
        }

        /// <summary>
        /// Convolves two lattice mass vectors sharing one step by fast Fourier transform: the
        /// vectors are zero-padded to a power-of-two complex length, transformed, multiplied, and
        /// inverse-transformed, with the tiny negative ringing of the roundtrip clamped to zero.
        /// A single-node vector is a scalar atom at zero and short-circuits to a rescale.
        /// </summary>
        /// <param name="left">The first lattice mass vector.</param>
        /// <param name="right">The second lattice mass vector.</param>
        /// <returns>The convolved lattice mass vector, length <c>left + right − 1</c>.</returns>
        private static double[] ConvolvePair(double[] left, double[] right)
        {
            // A degenerate operand is a point mass at zero: convolution is an exact rescale
            // (Numerics ExtensionMethods.Multiply — same loop, no transform round-trip).
            if (right.Length == 1) return left.Multiply(right[0]);
            if (left.Length == 1) return right.Multiply(left[0]);

            int resultLength = left.Length + right.Length - 1;
            int size = Tools.NextPowerOfTwo(resultLength);
            var leftComplex = new double[2 * size];
            var rightComplex = new double[2 * size];
            for (int i = 0; i < left.Length; i++)
            {
                leftComplex[2 * i] = left[i];
            }
            for (int i = 0; i < right.Length; i++)
            {
                rightComplex[2 * i] = right[i];
            }

            Fourier.FFT(leftComplex);
            Fourier.FFT(rightComplex);
            for (int i = 0; i < size; i++)
            {
                int index = 2 * i;
                double real = leftComplex[index] * rightComplex[index] - leftComplex[index + 1] * rightComplex[index + 1];
                double imaginary = leftComplex[index] * rightComplex[index + 1] + leftComplex[index + 1] * rightComplex[index];
                leftComplex[index] = real;
                leftComplex[index + 1] = imaginary;
            }
            Fourier.FFT(leftComplex, inverse: true);

            var result = new double[resultLength];
            for (int i = 0; i < resultLength; i++)
            {
                double value = leftComplex[2 * i] / size;
                result[i] = value > 0d ? value : 0d;
            }
            return result;
        }

    }
}
