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
        /// <summary>The ProgID ToolStatusService already probes for its MATLAB status dot.</summary>
        public const string ProgId = "matlab.application";

        private object? _matlab;
        private readonly bool _weLaunchedIt;
        private bool _disposed;

        public bool IsAttachedToExistingInstance => !_weLaunchedIt;

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
        [SupportedOSPlatform("windows")]
        public MatlabComSession(bool allowLaunch = true)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "MatlabComSession requires Windows COM automation. On any other platform, " +
                    "supply a different IMatlabSession implementation.");

            _matlab = TryAttachToRunningInstance();
            if (_matlab is not null)
            {
                _weLaunchedIt = false;
                return;
            }

            if (!allowLaunch)
                throw new MatlabStageException(
                    "No running MATLAB was found, and launching one was not permitted.\n\n" +
                    "Start MATLAB, open the turbine folder, and try again -- or pass " +
                    "allowLaunch: true to have this start one (expect a cold start to take " +
                    "tens of seconds).");

            var type = Type.GetTypeFromProgID(ProgId);
            if (type is null)
                throw new MatlabStageException(
                    $"MATLAB's COM server ('{ProgId}') is not registered on this machine.\n\n" +
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

        /// <summary>Wrap an already-obtained COM object, for callers using another transport.</summary>
        [SupportedOSPlatform("windows")]
        public MatlabComSession(object matlabComObject, bool weLaunchedIt = false)
        {
            _matlab = matlabComObject ?? throw new ArgumentNullException(nameof(matlabComObject));
            _weLaunchedIt = weLaunchedIt;
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
        private static object? TryAttachToRunningInstance()
        {
            // Marshal.GetActiveObject WAS the one-liner for this and was REMOVED in .NET Core.
            // It has never returned to .NET, so the Running Object Table has to be reached
            // through the underlying COM APIs directly. This is the documented replacement, not
            // a workaround.
            try
            {
                CLSIDFromProgID(ProgId, out var clsid);
                GetActiveObject(ref clsid, IntPtr.Zero, out var instance);
                return instance;
            }
            catch (Exception)
            {
                // Nothing in the ROT (MOST COMMON -- MATLAB simply is not running), or the
                // ProgID is not registered. Both are ordinary, and both mean "no instance to
                // attach to"; the caller decides whether to launch one.
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
            // and would lose unsaved work. Only an instance we launched is ours to close.
            if (_weLaunchedIt)
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
