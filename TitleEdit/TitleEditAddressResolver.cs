using System;
using System.Runtime.InteropServices;
using Dalamud.Game;

namespace TitleEdit
{
    public static class TitleEditAddressResolver
    {
        private static IntPtr CameraBase { get; set; }
        
        public static IntPtr RenderCamera
        {
            get
            {
                var base2 = Marshal.ReadIntPtr(CameraBase);
                return base2 == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(base2, 240);
            }
        }

        public static IntPtr LobbyCamera => CameraBase == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(CameraBase, 16);

        // public static IntPtr LobbyCamera { get; private set; }
        // public static IntPtr RenderCamera { get; private set; }
        public static IntPtr LoadLogoResource { get; private set; }
        public static IntPtr SetTime { get; private set; }
        public static IntPtr CreateScene { get; private set; }
        public static IntPtr FixOn { get; private set; }
        public static IntPtr PlayMusic { get; private set; }
        public static IntPtr BgmControl { get; private set; }
        public static IntPtr WeatherPtr { get; private set; }

        public static void Setup64Bit(SigScanner sig)
        {
            LoadLogoResource = sig.ScanText("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 41 8B F9 41 0F B6 F0 48 8B D9 48 85 D2 75 12");
            CameraBase = sig.GetStaticAddressFromSig(
                "48 8B 05 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? 45 33 C0 33 D2 C6 40 09 01 E8 ?? ?? ?? ?? 48 8B 35 ?? ?? ?? ?? 48 85 F6 74 1E 48 8D 54 24 ?? 48 8B CF E8 ?? ?? ?? ?? 48 8B CE 8B 50 04 89 96 ?? ?? ?? ?? E8 ?? ?? ?? ??", 9);
            SetTime = sig.ScanText("40 53 48 83 EC 20 44 0F BF C1 B8 ?? ?? ?? ?? 41 F7 E8 66 89 0D ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? C1 FA 05 8B C2 C1 E8 1F 03 D0");
            CreateScene = sig.ScanText("E8 ?? ?? ?? ?? 66 89 1D ?? ?? ?? ?? E9 ?? ?? ?? ??");
            FixOn = sig.ScanText("C6 81 ?? ?? ?? ?? ?? 8B 02 89 41 60");
            PlayMusic = sig.ScanText("E8 ?? ?? ?? ?? 48 89 47 18 89 5F 20");
            BgmControl = sig.GetStaticAddressFromSig("48 8B 05 ?? ?? ?? ?? 48 85 C0 74 42 83 78 08 0A", 3);
            WeatherPtr = sig.GetStaticAddressFromSig("40 55 41 56 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 20 48 8B 15 ?? ?? ?? ?? 4C 8B F1 48 0F BE 42 ?? 85 C0 78 05 83 F8 20 72 0E", 0x25);
            WeatherPtr = Marshal.ReadIntPtr(WeatherPtr) + 0x27;
        }
    }
}