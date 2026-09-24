using System;

namespace WinResizer.Common.Exceptions;

public class WinResizerException : Exception
{
    public WinResizerException(string message)
        : base(message)
    {
    }

    public WinResizerException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
