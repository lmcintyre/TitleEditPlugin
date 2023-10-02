using System;
using System.Runtime.InteropServices;

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

        public static IntPtr WeatherPtr => Marshal.ReadIntPtr(WeatherPtrBase) + 0x27;

        public static IntPtr LoadLogoResource { get; private set; }
        public static IntPtr SetTime { get; private set; }
        public static IntPtr CreateScene { get; private set; }
        public static IntPtr FixOn { get; private set; }
        public static IntPtr PlayMusic { get; private set; }
        public static IntPtr BgmControl { get; private set; }
        public static IntPtr WeatherPtrBase { get; private set; }

        public static void Setup64Bit()
        {
            LoadLogoResource = DalamudApi.SigScanner.ScanText("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 41 8B F9 41 0F B6 F0 48 8B D9 48 85 D2 75 12");
            CameraBase = DalamudApi.SigScanner.GetStaticAddressFromSig("4C 8D 35 ?? ?? ?? ?? 85 D2");
            SetTime = DalamudApi.SigScanner.ScanText("40 53 48 83 EC 20 44 0F BF C1 B8 ?? ?? ?? ?? 41 F7 E8 66 89 0D ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? C1 FA 05 8B C2 C1 E8 1F 03 D0");
            CreateScene = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 66 89 1D ?? ?? ?? ?? E9 ?? ?? ?? ??");
            FixOn = DalamudApi.SigScanner.ScanText("C6 81 ?? ?? ?? ?? ?? 8B 02 89 41 60");
            PlayMusic = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 89 47 18 89 5F 20");
            BgmControl = DalamudApi.SigScanner.GetStaticAddressFromSig("48 8B 05 ?? ?? ?? ?? 48 85 C0 74 37 83 78 08 04");
            WeatherPtrBase = DalamudApi.SigScanner.GetStaticAddressFromSig("48 8B 05 ?? ?? ?? ?? 48 8B D9 0F 29 7C 24 ?? 41 8B FF");
        }
    }
}