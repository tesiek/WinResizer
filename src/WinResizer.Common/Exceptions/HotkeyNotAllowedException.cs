namespace WinResizer.Common.Exceptions;

public sealed class HotkeyNotAllowedException : WinResizerException
{
    public const string Title = "Hotkey Not Allowed";
    public const string Explanation = "Ctrl+Alt shortcuts without the Windows key can conflict with AltGr and prevent typing national characters.\nAdd the Windows key or choose another shortcut.";

    public HotkeyNotAllowedException() : base(Explanation)
    {
    }
}
