using System;
using System.Windows.Automation;

namespace WinResizer.Core.WindowControl;

public class WindowEventHandler:IDisposable
{
    private readonly AutomationEventHandler _eventHandler;
    private bool _isRegistered;

    public WindowEventHandler(Action<IntPtr> windowCreatedHandler)
    {
        _eventHandler = (sender, _) =>
        {
            var src = sender as AutomationElement;
            if (src is null)
            {
                return;
            }

            IntPtr handle;
            try
            {
                handle = new IntPtr(src.Current.NativeWindowHandle);
            }
            catch (ElementNotAvailableException)
            {
                // The UI Automation element can disappear before its HWND is read.
                return;
            }
            catch (InvalidOperationException)
            {
                // A UI Automation provider can become invalid while Current is read.
                return;
            }

            if (handle != IntPtr.Zero)
            {
                windowCreatedHandler(handle);
            }
        };
    }

    public void AddWindowCreateHandle()
    {
        if (_isRegistered)
        {
            return;
        }

        Automation.AddAutomationEventHandler(
            WindowPattern.WindowOpenedEvent,
            AutomationElement.RootElement,
            TreeScope.Children,
            _eventHandler
        );
        _isRegistered = true;
    }

    public void RemoveWindowCreateHandle()
    {
        if (!_isRegistered)
        {
            return;
        }

        Automation.RemoveAutomationEventHandler(
            WindowPattern.WindowOpenedEvent,
            AutomationElement.RootElement,
            _eventHandler
        );
        _isRegistered = false;
    }

    public void Dispose()
    {
        RemoveWindowCreateHandle();
    }
}
