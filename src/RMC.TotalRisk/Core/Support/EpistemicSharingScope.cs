using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Numerics.Sampling;
using RMC.TotalRisk.Core.Enums;

namespace RMC.TotalRisk.Core.Support
{
    /// <summary>
    /// The ambient shared-epistemic-variable scope. While a scope is entered on the current
    /// thread, every epistemic-mixture composite whose <c>EpistemicVariable</c> names one of the
    /// scope's variables overwrites its branch-selector column with the variable's shared draw
    /// during <c>SetupSampler</c>, so every binder of one variable selects the same branch draw
    /// per realization — the state-of-knowledge-correlation requirement of a logic tree.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// An ambient thread-static scope rather than a threaded parameter for the same reason the
    /// tree compilation scope is: <c>SetupSampler(int, int, SamplingScheme)</c> is the closed
    /// cluster contract, and a bound composite nested inside another composite is seeded by its
    /// parent's recursion, which no walk-level parameter can reach. Seeding is single-threaded at
    /// run start, so the thread-static is race-free by construction; a <c>SetupSampler</c> call
    /// with no scope entered (a standalone function, a clone summary) leaves the composite's own
    /// content-seeded selector untouched — binding without a scope degrades to an independent
    /// selection, never an error.
    /// </para>
    /// <para>
    /// The shared draw is applied by overwriting the selector column <i>after</i> the composite's
    /// own sampler is seeded (<c>RiskFunctionBase.OverrideSelectorColumn</c>, the fractile-pin
    /// pattern), so the share is seed-inert: no walk ordinal is added, no other function's stream
    /// moves, and captured sampler seed maps stay valid.
    /// </para>
    /// <para>
    /// Each variable's column derives from the scope's base seed and the variable name alone —
    /// <c>HashCombine(baseSeed, SHA-256(UTF-8(name)), 0)</c> through the run's sampling scheme —
    /// so binders agree no matter where they sit, renaming a variable deliberately re-rolls its
    /// draw (the name is the variable's identity), and two scopes with the same base seed and
    /// realization count reproduce bit-identically.
    /// </para>
    /// <para>
    /// The exact logic-tree enumerator extends the scope in two ways, both inert outside an
    /// enumeration run. A scope may additionally carry columns keyed by function <c>Id</c>, which
    /// an <i>unbound</i> epistemic composite (no named variable) consults for its own id — the
    /// runtime seat that lets the enumerator force every walked epistemic composite without the
    /// author naming a variable; no ordinary run ever populates id columns, so the unbound
    /// sampling path is untouched by default. And a scope may carry applied-key sinks that record
    /// every name and id whose column a composite actually consumed, so the enumerator can prove
    /// after seeding that every discovered axis was reached and refuse loudly otherwise.
    /// </para>
    /// </remarks>
    internal static class EpistemicSharingScope
    {
        /// <summary>
        /// The name-keyed columns of the currently entered scope; null when no scope is active on
        /// this thread.
        /// </summary>
        [ThreadStatic]
        private static Dictionary<string, double[]>? _current;

        /// <summary>
        /// The function-id-keyed columns of the currently entered scope — populated only by the
        /// logic-tree enumerator's forcing scope; null otherwise.
        /// </summary>
        [ThreadStatic]
        private static Dictionary<Guid, double[]>? _currentById;

        /// <summary>
        /// The sink recording every variable name whose column was consumed under the current
        /// scope; null when the scope does not reconcile applications.
        /// </summary>
        [ThreadStatic]
        private static ISet<string>? _appliedNames;

        /// <summary>
        /// The sink recording every function id whose column was consumed under the current
        /// scope; null when the scope does not reconcile applications.
        /// </summary>
        [ThreadStatic]
        private static ISet<Guid>? _appliedIds;

        /// <summary>
        /// True when a scope is entered on the current thread.
        /// </summary>
        internal static bool IsActive => _current != null || _currentById != null;

        /// <summary>
        /// Looks up the shared selector column for a variable in the current scope, recording the
        /// application when the scope reconciles.
        /// </summary>
        /// <param name="variable">The variable name.</param>
        /// <returns>The shared column, or null when no scope is active or the scope does not
        /// carry the variable.</returns>
        internal static double[]? TryGetColumn(string variable)
        {
            var current = _current;
            if (current == null || string.IsNullOrEmpty(variable)) return null;
            if (!current.TryGetValue(variable, out var column)) return null;
            _appliedNames?.Add(variable);
            return column;
        }

        /// <summary>
        /// Looks up the shared selector column for a function id in the current scope — the
        /// unbound-composite seat of the logic-tree enumerator — recording the application when
        /// the scope reconciles.
        /// </summary>
        /// <param name="functionId">The consulting function's id.</param>
        /// <returns>The forcing column, or null when no scope is active or the scope carries no
        /// column for the id (every scope outside an enumeration run).</returns>
        internal static double[]? TryGetColumnById(Guid functionId)
        {
            var current = _currentById;
            if (current == null) return null;
            if (!current.TryGetValue(functionId, out var column)) return null;
            _appliedIds?.Add(functionId);
            return column;
        }

        /// <summary>
        /// Enters a scope holding the given name-keyed columns, returning the token that restores
        /// the prior scope on dispose (scopes nest by save-and-restore).
        /// </summary>
        /// <param name="columns">The shared columns, keyed by variable name.</param>
        /// <returns>The restoration token.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the map is null.</exception>
        internal static IDisposable Enter(Dictionary<string, double[]> columns)
        {
            if (columns == null) throw new ArgumentNullException(nameof(columns));
            return Enter(columns, null, null, null);
        }

        /// <summary>
        /// Enters a scope holding name-keyed and optionally id-keyed columns with optional
        /// applied-key reconciliation sinks — the logic-tree enumerator's forcing entry. The
        /// returned token restores the prior scope, sinks included, on dispose.
        /// </summary>
        /// <param name="columns">The name-keyed columns (empty when only id columns force).</param>
        /// <param name="idColumns">The function-id-keyed columns, or null.</param>
        /// <param name="appliedNames">The sink recording consumed variable names, or null.</param>
        /// <param name="appliedIds">The sink recording consumed function ids, or null.</param>
        /// <returns>The restoration token.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the name-keyed map is null.</exception>
        internal static IDisposable Enter(Dictionary<string, double[]> columns,
            Dictionary<Guid, double[]>? idColumns, ISet<string>? appliedNames, ISet<Guid>? appliedIds)
        {
            if (columns == null) throw new ArgumentNullException(nameof(columns));
            var restorer = new Restorer(_current, _currentById, _appliedNames, _appliedIds);
            _current = columns;
            _currentById = idColumns;
            _appliedNames = appliedNames;
            _appliedIds = appliedIds;
            return restorer;
        }

        /// <summary>
        /// Generates one shared selector column per variable from the base seed under the given
        /// scheme — the single derivation both the analysis run and the standalone component path
        /// use, differing only in the base seed they hand in.
        /// </summary>
        /// <param name="variables">The distinct variable names.</param>
        /// <param name="baseSeed">The deriving seed (the run's PRNG seed at analysis scope; the
        /// component seed on the standalone path).</param>
        /// <param name="sampleSize">The realization count.</param>
        /// <param name="scheme">The run's sampling scheme.</param>
        /// <returns>The columns, keyed by variable name.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the variable set is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the sample size is not positive.</exception>
        /// <exception cref="NotSupportedException">Thrown when the sampling scheme is unrecognized.</exception>
        internal static Dictionary<string, double[]> BuildColumns(
            IEnumerable<string> variables, int baseSeed, int sampleSize, SamplingScheme scheme)
        {
            if (variables == null) throw new ArgumentNullException(nameof(variables));
            if (sampleSize <= 0) throw new ArgumentOutOfRangeException(nameof(sampleSize), "The sample size must be positive.");

            var columns = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (string variable in variables)
            {
                if (string.IsNullOrEmpty(variable) || columns.ContainsKey(variable)) continue;
                int seed = SeedHelpers.ToPositiveSeed(SeedHelpers.HashCombine(baseSeed, HashVariableName(variable), 0));
                double[,] matrix = scheme switch
                {
                    SamplingScheme.LatinHypercube => LatinHypercube.Random(sampleSize, 1, seed),
                    SamplingScheme.LatinHypercubeMedian => LatinHypercube.Median(sampleSize, 1, seed),
                    SamplingScheme.MonteCarlo => SeedHelpers.IndependentUniform(sampleSize, 1, seed),
                    SamplingScheme.ScrambledSobol => SeedHelpers.ScrambledSobol(sampleSize, 1, seed),
                    _ => throw new NotSupportedException($"The sampling scheme '{scheme}' is not supported."),
                };
                var column = new double[sampleSize];
                for (int i = 0; i < sampleSize; i++)
                {
                    column[i] = matrix[i, 0];
                }
                columns.Add(variable, column);
            }
            return columns;
        }

        /// <summary>
        /// The content hash of a variable name — the identity a shared column derives from.
        /// </summary>
        /// <param name="variable">The variable name.</param>
        /// <returns>The SHA-256 of the name's UTF-8 bytes.</returns>
        private static byte[] HashVariableName(string variable)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(variable));
        }

        /// <summary>
        /// The scope-restoration token <see cref="Enter(Dictionary{string, double[]})"/> hands out.
        /// </summary>
        private sealed class Restorer : IDisposable
        {
            /// <summary>
            /// The name-keyed columns to restore on dispose (null when the scope being entered
            /// was outermost).
            /// </summary>
            private readonly Dictionary<string, double[]>? _previous;

            /// <summary>
            /// The id-keyed columns to restore on dispose.
            /// </summary>
            private readonly Dictionary<Guid, double[]>? _previousById;

            /// <summary>
            /// The applied-name sink to restore on dispose.
            /// </summary>
            private readonly ISet<string>? _previousAppliedNames;

            /// <summary>
            /// The applied-id sink to restore on dispose.
            /// </summary>
            private readonly ISet<Guid>? _previousAppliedIds;

            /// <summary>
            /// True once disposed, making a double dispose inert.
            /// </summary>
            private bool _disposed;

            /// <summary>
            /// Captures the scope state to restore.
            /// </summary>
            /// <param name="previous">The prior name-keyed columns.</param>
            /// <param name="previousById">The prior id-keyed columns.</param>
            /// <param name="previousAppliedNames">The prior applied-name sink.</param>
            /// <param name="previousAppliedIds">The prior applied-id sink.</param>
            public Restorer(Dictionary<string, double[]>? previous, Dictionary<Guid, double[]>? previousById,
                ISet<string>? previousAppliedNames, ISet<Guid>? previousAppliedIds)
            {
                _previous = previous;
                _previousById = previousById;
                _previousAppliedNames = previousAppliedNames;
                _previousAppliedIds = previousAppliedIds;
            }

            /// <summary>
            /// Restores the prior scope state.
            /// </summary>
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _current = _previous;
                _currentById = _previousById;
                _appliedNames = _previousAppliedNames;
                _appliedIds = _previousAppliedIds;
            }
        }
    }
}
