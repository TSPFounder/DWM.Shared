// TurbineScenario.cs
// The four wind scenarios wtRunSimulation.m accepts, as a type rather than a string.
//
// WHY AN ENUM WHEN MATLAB ONLY WANTS A STRING: the scenario name is interpolated into a
// MATLAB command, so a typo is not a compile error on either side -- MATLAB would report
// an unknown scenario at run time, after DWMStudio had already opened a COM session and
// added a path. Making it a type moves that failure to the C# call site.

using System;

namespace DWM.Shared.Matlab
{
    public enum TurbineScenario
    {
        /// <summary>Step change in wind speed.</summary>
        Step,

        /// <summary>
        /// Ramp through the full operating envelope. THE DEFAULT, and the right choice for
        /// world-package export: it is the only scenario that sweeps rotor speed across its
        /// whole range, so the exported motion actually shows the machine doing something.
        /// </summary>
        Ramp,

        /// <summary>Turbulent inflow.</summary>
        Turbulent,

        /// <summary>
        /// IEC extreme operating gust. Excellent for stressing the pitch controller, POOR for
        /// export: holding rotor speed constant through a gust is exactly what the controller
        /// is for, so the rotor channel comes out nearly flat and the export looks identical
        /// to a constant-rate placeholder. See <see cref="MatlabStageService"/>, which warns.
        /// </summary>
        Gust
    }

    public static class TurbineScenarioExtensions
    {
        /// <summary>
        /// The lower-case token wtRunSimulation.m expects, e.g. TurbineScenario.Ramp -> "ramp".
        /// </summary>
        public static string ToMatlabToken(this TurbineScenario scenario)
        {
            switch (scenario)
            {
                case TurbineScenario.Step:      return "step";
                case TurbineScenario.Ramp:      return "ramp";
                case TurbineScenario.Turbulent: return "turbulent";
                case TurbineScenario.Gust:      return "gust";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(scenario), scenario,
                        "Unknown scenario. If a fifth scenario was added to wtRunSimulation.m, " +
                        "add it here too -- this switch is deliberately exhaustive so the two " +
                        "cannot drift silently.");
            }
        }
    }
}
