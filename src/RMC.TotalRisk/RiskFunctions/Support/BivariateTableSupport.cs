using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Numerics.Data;
using RMC.TotalRisk.Core;

namespace RMC.TotalRisk.RiskFunctions.Support
{
    /// <summary>
    /// The serialization, structural-validation, and guard rules shared by the deterministic
    /// bivariate two-way tables (<c>BivariateTransform</c> and <c>BivariateConsequence</c>):
    /// pipe-joined axis payloads, the row-per-primary-value surface payload, the axis/surface
    /// structural checks, and the interpolation-transform guards.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     <b>Authors:</b>
    ///     Haden Smith, USACE Risk Management Center, cole.h.smith@usace.army.mil
    /// </para>
    /// <para>
    /// Extracted so the two table clusters cannot drift apart on the rules that must agree between
    /// them — the same reason <see cref="CompositeSupport"/> owns the composite guards. Reads are
    /// permissive and shape-preserving: a ragged or partially unparseable surface payload
    /// reconstructs to exactly the parsed shape with <see cref="double.NaN"/> in the absent cells,
    /// so <c>Validate()</c> reports the corruption honestly instead of the reader silently
    /// truncating or fabricating values.
    /// </para>
    /// </remarks>
    internal static class BivariateTableSupport
    {
        /// <summary>
        /// Writes an axis as a pipe-joined "G17" invariant payload element.
        /// </summary>
        /// <param name="elementName">The axis element name.</param>
        /// <param name="values">The axis values.</param>
        /// <returns>The serialized axis element.</returns>
        internal static XElement WriteAxis(string elementName, double[] values)
        {
            var tokens = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                tokens[i] = SerializationUtilities.FormatDouble(values[i]);
            }
            return new XElement(elementName, string.Join("|", tokens));
        }

        /// <summary>
        /// Reads a pipe-joined axis payload, permissively: a missing or empty element yields an
        /// empty axis, and an unparseable token yields <see cref="double.NaN"/> so validation
        /// rejects it rather than a silent default entering the surface.
        /// </summary>
        /// <param name="xElement">The parent serialized form.</param>
        /// <param name="elementName">The axis element name.</param>
        /// <returns>The parsed axis values.</returns>
        internal static double[] ReadAxis(XElement xElement, string elementName)
        {
            string? text = xElement.Element(elementName)?.Value;
            if (string.IsNullOrEmpty(text)) return Array.Empty<double>();

            string[] tokens = text!.Split('|');
            var values = new double[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                values[i] = SerializationUtilities.ParseDouble(tokens[i], double.NaN);
            }
            return values;
        }

        /// <summary>
        /// Writes a surface as one pipe-joined "G17" row element per primary (X1) value, cells in
        /// secondary (X2) order — the <c>z[i, j] = z(x1[i], x2[j])</c> bilinear convention.
        /// </summary>
        /// <param name="elementName">The surface element name.</param>
        /// <param name="rowName">The row element name.</param>
        /// <param name="zValues">The surface values.</param>
        /// <returns>The serialized surface element.</returns>
        internal static XElement WriteGrid(string elementName, string rowName, double[,] zValues)
        {
            var gridElement = new XElement(elementName);
            int rows = zValues.GetLength(0);
            int columns = zValues.GetLength(1);
            for (int i = 0; i < rows; i++)
            {
                var tokens = new string[columns];
                for (int j = 0; j < columns; j++)
                {
                    tokens[j] = SerializationUtilities.FormatDouble(zValues[i, j]);
                }
                gridElement.Add(new XElement(rowName, string.Join("|", tokens)));
            }
            return gridElement;
        }

        /// <summary>
        /// Reads a surface payload, permissively and shape-preservingly: the result has one row
        /// per serialized row element and the widest parsed row's column count, with absent or
        /// unparseable cells filled with <see cref="double.NaN"/>. Nothing parsed is ever dropped,
        /// so a ragged payload survives to <c>Validate()</c> instead of being silently truncated.
        /// </summary>
        /// <param name="xElement">The parent serialized form.</param>
        /// <param name="elementName">The surface element name.</param>
        /// <param name="rowName">The row element name.</param>
        /// <returns>The parsed surface; empty (0 × 0) when the element is missing or has no rows.</returns>
        internal static double[,] ReadGrid(XElement xElement, string elementName, string rowName)
        {
            var gridElement = xElement.Element(elementName);
            if (gridElement == null) return new double[0, 0];

            var parsedRows = new List<double[]>();
            int width = 0;
            foreach (var rowElement in gridElement.Elements(rowName))
            {
                string text = rowElement.Value;
                double[] row;
                if (string.IsNullOrEmpty(text))
                {
                    row = Array.Empty<double>();
                }
                else
                {
                    string[] tokens = text.Split('|');
                    row = new double[tokens.Length];
                    for (int j = 0; j < tokens.Length; j++)
                    {
                        row[j] = SerializationUtilities.ParseDouble(tokens[j], double.NaN);
                    }
                }
                parsedRows.Add(row);
                if (row.Length > width) width = row.Length;
            }

            var zValues = new double[parsedRows.Count, width];
            for (int i = 0; i < parsedRows.Count; i++)
            {
                double[] row = parsedRows[i];
                for (int j = 0; j < width; j++)
                {
                    zValues[i, j] = j < row.Length ? row[j] : double.NaN;
                }
            }
            return zValues;
        }

        /// <summary>
        /// Determines whether a table is structurally usable for interpolation: both axes have at
        /// least two finite, strictly ascending values; the surface has one row per primary value
        /// and one column per secondary value; and every cell is finite. Interpolation-transform
        /// compatibility is a validation concern only and never gates evaluation — the
        /// <c>TabularTransform</c> posture.
        /// </summary>
        /// <param name="x1Values">The primary (X1) axis values.</param>
        /// <param name="x2Values">The secondary (X2) axis values.</param>
        /// <param name="zValues">The surface values.</param>
        /// <returns>True when the table can be interpolated.</returns>
        internal static bool IsUsable(double[] x1Values, double[] x2Values, double[,] zValues)
        {
            if (!AxisIsUsable(x1Values) || !AxisIsUsable(x2Values)) return false;
            if (zValues.GetLength(0) != x1Values.Length || zValues.GetLength(1) != x2Values.Length) return false;

            for (int i = 0; i < zValues.GetLength(0); i++)
            {
                for (int j = 0; j < zValues.GetLength(1); j++)
                {
                    if (!double.IsFinite(zValues[i, j])) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Validates the table structure, appending precise per-rule errors: each axis needs at
        /// least two finite, strictly ascending values; the surface must be dimensioned one row
        /// per primary value by one column per secondary value; and every cell must be finite.
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <param name="functionLabel">The cluster wording (e.g., "bivariate transform function").</param>
        /// <param name="x1Values">The primary (X1) axis values.</param>
        /// <param name="x2Values">The secondary (X2) axis values.</param>
        /// <param name="zValues">The surface values.</param>
        /// <returns>True when the table is structurally usable and the transform guards can run.</returns>
        internal static bool ValidateStructure(List<string> messages, string functionLabel,
            double[] x1Values, double[] x2Values, double[,] zValues)
        {
            // Bitwise-and on purpose: every axis reports its own errors rather than the first
            // failure hiding the second axis's problems.
            bool usable = ValidateAxisStructure(messages, functionLabel, "primary hazard", x1Values);
            usable &= ValidateAxisStructure(messages, functionLabel, "secondary hazard", x2Values);

            if (zValues.GetLength(0) != x1Values.Length || zValues.GetLength(1) != x2Values.Length)
            {
                messages.Add($"Error: The {functionLabel}'s surface must have one row of values per primary hazard value and one column per secondary hazard value.");
                return false;
            }

            for (int i = 0; i < zValues.GetLength(0); i++)
            {
                for (int j = 0; j < zValues.GetLength(1); j++)
                {
                    if (!double.IsFinite(zValues[i, j]))
                    {
                        messages.Add($"Error: The {functionLabel}'s surface values must all be finite.");
                        return false;
                    }
                }
            }
            return usable;
        }

        /// <summary>
        /// Validates one interpolation-transform guard for an ascending axis: a logarithmic axis
        /// rejects values below zero (zero itself is floored by the Numerics log, the exact v1.0
        /// behavior), and a normal-Z axis rejects values outside the probability range [0, 1]
        /// (outside which <c>Normal.StandardZ</c> throws at interpolation time). Ascending order
        /// is guaranteed by the structural checks, so the endpoints decide both rules.
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <param name="axisLabel">The axis wording (e.g., "hazard", "secondary hazard").</param>
        /// <param name="transform">The axis interpolation transform.</param>
        /// <param name="values">The ascending axis values.</param>
        internal static void ValidateAxisTransform(List<string> messages, string axisLabel,
            Transform transform, double[] values)
        {
            if (transform == Transform.Logarithmic && values[0] < 0d)
            {
                messages.Add($"Error: The {axisLabel} interpolation transform cannot be logarithmic. There are {axisLabel} values less than zero.");
            }
            else if (transform == Transform.NormalZ && (values[0] < 0d || values[values.Length - 1] > 1d))
            {
                messages.Add($"Error: The {axisLabel} interpolation transform cannot be normal Z. There are {axisLabel} values outside the probability range [0, 1].");
            }
        }

        /// <summary>
        /// Validates one interpolation-transform guard for the output surface — the same rules as
        /// <see cref="ValidateAxisTransform"/>, scanned over every cell because cells carry no
        /// ordering guarantee.
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <param name="axisLabel">The output wording (e.g., "transform", "consequence").</param>
        /// <param name="transform">The output interpolation transform.</param>
        /// <param name="zValues">The surface values.</param>
        internal static void ValidateSurfaceTransform(List<string> messages, string axisLabel,
            Transform transform, double[,] zValues)
        {
            if (transform != Transform.Logarithmic && transform != Transform.NormalZ) return;

            bool anyNegative = false;
            bool anyOutsideUnit = false;
            for (int i = 0; i < zValues.GetLength(0); i++)
            {
                for (int j = 0; j < zValues.GetLength(1); j++)
                {
                    double cell = zValues[i, j];
                    if (cell < 0d) anyNegative = true;
                    if (cell < 0d || cell > 1d) anyOutsideUnit = true;
                }
            }

            if (transform == Transform.Logarithmic && anyNegative)
            {
                messages.Add($"Error: The {axisLabel} interpolation transform cannot be logarithmic. There are {axisLabel} values less than zero.");
            }
            else if (transform == Transform.NormalZ && anyOutsideUnit)
            {
                messages.Add($"Error: The {axisLabel} interpolation transform cannot be normal Z. There are {axisLabel} values outside the probability range [0, 1].");
            }
        }

        /// <summary>
        /// Determines whether any surface cell is negative — the bivariate consequence's advisory
        /// warning probe.
        /// </summary>
        /// <param name="zValues">The surface values.</param>
        /// <returns>True when at least one cell is below zero.</returns>
        internal static bool AnyCellIsNegative(double[,] zValues)
        {
            for (int i = 0; i < zValues.GetLength(0); i++)
            {
                for (int j = 0; j < zValues.GetLength(1); j++)
                {
                    if (zValues[i, j] < 0d) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Computes the minimum and maximum surface values — the transformed-hazard bounds of a
        /// deterministic two-way table.
        /// </summary>
        /// <param name="zValues">The surface values.</param>
        /// <returns>The surface minimum and maximum.</returns>
        internal static (double Min, double Max) GridBounds(double[,] zValues)
        {
            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            for (int i = 0; i < zValues.GetLength(0); i++)
            {
                for (int j = 0; j < zValues.GetLength(1); j++)
                {
                    double cell = zValues[i, j];
                    if (cell < min) min = cell;
                    if (cell > max) max = cell;
                }
            }
            return (min, max);
        }

        /// <summary>
        /// Determines whether one axis is structurally usable: at least two values, all finite,
        /// strictly ascending — the predicate form of <see cref="ValidateAxisStructure"/>.
        /// </summary>
        /// <param name="values">The axis values.</param>
        /// <returns>True when the axis can back interpolation.</returns>
        private static bool AxisIsUsable(double[] values)
        {
            if (values.Length < 2) return false;
            if (!double.IsFinite(values[0])) return false;
            for (int i = 1; i < values.Length; i++)
            {
                if (!double.IsFinite(values[i]) || values[i] <= values[i - 1]) return false;
            }
            return true;
        }

        /// <summary>
        /// Validates one axis's structure: at least two values, all finite, strictly ascending.
        /// Order is assessed only over finite values — a non-finite axis reports the finiteness
        /// error alone, because ordering is meaningless against NaN.
        /// </summary>
        /// <param name="messages">The message sink.</param>
        /// <param name="functionLabel">The cluster wording.</param>
        /// <param name="axisLabel">The axis wording.</param>
        /// <param name="values">The axis values.</param>
        /// <returns>True when the axis is structurally usable.</returns>
        private static bool ValidateAxisStructure(List<string> messages, string functionLabel,
            string axisLabel, double[] values)
        {
            if (values.Length < 2)
            {
                messages.Add($"Error: The {functionLabel} must have at least two {axisLabel} values.");
                return false;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (!double.IsFinite(values[i]))
                {
                    messages.Add($"Error: The {functionLabel}'s {axisLabel} values must all be finite.");
                    return false;
                }
            }

            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] <= values[i - 1])
                {
                    messages.Add($"Error: The {functionLabel}'s {axisLabel} values must be strictly ascending.");
                    return false;
                }
            }
            return true;
        }
    }
}
