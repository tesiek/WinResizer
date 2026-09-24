using System;

namespace WinResizer.Common.Exceptions;

public class HotkeyHookException : WinResizerException
{
    public HotkeyHookException()
        : base("Unable to register hotkeys.")
    {
    }

    public HotkeyHookException(string message)
        : base(message)
    {
    }

    public HotkeyHookException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
