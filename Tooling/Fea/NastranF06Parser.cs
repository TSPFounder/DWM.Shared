// NastranF06Parser.cs / ModalResults.cs
// Reading MYSTRAN's .f06 print file.
//
// THE EXIT CODE IS NOT THE ANSWER
//
// Solvers in the Nastran lineage routinely return exit code 0 after writing FATAL to the
// print file and producing no results. So a runner that trusts the exit code reports success
// on a deck that did not solve -- the same shape of mistake as trusting MATLAB's COM Execute
// return value, which also comes back clean on failure. In both cases the truth is in the
// text, not the status, and in both cases believing the status produces a confident wrong
// answer rather than an error.
//
// FORMAT CAVEAT, READ BEFORE TRUSTING THIS
//
// The layout below is the Nastran-standard real-eigenvalue table, which MYSTRAN follows
// closely. It has NOT yet been checked against output from this MYSTRAN build. The parser is
// therefore written to be tolerant -- it locates the table by a whitespace-insensitive header
// match and accepts any row with enough numeric columns, rather than relying on fixed
// character positions. If a real .f06 disagrees, the fix belongs here and the sample belongs
// in the tests.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DWM.Shared.Tooling.Fea
{
    public sealed class ModalResult
    {
        public int ModeNumber { get; init; }
        public double Eigenvalue { get; init; }
        public double RadiansPerSecond { get; init; }
        public double Hertz { get; init; }
        public double GeneralizedMass { get; init; }
        public double GeneralizedStiffness { get; init; }

        public override string ToString() =>
            $"Mode {ModeNumber}: {Hertz:F4} Hz ({RadiansPerSecond:F4} rad/s)";
    }

    public sealed class ModalResults
    {
        public IReadOnlyList<ModalResult> Modes { get; init; } = Array.Empty<ModalResult>();

        /// <summary>Lines containing FATAL. Non-empty means the run did not produce answers.</summary>
        public IReadOnlyList<string> FatalMessages { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> WarningMessages { get; init; } = Array.Empty<string>();

        public bool HasFatal => FatalMessages.Count > 0;

        public double? FirstFrequencyHz => Modes.Count > 0 ? Modes[0].Hertz : null;

        /// <summary>
        /// Whether the first mode sits inside a soft-stiff window, and by what margin. The
        /// turbine's window is 1P + 10% to 3P - 10%; a PASS with 9% margin and a PASS with 25%
        /// margin are the same verdict and very different situations, so both are returned.
        /// </summary>
        public (bool InWindow, double LowerMarginPercent, double UpperMarginPercent)? CheckWindow(
            double lowerHz, double upperHz)
        {
            if (FirstFrequencyHz is not double f) return null;
            return (f >= lowerHz && f <= upperHz,
                    (f - lowerHz) / lowerHz * 100.0,
                    (upperHz - f) / upperHz * 100.0);
        }
    }

    public static class NastranF06Parser
    {
        /// <summary>
        /// Header as it appears in the print file, letter-spaced: "R E A L   E I G E N V A L U E S".
        /// Matched with all whitespace stripped so spacing differences between solvers and
        /// releases cannot break detection.
        /// </summary>
        private const string EigenvalueHeader = "REALEIGENVALUES";

        public static ModalResults Parse(string f06Text)
        {
            var modes = new List<ModalResult>();
            var fatals = new List<string>();
            var warnings = new List<string>();

            var lines = (f06Text ?? string.Empty).Split('\n');
            var inTable = false;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');

                if (line.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) >= 0)
                    fatals.Add(line.Trim());
                else if (line.IndexOf("WARNING", StringComparison.OrdinalIgnoreCase) >= 0)
                    warnings.Add(line.Trim());

                var squeezed = Squeeze(line);

                if (squeezed.Contains(EigenvalueHeader, StringComparison.OrdinalIgnoreCase))
                {
                    inTable = true;
                    continue;
                }

                if (!inTable) continue;

                // A page break or a new titled block ends the table. Blank lines do NOT --
                // print files pad tables with them, and stopping at the first blank would
                // silently truncate the mode list at mode 1 on some layouts.
                if (squeezed.Length > 0 && IsSectionHeader(squeezed))
                {
                    inTable = false;
                    continue;
                }

                if (TryParseModeRow(line, out var mode)) modes.Add(mode);
            }

            return new ModalResults
            {
                Modes = modes.OrderBy(m => m.ModeNumber).ToList(),
                FatalMessages = fatals,
                WarningMessages = warnings
            };
        }

        /// <summary>
        /// One row of the eigenvalue table:
        ///   MODE  EXTRACTION  EIGENVALUE  RADIANS  CYCLES  GEN.MASS  GEN.STIFFNESS
        ///
        /// Parsed by SPLITTING ON WHITESPACE rather than by column position. Fixed-column
        /// parsing is the traditional approach and is exactly what broke the Nastran deck
        /// writer three times: an 8-character value in an 8-character field leaves no gap, and
        /// any assumption about where a number starts stops holding the moment one is wide.
        /// </summary>
        private static bool TryParseModeRow(string line, out ModalResult mode)
        {
            mode = null!;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 7) return false;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var modeNumber))
                return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return false;   // extraction order; its presence is what distinguishes a data row

            if (!TryNumber(parts[2], out var eigenvalue)) return false;
            if (!TryNumber(parts[3], out var radians)) return false;
            if (!TryNumber(parts[4], out var hertz)) return false;
            TryNumber(parts[5], out var genMass);
            TryNumber(parts[6], out var genStiffness);

            mode = new ModalResult
            {
                ModeNumber = modeNumber,
                Eigenvalue = eigenvalue,
                RadiansPerSecond = radians,
                Hertz = hertz,
                GeneralizedMass = genMass,
                GeneralizedStiffness = genStiffness
            };
            return true;
        }

        /// <summary>
        /// Fortran writes exponents several ways. "1.0E+03" is ordinary, but "1.0+3" -- the
        /// exponent shorthand with no E -- is legal in this lineage and defeats double.Parse.
        /// The deck writer had to emit that form; the reader has to accept it.
        /// </summary>
        private static bool TryNumber(string token, out double value)
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;

            // Look for a sign that is not at position 0 and not preceded by E/e/D/d.
            for (int i = 1; i < token.Length; i++)
            {
                if (token[i] != '+' && token[i] != '-') continue;

                var previous = token[i - 1];
                if (previous is 'E' or 'e' or 'D' or 'd') continue;

                var mantissa = token.Substring(0, i);
                var exponent = token.Substring(i);
                if (double.TryParse(mantissa, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) &&
                    int.TryParse(exponent, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
                {
                    value = m * Math.Pow(10, e);
                    return true;
                }
            }

            value = 0;
            return false;
        }

        private static bool IsSectionHeader(string squeezed) =>
            squeezed.Contains("EIGENVECTOR", StringComparison.OrdinalIgnoreCase) ||
            squeezed.Contains("DISPLACEMENTVECTOR", StringComparison.OrdinalIgnoreCase) ||
            squeezed.Contains("ENDOFJOB", StringComparison.OrdinalIgnoreCase);

        private static string Squeeze(string line)
        {
            Span<char> buffer = line.Length <= 512 ? stackalloc char[line.Length] : new char[line.Length];
            var n = 0;
            foreach (var c in line)
                if (!char.IsWhiteSpace(c)) buffer[n++] = char.ToUpperInvariant(c);
            return new string(buffer[..n]);
        }
    }
}
