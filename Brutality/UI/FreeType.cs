using System.Runtime.InteropServices;

namespace Brutality.UI;

/// <summary>
/// The handful of FreeType calls the font atlas needs. Ahwoo.BRUTAL.FreeType ships only the native library, so these
/// are declared here, with struct layouts taken from the FreeType 2.14 headers in that package. C <c>long</c>
/// (FT_Long, FT_Pos, FT_Fixed) is 4 bytes on Windows and 8 elsewhere, which is what CLong models.
/// </summary>
static unsafe partial class FreeType
{
    const string LIBRARY = "freetype";

    public const int LOAD_RENDER = 1 << 2;

    /// <summary>
    /// FT_LOAD_TARGET_LIGHT: hints vertically only, which keeps glyph shapes closer to the design at small sizes.
    /// </summary>
    public const int LOAD_TARGET_LIGHT = 1 << 16;

    public const byte PIXEL_MODE_GRAY = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Generic
    {
        public nint Data;
        public nint Finalizer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BBox
    {
        public CLong XMin, YMin, XMax, YMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vector
    {
        public CLong X, Y;
    }

    /// <summary>
    /// The public head of FT_FaceRec, up to the fields read here. Only ever used through a pointer from FreeType.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FaceRec
    {
        public CLong NumFaces, FaceIndex, FaceFlags, StyleFlags, NumGlyphs;
        public nint FamilyName, StyleName;
        public int NumFixedSizes;
        public nint AvailableSizes;
        public int NumCharmaps;
        public nint Charmaps;
        public Generic Generic;
        public BBox BBox;
        public ushort UnitsPerEM;
        public short Ascender, Descender, Height;
        public short MaxAdvanceWidth, MaxAdvanceHeight;
        public short UnderlinePosition, UnderlineThickness;
        public GlyphSlotRec* Glyph;
        public SizeRec* Size;
        public nint Charmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeMetrics
    {
        public ushort XPpem, YPpem;
        public CLong XScale, YScale;

        /// <summary>
        /// 26.6 fixed point pixels, like every FT_Pos below.
        /// </summary>
        public CLong Ascender, Descender, Height, MaxAdvance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SizeRec
    {
        public nint Face;
        public Generic Generic;
        public SizeMetrics Metrics;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GlyphMetrics
    {
        public CLong Width, Height;
        public CLong HoriBearingX, HoriBearingY, HoriAdvance;
        public CLong VertBearingX, VertBearingY, VertAdvance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Bitmap
    {
        public uint Rows;
        public uint Width;
        public int Pitch;
        public byte* Buffer;
        public ushort NumGrays;
        public byte PixelMode;
        public byte PaletteMode;
        public nint Palette;
    }

    /// <summary>
    /// The public head of FT_GlyphSlotRec, up to the rendered bitmap and its offsets.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GlyphSlotRec
    {
        public nint Library, Face, Next;
        public uint GlyphIndex;
        public Generic Generic;
        public GlyphMetrics Metrics;
        public CLong LinearHoriAdvance, LinearVertAdvance;
        public Vector Advance;
        public uint Format;
        public Bitmap Bitmap;
        public int BitmapLeft;
        public int BitmapTop;
    }

    [LibraryImport(LIBRARY, EntryPoint = "FT_Init_FreeType")]
    public static partial int InitFreeType(out nint library);

    [LibraryImport(LIBRARY, EntryPoint = "FT_Done_FreeType")]
    public static partial int DoneFreeType(nint library);

    [LibraryImport(LIBRARY, EntryPoint = "FT_New_Memory_Face")]
    public static partial int NewMemoryFace(nint library, byte* data, CLong size, CLong faceIndex, out FaceRec* face);

    [LibraryImport(LIBRARY, EntryPoint = "FT_Done_Face")]
    public static partial int DoneFace(FaceRec* face);

    [LibraryImport(LIBRARY, EntryPoint = "FT_Set_Pixel_Sizes")]
    public static partial int SetPixelSizes(FaceRec* face, uint width, uint height);

    [LibraryImport(LIBRARY, EntryPoint = "FT_Get_Char_Index")]
    public static partial uint GetCharIndex(FaceRec* face, CULong charCode);

    [LibraryImport(LIBRARY, EntryPoint = "FT_Load_Char")]
    public static partial int LoadChar(FaceRec* face, CULong charCode, int loadFlags);

    public static void Check(int error, string call)
    {
        if (error != 0)
            throw new InvalidOperationException($"{call} failed with FreeType error 0x{error:X2}");
    }
}
