// MatlabComSession.cs
// IMatlabSession over MATLAB's COM automation server, by LATE BINDING.
//
// WHY LATE BINDING RATHER THAN A REFERENCE ON MLApp
//
// An interop assembly for MLApp is version-specific and machine-specific: it is generated
// from whichever MATLAB happens to be installed on the machine that built it. The MVP model
// runs on R2011a while newer releases may sit alongside it, and the build agent has no
// MATLAB at all. Late binding through IDispatch needs no reference, no interop assembly, and
// no build-time MATLAB -- at the cost of losing compile-time checking on three method names,
// which is a trade worth making for exactly three method names.
//
// WINDOWS ONLY. Every entry point is guarded; off Windows this throws
// PlatformNotSupportedException rather than failing obscurely inside the marshaller. The
// containing project targets net10.0 (not net10.0-windows) because DWM.Shared is also
// consumed by the test project and CLIs that build and run on Linux; the COM path simply is
// not reachable there.

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DWM.Shared.Matlab
{
    public sealed class MatlabComSession : IMatlabSession
    {
        /// <summary>
        /// The version-agnostic ProgID, and the one ToolStatusService probes for its MATLAB
        /// status dot.
        ///
        /// IT DOES NOT MEAN "WHICHEVER MATLAB IS RUNNING". It resolves to a single CLSID --
        /// whichever MATLAB registered itself last, typically the newest installed. On a machine
        /// with more than one release that is the WRONG ONE for this project: the MVP turbine
        /// model runs under R2011a (SCOPE.md 2026-08-02).
        ///
        /// The consequence is sharper than "it might pick the wrong one". GetActiveObject
        /// resolves this ProgID to a CLSID and then looks for THAT CLSID in the Running Object
        /// Table, so with R2011a open and R2025b registered, the attach does not find the open
        /// R2011a -- it misses, and a new R2025b is launched instead. Passing a versioned
        /// ProgID to the constructor is the fix; see the progId parameter.
        /// </summary>
        public const string DefaultProgId = "matlab.application";

        /// <summary>The ProgID this session actually used.</summary>
        public string ProgId { get; }

        private object? _matlab;
        private readonly bool _weLaunchedIt;
        private bool _disposed;
        private bool _leaveRunning;

        public bool IsAttachedToExistingInstance => !_weLaunchedIt;

        /// <summary>
        /// Release this session WITHOUT quitting MATLAB, even when this object launched it.
        ///
        /// Dispose normally quits an instance it started, which is right for a batch job: run
        /// the model, take the results, clean up. It is exactly WRONG for a hand-off. Opening
        /// wtGui and then disposing would launch MATLAB, start the GUI, and close it again --
        /// from the outside, indistinguishable from nothing having happened at all.
        ///
        /// Call this when the point of the session was to LEAVE something running for the
        /// user. The COM reference is still released on Dispose; only the Quit is skipped.
        /// </summary>
        public void Detach() => _leaveRunning = true;

        /// <summary>
        /// Attach to a MATLAB the user already has open; launch one only if there is none.
        ///
        /// ATTACHING IS STRONGLY PREFERRED and is tried first. A cold MATLAB start plus a
        /// Simulink load is tens of seconds, and the user working on the turbine already has
        /// the model open and warm. It also means the run happens in the MATLAB they are
        /// watching, so the plots and the Command Window output land where they expect.
        /// </summary>
        /// <param name="allowLaunch">
        /// When false, refuse to start a new MATLAB and throw instead. Useful for a UI that
        /// wants to say "start MATLAB first" rather than silently spending 30 seconds.
        /// </param>
        /// <param name="progId">
        /// Which MATLAB. Defaults to <see cref="DefaultProgId"/>, which is only correct on a
        /// machine with ONE MATLAB installed.
        ///
        /// MATLAB also registers a VERSIONED ProgID alongside the generic one -- for R2011a
        /// (MATLAB 7.12) that is "matlab.application.7.12". Passing it makes both the Running
        /// Object Table attach and any launch target that specific release, which is the only
        /// reliable way to reach R2011a on a machine that also has a current MATLAB.
        ///
        /// To see what is registered, from a command prompt:
        ///     reg query HKCR /f "matlab.application" /k
        /// </param>
        [SupportedOSPlatform("windows")]
        public MatlabComSession(bool allowLaunch = true, string? progId = null)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "MatlabComSession requires Windows COM automation. On any other platform, " +
                    "supply a different IMatlabSession implementation.");

            ProgId = string.IsNullOrWhiteSpace(progId) ? DefaultProgId : progId!;

            _matlab = TryAttachToRunningInstance(ProgId);
            if (_matlab is not null)
            {
                _weLaunchedIt = false;
                return;
            }

            if (!allowLaunch)
                throw new MatlabStageException(
                    $"No running MATLAB was found for ProgID '{ProgId}', and launching one was " +
                    "not permitted.\n\n" +
                    "Start MATLAB, open the turbine folder, and try again -- or pass " +
                    "allowLaunch: true to have this start one (expect a cold start to take " +
                    "tens of seconds).\n\n" +
                    (ProgId == DefaultProgId
                        ? "NOTE: '" + DefaultProgId + "' resolves to ONE release -- whichever " +
                          "registered last. If the MATLAB you want is open but a newer one is " +
                          "installed, the attach looks for the newer one's CLSID and misses. " +
                          "Pass the versioned ProgID instead, e.g. 'matlab.application.7.12' " +
                          "for R2011a."
                        : "The versioned ProgID was used, so this means no MATLAB of that " +
                          "release is currently running."));

            var type = Type.GetTypeFromProgID(ProgId);
            if (type is null)
                throw new MatlabStageException(
                    $"MATLAB's COM server ('{ProgId}') is not registered on this machine.\n\n" +
                    "If this was a versioned ProgID, check the exact spelling with:\n" +
                    "    reg query HKCR /f \"matlab.application\" /k\n\n" +
                    "This usually means MATLAB is not installed, or was installed without " +
                    "registering the automation server. Re-register with:\n" +
                    "    matlab -regserver\n" +
                    "run from an elevated prompt in MATLAB's bin directory.\n\n" +
                    "NOTE: registration is what ToolStatusService checks for its MATLAB status " +
                    "dot, so a green dot means REGISTERED -- not running, not licensed, and " +
                    "not necessarily the release the turbine model needs.");

            try
            {
                _matlab = Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                throw new MatlabStageException(
                    "MATLAB's COM server is registered but would not start. A licence failure " +
                    "typically surfaces here rather than as a licence message.", ex);
            }

            if (_matlab is null)
                throw new MatlabStageException("MATLAB's COM server returned no instance.");

            _weLaunchedIt = true;

            // Only touch visibility on an instance we created. Forcing Visible on a MATLAB the
            // user already had arranged would rearrange their desktop for them.
            TrySetVisible(true);
        }

        /// <summary>
        /// Wrap an already-obtained COM object, for callers using another transport.
        /// <paramref name="progId"/> is recorded for reporting only -- this constructor does no
        /// resolution, so it is whatever the caller says it is.
        /// </summary>
        [SupportedOSPlatform("windows")]
        public MatlabComSession(object matlabComObject, bool weLaunchedIt = false, string? progId = null)
        {
            _matlab = matlabComObject ?? throw new ArgumentNullException(nameof(matlabComObject));
            _weLaunchedIt = weLaunchedIt;
            ProgId = string.IsNullOrWhiteSpace(progId) ? "(supplied instance)" : progId!;
        }

        // ------------------------------------------------------------------
        public string Execute(string command)
        {
            if (command is null) throw new ArgumentNullException(nameof(command));
            var matlab = Live();

            try
            {
                var result = matlab.GetType().InvokeMember(
                    "Execute",
                    System.Reflection.BindingFlags.InvokeMethod,
                    binder: null, target: matlab,
                    args: new object[] { command });

                return result as string ?? string.Empty;
            }
            catch (COMException ex)
            {
                throw new MatlabStageException(
                    "The MATLAB COM call failed at the transport level (not a MATLAB error -- " +
                    "a MATLAB error would come back as ordinary text). The usual cause is that " +
                    "MATLAB was closed while this stage was running.\n\n" +
                    $"Command was:\n{command}", ex);
            }
        }

        public string GetCharArray(string variableName)
        {
            if (string.IsNullOrWhiteSpace(variableName))
                throw new ArgumentException("Variable name is required.", nameof(variableName));
            var matlab = Live();

            try
            {
                var result = matlab.GetType().InvokeMember(
                    "GetCharArray",
                    System.Reflection.BindingFlags.InvokeMethod,
                    binder: null, target: matlab,
                    args: new object[] { variableName, "base" });

                return result as string ?? string.Empty;
            }
            catch (COMException ex)
            {
                throw new MatlabStageException(
                    $"Could not read '{variableName}' from MATLAB's base workspace. " +
                    "MatlabStageService always assigns its sentinel before reading it, so this " +
                    "usually means the command that should have assigned it never ran.", ex);
            }
        }

        // ------------------------------------------------------------------
        private object Live()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _matlab ?? throw new MatlabStageException("The MATLAB session is not connected.");
        }

        private void TrySetVisible(bool visible)
        {
            try
            {
                _matlab!.GetType().InvokeMember(
                    "Visible",
                    System.Reflection.BindingFlags.SetProperty,
                    binder: null, target: _matlab,
                    args: new object[] { visible ? 1 : 0 });
            }
            catch (Exception)
            {
                // Non-fatal by design. An invisible MATLAB still runs the simulation correctly;
                // failing the whole export because a window would not show itself would trade a
                // cosmetic problem for a real one.
            }
        }

        [SupportedOSPlatform("windows")]
        private static object? TryAttachToRunningInstance(string progId)
        {
            // Marshal.GetActiveObject WAS the one-liner for this and was REMOVED in .NET Core.
            // It has never returned to .NET, so the Running Object Table has to be reached
            // through the underlying COM APIs directly. This is the documented replacement, not
            // a workaround.
            try
            {
                CLSIDFromProgID(progId, out var clsid);
                GetActiveObject(ref clsid, IntPtr.Zero, out var instance);
                return instance;
            }
            catch (Exception)
            {
                // Nothing in the ROT, or the ProgID is not registered. Both are ordinary and
                // both mean "no instance to attach to"; the caller decides whether to launch.
                //
                // WORTH KNOWING WHEN THIS SURPRISES YOU: the lookup is by CLSID, not by name.
                // CLSIDFromProgID resolves the ProgID to one CLSID and GetActiveObject then
                // searches the ROT for exactly that. So a generic ProgID pointing at R2025b
                // will MISS an open R2011a and report "nothing running" while the release you
                // wanted is on screen. That is not a bug here -- it is what the generic ProgID
                // means -- but it is the least intuitive failure in this file.
                return null;
            }
        }

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CLSIDFromProgID(
            [MarshalAs(UnmanagedType.LPWStr)] string lpszProgID, out Guid lpclsid);

        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(
            ref Guid rclsid, IntPtr pvReserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

        // ------------------------------------------------------------------
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_matlab is null) return;

            // NEVER quit a MATLAB we attached to. The user has the turbine model open in it;
            // closing their session because an export finished would be an unpleasant surprise
            // and would lose unsaved work. Only an instance we launched is ours to close --
            // and not even then if Detach() was called, which is how a hand-off says "this
            // one is the user's now".
            if (_weLaunchedIt && !_leaveRunning)
            {
                try
                {
                    _matlab.GetType().InvokeMember(
                        "Quit", System.Reflection.BindingFlags.InvokeMethod,
                        binder: null, target: _matlab, args: null);
                }
                catch (Exception)
                {
                    // Best effort. A MATLAB that will not quit is not a reason to fail an
                    // export that has already written its output.
                }
            }

            if (OperatingSystem.IsWindows() && Marshal.IsComObject(_matlab))
                Marshal.ReleaseComObject(_matlab);

            _matlab = null;
        }
    }
}
