using System;
using Newtonsoft.Json;
using WinResizer.Configuration;

namespace WinResizer.Base
{
    public static class ConfigUtils
    {
        public static bool Load(string? configPath, Action<string>? onError)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(configPath))
                {
                    ConfigFactory.InitializePortablePath();
                    ConfigFactory.Load();
                }
                else
                {
                    ConfigFactory.LoadExplicit(configPath!);
                }

                return true;
            }
            catch (ConfigurationReadException exception)
            {
                onError?.Invoke($"Could not read configuration file from <{exception.Path}>.");
                return false;
            }
            catch (JsonReaderException exception)
            {
                onError?.Invoke($"Configuration JSON from <{configPath ?? ConfigFactory.ConfigPath}> is invalid: {exception.Message}");
                return false;
            }
            catch (JsonSerializationException exception)
            {
                onError?.Invoke($"Configuration data from <{configPath ?? ConfigFactory.ConfigPath}> is invalid: {exception.Message}");
                return false;
            }
            catch (ConfigurationPersistenceException exception)
            {
                onError?.Invoke($"Could not write portable configuration to <{exception.Path}>. Move WinResizer to a writable folder or fix permissions.");
                return false;
            }
            catch (Exception exception)
            {
                onError?.Invoke($"Configuration operation failed: {exception.Message}");
                return false;
            }
        }
    }
}
