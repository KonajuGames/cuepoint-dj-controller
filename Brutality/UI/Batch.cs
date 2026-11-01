using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// The vertex layout of the UI pipeline: a position in pixels from the top-left of the screen, a UV into the font
/// atlas, and a linear-space colour with alpha.
/// </summary>
struct Vertex(float2 position, float2 uv, float4 color)
{
    public float2 Position = position;
    public float2 Uv = uv;
    public float4 Color = color;
}

/// <summary>
/// Colours for the UI. Written as sRGB (as colour pickers and hex codes are) and converted to the linear values the
/// sRGB swapchain expects.
/// </summary>
static class Colors
{
    /// <summary>
    /// A colour from a 0xRRGGBB hex value.
    /// </summary>
    public static float4 Hex(uint rgb, float alpha = 1)
    {
        return new float4(
            ToLinear((rgb >> 16) & 0xFF),
            ToLinear((rgb >> 8) & 0xFF),
            ToLinear(rgb & 0xFF),
            alpha
        );
    }

    static float ToLinear(uint channel)
    {
        var c = channel / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    public static readonly float4 White = Hex(0xFFFFFF);
    public static readonly float4 Transparent = new(0, 0, 0, 0);
}

/// <summary>
/// Collects the triangles for one frame of UI: solid rectangles, text and images, drawn in the order they're added.
/// </summary>
sealed class Batch
{
    readonly List<Vertex> _vertices = [];
    readonly FontAtlas _atlas;

    public Batch(FontAtlas atlas) => _atlas = atlas;

    /// <summary>
    /// Clip rectangles as (min, max) corners; the top one applies to everything added.
    /// </summary>
    readonly Stack<(float2 Min, float2 Max)> _clips = [];

    public ReadOnlySpan<Vertex> Vertices => CollectionsMarshal.AsSpan(_vertices);

    public void Clear()
    {
        _vertices.Clear();
        _clips.Clear();
    }

    /// <summary>
    /// Clips everything added until the matching <see cref="PopClip"/> to this rectangle, within any clip already
    /// in effect.
    /// </summary>
    public void PushClip(float2 position, float2 size)
    {
        var min = position;
        var max = position + size;
        if (_clips.TryPeek(out var outer))
        {
            min = float2.Max(min, outer.Min);
            max = float2.Min(max, outer.Max);
        }
        _clips.Push((min, float2.Max(min, max)));
    }

    public void PopClip() => _clips.Pop();

    /// <summary>
    /// The clip rectangle in effect as (min, max) corners, or null if nothing is clipped. Elements with many parts,
    /// such as a long list, use it to skip drawing what can't be seen.
    /// </summary>
    public (float2 Min, float2 Max)? ClipBounds => _clips.TryPeek(out var clip) ? clip : null;

    public void AddRect(float2 position, float2 size, float4 color)
    {
        if (color.W <= 0 || size.X <= 0 || size.Y <= 0)
            return;
        AddQuad(position, position + size, _atlas.WhiteUv, _atlas.WhiteUv, color);
    }

    /// <summary>
    /// A rectangle outline <paramref name="thickness"/> pixels wide, drawn inside the given bounds.
    /// </summary>
    public void AddBorder(float2 position, float2 size, float thickness, float4 color)
    {
        if (thickness <= 0)
            return;
        var t = MathF.Min(thickness, MathF.Min(size.X, size.Y) / 2);
        AddRect(position, new float2(size.X, t), color);
        AddRect(new float2(position.X, position.Y + size.Y - t), new float2(size.X, t), color);
        AddRect(new float2(position.X, position.Y + t), new float2(t, size.Y - 2 * t), color);
        AddRect(new float2(position.X + size.X - t, position.Y + t), new float2(t, size.Y - 2 * t), color);
    }

    /// <summary>
    /// Draws <paramref name="text"/> with its top-left at <paramref name="position"/>; '\n' starts a new line.
    /// </summary>
    public void AddText(Font font, ReadOnlySpan<char> text, float2 position, float4 color)
    {
        // Whole pixels, so glyphs land on texel boundaries and stay sharp.
        var origin = new float2(MathF.Round(position.X), MathF.Round(position.Y));
        var penX = origin.X;
        var baseline = origin.Y + font.Ascender;

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                penX = origin.X;
                baseline += font.LineHeight;
                continue;
            }

            var glyph = font.GetGlyph(rune.Value);
            if (glyph.Size.X > 0 && glyph.Size.Y > 0)
            {
                var min = new float2(MathF.Round(penX) + glyph.Bearing.X, baseline - glyph.Bearing.Y);
                AddQuad(min, min + new float2(glyph.Size.X, glyph.Size.Y), glyph.UvMin, glyph.UvMax, color);
            }
            penX += glyph.Advance;
        }
    }

    /// <summary>
    /// Draws <paramref name="image"/> with its top-left at <paramref name="position"/>, at its own size unless given
    /// another, times <paramref name="tint"/>: white leaves a colour image as it is, and colours a white icon.
    /// </summary>
    public void AddImage(Image image, float2 position, float4 tint, float2? size = null)
    {
        // Whole pixels at its own size, so it maps texel for texel and stays sharp.
        var drawn = size ?? new float2(image.Size.X, image.Size.Y);
        if (size is null)
            position = new float2(MathF.Round(position.X), MathF.Round(position.Y));
        if (tint.W <= 0 || drawn.X <= 0 || drawn.Y <= 0)
            return;
        AddQuad(position, position + drawn, image.UvMin, image.UvMax, tint);
    }

    void AddQuad(float2 min, float2 max, float2 uvMin, float2 uvMax, float4 color)
    {
        // Every quad is axis-aligned, so clipping is just trimming it and its UVs by the same fraction.
        if (_clips.TryPeek(out var clip))
        {
            var clippedMin = float2.Max(min, clip.Min);
            var clippedMax = float2.Min(max, clip.Max);
            if (clippedMin.X >= clippedMax.X || clippedMin.Y >= clippedMax.Y)
                return;

            var size = max - min;
            var uvSize = uvMax - uvMin;
            (uvMin, uvMax) = (uvMin + (clippedMin - min) / size * uvSize, uvMin + (clippedMax - min) / size * uvSize);
            (min, max) = (clippedMin, clippedMax);
        }

        var topRight = new Vertex(new float2(max.X, min.Y), new float2(uvMax.X, uvMin.Y), color);
        var bottomLeft = new Vertex(new float2(min.X, max.Y), new float2(uvMin.X, uvMax.Y), color);

        _vertices.Add(new Vertex(min, uvMin, color));
        _vertices.Add(topRight);
        _vertices.Add(bottomLeft);
        _vertices.Add(bottomLeft);
        _vertices.Add(topRight);
        _vertices.Add(new Vertex(max, uvMax, color));
    }
}
