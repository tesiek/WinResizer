using System;
using System.Windows.Forms;
using WinResizer.Common.Shortcuts;

namespace WinResizer.Core.Shortcuts;

public interface IGlobalHotkeyRegistrar : IDisposable
{
    event EventHandler<KeyPressedEventArgs>? KeyPressed;

    int RegisterHotKey(ModifierKeys modifier, Keys key);

    void UnRegisterHotKey(int id);

    void UnRegisterHotKey();
}
