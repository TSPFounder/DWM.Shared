// IMatlabSession.cs
// The narrowest MATLAB surface MatlabStageService needs: run a command, read a string
// variable back out of the base workspace.
//
// WHY AN INTERFACE RATHER THAN CALLING COM DIRECTLY
//
// Two reasons, and the second is the one that matters.
//
// 1. TESTABILITY. COM automation cannot run on the build agent (it needs a Windows box with
//    MATLAB installed and licensed). Every interesting failure in this stage -- MATLAB
//    raising an error, a CSV not appearing, a STALE CSV appearing -- is orchestration logic,
//    not transport. Behind this interface all of it is testable with a fake.
//
// 2. THE TRANSPORT IS EXPECTED TO CHANGE. MatlabComSession is deliberately dependency-free
//    late binding, so DWMStudio gains this stage without taking a reference on anything.
//    TSPFounder/MatlabLibrary is the richer .NET interface and is the likely long-term
//    transport; when it is swapped in, it implements this interface and MatlabStageService
//    does not change.
//
// DIRECTION OF THE ARROW. DWMStudio drives MATLAB, never the reverse. MATLAB can load .NET
// assemblies via NET.addAssembly, so "wtGui calls WriteTurbine directly" looks tempting --
// but the MVP turbine model runs under R2011a (SCOPE.md 2026-08-02), whose .NET bridge
// targets .NET Framework 4.0, and DWM.Shared targets net10.0. R2011a cannot load it. This
// is a hard constraint, not a preference.

using System;

namespace DWM.Shared.Matlab
{
    public interface IMatlabSession : IDisposable
    {
        /// <summary>
        /// Run a MATLAB command in the base workspace and return whatever it printed.
        ///
        /// CRITICAL: MATLAB's COM Execute does NOT throw when the command errors. It returns
        /// the error text as an ordinary string, so a caller that ignores the return value
        /// sees success. MatlabStageService therefore never relies on this return value for
        /// error detection -- it wraps every command in a MATLAB-side try/catch and reads a
        /// sentinel variable back via <see cref="GetCharArray"/>. See BuildGuardedCommand.
        /// </summary>
        string Execute(string command);

        /// <summary>
        /// Read a char array (MATLAB string) out of the base workspace.
        /// Throws if the variable does not exist, so callers should initialise it first.
        /// </summary>
        string GetCharArray(string variableName);

        /// <summary>
        /// True when this session attached to a MATLAB the user already had open, false when
        /// it launched a new one. Surfaced because the two behave differently in ways that
        /// matter: an attached session inherits the user's current directory, path, and
        /// workspace, so a leftover variable from their own experimenting is visible to us.
        /// </summary>
        bool IsAttachedToExistingInstance { get; }
    }
}
