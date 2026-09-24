using System;
using System.IO;

namespace WinResizer.Configuration;

public sealed class ConfigurationReadException : IOException
{
    public ConfigurationReadException(string path, Exception innerException)
        : base($"Could not read configuration file '{path}'.", innerException)
    {
        Path = path;
    }

    public string Path { get; }
}

public sealed class ConfigurationPersistenceException : IOException
{
    public ConfigurationPersistenceException(string path, string operation, Exception innerException)
        : base($"Could not {operation} configuration file '{path}'.", innerException)
    {
        Path = path;
        Operation = operation;
    }

    public string Path { get; }

    public string Operation { get; }
}
