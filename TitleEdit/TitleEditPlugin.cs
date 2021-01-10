using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Actors;
using Dalamud.Game.Command;
using Dalamud.Game.Internal;
using Dalamud.Plugin;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace TitleEdit
{
    internal class TitleEditPlugin : IDalamudPlugin
    {
        public string Name => "Title Edit Plugin";

        public string AssemblyLocation { get; set; } = Assembly.GetExecutingAssembly().Location;

        private const int LookAtOffset = 192;
        private const int EyesPosOffset = 144;

        private TitleEditConfiguration _configuration;
        private DalamudPluginInterface _pluginInterface;
        private BgmSheetManager _bgmSheet;
        private TitleEdit _titleEdit;

        // Settings and other values
        private string _titleScreenFolder;
        private string[] _titleScreens;
        private string[] _titleScreensExport;
        private readonly string[] _titleLogos = {"A Realm Reborn", "FFXIV Free Trial", "Heavensward", "Stormblood", "Shadowbringers"};
        private readonly string[] _titleLogosCreate = {"A Realm Reborn", "FFXIV Free Trial", "Heavensward", "Stormblood", "Shadowbringers", "Unspecified"};
        private bool _canChangeUiVisibility = true;
        private bool _isImguiTitleEditOpen;
        private int _selectedTitleIndex;
        private int _selectedTitleIndexExport;
        private int _selectedLogoIndex;
        private bool _fileWasCreatedRecently;
        private ushort _lastTerritoryId;
        private ushort[] _territoryWeathers;
        private float _widestScreenName = 0f;

        // Import values
        private TitleEditScreen _importExistsScreen;
        private bool _importParsed = true;
        private string _importName = "";
        private string _importError = "";
        private string _importRef = "";
        private string _exportRef = "";

        // Custom title screen ImGui stuff
        private bool _nameContainsInvalidCharacters;
        private bool _nameAlreadyExists;
        private bool _nameEmpty = true;
        private string _titleScreenSavePath = "";
        private int _selectedLogoIndexCreate = 5;
        private bool _selectedLogoVisibleCreate;
        private string _customTsName = "";
        private float _fovY = 1f;
        private int _inputIntWeatherId;
        private int _weatherId;
        private int _selectedWeatherIndex;
        private int _tsTimeHrs;
        private int _tsTimeMin;
        private int _tsTimeOffset;
        private int _selectedBgmId;
        private string _terriPath = "";
        private ushort _lastBgmId = 0;
        private string _songDescription = ""; // This is global so we don't have to do text size calc every frame

        private Dictionary<uint, TerritoryType> _territoryPaths;
        private Dictionary<uint, string> _weathers;

        public void Initialize(DalamudPluginInterface pluginInterface)
        {
            _pluginInterface = pluginInterface;

            _pluginInterface.CommandManager.AddHandler("/ptitle", new CommandInfo(OnTitleEditCommand)
            {
                HelpMessage = "Open a window to set the title screen version.",
                ShowInHelp = true
            });

            _configuration = pluginInterface.GetPluginConfig() as TitleEditConfiguration ?? new TitleEditConfiguration();
            _configuration.Initialize(pluginInterface);

            _titleScreenFolder = Path.Combine(GetPluginConfigFolder(), "TitleEdit");
            if (!Directory.Exists(_titleScreenFolder))
                Directory.CreateDirectory(_titleScreenFolder);
            PrepareAssets();
            EnumerateTitleScreenFiles();

            _territoryPaths = pluginInterface.Data.GetExcelSheet<TerritoryType>()
                .ToDictionary(row => row.RowId, row => row);
            _weathers = pluginInterface.Data.GetExcelSheet<Weather>()
                .ToDictionary(row => row.RowId, row => row.Name.ToString());
            var bgms = pluginInterface.Data.GetExcelSheet<BGM>()
                .ToDictionary(row => (ushort) row.RowId, row => row.File.ToString());
            _bgmSheet = new BgmSheetManager(_titleScreenFolder, bgms);
            
            _titleEdit = new TitleEdit(pluginInterface, _configuration, _titleScreenFolder);
            _titleEdit.Enable();

            _pluginInterface.UiBuilder.OnBuildUi += UiBuilder_OnBuildUi;
            _pluginInterface.Framework.OnUpdateEvent += CheckHotkey;
            _pluginInterface.UiBuilder.OnOpenConfigUi += (_, _) => _isImguiTitleEditOpen = true;
        }
        
        private void PrepareAssets()
        {
            var temp = Path.Combine(Path.GetDirectoryName(AssemblyLocation), "titlescreens");
            var assets = Directory.GetFiles(temp);
            foreach (var asset in assets)
            {
                var destPath = Path.Combine(_titleScreenFolder, Path.GetFileName(asset));
                if (!File.Exists(destPath))
                    File.Copy(asset, destPath);
                else
                {
                    FileInfo assetFile = new FileInfo(asset);
                    FileInfo destFile = new FileInfo(destPath);
                    if (destFile.Exists)
                        if (assetFile.LastWriteTime > destFile.LastWriteTime)
                            assetFile.CopyTo(destFile.FullName, true);
                }
            }
        }

        private void EnumerateTitleScreenFiles()
        {
            var tmp =
                Directory.GetFiles(_titleScreenFolder)
                    .Where(file => file.EndsWith(".json"))
                    .Select(Path.GetFileNameWithoutExtension)
                    .ToList();
            tmp.Sort();
            _titleScreensExport = tmp.ToArray();
            tmp.Add("Random (custom)");
            tmp.Add("Random");
            _titleScreens = tmp.ToArray();

            // Let's prune any titles that we found in settings
            // that aren't in this folder, because if we end up
            // trying to load that, we will reset the user's entire
            // settings, which we'd like to avoid
            var removeList = new List<string>();
            foreach (var title in _configuration.TitleList)
                if (!_titleScreens.Contains(title))
                    removeList.Add(title);

            while (removeList.Count > 0)
            {
                _configuration.TitleList.Remove(removeList[0]);
                removeList.Remove(removeList[0]);
            }

            foreach (var title in _titleScreens)
            {
                var size = ImGui.CalcTextSize(title).X;
                if (size > _widestScreenName)
                    _widestScreenName = size;
            }
            
            // Update our selected indices because we modified the collections
            _selectedTitleIndex = GetIndexOfSelectedTitle();
            _selectedLogoIndex = GetIndexOfSelectedLogo();

            _configuration.Save();
        }

        private float GuiScale(float f)
        {
            return f * ImGui.GetIO().FontGlobalScale;
        }

        private void CheckHotkey(Framework framework)
        {
            // ctrl+t only on title screen (maybe?)
            if (_pluginInterface.ClientState.KeyState[0x11] &&
                _pluginInterface.ClientState.KeyState[0x54] &&
                _pluginInterface.ClientState.LocalPlayer == null &&
                _canChangeUiVisibility)
            {
                _isImguiTitleEditOpen = !_isImguiTitleEditOpen;
                _canChangeUiVisibility = false;
                Task.Delay(200).ContinueWith(_ => _canChangeUiVisibility = true);
            }
        }

        private void OnTitleEditCommand(string command, string arguments)
        {
            _isImguiTitleEditOpen = true;
        }

        private void UiBuilder_OnBuildUi()
        {
            if (!_isImguiTitleEditOpen)
                return;

            ImGui.SetNextWindowSize(new Vector2(GuiScale(480), GuiScale(450)));
            ImGui.Begin("Title Editing", ref _isImguiTitleEditOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            ImGui.BeginTabBar("TitleEditMainTabBar");
            DrawCreation();
            DrawManage();
            DrawSettings();
            DrawInformation();
            DrawCredits();
#if DEBUG
            TitleEditDebug.DrawDebug(_pluginInterface, _titleEdit, _weathers);
#endif
            ImGui.EndTabBar();

            ImGui.Spacing();
            ImGui.End();
        }

        private void DrawCreation()
        {
            if (!ImGui.BeginTabItem("Create"))
                return;

            ImGui.Text("This tab allows you to create a custom title screen.");
            ImGui.Text("It is recommended to use first-person view.");
#if DEBUG
            ImGui.BeginChild("scrolling", new Vector2(0, GuiScale(320)), true, ImGuiWindowFlags.NoScrollbar);
#else
            ImGui.BeginChild("scrolling", new Vector2(0, GuiScale(320)), true, ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar);
#endif
            bool stateInvalid;
            BgmInfo selectedBgm = new BgmInfo {Title = "Unknown", FilePath = ""};
            Vector3 eyesPos = default;
            Vector3 lookAt = default;
#if !DEBUG
            if (_pluginInterface?.ClientState?.LocalPlayer?.Position == null)
            {
                ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "The current game state is invalid for creating a title screen.");
                stateInvalid = true;
                _nameEmpty = false; //hax
            }
            else
#endif
            {
                ImGui.Text("Name of custom title screen:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(GuiScale(280f));
                if (ImGui.InputText("##title_screen_name", ref _customTsName, 64))
                {
                    _nameEmpty = false;
                    _nameContainsInvalidCharacters = false;
                    _nameAlreadyExists = false;

                    if (string.IsNullOrEmpty(_customTsName))
                        _nameEmpty = true;
                    else if (_customTsName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || _customTsName.StartsWith("TE_"))
                        _nameContainsInvalidCharacters = true;
                    else
                    {
                        _titleScreenSavePath = Path.Combine(_titleScreenFolder, _customTsName + ".json");
                        if (File.Exists(_titleScreenSavePath) || _customTsName == "Random" || _customTsName == "Random (custom)")
                            _nameAlreadyExists = true;
                    }
                }

                stateInvalid = _nameAlreadyExists | _nameContainsInvalidCharacters | _nameEmpty;
                
                ImGui.Text("Logo setting:");
                ImGui.SameLine();
                if (_titleLogosCreate[_selectedLogoIndexCreate] != "Unspecified")
                    ImGui.SetNextItemWidth(GuiScale(263f));
                else
                    ImGui.SetNextItemWidth(GuiScale(368f));
                ImGui.Combo("##titleeditLogoSetting", ref _selectedLogoIndexCreate, _titleLogosCreate, _titleLogosCreate.Length);
                if (_titleLogosCreate[_selectedLogoIndexCreate] != "Unspecified")
                {
                    ImGui.SameLine();
                    ImGui.Checkbox("Display logo", ref _selectedLogoVisibleCreate);
                }
#if DEBUG
                ImGui.Text("Terri path:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(GuiScale(220f));
                ImGui.InputText("##manualTerritype", ref _terriPath, 64);
                var search = _territoryPaths.Values.Where(row => row.Bg == _terriPath);
                var results = search as TerritoryType[] ?? search.ToArray();
                if (results.Any())
                {
                    var val = results[0];
                    ImGui.SameLine();
                    ImGui.PushTextWrapPos(GuiScale(450f));
                    ImGui.TextWrapped($"{val.PlaceName.Value.Name}");
                    ImGui.PopTextWrapPos();
                }
#else
                if (!_territoryPaths.ContainsKey(_pluginInterface.ClientState.TerritoryType))
                {
                    ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "The current territory is not valid for a title screen.");
                    stateInvalid = true;
                }
                else
                {
                    ImGui.Text($"Title screen zone: {_territoryPaths[_pluginInterface.ClientState.TerritoryType].PlaceName.Value.Name}");
                }
#endif

                eyesPos = EyesPos(_pluginInterface.ClientState?.LocalPlayer?.Position ?? new Position3{X = 0, Y = 0, Z = 0});
                ImGui.Text($"Camera position: {eyesPos.X:F2}, {eyesPos.Y:F2}, {eyesPos.Z:F2}");

                lookAt = LookAt(new SharpDX.Vector3(eyesPos.X, eyesPos.Y, eyesPos.Z));
                ImGui.Text($"\"Fix-on\" position: {lookAt.X:F2}, {lookAt.Y:F2}, {lookAt.Z:F2}");

                ImGui.Text("Title camera FOV (in-game FoV will not change)");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(GuiScale(128f));
                ImGui.SliderFloat("##customTsFovY", ref _fovY, 0.01f, 3f, "%.2f");
                ImGui.SameLine();
                if (ImGui.Button("Reset##resetFovY"))
                    _fovY = 1f;
                
                // Weather
                var newType = _pluginInterface?.ClientState?.TerritoryType;
                if (newType.HasValue && newType.Value != _lastTerritoryId)
                {
                    _lastTerritoryId = newType.Value;
                    UpdateWeathers(newType.Value);
                }
                
                byte currentWeather = _titleEdit.GetWeather();
                ImGui.Text($"Current weather: {GetWeatherDescriptor(currentWeather)}");
                ImGui.SameLine();
                ImGui.SetCursorPosX(GuiScale(383f));
                if (ImGui.Button("Set Current##weather"))
                {
                    _inputIntWeatherId = currentWeather;
                    _weatherId = currentWeather;
                    for (int i = 0; i < _territoryWeathers.Length; i++)
                    {
                        if (_territoryWeathers[i] != _weatherId) continue;
                        _selectedWeatherIndex = i;
                        break;
                    }
                }
                ImGui.Text("Title zone weather:");
                ImGui.SameLine();
#if !DEBUG
                if (_territoryWeathers == null || _territoryWeathers.Length == 0)
#endif
                {
                    ImGui.SetNextItemWidth(GuiScale(90));
                    if (ImGui.InputInt("##customTSweather", ref _inputIntWeatherId))
                        _weatherId = _inputIntWeatherId;
                }
#if !DEBUG
                else
                {
                    ImGui.SetNextItemWidth(GuiScale(332f));
                    if (ImGui.BeginCombo("##customTSweatherCombo", GetWeatherDescriptor(_territoryWeathers[_selectedWeatherIndex])))
                    {
                        for (int i = 0; i < _territoryWeathers.Length; i++)
                        {
                            if (ImGui.Selectable(GetWeatherDescriptor(_territoryWeathers[i])))
                            {
                                _selectedWeatherIndex = i;
                                _weatherId = _territoryWeathers[_selectedWeatherIndex];
                            }
                        }
                        ImGui.EndCombo();
                    }
                }
#endif

                if (_weatherId < 1 || _weatherId > 255 || !_weathers.ContainsKey((uint) _inputIntWeatherId))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "(invalid)");
                    stateInvalid = true;
                }
                else if (_territoryWeathers == null || _territoryWeathers.Length == 0)
                {
                    ImGui.SameLine();
                    ImGui.Text(GetWeatherDescriptor((ushort) _weatherId));
                }

                // Music
                ushort currentBgmId = _titleEdit.GetSong();
                var currentBgm = _bgmSheet.GetBgmInfo(currentBgmId);
                if (_lastBgmId != currentBgmId)
                {
                    _lastBgmId = currentBgmId;
                    UpdateBgmDescription(currentBgmId, currentBgm);
                }
                ImGui.Text("Current song:");
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1));
                ImGui.Text(_songDescription);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    DrawBgmTooltip(currentBgm);
                ImGui.SameLine();
                ImGui.SetCursorPosX(GuiScale(383f));
                if (ImGui.Button("Set Current##music"))
                    _selectedBgmId = currentBgmId;
                ImGui.Text("Title zone music:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(GuiScale(150f));
                ImGui.InputInt("##customTSmusic", ref _selectedBgmId);
                selectedBgm = _bgmSheet.GetBgmInfo((ushort) _selectedBgmId);
                if (ImGui.IsItemHovered())
                    DrawBgmTooltip(selectedBgm);
                ImGui.SameLine();
                if (_selectedBgmId < 1 || currentBgm.Title == "Invalid")
                {
                    ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "(invalid)");
                    stateInvalid = true;
                }
                else
                {
                    ImGui.TextWrapped($"{selectedBgm.Title}");
                }

                // Time
                long etS = Marshal.ReadInt64(_pluginInterface.Framework.Address.BaseAddress + 0x1608);
                var et = DateTimeOffset.FromUnixTimeSeconds(etS);
                ImGui.Text($"Current time: {et.Hour:D2}:{et.Minute:D2}");
                ImGui.SameLine();
                ImGui.SetCursorPosX(GuiScale(383f));
                if (ImGui.Button("Set Current##time"))
                {
                    _tsTimeHrs = et.Hour;
                    _tsTimeMin = et.Minute;
                    var scaled = _tsTimeMin / 60f * 100;
                    _tsTimeOffset = (int) (_tsTimeHrs * 100 + scaled % 100);
                }
                ImGui.Text("Time:");
                ImGui.SameLine();
                float timeWidth = GuiScale(50f);
                ImGui.SetNextItemWidth(timeWidth);
                if (ImGui.InputInt("hrs", ref _tsTimeHrs, 0, 23))
                {
                    var scaled = _tsTimeMin / 60f * 100;
                    _tsTimeOffset = (int) (_tsTimeHrs * 100 + scaled % 100);
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(timeWidth);
                if (ImGui.InputInt("mins", ref _tsTimeMin, 0, 59))
                {
                    var scaled = _tsTimeMin / 60f * 100;
                    _tsTimeOffset = (int) (_tsTimeHrs * 100 + scaled % 100);
                }
            }

#if DEBUG
            ImGui.Text($"_stateInvalid: {stateInvalid}");
            ImGui.Text($"_nameEmpty: {_nameEmpty}");
            ImGui.Text($"_fileWasCreatedRecently: {_fileWasCreatedRecently}");
            ImGui.Text($"_nameAlreadyExists: {_nameAlreadyExists}");
            ImGui.Text($"_nameContainsInvalidCharacters: {_nameContainsInvalidCharacters}");
#endif
            ImGui.EndChild();

            if (_fileWasCreatedRecently)
            {
                ImGui.TextColored(new Vector4(0f, 1f, 0f, 1f), "Title screen created!");
                ImGui.EndTabItem();
                return;
            }

            if (_nameEmpty)
                ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "The title screen name is empty.");

            if (_nameAlreadyExists)
                ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "A custom title screen already exists by this name.");
            else if (_nameContainsInvalidCharacters)
                ImGui.TextColored(new Vector4(1f, 0f, 0f, 1f), "Title must be a valid filename without extension.");

            if (stateInvalid)
            {
                ImGui.EndTabItem();
                return;
            }

            if (ImGui.Button("Generate"))
            {
                TitleEditScreen scr = new TitleEditScreen();
                scr.Name = _customTsName;
                scr.Logo = _titleLogosCreate[_selectedLogoIndexCreate];
                scr.DisplayLogo = _selectedLogoVisibleCreate;
#if DEBUG
                scr.TerritoryPath = _terriPath;
#else
                scr.TerritoryPath = _territoryPaths[_pluginInterface.ClientState.TerritoryType].Bg.ToString();
#endif
                scr.CameraPos = eyesPos;
                scr.FixOnPos = lookAt;
                scr.FovY = _fovY;
                scr.WeatherId = (byte) _weatherId;
                scr.TimeOffset = (ushort) _tsTimeOffset;
                scr.BgmPath = selectedBgm.FilePath;
                var text = JsonConvert.SerializeObject(scr, Formatting.Indented);
                bool createSuccess = false;
                try
                {
                    File.WriteAllText(_titleScreenSavePath, text);
                    EnumerateTitleScreenFiles();
                    createSuccess = true;
                }
                catch (Exception e)
                {
                    PluginLog.LogError(e, "Error occurred saving title screen.");
                }

                if (createSuccess)
                {
                    _nameAlreadyExists = true;
                    _fileWasCreatedRecently = true;
                    Task.Delay(2000).ContinueWith(_ => _fileWasCreatedRecently = false);
                }
            }

            ImGui.EndTabItem();
        }

        private void DrawBgmTooltip(BgmInfo bgm)
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(GuiScale(400f));
            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Song Info");
            ImGui.TextWrapped($"Title: {bgm.Title}");
            ImGui.TextWrapped(string.IsNullOrEmpty(bgm.Location) ? "Location: Unknown" : $"Location: {bgm.Location}");
            if (!string.IsNullOrEmpty(bgm.AdditionalInfo))
                ImGui.TextWrapped($"Info: {bgm.AdditionalInfo}");
            ImGui.Text($"File path: {bgm.FilePath}");
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        private void DrawManage()
        {
            if (!ImGui.BeginTabItem("Manage Screens"))
                return;

            ImGui.Text("This tab allows you to manage installed title screen presets.");
            ImGui.Separator();
            ImGui.BeginChild("scrolling", new Vector2(0, 0), false);

            if (ImGui.CollapsingHeader("Export##titleEditExport"))
            {
                if (ImGui.Combo("##exportComboBox", ref _selectedTitleIndexExport, _titleScreensExport, _titleScreensExport.Length))
                {
                    _exportRef = "";
                    string fileName = Path.Combine(_titleScreenFolder, _titleScreensExport[_selectedTitleIndexExport] + ".json");
                    PluginLog.Log($"Exporting {fileName}");
                    if (File.Exists(fileName))
                    {
                        string text = File.ReadAllText(fileName);
                        var bytes = Encoding.UTF8.GetBytes(text);
                        string base64 = "TE2" + Convert.ToBase64String(bytes);
                        _exportRef = base64;
                    }
                }

                ImGui.SameLine();
                if (ImGui.Button("Copy"))
                    ImGui.SetClipboardText(_exportRef);
                ImGui.BeginChild("##exportthingydo", new Vector2(0, GuiScale(150)), true);
                ImGui.TextWrapped(_exportRef);
                ImGui.EndChild();
            }

            if (ImGui.CollapsingHeader("Import##titleEditImport"))
            {
                ImGui.Text("Paste import text here:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(GuiScale(254));
                ImGui.InputText("##importextThingydo", ref _importRef, 2048);
                ImGui.SameLine();
                TitleEditScreen screen = null;
                if (ImGui.Button("Import"))
                {
                    if (!_importRef.StartsWith("TE2"))
                    {
                        _importParsed = false;
                        Task.Delay(2000).ContinueWith(_ => _importParsed = true);
                    }
                    else
                    {
                        try
                        {
                            string toImport = Encoding.UTF8.GetString(Convert.FromBase64String(_importRef.Substring(3).Trim()));
                            screen = JsonConvert.DeserializeObject<TitleEditScreen>(toImport);
                            string fileName = Path.Combine(_titleScreenFolder, screen.Name + ".json");
                            PluginLog.Log($"Importing {fileName}");
                            if (!File.Exists(fileName))
                            {
                                File.WriteAllText(fileName, toImport);
                                _importName = screen.Name;
                                EnumerateTitleScreenFiles();
                                Task.Delay(2000).ContinueWith(_ => _importName = "");
                            }
                            else
                                _importExistsScreen = screen;
                        }
                        catch (Exception e)
                        {
                            if (screen == null)
                            {
                                PluginLog.Error(e, $"Failed to parse input text!");
                                _importParsed = false;
                                Task.Delay(2000).ContinueWith(_ => _importParsed = true);
                            }
                        }
                    }
                }

                if (!_importParsed)
                {
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), $"Could not parse input.");
                }
                else if (_importExistsScreen != null)
                {
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), $"A title screen already exists by the name {_importExistsScreen.Name}.");
                    ImGui.Text("Would you like to save this by a different name?");
                    ImGui.SameLine();
                    if (ImGui.Button("Yes##YesImportThisFilePleaseI'mBeggingYou"))
                    {
                        int i = 1;
                        while (_titleScreens.Contains(_importExistsScreen.Name + $" ({i})"))
                            i++;
                        _importExistsScreen.Name += $" ({i})";
                        var toImport = JsonConvert.SerializeObject(_importExistsScreen, Formatting.Indented);
                        var fileName = Path.Combine(_titleScreenFolder, _importExistsScreen.Name + ".json");
                        try
                        {
                            File.WriteAllText(fileName, toImport);
                            _importName = _importExistsScreen.Name;
                            EnumerateTitleScreenFiles();
                            Task.Delay(2000).ContinueWith(_ => _importName = "");
                        }
                        catch (Exception e)
                        {
                            PluginLog.Error(e, $"Failed to save {_importExistsScreen.Name} to {fileName}");
                            _importError = _importExistsScreen.Name;
                            Task.Delay(2000).ContinueWith(_ => _importError = "");
                        }

                        _importExistsScreen = null;
                        _importRef = "";
                    }
                }

                if (!string.IsNullOrEmpty(_importName))
                    ImGui.TextColored(new Vector4(0, 1, 0, 1), $"Successfully imported {_importName}");
                else if (!string.IsNullOrEmpty(_importName))
                    ImGui.TextColored(new Vector4(1, 0, 0, 1), $"Failed to import {_importError}. Please check the log!");
            }

            if (ImGui.CollapsingHeader("Installed Screens"))
            {
                var width = GuiScale(_widestScreenName + 100);
                ImGui.BeginChild("scrollingCustomList", new Vector2(width, GuiScale(150)), true, ImGuiWindowFlags.HorizontalScrollbar);
                foreach (var titleScreen in _titleScreensExport)
                {
                    ImGui.Text(titleScreen);
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(GuiScale(_widestScreenName + 15f));
                    if (ImGui.Button($"Delete##{titleScreen}"))
                    {
                        try
                        {
                            File.Delete(GetTitlePath(titleScreen));
                            if (_customTsName == titleScreen)
                                _nameAlreadyExists = false;
                            EnumerateTitleScreenFiles();
                        }
                        catch (Exception e)
                        {
                            PluginLog.Error(e, $"Could not delete title file for {titleScreen} from {GetTitlePath(titleScreen)}");
                        }
                    }
                }
                ImGui.EndChild();
                if (ImGui.Button("Open presets folder"))
                    Process.Start(_titleScreenFolder);
            }

            ImGui.EndChild();
            ImGui.EndTabItem();
        }

        private string GetTitlePath(string name)
        {
            return Path.Combine(_titleScreenFolder, name + ".json");
        }

        private Vector3 LookAt(SharpDX.Vector3 playerPos)
        {
            var viewMatrix = new Matrix();
            
            var renderCam = TitleEditAddressResolver.RenderCamera;
            if (renderCam != IntPtr.Zero)
            {
                unsafe
                {
                    var rawMatrix = (float*) (renderCam + LookAtOffset).ToPointer();
                    for (var i = 0; i < 16; i++, rawMatrix++)
                        viewMatrix[i] = *rawMatrix;
                }
            }

            var result = playerPos + viewMatrix.Left * 10f;
            return new Vector3(result.X, result.Y, result.Z);
        }

        private Vector3 EyesPos(Vector3 playerPos)
        {
            var ret = playerPos;

            var renderCam = TitleEditAddressResolver.RenderCamera;
            if (renderCam != IntPtr.Zero)
            {
                unsafe
                {
                    var rawVector = (float*) (renderCam + EyesPosOffset).ToPointer();
                    ret.X = rawVector[0];
                    ret.Y = rawVector[1];
                    ret.Z = rawVector[2];
                }    
            }
            
            return ret;
        }

        private void UpdateWeathers(ushort id)
        {
            _territoryWeathers = new ushort[0];
            var weathers = new List<ushort>();
            if (!_territoryPaths.TryGetValue(id, out var path)) return;
            try
            {
                var file = _pluginInterface.Data.GetFile<LvbFile>($"bg/{path.Bg}.lvb");
                if (file?.weatherIds == null || file.weatherIds.Length == 0)
                    return;
                foreach (var weather in file.weatherIds)
                    if (weather > 0 && weather < 255)
                        weathers.Add(weather);
                weathers.Sort();
                _territoryWeathers = weathers.ToArray();
                if (_territoryWeathers.Length > 0)
                {
                    _selectedWeatherIndex = 0;
                    _weatherId = _territoryWeathers[0];    
                }
            }
            catch (Exception e)
            {
                PluginLog.Error(e, $"Failed to load lvb for {path}");
            }
        }

        private void UpdateBgmDescription(ushort currentBgmId, BgmInfo currentBgm)
        {
            string songDescription = $"({currentBgmId}) {currentBgm.Title}";
            string truncateFormat = "{0} ... (?)";
            if (ImGui.CalcTextSize(songDescription).X < 280f)
                _songDescription = songDescription;
            else
            {
                string tmpDesc = "";
                int substring = songDescription.Length;
                do
                {
                    tmpDesc = string.Format(truncateFormat, songDescription.Substring(0, substring--));
                } while (ImGui.CalcTextSize(tmpDesc).X > 280f);
                _songDescription = tmpDesc;
            }
        }

        private string GetWeatherDescriptor(ushort weather)
        {
            return _weathers.TryGetValue(weather, out string weatherName) ? $"({weather}) {weatherName}" : $"({weather})";
        }

        private void DrawSettings()
        {
            if (!ImGui.BeginTabItem("Settings"))
                return;

            bool canSave = true;
            ImGui.Text("This window allows you to change what title screen plays when you start the game.");
            ImGui.BeginChild("scrolling", new Vector2(0, GuiScale(341)), true);
            
            ImGui.Combo("Title screen file to use", ref _selectedTitleIndex, _titleScreens, _titleScreens.Length);
            if (_titleScreens[_selectedTitleIndex] == "Random (custom)" && _titleScreens.Length > 2)
            {
                ImGui.BeginChild("scrollingCustomList", new Vector2(GuiScale(200), GuiScale(150)), true, ImGuiWindowFlags.HorizontalScrollbar);
                foreach (var titleScreen in _titleScreens)
                {
                    if (titleScreen == "Random" || titleScreen == "Random (custom)")
                        continue;
                    bool contained = _configuration.TitleList.Contains(titleScreen);
                    if (ImGui.Checkbox($"{titleScreen}", ref contained))
                    {
                        if (contained)
                            _configuration.TitleList.Add(titleScreen);
                        else
                            _configuration.TitleList.Remove(titleScreen);
                    }
                }

                ImGui.EndChild();
                if (_configuration.TitleList.Count < 2)
                    canSave = false;
            }
            ImGui.Combo("Title screen logo to use", ref _selectedLogoIndex, _titleLogos, _titleLogos.Length);

            if (ImGui.BeginCombo("Title screen logo override", 
                GetOverrideSettingString(_configuration.Override)))
            {
                if (ImGui.Selectable(GetOverrideSettingString(OverrideSetting.Override)))
                    _configuration.Override = OverrideSetting.Override;
                if (ImGui.Selectable(GetOverrideSettingString(OverrideSetting.UseIfLogoUnspecified)))
                    _configuration.Override = OverrideSetting.UseIfLogoUnspecified;
                ImGui.EndCombo();
            }

            bool shouldDisplayLogo = _configuration.DisplayTitleLogo;
            if (ImGui.Checkbox("Display title screen logo", ref shouldDisplayLogo))
            {
                if (shouldDisplayLogo)
                    _titleEdit.EnableTitleLogo();
                else
                    _titleEdit.DisableTitleLogo(1001);
                _configuration.DisplayTitleLogo = shouldDisplayLogo;
            }

            bool debugLogging = _configuration.DebugLogging;
            if (ImGui.Checkbox("Enable debug logging", ref debugLogging))
                _configuration.DebugLogging = debugLogging;

            ImGui.EndChild();

            if (!canSave)
                ImGui.TextColored(new Vector4(1, 0, 0, 1), "Please select at least two title screens to shuffle between.");
            else
            {
                if (ImGui.Button("Save"))
                {
                    UpdateConfig();
                    _configuration.Save();
                }

                ImGui.SameLine();
                if (ImGui.Button("Save and Close"))
                {
                    UpdateConfig();
                    _configuration.Save();
                    _isImguiTitleEditOpen = false;
                }
            }
            ImGui.EndTabItem();
        }

        private void DrawInformation()
        {
            if (!ImGui.BeginTabItem("Information"))
                return;

            ImGui.BeginChild("scrolling", new Vector2(0, 0), false);

            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Intro");
            ImGui.TextWrapped("This plugin allows you to create and set custom title screens.");
            ImGui.TextWrapped("To create a title screen, open the Create tab, head to your favorite location, " +
                              "and point your camera in the direction you'd like.");
            ImGui.TextWrapped("Give it a name, set the weather, the BGM, and the time for the title screen," +
                              " then click Generate. You can then head over to the Settings tab and use the " +
                              "title screen you defined as your new title screen upon opening the game.");

            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Tips and tricks");
            ImGui.TextWrapped("Selecting Random as your title screen shuffles all installed screens. " +
                              "Selecting Random (custom) allows you to shuffle between a set of screens.");
            ImGui.TextWrapped("Pressing Ctrl + T on the title screen will show the TitleEdit menu.");
            ImGui.TextWrapped("TitleEdit presets are stored in the pluginConfigs/TitleEdit folder! " +
                              "If you are changing a file that is not there, it does not exist to TitleEdit!");
            ImGui.TextWrapped("TitleEdit reserves the TE_ suffix on new titles to avoid overwriting player presets.");
            ImGui.TextWrapped("Accessing the lobby, then going back to the title screen will re-load " +
                              "the title screen preset file, allowing for fast iteration on presets.");

            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Known issues");
            ImGui.TextWrapped("- The sun and moon do not exist on title screens.");
            ImGui.TextWrapped("");

            ImGui.EndChild();
            ImGui.EndTabItem();
        }

        private void DrawCredits()
        {
            if (!ImGui.BeginTabItem("Credits"))
                return;
            ImGui.Text("attick - Title Edit 1.0 and many functions of 2.0");
            ImGui.Text("perchbird - Custom title screens and supporting features");
            ImGui.Text("ff-meli - BGM now playing code");
            ImGui.Text("goat - being a caprine individual");
            ImGui.EndTabItem();
        }
        
        private string GetPluginConfigFolder()
        {
            var dalamudAssembly = Assembly.GetAssembly(typeof(DalamudPluginInterface)).Location;
            var parent = Path.GetDirectoryName(dalamudAssembly);
            parent = Path.GetDirectoryName(parent);
            parent = Path.GetDirectoryName(parent);
            var configFolder = Path.Combine(parent, "pluginConfigs");
            return configFolder;
        }

        private void UpdateConfig()
        {
            _configuration.SelectedTitleFileName = _titleScreens[_selectedTitleIndex];
            _configuration.SelectedLogoName = _titleLogos[_selectedLogoIndex];
            // bool isn't here because it's loaded live from config
        }

        private string GetOverrideSettingString(OverrideSetting setting)
        {
            return setting switch
            {
                OverrideSetting.Override => "Override logo from preset",
                OverrideSetting.UseIfLogoUnspecified => "Only override if unspecified by preset",
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        private int GetIndexOfSelectedTitle()
        {
            for (int i = 0; i < _titleScreens.Length; i++)
                if (_titleScreens[i] == _configuration.SelectedTitleFileName)
                    return i;
            return 0;
        }

        private int GetIndexOfSelectedLogo()
        {
            for (int i = 0; i < _titleLogos.Length; i++)
                if (_titleLogos[i] == _configuration.SelectedLogoName)
                    return i;
            return 0;
        }

        public void Dispose()
        {
            _titleEdit.Dispose();
            _pluginInterface.CommandManager.RemoveHandler("/ptitle");
            _pluginInterface.Dispose();
        }
    }
}