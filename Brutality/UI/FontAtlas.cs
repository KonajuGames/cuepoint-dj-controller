using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// One glyph's metrics, in pixels, and where it sits in the atlas.
/// </summary>
readonly record struct Glyph(int2 Size, int2 Bearing, float Advance, float2 UvMin, float2 UvMax);

/// <summary>
/// A typeface at one pixel size. Get one from <see cref="FontAtlas.Add"/>.
/// </summary>
sealed class Font
{
    /// <summary>
    /// Distance from the top of a line to the baseline.
    /// </summary>
    public float Ascender { get; }

    /// <summary>
    /// Distance from one baseline to the next.
    /// </summary>
    public float LineHeight { get; }

    public float PixelSize { get; }

    internal readonly Dictionary<int, Glyph> Glyphs = [];
    internal Glyph Fallback;

    internal Font(float pixelSize, float ascender, float lineHeight)
    {
        PixelSize = pixelSize;
        Ascender = ascender;
        LineHeight = lineHeight;
    }

    internal Glyph GetGlyph(int codePoint) => Glyphs.GetValueOrDefault(codePoint, Fallback);

    /// <summary>
    /// The size of the box <paramref name="text"/> fills when drawn, with '\n' starting a new line.
    /// </summary>
    public float2 Measure(ReadOnlySpan<char> text)
    {
        float width = 0, lineWidth = 0;
        var lines = 1;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                width = MathF.Max(width, lineWidth);
                lineWidth = 0;
                lines++;
                continue;
            }
            lineWidth += GetGlyph(rune.Value).Advance;
        }
        return new float2(MathF.Ceiling(MathF.Max(width, lineWidth)), lines * LineHeight);
    }
}

/// <summary>
/// Rasterises fonts with FreeType and packs their glyphs, plus a white texel for solid fills, into one single-channel
/// coverage texture. Add every font first; <see cref="Build"/> then lays out the texture and fixes each glyph's UVs.
/// </summary>
sealed unsafe class FontAtlas
{
    /// <summary>
    /// Printable ASCII and Latin-1, plus a little typographic punctuation.
    /// </summary>
    static readonly int[] DefaultCharacters =
    [
        .. Enumerable.Range(0x20, 0x7F - 0x20),
        .. Enumerable.Range(0xA0, 0x100 - 0xA0),
        0x2013, // en dash
        0x2014, // em dash
        0x2018, 0x2019, 0x201C, 0x201D, // curly quotes
        0x2022, // bullet
        0x2026, // ellipsis
        0xFFFD, // replacement character, used for anything missing
    ];

    const int PADDING = 1;
    const int WHITE_SIZE = 3;

    /// <summary>
    /// A rasterised glyph waiting for its place in the atlas.
    /// </summary>
    sealed record PendingGlyph(Font Font, int CodePoint, int2 Size, int2 Bearing, float Advance, byte[] Coverage);

    readonly List<PendingGlyph> _pending = [];
    readonly List<Font> _fonts = [];

    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[]? Pixels { get; private set; }

    /// <summary>
    /// The UV of a texel that's fully covered, so solid rectangles share the text pipeline.
    /// </summary>
    public float2 WhiteUv { get; private set; }

    /// <summary>
    /// Rasterises <paramref name="path"/> (a TTF, OTF or TTC file) at <paramref name="pixelSize"/> pixels per em.
    /// </summary>
    public Font Add(string path, float pixelSize, IEnumerable<int>? characters = null)
    {
        if (Pixels is not null)
            throw new InvalidOperationException("Fonts can't be added after the atlas is built");

        var file = File.ReadAllBytes(path);
        FreeType.Check(FreeType.InitFreeType(out var library), "FT_Init_FreeType");
        try
        {
            fixed (byte* data = file)
            {
                FreeType.Check(
                    FreeType.NewMemoryFace(library, data, new CLong(file.Length), new CLong(0), out var face),
                    $"FT_New_Memory_Face({path})"
                );
                try
                {
                    var size = (uint)MathF.Round(pixelSize);
                    FreeType.Check(FreeType.SetPixelSizes(face, 0, size), "FT_Set_Pixel_Sizes");

                    var metrics = face->Size->Metrics;
                    var font = new Font(
                        size,
                        MathF.Round(metrics.Ascender.Value / 64f),
                        MathF.Round(metrics.Height.Value / 64f)
                    );

                    foreach (var codePoint in characters ?? DefaultCharacters)
                    {
                        // Skip characters the face doesn't have instead of baking its .notdef box for each.
                        if (FreeType.GetCharIndex(face, new CULong((uint)codePoint)) == 0)
                            continue;

                        FreeType.Check(
                            FreeType.LoadChar(
                                face,
                                new CULong((uint)codePoint),
                                FreeType.LOAD_RENDER | FreeType.LOAD_TARGET_LIGHT
                            ),
                            "FT_Load_Char"
                        );
                        _pending.Add(ReadGlyph(font, codePoint, face->Glyph));
                    }

                    _fonts.Add(font);
                    return font;
                }
                finally
                {
                    FreeType.DoneFace(face);
                }
            }
        }
        finally
        {
            FreeType.DoneFreeType(library);
        }
    }

    static PendingGlyph ReadGlyph(Font font, int codePoint, FreeType.GlyphSlotRec* slot)
    {
        ref var bitmap = ref slot->Bitmap;
        if (bitmap.Width > 0 && bitmap.PixelMode != FreeType.PIXEL_MODE_GRAY)
            throw new NotSupportedException($"Glyph U+{codePoint:X4} rendered as pixel mode {bitmap.PixelMode}");

        var width = (int)bitmap.Width;
        var height = (int)bitmap.Rows;
        var coverage = new byte[width * height];

        // Rows can be padded. Grey bitmaps from FT_Load_Char are always stored top-down (positive pitch).
        if (bitmap.Pitch < 0)
            throw new NotSupportedException($"Glyph U+{codePoint:X4} has a bottom-up bitmap");
        for (var y = 0; y < height; y++)
            new ReadOnlySpan<byte>(bitmap.Buffer + y * bitmap.Pitch, width).CopyTo(coverage.AsSpan(y * width));

        return new PendingGlyph(
            font,
            codePoint,
            new int2(width, height),
            new int2(slot->BitmapLeft, slot->BitmapTop),
            slot->Advance.X.Value / 64f,
            coverage
        );
    }

    /// <summary>
    /// Packs every glyph into rows, tallest first, in the narrowest power-of-two texture that fits.
    /// </summary>
    public void Build()
    {
        if (Pixels is not null)
            return;

        var glyphs = _pending.OrderByDescending(g => g.Size.Y).ThenByDescending(g => g.Size.X).ToList();
        var area = glyphs.Sum(g => (g.Size.X + PADDING) * (g.Size.Y + PADDING));
        Width = (int)Math.Max(128, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)MathF.Ceiling(MathF.Sqrt(area))));

        // Lay out rows: the white block first, then glyphs left to right.
        var positions = new int2[glyphs.Count];
        int x = WHITE_SIZE + PADDING, y = 0, rowHeight = WHITE_SIZE;
        for (var i = 0; i < glyphs.Count; i++)
        {
            var size = glyphs[i].Size;
            if (x + size.X > Width)
            {
                y += rowHeight + PADDING;
                x = 0;
                rowHeight = 0;
            }
            positions[i] = new int2(x, y);
            x += size.X + PADDING;
            rowHeight = Math.Max(rowHeight, size.Y);
        }
        Height = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(y + rowHeight));

        Pixels = new byte[Width * Height];
        for (var row = 0; row < WHITE_SIZE; row++)
            Pixels.AsSpan(row * Width, WHITE_SIZE).Fill(255);
        WhiteUv = new float2(WHITE_SIZE / 2f, WHITE_SIZE / 2f) / new float2(Width, Height); // Centre of the block.

        var texel = new float2(1f / Width, 1f / Height);
        for (var i = 0; i < glyphs.Count; i++)
        {
            var glyph = glyphs[i];
            var position = positions[i];
            for (var row = 0; row < glyph.Size.Y; row++)
                glyph
                    .Coverage.AsSpan(row * glyph.Size.X, glyph.Size.X)
                    .CopyTo(Pixels.AsSpan((position.Y + row) * Width + position.X));

            glyph.Font.Glyphs[glyph.CodePoint] = new Glyph(
                glyph.Size,
                glyph.Bearing,
                glyph.Advance,
                new float2(position.X, position.Y) * texel,
                new float2(position.X + glyph.Size.X, position.Y + glyph.Size.Y) * texel
            );
        }

        foreach (var font in _fonts)
            font.Fallback = font.Glyphs.GetValueOrDefault(0xFFFD, font.Glyphs.GetValueOrDefault('?'));

        _pending.Clear();
    }

    /// <summary>
    /// The path of a common sans-serif font installed with the OS, or null if none of the usual ones are there.
    /// </summary>
    public static string? FindSystemFont(bool bold = false)
    {
        string[] candidates;
        if (OperatingSystem.IsWindows())
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            candidates = bold
                ? [Path.Combine(fonts, "segoeuib.ttf"), Path.Combine(fonts, "arialbd.ttf")]
                : [Path.Combine(fonts, "segoeui.ttf"), Path.Combine(fonts, "arial.ttf")];
        }
        else if (OperatingSystem.IsMacOS())
            candidates = bold
                ? ["/System/Library/Fonts/Supplemental/Arial Bold.ttf", "/Library/Fonts/Arial Bold.ttf"]
                : ["/System/Library/Fonts/Supplemental/Arial.ttf", "/Library/Fonts/Arial.ttf"];
        else
            candidates = bold
                ?
                [
                    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
                    "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf",
                    "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans-Bold.ttf",
                    "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
                ]
                :
                [
                    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                    "/usr/share/fonts/TTF/DejaVuSans.ttf",
                    "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf",
                    "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
                ];

        return candidates.FirstOrDefault(File.Exists);
    }
}
