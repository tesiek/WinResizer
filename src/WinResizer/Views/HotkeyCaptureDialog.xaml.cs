using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using Forms = System.Windows.Forms;
using WinResizer.Common.Shortcuts;
using WinResizer.Core.Shortcuts;

namespace WinResizer.Views;

public enum HotkeyCaptureResult
{
    Cancel,
    Set,
    Clear,
}

public partial class HotkeyCaptureDialog : Window
{
    private readonly GlobalKeyboardHook _globalHook = new GlobalKeyboardHook();
    private readonly HashSet<Forms.Keys> _pressedKeys = new HashSet<Forms.Keys>();
    private readonly Hotkeys _capturedHotkey = new Hotkeys();
    private readonly bool _allowClear;
    private bool _capturing;

    public HotkeyCaptureDialog(string actionName, string currentKey, bool allowClear = false)
    {
        InitializeComponent();
        _allowClear = allowClear;
        ClearButton.Visibility = allowClear ? Visibility.Visible : Visibility.Collapsed;
        ActionText.Text = $"Set {actionName} key";
        CurrentKeyText.Text = string.IsNullOrWhiteSpace(currentKey)
            ? "Waiting for input…"
            : $"Current: {currentKey}";

        _globalHook.KeyDown += HookOnKeyDown;
        _globalHook.KeyUp += HookOnKeyUp;
    }

    public Hotkeys? ResultHotkey { get; private set; }

    public HotkeyCaptureResult Result { get; private set; } = HotkeyCaptureResult.Cancel;

    private void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _globalHook.Hook();
            _capturing = true;
            Activate();
        }
        catch (Win32Exception exception)
        {
            _capturing = false;
            _pressedKeys.Clear();
            MessageBox.Show(
                this,
                $"The keyboard recorder could not be started.\n\n{exception.Message}",
                "WinResizer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            DialogResult = false;
        }
    }

    private void Dialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _capturing = false;
        _pressedKeys.Clear();
        _globalHook.UnHook();
        _globalHook.Dispose();
    }

    private void HookOnKeyDown(object sender, Forms.KeyEventArgs args)
    {
        args.Handled = true;
        if (!_capturing)
        {
            return;
        }

        var key = args.KeyCode;
        Dispatcher.BeginInvoke(new Action(() => HandleKeyDown(key)));
    }

    private void HookOnKeyUp(object sender, Forms.KeyEventArgs args)
    {
        args.Handled = true;
        if (!_capturing)
        {
            return;
        }

        var key = args.KeyCode;
        Dispatcher.BeginInvoke(new Action(() => HandleKeyUp(key)));
    }

    private void HandleKeyDown(Forms.Keys key)
    {
        if (!_capturing || !_pressedKeys.Add(key))
        {
            return;
        }

        if (key.IsModifierKey())
        {
            _capturedHotkey.ModifierKeys.Add(key.ToKeyString());
        }
        else
        {
            _capturedHotkey.Key = key.ToKeyString();
        }

        CurrentKeyText.Text = $"{_capturedHotkey.ToKeysString()} …";
    }

    private void HandleKeyUp(Forms.Keys key)
    {
        if (!_capturing)
        {
            return;
        }

        _pressedKeys.Remove(key);
        if (_pressedKeys.Count != 0)
        {
            return;
        }

        if (!_capturedHotkey.IsValid())
        {
            _capturedHotkey.Clear();
            CurrentKeyText.Text = "Waiting for input…";
            return;
        }

        ResultHotkey = new Hotkeys
        {
            ModifierKeys = new HashSet<string>(_capturedHotkey.ModifierKeys),
            Key = _capturedHotkey.Key,
        };
        Result = HotkeyCaptureResult.Set;
        DialogResult = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowClear)
        {
            return;
        }

        ResultHotkey = null;
        Result = HotkeyCaptureResult.Clear;
        DialogResult = true;
    }
}
