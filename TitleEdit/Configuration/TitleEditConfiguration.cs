using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

// ReSharper disable once CheckNamespace
namespace TitleEdit
{
    public enum OverrideSetting
    {
        Override,
        UseIfLogoUnspecified
    }

    [Serializable]
    public class TitleEditConfiguration : IPluginConfiguration
    {
        public List<string> TitleList { get; set; } = new();
        public string SelectedTitleFileName { get; set; } = "Shadowbringers";
        public string SelectedLogoName { get; set; } = "Shadowbringers";
        public bool DisplayTitleLogo { get; set; } = true;
        public OverrideSetting Override { get; set; } = OverrideSetting.UseIfLogoUnspecified;
        public bool DebugLogging { get; set; }

        int IPluginConfiguration.Version { get; set; } = 2;

        [NonSerialized] private DalamudPluginInterface _pluginInterface;
        
        public void Initialize(DalamudPluginInterface pluginInterface)
        {
            _pluginInterface = pluginInterface;
        }
        
        public void Save()
        {
            _pluginInterface.SavePluginConfig(this);
        }
    }
}
