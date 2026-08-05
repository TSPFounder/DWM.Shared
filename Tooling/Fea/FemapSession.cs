// FemapSession.cs
// FEMAP over COM, mirroring MatlabComSession -- same shape, same hazards, same habits.
//
// WHAT IS GUESSED HERE, STATED PLAINLY
//
// FEMAP's API is large and version-specific, and none of the names below have been checked
// against the 10.2 reference on the machine that has it. They are therefore DATA
// (FemapApiNames), not literals buried in call sites: a wrong name is corrected in one
// place, by a caller, without a rebuild -- exactly the shape the ProgID and the MYSTRAN
// executable path both ended up needing after being wrong first.
//
// The failure message names the method it tried and the arguments it passed, because the
// alternative -- "COM call failed" -- would send someone reading FEMAP's whole API reference
// rather than one entry of it.
//
// THE LIFETIME RULE IS THE SAME ONE MATLAB TAUGHT. A COM server launched by a client belongs
// to that client and exits when the last reference goes. FEMAP started this way would vanish
// the moment the session disposed, taking the results with it -- so a session meant to leave
// FEMAP open for the user must Detach().

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DWM.Shared.Tooling.Fea
{
    /// <summary>One way of calling FEMAP for a given job: a method name and an argument shape.</summary>
    public sealed class FemapCallShape
    {
        public string Method { get; init; } = string.Empty;

        /// <summary>Builds the argument list from the file path.</summary>
        public Func<string, object[]> Args { get; init; } = path => new object[] { path };

        /// <summary>Shown when this shape is the one that worked, so it can become the default.</summary>
        public string Signature { get; init; } = string.Empty;
    }

    /// <summary>
    /// How to ask FEMAP to do each job -- as an ORDERED LIST OF CANDIDATES, not one guess.
    ///
    /// The first real attempt got 'feFileReadNastran' failed with 1 argument(s): Type mismatch
    /// (DISP_E_TYPEMISMATCH). That is a useful failure: the ProgID was right, the METHOD EXISTS
    /// -- a wrong name raises MissingMethodException instead -- and only the argument shape was
    /// wrong. Most likely it wants (setId, filename) and was handed a single string to coerce
    /// into a number.
    ///
    /// Since the API reference has not been read, guessing one harder would just be the same
    /// mistake again. Instead the shapes are tried in order until one succeeds, and the one
    /// that worked is REPORTED -- so it can be promoted to the default with evidence rather
    /// than another guess. Attempting is the probe; that principle has already earned its place
    /// twice today.
    /// </summary>
    public sealed class FemapApiNames
    {
        public IReadOnlyList<FemapCallShape> ReadNastranModel { get; init; } = new[]
        {
            new FemapCallShape
            {
                Method = "feFileReadNastran",
                Args = path => new object[] { 0, path },
                Signature = "feFileReadNastran(setId, filename)"
            },
            new FemapCallShape
            {
                Method = "feFileReadNastran",
                Args = path => new object[] { path },
                Signature = "feFileReadNastran(filename)"
            },
            new FemapCallShape
            {
                Method = "feFileReadNastranModel",
                Args = path => new object[] { path },
                Signature = "feFileReadNastranModel(filename)"
            }
        };

        public IReadOnlyList<FemapCallShape> ReadNastranResults { get; init; } = new[]
        {
            new FemapCallShape
            {
                Method = "feFileReadNastranResults",
                Args = path => new object[] { 1, path },
                Signature = "feFileReadNastranResults(setId, filename)"
            },
            new FemapCallShape
            {
                Method = "feFileReadNastranResults",
                Args = path => new object[] { path },
                Signature = "feFileReadNastranResults(filename)"
            },
            new FemapCallShape
            {
                Method = "feFileReadNastran",
                Args = path => new object[] { 1, path },
                Signature = "feFileReadNastran(setId, filename) -- if this one reads RESULTS"
            }
        };

        /// <summary>Show the FEMAP window. Failure here is not fatal.</summary>
        public string SetVisible { get; init; } = "feAppVisible";

        /// <summary>
        /// Start an empty model, so a repeat load has somewhere clean to land.
        ///
        /// Needed because re-importing into a populated FEMAP does not replace -- it collides.
        /// The 2026-08-05 second run produced "Overwriting existing Property 101..110",
        /// "Overwriting existing Element 1..10" and a SECOND set of six output sets, leaving
        /// twelve where there should be six. Nothing was lost, but the results view stopped
        /// meaning one run.
        /// </summary>
        public IReadOnlyList<FemapCallShape> NewModel { get; init; } = new[]
        {
            new FemapCallShape
            {
                Method = "feFileNew",
                Args = _ => Array.Empty<object>(),
                Signature = "feFileNew()"
            }
        };
    }

    public interface IFemapSession : IDisposable
    {
        /// <summary>Call one FEMAP API method. Returns whatever it returned, usually a status int.</summary>
        object? Invoke(string method, params object[] args);

        bool IsAttachedToExistingInstance { get; }

        /// <summary>Release without closing FEMAP. See the lifetime note in the file header.</summary>
        void Detach();
    }

    public sealed class FemapComSession : IFemapSession
    {
        public const string DefaultProgId = "femap.model";

        private object? _femap;
        private readonly bool _weLaunchedIt;
        private bool _leaveRunning;
        private bool _disposed;

        public string ProgId { get; }
        public bool IsAttachedToExistingInstance => !_weLaunchedIt;

        [SupportedOSPlatform("windows")]
        public FemapComSession(bool allowLaunch = true, string? progId = null)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("FemapComSession requires Windows COM automation.");

            ProgId = string.IsNullOrWhiteSpace(progId) ? DefaultProgId : progId!;

            _femap = TryAttach(ProgId);
            if (_femap is not null) { _weLaunchedIt = false; return; }

            if (!allowLaunch)
                throw new FemapSessionException(
                    $"No running FEMAP was found for ProgID '{ProgId}', and launching one was not permitted.");

            var type = Type.GetTypeFromProgID(ProgId);
            if (type is null)
                throw new FemapSessionException(
                    $"FEMAP's COM server ('{ProgId}') is not registered on this machine.\n\n" +
                    "Check the exact ProgID with:\n" +
                    "    reg query HKCR /f \"femap\" /k\n\n" +
                    $"'{DefaultProgId}' is this build's default and has NOT been verified against " +
                    "an installed FEMAP -- if the registry says otherwise, pass the right one.");

            _femap = Activator.CreateInstance(type)
                     ?? throw new FemapSessionException("FEMAP's COM server returned no instance.");
            _weLaunchedIt = true;
        }

        public void Detach() => _leaveRunning = true;

        public object? Invoke(string method, params object[] args)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var femap = _femap ?? throw new FemapSessionException("The FEMAP session is not connected.");

            try
            {
                return femap.GetType().InvokeMember(
                    method, BindingFlags.InvokeMethod, binder: null, target: femap, args: args);
            }
            catch (MissingMethodException ex)
            {
                // The most likely failure, and the one worth being specific about: FEMAP is
                // there and answering, the method name is simply wrong for this version.
                throw new FemapSessionException(
                    $"FEMAP has no method '{method}'.\n\n" +
                    "The API names in FemapApiNames are UNVERIFIED against FEMAP 10.2. Look this " +
                    "one up in the API reference under the FEMAP install and pass a corrected " +
                    "FemapApiNames -- no rebuild is needed, the names are data.", ex);
            }
            catch (TargetInvocationException ex)
            {
                throw new FemapSessionException(
                    $"FEMAP's '{method}' failed with {args.Length} argument(s): " +
                    (ex.InnerException?.Message ?? ex.Message) + "\n\n" +
                    "If the method exists but the arguments are wrong, the reference entry for " +
                    "it gives the expected order and types.", ex);
            }
            catch (COMException ex)
            {
                throw new FemapSessionException(
                    $"The FEMAP COM call '{method}' failed at the transport level. The usual " +
                    "cause is FEMAP being closed while this was running.", ex);
            }
        }

        [SupportedOSPlatform("windows")]
        private static object? TryAttach(string progId)
        {
            // Same Running Object Table dance as MATLAB, and the same caveat: the lookup is by
            // CLSID, so a ProgID naming the wrong FEMAP misses an open one entirely.
            try
            {
                CLSIDFromProgID(progId, out var clsid);
                GetActiveObject(ref clsid, IntPtr.Zero, out var instance);
                return instance;
            }
            catch (Exception)
            {
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_femap is null) return;

            // NEVER close a FEMAP we attached to, and not one we launched either once Detach
            // has been called -- the whole point of the post-processing hand-off is that the
            // user is left looking at their results.
            if (OperatingSystem.IsWindows() && Marshal.IsComObject(_femap) && !_leaveRunning)
                Marshal.ReleaseComObject(_femap);

            _femap = null;
        }
    }

    /// <summary>
    /// FEMAP went wrong. Its own type rather than a shared one, because the recovery is
    /// specific: nearly every failure here is a wrong API name or a wrong ProgID, both of
    /// which are data a caller can correct.
    /// </summary>
    public sealed class FemapSessionException : Exception
    {
        public FemapSessionException(string message) : base(message) { }
        public FemapSessionException(string message, Exception inner) : base(message, inner) { }
    }
}
