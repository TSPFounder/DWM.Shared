// MatlabStageException.cs
// One exception type for every way the MATLAB stage can fail, so a UI can catch a single
// type and show the message. Messages are written to be READ BY A HUMAN AT 11PM -- they say
// what was expected, what was found, and what to do, because the alternative is a stack
// trace pointing at a COM marshaller.

using System;

namespace DWM.Shared.Matlab
{
    public sealed class MatlabStageException : Exception
    {
        public MatlabStageException(string message) : base(message) { }
        public MatlabStageException(string message, Exception inner) : base(message, inner) { }
    }
}
