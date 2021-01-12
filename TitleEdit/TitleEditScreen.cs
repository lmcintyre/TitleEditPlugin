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
        public TitleEditMenuVAlign VAlign = TitleEditMenuVAlign.Default;
        public TitleEditMenuHAlign HAlign = TitleEditMenuHAlign.Default;
        public TitleEditMenuHAlign TextAlign = TitleEditMenuHAlign.Default;
        public float VInset = 0.025f;
        public float HInset = 0.05f;
    }

    public enum TitleEditMenuVAlign {
        Default,
        Top,
        Center,
        Bottom,
    }

    public enum TitleEditMenuHAlign {
        Default,
        Left,
        Center,
        Right
    }
}