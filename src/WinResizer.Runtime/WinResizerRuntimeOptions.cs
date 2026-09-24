using System;
using System.Collections.Generic;
using WinResizer.Configuration;
using WinResizer.Core.Shortcuts;

namespace WinResizer.Runtime;

public sealed class WinResizerRuntimeOptions
{
    public bool EnableGlobalHotkeys { get; set; } = true;

    public bool EnableWindowMonitoring { get; set; } = true;

    public IGlobalHotkeyRegistrar? HotkeyRegistrar { get; set; }

    public bool OwnsHotkeyRegistrar { get; set; } = true;

    public IDictionary<HotkeysType, int>? RegisteredHotkeys { get; set; }

    public IDictionary<string, int>? RegisteredPresetHotkeys { get; set; }

    /// <summary>
    /// Optional persistence seam for runtime tests. Production callers leave
    /// this unset and use ConfigFactory.Save.
    /// </summary>
    public Action? SaveConfiguration { get; set; }

    public Action<string>? Log { get; set; }
}
