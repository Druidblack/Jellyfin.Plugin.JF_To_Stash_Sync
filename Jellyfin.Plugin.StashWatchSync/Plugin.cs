using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

[assembly: CLSCompliant(false)]

namespace JFToStashSync;

public sealed class Plugin : BasePlugin<Configuration.PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public override string Name => "JF To Stash Sync";

    public override Guid Id => Guid.Parse("61db1999-72b9-4f58-ac15-25aedcabd583");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        // Migrate settings from the pre-rename assembly configuration file before
        // BasePlugin lazily loads Configuration from the new file name.
        JFToStashSync.Configuration.LegacyConfigurationMigrator.TryMigrate(
            applicationPaths,
            xmlSerializer,
            ConfigurationFilePath);

        Instance = this;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        };
    }
}
