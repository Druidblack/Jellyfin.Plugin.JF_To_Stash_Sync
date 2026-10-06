using System;
using System.IO;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;

namespace JFToStashSync.Configuration;

internal static class LegacyConfigurationMigrator
{
    private const string LegacyConfigurationFileName = "Jellyfin.Plugin.StashWatchSync.xml";

    public static bool TryMigrate(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        string currentConfigurationPath)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(xmlSerializer);

        if (string.IsNullOrWhiteSpace(currentConfigurationPath))
        {
            return false;
        }

        var legacyConfigurationPath = Path.Combine(
            applicationPaths.PluginConfigurationsPath,
            LegacyConfigurationFileName);

        if (!File.Exists(legacyConfigurationPath))
        {
            return false;
        }

        try
        {
            var legacy = Deserialize(xmlSerializer, legacyConfigurationPath);
            if (legacy is null)
            {
                return false;
            }

            var currentFileExists = File.Exists(currentConfigurationPath);
            var currentFromDisk = currentFileExists
                ? Deserialize(xmlSerializer, currentConfigurationPath)
                : null;
            var current = currentFromDisk ?? new PluginConfiguration();

            var defaults = new PluginConfiguration();
            var changed = MergeLegacyValues(current, legacy, defaults);

            if (changed || !currentFileExists || currentFromDisk is null)
            {
                var directory = Path.GetDirectoryName(currentConfigurationPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                xmlSerializer.SerializeToFile(current, currentConfigurationPath);
            }

            MoveLegacyFileToBackup(legacyConfigurationPath);
            return true;
        }
        catch
        {
            // A migration problem must never prevent Jellyfin from loading the plugin.
            // Keep the legacy file untouched so the migration can be retried after the issue is fixed.
            return false;
        }
    }

    private static PluginConfiguration? Deserialize(IXmlSerializer xmlSerializer, string path)
    {
        try
        {
            return xmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), path) as PluginConfiguration;
        }
        catch
        {
            return null;
        }
    }

    private static bool MergeLegacyValues(
        PluginConfiguration current,
        PluginConfiguration legacy,
        PluginConfiguration defaults)
    {
        var changed = false;
        var properties = typeof(PluginConfiguration).GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

        foreach (var property in properties)
        {
            if (!property.CanRead ||
                !property.CanWrite ||
                property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var currentValue = property.GetValue(current);
            var legacyValue = property.GetValue(legacy);
            var defaultValue = property.GetValue(defaults);

            // Preserve values already configured in the new file. Only fill settings
            // that are still at their current-version defaults.
            if (!ValuesEqual(currentValue, defaultValue) || ValuesEqual(legacyValue, defaultValue))
            {
                continue;
            }

            property.SetValue(current, legacyValue);
            changed = true;
        }

        return changed;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is string leftString && right is string rightString)
        {
            return string.Equals(leftString, rightString, StringComparison.Ordinal);
        }

        return Equals(left, right);
    }

    private static void MoveLegacyFileToBackup(string legacyConfigurationPath)
    {
        var backupPath = legacyConfigurationPath + ".migrated.bak";
        if (!File.Exists(backupPath))
        {
            File.Move(legacyConfigurationPath, backupPath);
            return;
        }

        for (var index = 1; index < 1000; index++)
        {
            var candidate = legacyConfigurationPath + $".migrated.{index}.bak";
            if (File.Exists(candidate))
            {
                continue;
            }

            File.Move(legacyConfigurationPath, candidate);
            return;
        }

        // Extremely unlikely fallback: leave the legacy file in place rather than deleting it.
    }
}
