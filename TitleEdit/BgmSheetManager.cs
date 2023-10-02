using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Lumina.Excel.GeneratedSheets;

namespace TitleEdit;

public struct BgmInfo
{
    public string Title;
    public string Location;
    public string FilePath;
    public string AdditionalInfo;
}

public class BgmSheetManager
{
    private const string SheetPath = @"https://docs.google.com/spreadsheets/d/1qAkxPiXWF-EUHbIXdNcO-Ilo2AwLnqvdpW9tjKPitPY/gviz/tq?tqx=out:csv&sheet={0}";
    private const string SheetFileName = "xiv_bgm_{0}.csv";
    private readonly Dictionary<int, BgmInfo> _bgms;
    private readonly Dictionary<uint, string> _bgmPaths;
    private readonly HttpClient _client = new();

    public BgmSheetManager()
    {
        _bgms = new Dictionary<int, BgmInfo>();
        _bgmPaths = DalamudApi.DataManager.GetExcelSheet<BGM>()!.ToDictionary(r => r.RowId, r => r.File.ToString());
        
        try
        {
            DalamudApi.PluginLog.Information("[SongList] Checking for updated bgm sheets");
            LoadLangSheet(GetRemoteSheet("en"), "en");
        }
        catch (Exception e)
        {
            DalamudApi.PluginLog.Error(e, "[SongList] Orchestrion failed to update bgm sheet; using previous version");
            LoadLangSheet(GetLocalSheet("en"), "en");
        }
    }
    
    private string GetRemoteSheet(string code)
    {
        return _client.GetStringAsync(string.Format(SheetPath, code)).Result;
    }

    private string GetLocalSheet(string code)
    {
        return File.ReadAllText(Path.Combine(DalamudApi.PluginInterface.AssemblyLocation.DirectoryName!, string.Format(SheetFileName, code)));
    }

    private void SaveLocalSheet(string text, string code)
    {
        File.WriteAllText(Path.Combine(DalamudApi.PluginInterface.AssemblyLocation.DirectoryName!, string.Format(SheetFileName, code)), text);
    }

    private void LoadLangSheet(string sheetText, string code)
    {
        var sheetLines = sheetText.Split('\n'); // gdocs provides \n
        for (int i = 1; i < sheetLines.Length; i++)
        {
            // The formatting is odd here because gdocs adds quotes around columns and doubles each single quote
            var elements = sheetLines[i].Split(new[] { "\"," }, StringSplitOptions.None);
            var id = int.Parse(elements[0].Substring(1));
            var name = elements[1].Substring(1);
            var locations = elements[4].Substring(1);
            var addtlInfo = elements[5].Substring(1, elements[5].Length - 2).Replace("\"\"", "\"");

            if (string.IsNullOrEmpty(name) || name == "Null BGM" || name == "test")
                continue;

            if (!_bgmPaths.TryGetValue((uint)id, out var path))
                continue;
            
            var bgm = new BgmInfo
            {
                Title = name,
                FilePath = path,
                Location = locations,
                AdditionalInfo = addtlInfo,
            };
            _bgms[id] = bgm;
        }
        SaveLocalSheet(sheetText, code);
    }
    
    public BgmInfo GetBgmInfo(ushort id)
    {
        return !_bgms.TryGetValue(id, out var info) ? new BgmInfo {Title = "Invalid"} : info;
    }
}
