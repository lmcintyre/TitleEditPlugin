using System.Numerics;

namespace TitleEdit
{
    public class TitleEditScreen
    {
        public string Name;
        public string Logo;
        public bool DisplayLogo;
        public string TerritoryPath;
        public Vector3 CameraPos;
        public Vector3 FixOnPos;
        public float FovY;
        public byte WeatherId;
        public ushort TimeOffset;
        public string BgmPath;
        public Vector4? VersionTextColor;
        public Vector4? CopyrightTextColor;
        public Vector4? ButtonTextColor;
        public Vector4? ButtonHighlightColor;
    }
}