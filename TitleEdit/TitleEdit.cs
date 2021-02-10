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

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr SetAddonPosition(IntPtr self, short x, short y);

        private delegate void SetTimePrototype(ushort timeOffset);

        // The size of the BGMControl object
        private const int ControlSize = 88;
        private const int BaseHOffset = 46;
        private const int BaseVOffset = 27;
        private const int MenuWidth = 600;
        private const int MenuHeight = 178;
        // private const int RevisionWidth = 300; // fake
        private const int RealRevisionWidth = 915; // not fake
        private const int RevisionHeight = 26;
        private const int RightsWidth = 1000;
        private const int RightsHeight = 26;

        /*
         * Menu    760, 955 | 600, 178
            Rights 560, 1215 | 1000, 26
            Rev    53, 32 | 300, 26
         */

        private readonly DalamudPluginInterface _pi;
        private readonly TitleEditConfiguration _configuration;

        private readonly Hook<OnCreateScene> _createSceneHook;
        private readonly Hook<OnPlayMusic> _playMusicHook;
        private readonly Hook<OnFixOn> _fixOnHook;
        private readonly Hook<OnLoadLogoResource> _loadLogoResourceHook;
        private readonly Hook<SetAddonPosition> _setAddonPositionHook;

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

        public void RefreshCurrentTitleEditScreen()
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
            PluginLog.Log("TitleEdit hook init");
            _pi = pi;
            _configuration = configuration;

            TitleEditAddressResolver.Setup64Bit(pi.TargetModuleScanner);

            _titleScreenBasePath = screenDir;

            _createSceneHook = new Hook<OnCreateScene>(TitleEditAddressResolver.CreateScene, new OnCreateScene(HandleCreateScene), this);
            _playMusicHook = new Hook<OnPlayMusic>(TitleEditAddressResolver.PlayMusic, new OnPlayMusic(HandlePlayMusic), this);
            _fixOnHook = new Hook<OnFixOn>(TitleEditAddressResolver.FixOn, new OnFixOn(HandleFixOn), this);
            _loadLogoResourceHook = new Hook<OnLoadLogoResource>(TitleEditAddressResolver.LoadLogoResource, new OnLoadLogoResource(HandleLoadLogoResource), this);
            _setAddonPositionHook = new Hook<SetAddonPosition>(TitleEditAddressResolver.AtkUnitBaseSetPosition, new SetAddonPosition(HandleSetAddonPosition), this);

            _setTime = Marshal.GetDelegateForFunctionPointer<SetTimePrototype>(TitleEditAddressResolver.SetTime);
            RefreshCurrentTitleEditScreen();
            PluginLog.Log("TitleEdit hook init finished");
        }

        private int HandleCreateScene(string p1, uint p2, IntPtr p3, uint p4, IntPtr p5, int p6, uint p7)
        {
            Log($"HandleCreateScene {p1} {p2} {p3.ToInt64():X} {p4} {p5.ToInt64():X} {p6} {p7}");
            _titleCameraNeedsSet = false;
            _amForcingTime = false;
            _amForcingWeather = false;

            if (IsLobby(p1) && _pi.ClientState == null  || _pi.ClientState.LocalPlayer == null)
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
            _setAddonPositionHook.Enable();
        }

        public void Dispose()
        {
            _loadLogoResourceHook.Dispose();
            _createSceneHook.Dispose();
            _playMusicHook.Dispose();
            _fixOnHook.Dispose();
            _setAddonPositionHook.Disable();
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

        private void SetVisibleFlag(string addonName, bool state)
        {
            int logoResNode1Offset = 200;
            int logoResNode2Offset = 56;
            int logoResNodeFlagOffset = 0x9E;
            ushort visibleFlag = 0x10;

            IntPtr flag = _pi.Framework.Gui.GetUiObjectByName(addonName, 1);
            if (flag == IntPtr.Zero) return;
            flag = Marshal.ReadIntPtr(flag, logoResNode1Offset);
            if (flag == IntPtr.Zero) return;
            flag = Marshal.ReadIntPtr(flag, logoResNode2Offset);
            if (flag == IntPtr.Zero) return;
            flag += logoResNodeFlagOffset;

            unsafe
            {
                ushort flagVal = *(ushort*) flag.ToPointer();
                if (state)
                    *(ushort*) flag.ToPointer() = (ushort) (flagVal | visibleFlag);
                else
                    *(ushort*) flag.ToPointer() = (ushort) (flagVal & ~visibleFlag);
            }
        }

        private void SetAlpha(string addonName, byte alpha)
        {
            int logoResNode1Offset = 200;
            int logoResNode2Offset = 56;
            int logoResNodeAlphaOffset = 0x73;

            IntPtr alphaPtr = _pi.Framework.Gui.GetUiObjectByName(addonName, 1);
            if (alphaPtr == IntPtr.Zero) return;
            alphaPtr = Marshal.ReadIntPtr(alphaPtr, logoResNode1Offset);
            if (alphaPtr == IntPtr.Zero) return;
            alphaPtr = Marshal.ReadIntPtr(alphaPtr, logoResNode2Offset);
            if (alphaPtr == IntPtr.Zero) return;
            alphaPtr += logoResNodeAlphaOffset;

            Marshal.WriteByte(alphaPtr, alpha);
        }

        public void EnableTitleLogo()
        {
            SetVisibleFlag("_TitleLogo", true);
        }

        public void SetRevisionStringVisibility(bool state)
        {
            // SetVisibleFlag("_TitleRevision", state);
            byte alpha = state ? 255 : 0;
            // SetAlpha("_TitleRevision", alpha);
            Task.Run(() =>
            {
                // I didn't want to force this, but here we are
                Stopwatch sw = new Stopwatch();
                sw.Start();
                while (sw.ElapsedMilliseconds < 300)
                    SetAlpha("_TitleRevision", alpha);
            });

            var addonRights = _pi.Framework.Gui.GetAddonByName("_TitleRights", 1);
            if (addonRights.Address == IntPtr.Zero)
                return;
            var rightsPos = PositionRights(addonRights.X, addonRights.Y);
            _setAddonPositionHook.Original(addonRights.Address, rightsPos.x, rightsPos.y);
            
            var addonMenu = _pi.Framework.Gui.GetAddonByName("_TitleMenu", 1);
            if (addonMenu.Address == IntPtr.Zero)
                return;
            var menuPos = PositionMenu(addonMenu.X, addonMenu.Y);
            _setAddonPositionHook.Original(addonMenu.Address, menuPos.x, menuPos.y);
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

        private IntPtr HandleSetAddonPosition(IntPtr self, short x, short y)
        {
            if (Marshal.PtrToStringAnsi(self + 8) == "_TitleRevision")
            {
                PluginLog.Log("Positioning revision...");
                var position = PositionRevision(x, y);
                SetRevisionStringVisibility(_configuration.DisplayVersionText);
                return _setAddonPositionHook.Original(self, position.x, position.y);
            }

            if (Marshal.PtrToStringAnsi(self + 8) == "_TitleRights")
            {
                var position = PositionRights(x, y);
                return _setAddonPositionHook.Original(self, position.x, position.y);
            }

            if (Marshal.PtrToStringAnsi(self + 8) == "_TitleMenu")
            {
                var position = PositionMenu(x, y);
                return _setAddonPositionHook.Original(self, position.x, position.y);
            }

            return _setAddonPositionHook.Original(self, x, y);
        }

        private (short x, short y) PositionMenu(short x, short y)
        {
            (short x, short y) position = (x, y);

            var gameWindowWidth = Marshal.ReadInt16(TitleEditAddressResolver.GameWindowSize);
            var gameWindowHeight = Marshal.ReadInt16(TitleEditAddressResolver.GameWindowSize, 4);

            var hInset = (short) (gameWindowWidth * _currentScreen.HInset);
            var vInset = (short) (gameWindowWidth * _currentScreen.VInset);

            position.x = _currentScreen.HAlign switch
            {
                TitleEditMenuHAlign.Left => hInset,
                TitleEditMenuHAlign.Right => (short) (gameWindowWidth - MenuWidth - hInset),
                _ => (short) (gameWindowWidth / 2f - MenuWidth / 2f + hInset)
            };
            
            var effectiveRevisionHeight = _configuration.DisplayVersionText ? RevisionHeight : 0;

            var addonRightsY = _currentScreen.VAlign switch
            {
                TitleEditMenuVAlign.Top => BaseVOffset + effectiveRevisionHeight,
                _ => (short) (gameWindowHeight - RightsHeight - effectiveRevisionHeight)
            };

            position.y = _currentScreen.VAlign switch
            {
                TitleEditMenuVAlign.Top => (short) (vInset + addonRightsY + RightsHeight),
                TitleEditMenuVAlign.Bottom => (short) (addonRightsY - MenuHeight - vInset),
                TitleEditMenuVAlign.Default => position.y,
                _ => (short) (gameWindowHeight / 2f - MenuHeight / 2f + vInset)
            };

            AlignMenuButtons();

            return position;
        }

        private void AlignMenuButtons()
        {
            var addonMenu = _pi.Framework.Gui.GetAddonByName("_TitleMenu", 1);
            var nodeList = Marshal.ReadIntPtr(addonMenu.Address, 0x78);
            if (nodeList != IntPtr.Zero)
            {
                var nodeListCount = (ushort) Marshal.ReadInt16(addonMenu.Address, 0x6A);
                for (var i = 4; i < nodeListCount; i++)
                {
                    var buttonComponentNode = Marshal.ReadIntPtr(nodeList, i * 8);
                    if (buttonComponentNode == IntPtr.Zero) continue;
                    var buttonComponent = Marshal.ReadIntPtr(buttonComponentNode, 0xA8);
                    if (buttonComponent == IntPtr.Zero) continue;
                    var bNodeList = Marshal.ReadIntPtr(buttonComponent, 0x58);
                    if (bNodeList == IntPtr.Zero) continue;
                    var bNodeListCount = (ushort) Marshal.ReadInt16(buttonComponent, 0x4A);
                    if (bNodeListCount < 5) continue;
                    var textNode = Marshal.ReadIntPtr(bNodeList, 32);
                    if (textNode == IntPtr.Zero) continue;
                    var alignment = _currentScreen.TextAlign switch
                    {
                        TitleEditMenuHAlign.Left => (byte) 0x13,
                        TitleEditMenuHAlign.Right => (byte) 0x15,
                        _ => (byte) 0x14,
                    };
                    Marshal.WriteByte(textNode, 0x14C, alignment);
                }
            }
        }

        private (short x, short y) PositionRights(short x, short y)
        {
            (short x, short y) position = (x, y);

            var gameWindowWidth = Marshal.ReadInt16(TitleEditAddressResolver.GameWindowSize);
            var gameWindowHeight = Marshal.ReadInt16(TitleEditAddressResolver.GameWindowSize, 4);

            byte alignmentBits = _currentScreen.HAlign switch
            {
                TitleEditMenuHAlign.Left => 0x3,
                TitleEditMenuHAlign.Right => 0x5,
                _ => 0x4
            };

            position.x = _currentScreen.HAlign switch
            {
                TitleEditMenuHAlign.Left => BaseHOffset,
                TitleEditMenuHAlign.Default => position.x,
                TitleEditMenuHAlign.Center => (short) (gameWindowWidth / 2f - RightsWidth / 2f),
                _ => (short) (gameWindowWidth - BaseHOffset - RightsWidth)
            };

            var effectiveRevisionHeight = _configuration.DisplayVersionText ? RevisionHeight : 0;

            position.y = _currentScreen.VAlign switch
            {
                TitleEditMenuVAlign.Top => (short) (BaseVOffset + effectiveRevisionHeight),
                TitleEditMenuVAlign.Default => position.y,
                _ => (short) (gameWindowHeight - RightsHeight - effectiveRevisionHeight)
            };

            var addonRights = _pi.Framework.Gui.GetAddonByName("_TitleRights", 1);

            var resNode1 = Marshal.ReadIntPtr(addonRights.Address, 200);
            if (resNode1 == IntPtr.Zero) return (x, y);
            var resNode2 = Marshal.ReadIntPtr(resNode1, 56);
            if (resNode2 == IntPtr.Zero) return (x, y);
            var textNode = Marshal.ReadIntPtr(resNode2, 56);
            if (textNode == IntPtr.Zero) return (x, y);
            var existingAlignmentByte = Marshal.ReadByte(textNode, 0x14C) & 0xF0;
            Marshal.WriteByte(textNode, 0x14C, (byte) (existingAlignmentByte | alignmentBits));

            return position;
        }

        private (short x, short y) PositionRevision(short x, short y)
        {
            (short x, short y) position = (x, y);

            var gameWindowWidth = Marshal.ReadInt16(TitleEditAddressResolver.GameWindowSize);
            var gameWindowHeight = Marshal.ReadInt16(TitleEditAddressResolver.GameWindowSize, 4);

            byte alignmentBits = _currentScreen.HAlign switch
            {
                TitleEditMenuHAlign.Center => 0x5,
                TitleEditMenuHAlign.Right => 0x5,
                _ => 0x3
            };

            position.x = _currentScreen.HAlign switch
            {
                TitleEditMenuHAlign.Left => BaseHOffset,
                TitleEditMenuHAlign.Default => position.x,
                TitleEditMenuHAlign.Center => (short) (gameWindowWidth / 2f + RealRevisionWidth / 2f),
                _ => (short) (gameWindowWidth - BaseHOffset)
            };

            position.y = _currentScreen.VAlign switch
            {
                TitleEditMenuVAlign.Top => BaseVOffset,
                TitleEditMenuVAlign.Default => position.y,
                _ => (short) (gameWindowHeight - BaseVOffset)
            };

            var addonVersion = _pi.Framework.Gui.GetAddonByName("_TitleRevision", 1);

            var resNode1 = Marshal.ReadIntPtr(addonVersion.Address, 200);
            if (resNode1 == IntPtr.Zero) return (x, y);
            var resNode2 = Marshal.ReadIntPtr(resNode1, 56);
            if (resNode2 == IntPtr.Zero) return (x, y);
            var textNode = Marshal.ReadIntPtr(resNode2, 56);
            if (textNode == IntPtr.Zero) return (x, y);
            var existingAlignmentByte = Marshal.ReadByte(textNode, 0x14C) & 0xF0;
            Marshal.WriteByte(textNode, 0x14C, (byte) (existingAlignmentByte | alignmentBits));

            return position;
        }

        // I enjoy this code. So it's staying here
        // private (short x, short y) PositionRights(short x, short y) {
        //
        //     (short x, short y) position = (x, y);
        //     var addonRights = _pi.Framework.Gui.GetAddonByName("_TitleRights", 1);
        //     if (addonRights == null) return position;
        //     var addonVersion = _pi.Framework.Gui.GetAddonByName("_TitleRevision", 1);
        //     if (addonVersion == null) return position;
        //     
        //     var gameWindowWidth = Marshal.ReadInt16(TitleEditAddressResolver.GameWindowSize);
        //     // var gameWindowHeight = Marshal.ReadInt16(TitleEditAddressResolver.GameWindowSize, 4);
        //
        //     var borderOffset = addonVersion.X;
        //
        //     bool hasEnoughWidth = gameWindowWidth >= addonRights.Width + borderOffset + addonVersion.Width + 100;
        //     byte alignmentBits = 0;
        //
        //     if (hasEnoughWidth)
        //     {
        //         position.x = (short) (gameWindowWidth - addonRights.Width - borderOffset);
        //         position.y = addonVersion.Y;
        //         alignmentBits = 0x5;
        //     }
        //     else
        //     {
        //         position.x = addonVersion.X;
        //         position.y = (short) (addonVersion.Y + addonVersion.Height);
        //         alignmentBits = 0x3;
        //     }
        //     
        //     var resNode1 = Marshal.ReadIntPtr(addonRights.Address, 200);
        //     if (resNode1 == IntPtr.Zero) return (x, y);
        //     var resNode2 = Marshal.ReadIntPtr(resNode1, 56);
        //     if (resNode2 == IntPtr.Zero) return (x, y);
        //     var textNode = Marshal.ReadIntPtr(resNode2, 56);
        //     if (textNode == IntPtr.Zero) return (x, y);
        //     var existingAlignmentByte = Marshal.ReadByte(textNode, 0x14C) & 0xF0;
        //     Marshal.WriteByte(textNode, 0x14C, (byte) (existingAlignmentByte | alignmentBits));
        //
        //     return position;
        // }

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