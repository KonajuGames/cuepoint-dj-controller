using System.Reflection;
using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// The app's pictures, packed into the atlas with the fonts: the logo, and small white icons for tinting, among them
/// the triangles drawn for arrows and the options button's cog. Each is a PNG built into the app (see
/// Assets\draw_assets.py).
/// </summary>
sealed record Images(Image Logo, Image Played, Image TriangleRight, Image TriangleDown, Image TriangleLeft, Image Cog)
{
    /// <summary>
    /// Adds every picture to <paramref name="atlas"/>, which mustn't be built yet.
    /// </summary>
    public static Images Load(FontAtlas atlas) =>
        new(
            Logo: atlas.AddImage(Read("logo-22.png")),
            Played: atlas.AddImage(Read("played-14.png")),
            TriangleRight: atlas.AddImage(Read("triangle-right-10.png")),
            TriangleDown: atlas.AddImage(Read("triangle-down-10.png")),
            TriangleLeft: atlas.AddImage(Read("triangle-left-10.png")),
            Cog: atlas.AddImage(Read("cog-18.png"))
        );

    /// <summary>
    /// A PNG built into the app, decoded. Throws if it's missing, which would mean a broken build.
    /// </summary>
    public static Png.Image Read(string name)
    {
        using var stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"The app is missing its image {name}");
        return Png.Decode(stream);
    }
}

/// <summary>
/// An image on its own, at its own size, tinted (white shows it as it is).
/// </summary>
sealed class Picture(Image image) : Element
{
    public Image Image { get; set; } = image;
    public float4 Tint { get; set; } = Colors.White;

    public override float2 Measure() => Size = new float2(Image.Size.X, Image.Size.Y);

    public override void Draw(Batch batch) => batch.AddImage(Image, ScreenPosition, Tint);
}
