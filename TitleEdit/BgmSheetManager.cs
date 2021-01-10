using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Dalamud.Plugin;

namespace TitleEdit
{
    public struct BgmInfo
    {
        public string Title;
        public string Location;
        public string FilePath;
        public string AdditionalInfo;
    }

    public class BgmSheetManager
    {
        private const string SheetPath = @"https://docs.google.com/spreadsheets/d/1gGNCu85sjd-4CDgqw-K5tefTe4HYuDK38LkRyvx_fEc/gviz/tq?tqx=out:csv&sheet=main";
        private const string Filename = "bgm.csv";
        private Dictionary<ushort, BgmInfo> _bgms;
        private readonly Dictionary<ushort, string> _bgmPaths;
        private readonly string _pluginConfigFolder;

        public BgmSheetManager(string pluginConfigFolder, Dictionary<ushort, string> bgmPathDict)
        {
            _pluginConfigFolder = pluginConfigFolder;
            _bgmPaths = bgmPathDict;
            _bgms = new Dictionary<ushort, BgmInfo>();
            Task.Run(UpdateSheet);
        }

        // Attempts to load supplemental bgm data from the csv file
        // Will always instantiate _bgms with path information
        private bool LoadSheet(string sheetText)
        {
            _bgms = new Dictionary<ushort, BgmInfo>();

            foreach (var key in _bgmPaths)
            {
                _bgms[key.Key] = new BgmInfo {FilePath = key.Value};
            }

            bool loadSuccess = true;
            try
            {
                var sheetLines = sheetText.Split('\n'); // gdocs provides \n
                for (int i = 1; i < sheetLines.Length; i++)
                {
                    var elements = sheetLines[i].Split(new[] {"\","}, StringSplitOptions.None);
                    var id = ushort.Parse(elements[0].Substring(1));
                    _bgms.TryGetValue(id, out var info);
                    info.Title = elements[1].Substring(1).Replace("\"\"", "\"");
                    info.Location = elements[2].Substring(1).Replace("\"\"", "\"");
                    info.AdditionalInfo = elements[3].Substring(1, elements[3].Substring(1).Length - 1).Replace("\"\"", "\"");
                    _bgms[id] = info;
                }
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "Could not read bgm sheet.");
                loadSuccess = false;
            }

            return loadSuccess;
        }

        private void UpdateSheet()
        {
            var destination = _pluginConfigFolder + "\\" + Filename;
            using var client = new WebClient();
            try
            {
                var newText = client.DownloadString(SheetPath);
                if (File.Exists(destination))
                {
                    string existingText = File.ReadAllText(destination);
                    if (newText == existingText)
                    {
                        LoadSheet(existingText);
                        PluginLog.Log("Loaded bgms.");
                    }
                    else if (LoadSheet(newText))
                    {
                        File.WriteAllText(destination, newText);
                        PluginLog.Log("Updated bgm sheet.");
                    }
                    else
                    {
                        PluginLog.Error("There was a new bgm sheet, but parsing it failed.");
                        PluginLog.Error("TitleEdit failed to update bgm sheet.");
                        // Assume the previous file loaded fine?
                        LoadSheet(existingText);
                    }
                }
                else
                {
                    if (LoadSheet(newText))
                    {
                        File.WriteAllText(destination, newText);
                        PluginLog.Log("Updated bgm sheet");
                    }
                    else
                    {
                        PluginLog.Error("Failed to parse fresh bgm sheet.");
                        PluginLog.Error("TitleEdit failed to update bgm sheet.");
                    }
                }
            }
            catch (Exception e)
            {
                PluginLog.Error(e, "TitleEdit failed to update bgm sheet.");
            }
        }

        public BgmInfo GetBgmInfo(ushort id)
        {
            return !_bgms.TryGetValue(id, out var info) ? new BgmInfo {Title = "Invalid"} : info;
        }
    }
}