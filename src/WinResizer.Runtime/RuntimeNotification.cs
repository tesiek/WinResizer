using System;

namespace WinResizer.Runtime;

public enum RuntimeNotificationLevel
{
    Success,
    Info,
    Warning,
    Error,
}

public sealed class RuntimeNotificationEventArgs : EventArgs
{
    public RuntimeNotificationEventArgs(
        string title,
        string message,
        RuntimeNotificationLevel level,
        bool openProcessSettings = false)
    {
        Title = title;
        Message = message;
        Level = level;
        OpenProcessSettings = openProcessSettings;
    }

    public string Title { get; }

    public string Message { get; }

    public RuntimeNotificationLevel Level { get; }

    public bool OpenProcessSettings { get; }
}

public sealed class RuntimeProfileInfo
{
    public RuntimeProfileInfo(string id, string name, bool isCurrent)
    {
        Id = id;
        Name = name;
        IsCurrent = isCurrent;
    }

    public string Id { get; }

    public string Name { get; }

    public bool IsCurrent { get; }
}
