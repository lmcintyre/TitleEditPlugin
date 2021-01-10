using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace TitleEdit
{
    public class TitleEdit
    {
        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate int OnCreateScene(string p1, uint p2, IntPtr p3, uint p4, IntPtr p5, int p6, uint p7);

        private delegate IntPtr OnFixOn(IntPtr self, [MarshalAs(UnmanagedType.LPArray, SizeConst = 3)]
            float[] cameraPos, [MarshalAs(UnmanagedType.LPArray, SizeConst = 3)]
            float[] focusPos, float fovY);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate ulong OnLoadLogoResource(IntPtr p1, string p2, int p3, int p4);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr OnPlayMusic(IntPtr self, string filename, float volume, uint fadeTime);

        private delegate void SetTimePrototype(ushort timeOffset);

        // The size of the BGMControl object
        private const int ControlSize = 88;

        private readonly DalamudPluginInterface _pi;
        private readonly TitleEditConfiguration _configuration;

        private readonly Hook<OnCreateScene> _createSceneHook;
        private readonly Hook<OnPlayMusic> _playMusicHook;
        private readonly Hook<OnFixOn> _fixOnHook;
        private readonly Hook<OnLoadLogoResource> _loadLogoResourceHook;

        private readonly SetTimePrototype _setTime;

        private readonly string _titleScreenBasePath;
        private bool _titleCameraNeedsSet;
        private bool _amForcingTime;
        private bool _amForcingWeather;

        private TitleEditScreen _currentScreen;

        // Hardcoded fallback info now that jank is resolved
        private static TitleEditScreen Shadowbringers => new()
        {
            Name = "Shadowbringers",
            TerritoryPath = "ex3/05_zon_z4/chr/z4c1/level/z4c1",
            Logo = "Shadowbringers",
            DisplayLogo = true,
            CameraPos = new Vector3(0, 5, 10),
            FixOnPos = new Vector3(0, 0, 0),
            FovY = 1,
            WeatherId = 2,
            BgmPath = "music/ex3/BGM_EX3_System_Title.scd"
        };

        private void RefreshCurrentTitleEditScreen()
        {
            var files = Directory.GetFiles(_titleScreenBasePath);
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
                    toLoad = "Shadowbringers";
                }
            }

            var path = Path.Combine(_titleScreenBasePath, toLoad + ".json");
            if (!File.Exists(path))
            {
                PluginLog.Log($"Title Edit tried to find {path}, but no title file was found, so title settings have been reset.");
                _configuration.TitleList = new List<string>();
                _configuration.DisplayTitleLogo = true;
                _configuration.SelectedTitleFileName = "Shadowbringers";
                _configuration.SelectedLogoName = "Shadowbringers";
                _configuration.Save();
                _currentScreen = Shadowbringers;
                return;
            }

            var contents = File.ReadAllText(path);
            _currentScreen = JsonConvert.DeserializeObject<TitleEditScreen>(contents);
            Log($"Title Edit loaded {path}");
        }

        public TitleEdit(DalamudPluginInterface pi, TitleEditConfiguration configuration, string screenDir)
        {
            _pi = pi;
            _configuration = configuration;

            TitleEditAddressResolver.Setup64Bit(pi.TargetModuleScanner);

            PluginLog.Log("===== T I T L E E D I T =====");
            _titleScreenBasePath = screenDir;

            _createSceneHook = new Hook<OnCreateScene>(TitleEditAddressResolver.CreateScene, new OnCreateScene(HandleCreateScene), this);
            _playMusicHook = new Hook<OnPlayMusic>(TitleEditAddressResolver.PlayMusic, new OnPlayMusic(HandlePlayMusic), this);
            _fixOnHook = new Hook<OnFixOn>(TitleEditAddressResolver.FixOn, new OnFixOn(HandleFixOn), this);
            _loadLogoResourceHook = new Hook<OnLoadLogoResource>(TitleEditAddressResolver.LoadLogoResource, new OnLoadLogoResource(HandleLoadLogoResource), this);

            _setTime = Marshal.GetDelegateForFunctionPointer<SetTimePrototype>(TitleEditAddressResolver.SetTime);
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
                var returnVal = _createSceneHook.Original(p1, p2, p3, p4, p5, p6, p7);
                _titleCameraNeedsSet = true;
                ForceWeather(_currentScreen.WeatherId, 5000);
                ForceTime(_currentScreen.TimeOffset, 5000);
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
            Log($"HandleFixOn {self.ToInt64():X} {cameraPos[0]} {cameraPos[1]} {cameraPos[2]} " +
                $"{focusPos[0]} {focusPos[1]} {focusPos[2]} {fovY} | {_titleCameraNeedsSet}");
            if (!_titleCameraNeedsSet || _currentScreen == null)
                return _fixOnHook.Original(self, cameraPos, focusPos, fovY);
            _titleCameraNeedsSet = false;
            return _fixOnHook.Original(self,
                FloatArrayFromVector3(_currentScreen.CameraPos),
                FloatArrayFromVector3(_currentScreen.FixOnPos),
                _currentScreen.FovY);
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
            if (!p2.Contains("Title_Logo") || _currentScreen == null) return _loadLogoResourceHook.Original(p1, p2, p3, p4);
            Log($"HandleLoadLogoResource {p1.ToInt64():X} {p2} {p3} {p4}");
            ulong result;

            var logo = _configuration.SelectedLogoName;
            var display = _configuration.DisplayTitleLogo;
            var over = _configuration.Override;
            if (over == OverrideSetting.UseIfLogoUnspecified && _currentScreen.Logo != "Unspecified")
            {
                logo = _currentScreen.Logo;
                display = _currentScreen.DisplayLogo;
            }

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
                default:
                    result = _loadLogoResourceHook.Original(p1, "Title_Logo500", p3, p4);
                    break;
            }

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
        }

        public void Dispose()
        {
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
                Stopwatch stop = Stopwatch.StartNew();
                do
                {
                    _setTime(timeOffset);
                } while (stop.ElapsedMilliseconds < forceTime && _amForcingTime);

                Log($"Done forcing time.");
            });
        }

        public byte GetWeather()
        {
            byte weather;
            unsafe
            {
                weather = *(byte*) TitleEditAddressResolver.WeatherPtr;
            }

            return weather;
        }

        public void SetWeather(byte weather)
        {
            unsafe
            {
                *(byte*) TitleEditAddressResolver.WeatherPtr = weather;
            }
        }

        private void ForceWeather(byte weather, int forceTime)
        {
            _amForcingWeather = true;
            Task.Run(() =>
            {
                Stopwatch stop = Stopwatch.StartNew();
                do
                {
                    SetWeather(weather);
                } while (stop.ElapsedMilliseconds < forceTime && _amForcingWeather);

                Log($"Done forcing weather.");
                Log($"Weather is now {GetWeather()}");
            });
        }

        // TODO: Eventually figure out how to do these without excluding free trial players
        private bool IsTitleScreen(string path)
        {
            return path == "ex3/05_zon_z4/chr/z4c1/level/z4c1" ||
                   path == "ex2/05_zon_z3/chr/z3c1/level/z3c1" ||
                   path == "ex1/05_zon_z2/chr/z2c1/level/z2c1"; // ||
            // path == "ffxiv/zon_z1/chr/z1c1/level/z1c1";
        }

        private bool IsLobby(string path)
        {
            return path == "ffxiv/zon_z1/chr/z1c1/level/z1c1";
        }

        public void DisableTitleLogo(int delay = 2001)
        {
            int logoResNode1Offset = 200;
            int logoResNode2Offset = 56;
            int logoResNodeAlphaOffset = 0x73;
            int logoResNodeFlagOffset = 0x9E;
            ushort visibleFlag = 0x10;

            // If we try to set a logo's visibility too soon before it
            // finishes its animation, it will simply set itself visible again
            Task.Delay(delay).ContinueWith(_ =>
            {
                Log($"Logo task running after {delay} delay");
                IntPtr flag = _pi.Framework.Gui.GetUiObjectByName("_TitleLogo", 1);
                if (flag == IntPtr.Zero) return;
                flag = Marshal.ReadIntPtr(flag, logoResNode1Offset);
                if (flag == IntPtr.Zero) return;
                flag = Marshal.ReadIntPtr(flag, logoResNode2Offset);
                if (flag == IntPtr.Zero) return;
                var alpha = flag + logoResNodeAlphaOffset;
                flag += logoResNodeFlagOffset;

                unsafe
                {
                    // The user has probably seen the logo by now, so don't abruptly hide it - be graceful
                    if (delay > 1000)
                    {
                        int fadeTime = 500;
                        Stopwatch stop = Stopwatch.StartNew();
                        do
                        {
                            int newAlpha = (int) ((fadeTime - stop.ElapsedMilliseconds) / (float) fadeTime * 255);
                            *(byte*) alpha.ToPointer() = (byte) newAlpha;
                        } while (stop.ElapsedMilliseconds < fadeTime);
                    }

                    // We still want to hide it at the end, though - reset alpha here
                    ushort flagVal = *(ushort*) flag.ToPointer();
                    *(ushort*) flag.ToPointer() = (ushort) (flagVal & ~visibleFlag);
                    *(byte*) alpha.ToPointer() = 255;
                }
            });
        }

        public void EnableTitleLogo()
        {
            int logoResNode1Offset = 200;
            int logoResNode2Offset = 56;
            int logoResNodeFlagOffset = 0x9E;
            ushort visibleFlag = 0x10;

            IntPtr flag = _pi.Framework.Gui.GetUiObjectByName("_TitleLogo", 1);
            if (flag == IntPtr.Zero) return;
            flag = Marshal.ReadIntPtr(flag, logoResNode1Offset);
            if (flag == IntPtr.Zero) return;
            flag = Marshal.ReadIntPtr(flag, logoResNode2Offset);
            if (flag == IntPtr.Zero) return;
            flag += logoResNodeFlagOffset;

            unsafe
            {
                ushort flagVal = *(ushort*) flag.ToPointer();
                *(ushort*) flag.ToPointer() = (ushort) (flagVal | visibleFlag);
            }
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
                    var readPoint = (ushort*) bgmControl.ToPointer();
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
        //         IntPtr flag = _pi.Framework.Gui.GetUiObjectByName("_TitleLogo", 1);
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
        //             *(ushort*) flag.ToPointer() = (ushort) (flagVal & ~visibleFlag);
        //         }
        //     } while (start.ElapsedMilliseconds < 5000);
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