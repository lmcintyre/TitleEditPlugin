using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud;
using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.Gui;
using Dalamud.Hooking;
using Dalamud.Interface.Internal.Notifications;
using Dalamud.Logging;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json;

namespace TitleEdit
{
    public class TitleEdit
    {
        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate int OnCreateScene(string p1, uint p2, IntPtr p3, uint p4, IntPtr p5, int p6, uint p7);

        private delegate IntPtr OnFixOn(IntPtr self,
            [MarshalAs(UnmanagedType.LPArray, SizeConst = 3)]
            float[] cameraPos,
            [MarshalAs(UnmanagedType.LPArray, SizeConst = 3)]
            float[] focusPos, float fovY);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate ulong OnLoadLogoResource(IntPtr p1, string p2, int p3, int p4);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr OnPlayMusic(IntPtr self, string filename, float volume, uint fadeTime);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate void OnLoadTitleScreenAssets(IntPtr p1, IntPtr p2, IntPtr p3);

        private delegate void SetTimePrototype(ushort timeOffset);

        // The size of the BGMControl object
        private const int ControlSize = 88;

        private readonly ClientState _clientState;
        private readonly GameGui _gameGui;
        private readonly DataManager _data;
        private readonly DalamudPluginInterface _pi;
        private readonly TitleEditConfiguration _configuration;

        private readonly Hook<OnCreateScene> _createSceneHook;
        private readonly Hook<OnPlayMusic> _playMusicHook;
        private readonly Hook<OnFixOn> _fixOnHook;
        private readonly Hook<OnLoadLogoResource> _loadLogoResourceHook;
        private readonly Hook<OnLoadTitleScreenAssets> _loadTitleScreenAssetsHook;

        private readonly SetTimePrototype _setTime;

        private readonly string _titleScreenBasePath;
        private bool _titleCameraNeedsSet;
        private bool _amForcingTime;
        private bool _amForcingWeather;

        private TitleEditScreen _currentScreen;

        // Hardcoded fallback info now that jank is resolved
        private static TitleEditScreen ARealmReborn => new()
        {
            Name = "A Realm Reborn",
            TerritoryPath = "ffxiv/zon_z1/chr/z1c1/level/z1c1",
            Logo = "A Realm Reborn",
            DisplayLogo = true,
            CameraPos = new Vector3(0, 0.5f, -1.3f),
            FixOnPos = new Vector3(0, 1.0f, 0),
            FovY = 45f,
            WeatherId = 2,
            BgmPath = "music/ffxiv/BGM_System_Title.scd",
            VersionTextColor = new Vector4(0.0f, 153.0f, 255.0f, 255.0f),
            CopyrightTextColor = new Vector4(0.0f, 153.0f, 255.0f, 255.0f),
            ButtonTextColor = new Vector4(0.0f, 153.0f, 255.0f, 255.0f),
            ButtonHighlightColor = new Vector4(0.0f, 127.0f, 219.0f, 255.0f)
        };

        public TitleEdit(
            SigScanner scanner,
            ClientState clientState,
            GameGui gameGui,
            DataManager data,
            DalamudPluginInterface pi,
            TitleEditConfiguration configuration,
            string screenDir)
        {
            PluginLog.Log("TitleEdit hook init");
            _clientState = clientState;
            _gameGui = gameGui;
            _data = data;
            _pi = pi;
            _configuration = configuration;

            TitleEditAddressResolver.Setup64Bit(scanner);
            FFXIVClientStructs.Resolver.Initialize();

            _titleScreenBasePath = screenDir;

            _createSceneHook = new Hook<OnCreateScene>(TitleEditAddressResolver.CreateScene, HandleCreateScene);
            _playMusicHook = new Hook<OnPlayMusic>(TitleEditAddressResolver.PlayMusic, HandlePlayMusic);
            _fixOnHook = new Hook<OnFixOn>(TitleEditAddressResolver.FixOn, HandleFixOn);
            _loadLogoResourceHook =
                new Hook<OnLoadLogoResource>(TitleEditAddressResolver.LoadLogoResource, HandleLoadLogoResource);
            _loadTitleScreenAssetsHook 
                = new Hook<OnLoadTitleScreenAssets>(TitleEditAddressResolver.LoadTitleScreenAssets, HandleLoadTitleScreenAssets);

            _setTime = Marshal.GetDelegateForFunctionPointer<SetTimePrototype>(TitleEditAddressResolver.SetTime);
            PluginLog.Log("TitleEdit hook init finished");
        }

        internal void RefreshCurrentTitleEditScreen()
        {
            var files = Directory.GetFiles(_titleScreenBasePath).Where(file => file.EndsWith(".json")).ToArray();
            var toLoad = _configuration.SelectedTitleFileName;

            if (_configuration.SelectedTitleFileName == "Random")
            {
                int index = new Random().Next(0, files.Length);
                // This is a list of files - not a list of title screens
                toLoad = Path.GetFileNameWithoutExtension(files[index]);
            }
            else if (_configuration.SelectedTitleFileName == "Random (custom)")
            {
                if (_configuration.TitleList.Count != 0)
                {
                    int index = new Random().Next(0, _configuration.TitleList.Count);
                    toLoad = _configuration.TitleList[index];
                }
                else
                {
                    // The custom title list was somehow empty
                    toLoad = "Endwalker";
                }
            }

            var path = Path.Combine(_titleScreenBasePath, toLoad + ".json");
            if (!File.Exists(path))
            {
                PluginLog.Log(
                    $"Title Edit tried to find {path}, but no title file was found, so title settings have been reset.");
                Fail();
                return;
            }

            var contents = File.ReadAllText(path);
            _currentScreen = JsonConvert.DeserializeObject<TitleEditScreen>(contents);

            if (!IsScreenValid(_currentScreen))
            {
                PluginLog.Log($"Title Edit tried to load {_currentScreen.Name}, but the necessary files are missing, so title settings have been reset.");
                Fail();
                return;
            }

            Log($"Title Edit loaded {path}");
            
            if (_configuration.DisplayTitleToast)
                _pi.UiBuilder.AddNotification($"Now displaying: {_currentScreen.Name}", "Title Edit", NotificationType.Info);
        }

        private bool IsScreenValid(TitleEditScreen screen)
        {
            return _data.FileExists($"bg/{screen.TerritoryPath}.lvb") &&
                   _data.FileExists(screen.BgmPath);
        }

        private void Fail()
        {
            _configuration.TitleList = new List<string>();
            _configuration.DisplayTitleLogo = true;
            _configuration.SelectedTitleFileName = "A Realm Reborn";
            _configuration.SelectedLogoName = "A Realm Reborn";
            _configuration.Save();
            _currentScreen = ARealmReborn;
        }

        private int HandleCreateScene(string p1, uint p2, IntPtr p3, uint p4, IntPtr p5, int p6, uint p7)
        {
            Log($"HandleCreateScene {p1} {p2} {p3.ToInt64():X} {p4} {p5.ToInt64():X} {p6} {p7}");
            _titleCameraNeedsSet = false;
            _amForcingTime = false;
            _amForcingWeather = false;

            if (IsLobby(p1))
            {
                Log("Loading lobby and lobby fixon.");
                var returnVal = _createSceneHook.Original(p1, p2, p3, p4, p5, p6, p7);
                FixOn(new Vector3(0, 0, 0), new Vector3(0, 0.8580103f, 0), 1);
                return returnVal;
            }

            if (IsTitleScreen(p1))
            {
                Log("Loading custom title.");
                RefreshCurrentTitleEditScreen();
                p1 = _currentScreen.TerritoryPath;
                Log($"Title zone: {p1}");
                var returnVal = _createSceneHook.Original(p1, p2, p3, p4, p5, p6, p7);
                _titleCameraNeedsSet = true;
                ForceWeather(_currentScreen.WeatherId, 5000);
                ForceTime(_currentScreen.TimeOffset, 5000);
                // SetRevisionStringVisibility(_configuration.DisplayVersionText);
                return returnVal;
            }

            return _createSceneHook.Original(p1, p2, p3, p4, p5, p6, p7);
        }

        private IntPtr HandlePlayMusic(IntPtr self, string filename, float volume, uint fadeTime)
        {
            Log($"HandlePlayMusic {self.ToInt64():X} {filename} {volume} {fadeTime}");
            if (filename.EndsWith("_System_Title.scd") && _currentScreen != null)
                filename = _currentScreen.BgmPath;
            return _playMusicHook.Original(self, filename, volume, fadeTime);
        }

        private IntPtr HandleFixOn(IntPtr self,
            [MarshalAs(UnmanagedType.LPArray, SizeConst = 3)]
            float[] cameraPos,
            [MarshalAs(UnmanagedType.LPArray, SizeConst = 3)]
            float[] focusPos,
            float fovY)
        {
            Log(
                $"HandleFixOn {self.ToInt64():X} | {cameraPos[0]} {cameraPos[1]} {cameraPos[2]} | {focusPos[0]} {focusPos[1]} {focusPos[2]} | {fovY} | {_titleCameraNeedsSet}");
            if (!_titleCameraNeedsSet || _currentScreen == null)
                return _fixOnHook.Original(self, cameraPos, focusPos, fovY);
            Log(
                $"HandleFixOn Result {self.ToInt64():X} | {_currentScreen.CameraPos.X} {_currentScreen.CameraPos.Y} {_currentScreen.CameraPos.Z} | {_currentScreen.FixOnPos.X} {_currentScreen.FixOnPos.Y} {_currentScreen.FixOnPos.Z} | {_currentScreen.FovY}");
            _titleCameraNeedsSet = false;
            return _fixOnHook.Original(self,
                FloatArrayFromVector3(_currentScreen.CameraPos),
                FloatArrayFromVector3(_currentScreen.FixOnPos),
                _currentScreen.FovY);
            // return _fixOnHook.Original(self, cameraPos, focusPos, fovY);
        }

        public TitleEditScreen FixOnCurrent()
        {
            Log("Requested FixOnCurrent");
            if (_currentScreen == null)
                RefreshCurrentTitleEditScreen();
            FixOn(_currentScreen.CameraPos, _currentScreen.FixOnPos, _currentScreen.FovY);
            if (TitleEditAddressResolver.LobbyCamera != IntPtr.Zero)
                _fixOnHook.Original(TitleEditAddressResolver.LobbyCamera,
                    FloatArrayFromVector3(_currentScreen.CameraPos),
                    FloatArrayFromVector3(_currentScreen.FixOnPos),
                    _currentScreen.FovY);
            return _currentScreen;
        }

        public void FixOn(Vector3 cameraPos, Vector3 focusPos, float fov)
        {
            Log($"Fixing on {focusPos.X}, {focusPos.Y}, {focusPos.Z} " +
                $"from {cameraPos.X}, {cameraPos.Y}, {cameraPos.Z} " +
                $"with FOV {fov}");
            if (TitleEditAddressResolver.LobbyCamera != IntPtr.Zero)
                _fixOnHook.Original(TitleEditAddressResolver.LobbyCamera,
                    FloatArrayFromVector3(cameraPos),
                    FloatArrayFromVector3(focusPos),
                    fov);
        }

        private ulong HandleLoadLogoResource(IntPtr p1, string p2, int p3, int p4)
        {
            if (!p2.Contains("Title_Logo") || _currentScreen == null)
                return _loadLogoResourceHook.Original(p1, p2, p3, p4);
            Log($"HandleLoadLogoResource {p1.ToInt64():X} {p2} {p3} {p4}");
            ulong result;

            var logo = _configuration.SelectedLogoName;
            var display = _configuration.DisplayTitleLogo;
            var over = _configuration.Override;
            var visOver = _configuration.VisibilityOverride;
            if (over == OverrideSetting.UseIfUnspecified && _currentScreen.Logo != "Unspecified")
                logo = _currentScreen.Logo;

            if (visOver == OverrideSetting.UseIfUnspecified && _currentScreen.Logo != "Unspecified")
                display = _currentScreen.DisplayLogo;

            switch (logo)
            {
                case "A Realm Reborn":
                    result = _loadLogoResourceHook.Original(p1, "Title_Logo", p3, p4);
                    break;
                case "FFXIV Online":
                    result = _loadLogoResourceHook.Original(p1, "Title_LogoOnline", p3, p4);
                    break;
                case "FFXIV Free Trial":
                    result = _loadLogoResourceHook.Original(p1, "Title_LogoFT", p3, p4);
                    break;
                case "Heavensward":
                    result = _loadLogoResourceHook.Original(p1, "Title_Logo300", p3, p4);
                    break;
                case "Stormblood":
                    result = _loadLogoResourceHook.Original(p1, "Title_Logo400", p3, p4);
                    break;
                case "Shadowbringers":
                    result = _loadLogoResourceHook.Original(p1, "Title_Logo500", p3, p4);
                    break;
                case "Endwalker":
                    result = _loadLogoResourceHook.Original(p1, "Title_Logo600", p3, p4);
                    break;
                default:
                    result = _loadLogoResourceHook.Original(p1, "Title_Logo600", p3, p4);
                    break;
            }

            // Somehow this works without this, the animation doesn't unhide it?
            // No idea
            // var delay = 2001;
            // if (logo == "Endwalker")
            //     delay = 10600;
            if (!display)
                DisableTitleLogo();
            return result;
        }

        private void HandleLoadTitleScreenAssets(IntPtr p1, IntPtr p2, IntPtr p3)
        {
            Log("LoadTitleScreenAssets Call");
            _loadTitleScreenAssetsHook.Original(p1, p2, p3);

            //Find a better hook? idk  
            Task.Run(() =>
            {
                for(int i = 0; i < 5; i++)
                {
                    Thread.Sleep(50);
                    SetTextColors();
                }               
            });      
        }

        public void Enable()
        {
            _loadTitleScreenAssetsHook.Enable();
            _loadLogoResourceHook.Enable();
            _createSceneHook.Enable();
            _playMusicHook.Enable();
            _fixOnHook.Enable();
        }

        public void Dispose()
        {
            _loadTitleScreenAssetsHook.Dispose();
            _loadLogoResourceHook.Dispose();
            _createSceneHook.Dispose();
            _playMusicHook.Dispose();
            _fixOnHook.Dispose();
        }

        private void ForceTime(ushort timeOffset, int forceTime)
        {
            _amForcingTime = true;
            Task.Run(() =>
            {
                try
                {
                    Stopwatch stop = Stopwatch.StartNew();
                    do
                    {
                        if (TitleEditAddressResolver.SetTime != IntPtr.Zero)
                            _setTime(timeOffset);
                        Thread.Sleep(50);
                    } while (stop.ElapsedMilliseconds < forceTime && _amForcingTime);

                    Log($"Done forcing time.");
                }
                catch (Exception e)
                {
                    PluginLog.Error(e, "An error occurred when forcing time.");
                }
            });
        }

        private void ForceWeather(byte weather, int forceTime)
        {
            _amForcingWeather = true;
            Task.Run(() =>
            {
                try
                {
                    Stopwatch stop = Stopwatch.StartNew();
                    do
                    {
                        SetWeather(weather);
                        Thread.Sleep(20);
                    } while (stop.ElapsedMilliseconds < forceTime && _amForcingWeather);

                    Log($"Done forcing weather.");
                    Log($"Weather is now {GetWeather()}");
                }
                catch (Exception e)
                {
                    PluginLog.Error(e, "An error occurred when forcing weather.");
                }
            });
        }

        public byte GetWeather()
        {
            byte weather = 2;
            unsafe
            {
                if (TitleEditAddressResolver.WeatherPtr != IntPtr.Zero)
                    weather = *(byte*)TitleEditAddressResolver.WeatherPtr;
            }

            return weather;
        }

        public void SetWeather(byte weather)
        {
            unsafe
            {
                if (TitleEditAddressResolver.WeatherPtr != IntPtr.Zero)
                    *(byte*)TitleEditAddressResolver.WeatherPtr = weather;
            }
        }

        // TODO: Eventually figure out how to do these without excluding free trial players
        private bool IsTitleScreen(string path)
        {
            var ret = 
                (path == "ex4/05_zon_z5/chr/z5c1/level/z5c1"
                 || path == "ex3/05_zon_z4/chr/z4c1/level/z4c1"
                 || path == "ex2/05_zon_z3/chr/z3c1/level/z3c1"
                 || path == "ex1/05_zon_z2/chr/z2c1/level/z2c1"
                 || path == "ffxiv/zon_z1/chr/z1c1/level/z1c1")
                && !IsLobby(path)
                && !IsCharaMake(path);
            Log($"IsTitleScreen: {ret}");
            return ret;
        }

        private bool IsLobby(string path)
        {
            var ret = 
                GetState("CharaSelect") == UiState.Visible
                && path == "ffxiv/zon_z1/chr/z1c1/level/z1c1";
            Log($"IsLobby: {ret}");
            return ret;
        }

        private bool IsCharaMake(string path)
        {
            var ret = 
                GetState("_CharaMakeBgSelector") == UiState.Visible
                && path == "ffxiv/zon_z1/chr/z1c1/level/z1c1";
            Log($"IsCharaMake: {ret}");
            return ret;
        }

        enum UiState
        {
            Null,
            NotNull,
            Visible
        }

        private unsafe UiState GetState(string uiName)
        {
            Log($"GetState({uiName})");
            var ui = (AtkUnitBase*)_gameGui.GetAddonByName(uiName, 1);
            var ret = UiState.Null;
            if (ui != null)
            {
                ret = ui->IsVisible ? UiState.Visible : UiState.NotNull;
            }
            Log($"GetState({uiName}): {ret}");
            return ret;
        }

        public unsafe void DisableTitleLogo(int delay = 2001)
        {
            // If we try to set a logo's visibility too soon before it
            // finishes its animation, it will simply set itself visible again
            Task.Delay(delay).ContinueWith(_ =>
            {
                Log($"Logo task running after {delay} delay");
                var addon = (AtkUnitBase*)_gameGui.GetAddonByName("_TitleLogo", 1);
                if (addon == null || addon->UldManager.NodeListCount < 2) return;
                var node = addon->UldManager.NodeList[1];
                if (node == null) return;

                // The user has probably seen the logo by now, so don't abruptly hide it - be graceful
                if (delay > 1000)
                {
                    int fadeTime = 500;
                    var sw = Stopwatch.StartNew();
                    do
                    {
                        if (node == null) continue;
                        byte newAlpha = (byte)((fadeTime - sw.ElapsedMilliseconds) / (float)fadeTime * 255);
                        node->Color.A = newAlpha;
                    } while (sw.ElapsedMilliseconds < fadeTime);

                    // We still want to hide it at the end, though - reset alpha here
                    if (node == null) return;
                    node->ToggleVisibility(false);
                    node->Color.A = 255;
                }
            });
        }

        public unsafe void EnableTitleLogo()
        {
            var addon = (AtkUnitBase*)_gameGui.GetAddonByName("_TitleLogo", 1);
            if (addon == null || addon->UldManager.NodeListCount < 2) return;
            var node = addon->UldManager.NodeList[1];
            if (node == null) return;

            node->ToggleVisibility(true);
        }

        // public void SetRevisionStringVisibility(bool state)
        // {
        //     Log($"Setting version string visibility to {state}");
        //     byte alpha = state ? (byte)255 : (byte)0;
        //     Task.Run(() =>
        //     {
        //         // I didn't want to force this, but here we are
        //         var sw = Stopwatch.StartNew();
        //         while (sw.ElapsedMilliseconds < 5000)
        //         {
        //             unsafe
        //             {
        //                 var rev = (AtkUnitBase*)_gameGui.GetAddonByName("_TitleRevision", 1);
        //                 if (rev == null || rev->UldManager.NodeListCount < 2) continue;
        //                 var node = rev->UldManager.NodeList[1];
        //                 if (node == null) continue;
        //                 // node->Color.A = alpha;
        //                 node->ToggleVisibility(false);
        //                 Thread.Sleep(250);
        //             }
        //         }
        //         Log("Done forcing revision string.");
        //     });
        // }


        public void SetTextColors()
        {
            Log($"Setting title screen colors.");
            unsafe
            {
                var menus = new string[] { "_TitleMenu", "_TitleRevision", "_TitleRights" };
                for (int menuIndex = 0; menuIndex < menus.Length; menuIndex++)
                {
                    var rev = (AtkUnitBase*)_gameGui.GetAddonByName(menus[menuIndex], 1);

                    if (rev == null)
                        continue;

                    for (int i = 0; i < rev->UldManager.NodeListCount; i++)
                    {
                        var node = rev->UldManager.NodeList[i];

                        if (node->Type == NodeType.Text && menuIndex > 0)
                        {
                            var textNode = node->GetAsAtkTextNode();

                            Vector4? glow_color = null;
                            switch (menuIndex)
                            {
                                case 1:
                                    glow_color = _currentScreen.VersionTextColor;
                                    break;
                                case 2:
                                    glow_color = _currentScreen.CopyrightTextColor;
                                    break;
                            }

                            if (glow_color != null)
                            {
                                textNode->EdgeColor.A = (byte)glow_color.Value.W;
                                textNode->EdgeColor.R = (byte)glow_color.Value.X;
                                textNode->EdgeColor.G = (byte)glow_color.Value.Y;
                                textNode->EdgeColor.B = (byte)glow_color.Value.Z;

                                node->Color.A = (byte)glow_color.Value.W;
                            }
                        }
                        else if ((int)node->Type == 1001 && menuIndex == 0) //Menu buttons use this type
                        {
                            var comp = (AtkComponentNode*)node;
                            for (int a = 0; a < comp->Component->UldManager.NodeListCount; a++)
                            {
                                var no = comp->Component->UldManager.NodeList[a];
                                if (no->Type == NodeType.Text && _currentScreen.ButtonTextColor != null) //The button text
                                {
                                    var tNode = no->GetAsAtkTextNode();
                                    tNode->EdgeColor.A = (byte)_currentScreen.ButtonTextColor.Value.W;
                                    tNode->EdgeColor.R = (byte)_currentScreen.ButtonTextColor.Value.X;
                                    tNode->EdgeColor.G = (byte)_currentScreen.ButtonTextColor.Value.Y;
                                    tNode->EdgeColor.B = (byte)_currentScreen.ButtonTextColor.Value.Z;

                                    no->Color.A = (byte)_currentScreen.ButtonTextColor.Value.W;
                                }
                                else if (no->Type == NodeType.NineGrid && _currentScreen.ButtonHighlightColor != null) //The button background
                                {
                                    //#007fe0 - Approximation of the texture color of the highlight, the game modifies the color by correcting it with Add
                                    no->AddRed = no->AddRed_2 = (ushort)_currentScreen.ButtonHighlightColor.Value.X;
                                    no->AddGreen = no->AddGreen_2 = (ushort)((int)_currentScreen.ButtonHighlightColor.Value.Y - (int)0x7F);
                                    no->AddBlue = no->AddBlue_2 = (ushort)((int)_currentScreen.ButtonHighlightColor.Value.Z - (int)0xE0);

                                    no->Color.A = (byte)_currentScreen.ButtonHighlightColor.Value.W;
                                }
                            }
                        }
                    }
                }
            }
            Log("Done setting title screen colors.");
        }

        public ushort GetSong()
        {
            ushort currentSong = 0;
            if (TitleEditAddressResolver.BgmControl != IntPtr.Zero)
            {
                var bgmControlSub = Marshal.ReadIntPtr(TitleEditAddressResolver.BgmControl);
                if (bgmControlSub == IntPtr.Zero) return 0;
                var bgmControl = Marshal.ReadIntPtr(bgmControlSub + 0xC0);
                if (bgmControl == IntPtr.Zero) return 0;

                unsafe
                {
                    var readPoint = (ushort*)bgmControl.ToPointer();
                    readPoint += 6;

                    for (int activePriority = 0; activePriority < 12; activePriority++)
                    {
                        ushort songId1 = readPoint[0];
                        ushort songId2 = readPoint[1];
                        readPoint += ControlSize / 2; // sizeof control / sizeof short

                        if (songId1 == 0)
                            continue;

                        if (songId2 != 0 && songId2 != 9999)
                        {
                            currentSong = songId2;
                            break;
                        }
                    }
                }
            }

            return currentSong;
        }

        private float[] FloatArrayFromVector3(Vector3 floats)
        {
            float[] ret = new float[3];
            ret[0] = floats.X;
            ret[1] = floats.Y;
            ret[2] = floats.Z;
            return ret;
        }

// This can be used to find new title screen (lol) logo animation lengths
// public void LogLogoVisible()
// {
//     int logoResNode1Offset = 200;
//     int logoResNode2Offset = 56;
//     int logoResNodeFlagOffset = 0x9E;
//     ushort visibleFlag = 0x10;
//
//     ushort flagVal;
//     var start = Stopwatch.StartNew();
//
//     do
//     {
//         IntPtr flag = _gameGui.GetAddonByName("_TitleLogo", 1);
//         if (flag == IntPtr.Zero) continue;
//         flag = Marshal.ReadIntPtr(flag, logoResNode1Offset);
//         if (flag == IntPtr.Zero) continue;
//         flag = Marshal.ReadIntPtr(flag, logoResNode2Offset);
//         if (flag == IntPtr.Zero) continue;
//         flag += logoResNodeFlagOffset;
//
//         unsafe
//         {
//             flagVal = *(ushort*) flag.ToPointer();
//             if ((flagVal & visibleFlag) == visibleFlag)
//                 PluginLog.Log($"visible: {(flagVal & visibleFlag) == visibleFlag} | {start.ElapsedMilliseconds}");
//             
//             // arr: 59
//             // arrft: 61
//             // hw: 57
//             // sb: 2060
//             // shb: 2060
//             // ew: 10500
//             *(ushort*) flag.ToPointer() = (ushort) (flagVal & ~visibleFlag);
//         }
//     } while (start.ElapsedMilliseconds < 15000);
//
//     start.Stop();
// }

        private void Log(string s)
        {
#if !DEBUG
            if (_configuration.DebugLogging)
#endif
            PluginLog.Log($"[dbg] {s}");
        }
    }
}