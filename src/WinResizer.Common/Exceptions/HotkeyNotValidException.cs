using System;

namespace WinResizer.Common.Exceptions;

public class HotkeyNotValidException : WinResizerException
{
    public HotkeyNotValidException()
        : base("Not a valid hotkey.")
    {
    }

    public HotkeyNotValidException(string message)
        : base(message)
    {
    }

    public HotkeyNotValidException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
