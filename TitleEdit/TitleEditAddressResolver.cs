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
        public static IntPtr LobbyUpdate { get; private set; }
        public static IntPtr LobbyCurrentMap { get; private set; }
        public static IntPtr LobbyThing { get; private set; }

        public static void Setup64Bit()
        {
            LoadLogoResource = DalamudApi.SigScanner.ScanText("E9 ?? ?? ?? ?? 90 5E");
            CameraBase = DalamudApi.SigScanner.GetStaticAddressFromSig("4C 8D 35 ?? ?? ?? ?? 48 8B 09");
            SetTime = DalamudApi.SigScanner.ScanText("40 53 48 83 EC 20 44 0F BF C1 B8 ?? ?? ?? ?? 41 F7 E8 66 89 0D ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? C1 FA 05 8B C2 C1 E8 1F 03 D0");
            CreateScene = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 66 89 1D ?? ?? ?? ?? E9");
            FixOn = DalamudApi.SigScanner.ScanText("C6 81 ?? ?? ?? ?? ?? 8B 02 89 41 60");
            PlayMusic = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 89 47 18 89 5F 20");
            BgmControl = DalamudApi.SigScanner.GetStaticAddressFromSig("48 8B 05 ?? ?? ?? ?? 48 85 C0 74 51 83 78 08 0B");
            WeatherPtrBase = DalamudApi.SigScanner.GetStaticAddressFromSig("4C 8B 05 ?? ?? ?? ?? 41 8B 90 ?? ?? ?? ?? 8B C2 C1 E8 07");
            LobbyUpdate = DalamudApi.SigScanner.ScanText("E8 ?? ?? ?? ?? 80 BF ?? ?? ?? ?? ?? 48 8D 35");
            LobbyCurrentMap = DalamudApi.SigScanner.GetStaticAddressFromSig("66 83 3D ?? ?? ?? ?? ?? 74 0F");
            // LobbyThing = DalamudApi.SigScanner.GetStaticAddressFromSig("48 8B 0D ?? ?? ?? ?? 8B 51 38 2B D3");
        }

        public static int GetGameExpectedTitleScreen()
        {
            unsafe
            {
                return *(int*) (*((nint*)LobbyThing) + 56);
            }
        }

        public static short CurrentLobbyMap
        {
            get => Marshal.ReadInt16(LobbyCurrentMap);
            set => Marshal.WriteInt16(LobbyCurrentMap, value);
        }
    }
}