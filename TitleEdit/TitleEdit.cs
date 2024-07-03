using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Hooking;
using Dalamud.Interface.ImGuiNotification;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json;

namespace TitleEdit;

public class TitleEdit
{
    private delegate int OnCreateScene(string p1, uint p2, IntPtr p3, uint p4, IntPtr p5, int p6, uint p7);

    private delegate IntPtr OnFixOn(IntPtr self,
        [MarshalAs(UnmanagedType.LPArray, SizeConst = 3)]
        float[] cameraPos,
        [MarshalAs(UnmanagedType.LPArray, SizeConst = 3)]
        float[] focusPos, float fovY);

    private delegate ulong OnLoadLogoResource(IntPtr p1, string p2, int p3, int p4);

    private delegate IntPtr OnPlayMusic(IntPtr self, string filename, float volume, uint fadeTime);

    private delegate void SetTimePrototype(ushort timeOffset);

    private delegate byte LobbyUpdate(GameLobbyType mapId, int time);

    // The size of the BGMControl object
    private const int ControlSize = 88;
    private readonly TitleEditConfiguration _configuration;

    private readonly Hook<OnCreateScene> _createSceneHook;
    private readonly Hook<OnPlayMusic> _playMusicHook;
    private readonly Hook<OnFixOn> _fixOnHook;
    private readonly Hook<OnLoadLogoResource> _loadLogoResourceHook;
    private readonly Hook<LobbyUpdate> _lobbyUpdateHook;
    private readonly SetTimePrototype _setTime;

    private readonly string _titleScreenBasePath;
    private bool _titleCameraNeedsSet;
    private bool _amForcingTime;
    private bool _amForcingWeather;
    private GameLobbyType _lastLobbyUpdateMapId = GameLobbyType.Movie;

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
        BgmPath = "music/ffxiv/BGM_System_Title.scd"
    };

    public TitleEdit(TitleEditConfiguration configuration, string screenDir)
    {
        DalamudApi.PluginLog.Info("TitleEdit hook init");
        _configuration = configuration;

        TitleEditAddressResolver.Setup64Bit();

        _titleScreenBasePath = screenDir;

        _createSceneHook = DalamudApi.Hooks.HookFromAddress<OnCreateScene>(TitleEditAddressResolver.CreateScene, HandleCreateScene);
        _playMusicHook = DalamudApi.Hooks.HookFromAddress<OnPlayMusic>(TitleEditAddressResolver.PlayMusic, HandlePlayMusic);
        _fixOnHook = DalamudApi.Hooks.HookFromAddress<OnFixOn>(TitleEditAddressResolver.FixOn, HandleFixOn);
        _loadLogoResourceHook =
            DalamudApi.Hooks.HookFromAddress<OnLoadLogoResource>(TitleEditAddressResolver.LoadLogoResource, HandleLoadLogoResource);
        _lobbyUpdateHook = DalamudApi.Hooks.HookFromAddress<LobbyUpdate>(TitleEditAddressResolver.LobbyUpdate, LobbyUpdateDetour);

        _setTime = Marshal.GetDelegateForFunctionPointer<SetTimePrototype>(TitleEditAddressResolver.SetTime);
        DalamudApi.PluginLog.Info("TitleEdit hook init finished");

        Task.Run(LogLogoVisible);
    }

    private byte LobbyUpdateDetour(GameLobbyType mapId, int time)
    {
        _lastLobbyUpdateMapId = mapId;
        var gameTitleScreen = TitleEditAddressResolver.GetGameExpectedTitleScreen(); // this is expac title, not GameLobbyType
        var currentMap = (GameLobbyType)TitleEditAddressResolver.CurrentLobbyMap;

        // var isARRTitleScreen = gameTitleScreen == 0;
        var isTitleScreenToLobby = currentMap == GameLobbyType.Title && mapId == GameLobbyType.CharaSelect;
        var isLobbyToTitleScreen = currentMap == GameLobbyType.CharaSelect && mapId == GameLobbyType.Title;
        // var shouldApply = isARRTitleScreen && (isTitleScreenToLobby || isLobbyToTitleScreen);
        var shouldApply = (isTitleScreenToLobby || isLobbyToTitleScreen);

        DalamudApi.PluginLog.Verbose($"[LobbyUpdateDetour] map {mapId} time {time} currentMap {currentMap} " +
                                     // $"gameTitleScreen {gameTitleScreen} isARRTitleScreen {isARRTitleScreen} " +
                                     $"gameTitleScreen {gameTitleScreen} " +
                                     $"isTitleScreenToLobby {isTitleScreenToLobby} isLobbyToTitleScreen {isLobbyToTitleScreen} " +
                                     $"shouldApply {shouldApply}");

        if (shouldApply)
        {
            DalamudApi.PluginLog.Debug($"[LobbyUpdateDetour] Running!");
            // This tells the game it was playing a movie so it skips the "same zone" check entirely
            TitleEditAddressResolver.CurrentLobbyMap = (short)GameLobbyType.Movie;
        }

        return _lobbyUpdateHook.Original(mapId, time);
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
            DalamudApi.PluginLog.Info(
                $"Title Edit tried to find {path}, but no title file was found, so title settings have been reset.");
            Fail();
            return;
        }

        var contents = File.ReadAllText(path);
        _currentScreen = JsonConvert.DeserializeObject<TitleEditScreen>(contents);

        if (!IsScreenValid(_currentScreen))
        {
            DalamudApi.PluginLog.Info($"Title Edit tried to load {_currentScreen.Name}, but the necessary files are missing, so title settings have been reset.");
            Fail();
            return;
        }

        Log($"Title Edit loaded {path}");

        if (_configuration.DisplayTitleToast)
        {
            Task.Delay(2000).ContinueWith(_ =>
            {
                if (GetState("_TitleMenu") == UiState.Visible)
                    DalamudApi.NotificationManager.AddNotification(new Notification
                    {
                        Content = $"Now displaying: {_currentScreen.Name}",
                        Title = "Title Edit",
                        Type = NotificationType.Info,
                    });
            });
        }
    }

    private bool IsScreenValid(TitleEditScreen screen)
    {
        return DalamudApi.DataManager.FileExists($"bg/{screen.TerritoryPath}.lvb") &&
               DalamudApi.DataManager.FileExists(screen.BgmPath);
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

        if (_lastLobbyUpdateMapId == GameLobbyType.CharaSelect)
        {
            Log("Loading lobby and lobby fixon.");
            var returnVal = _createSceneHook.Original(p1, p2, p3, p4, p5, p6, p7);
            FixOn(new Vector3(0, 0, 0), new Vector3(0, 0.8580103f, 0), 1);
            return returnVal;
        }

        if (_lastLobbyUpdateMapId == GameLobbyType.Title)
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

    public void Enable()
    {
        _loadLogoResourceHook.Enable();
        _createSceneHook.Enable();
        _playMusicHook.Enable();
        _fixOnHook.Enable();
        _lobbyUpdateHook.Enable();
    }

    public void Dispose()
    {
        _amForcingTime = false;
        _amForcingWeather = false;
        _loadLogoResourceHook.Dispose();
        _createSceneHook.Dispose();
        _playMusicHook.Dispose();
        _fixOnHook.Dispose();
        _lobbyUpdateHook.Dispose();
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
                    if (!_amForcingTime)
                        break;

                    if (TitleEditAddressResolver.SetTime != IntPtr.Zero)
                        _setTime(timeOffset);
                    Thread.Sleep(50);
                } while (stop.ElapsedMilliseconds < forceTime && _amForcingTime);

                Log($"Done forcing time.");
            }
            catch (Exception e)
            {
                DalamudApi.PluginLog.Error(e, "An error occurred when forcing time.");
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
                    if (!_amForcingWeather)
                        break;

                    SetWeather(weather);
                    Thread.Sleep(20);
                } while (stop.ElapsedMilliseconds < forceTime && _amForcingWeather);

                Log($"Done forcing weather.");
                Log($"Weather is now {GetWeather()}");
            }
            catch (Exception e)
            {
                DalamudApi.PluginLog.Error(e, "An error occurred when forcing weather.");
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

    enum UiState
    {
        Null,
        NotNull,
        Visible
    }

    private unsafe UiState GetState(string uiName)
    {
        // Log($"GetState({uiName})");
        var ui = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName(uiName);
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
            var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("_TitleLogo");
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
        var addon = (AtkUnitBase*)DalamudApi.GameGui.GetAddonByName("_TitleLogo");
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
    public unsafe void LogLogoVisible()
    {
        // arr: 59
        // arrft: 61
        // hw: 57
        // sb: 2060
        // shb: 2060
        // ew: 10500
        // dt: 20700
        
        var start = Stopwatch.StartNew();
        do
        {
            var ptr = DalamudApi.Framework.RunOnFrameworkThread(() => DalamudApi.GameGui.GetAddonByName("_TitleLogo")).Result;
            if (ptr == IntPtr.Zero) continue;
            var titleLogo = (AtkUnitBase*)ptr;
            DalamudApi.PluginLog.Info($"visible: {titleLogo->IsVisible} | {start.ElapsedMilliseconds}");
        } while (start.ElapsedMilliseconds < 30000);
    
        start.Stop();
    }

    private void Log(string s)
    {
        DalamudApi.PluginLog.Debug($"[dbg] {s}");
    }
}