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
        UseIfUnspecified
    }

    [Serializable]
    public class TitleEditConfiguration : IPluginConfiguration
    {
        public List<string> TitleList { get; set; } = new();
        public string SelectedTitleFileName { get; set; } = "Endwalker";
        public string SelectedLogoName { get; set; } = "Endwalker";
        public bool DisplayTitleLogo { get; set; } = true;
        public bool DisplayVersionText { get; set; } = true;
        public OverrideSetting Override { get; set; } = OverrideSetting.UseIfUnspecified;
        public OverrideSetting VisibilityOverride { get; set; } = OverrideSetting.UseIfUnspecified;
        public bool DisplayTitleToast { get; set; }
        public bool DebugLogging { get; set; }

        int IPluginConfiguration.Version { get; set; } = 2;

        [NonSerialized] private IDalamudPluginInterface _pluginInterface;
        
        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            _pluginInterface = pluginInterface;
        }
        
        public void Save()
        {
            _pluginInterface.SavePluginConfig(this);
        }
    }
}
