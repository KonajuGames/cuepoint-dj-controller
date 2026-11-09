using System.Reflection;
using Brutal.Numerics;

namespace Brutality.UI;

/// <summary>
/// The app's pictures, packed into the atlas with the fonts: the logo, and small white icons for tinting, among them
/// the triangles drawn for arrows, the options button's cog, the headphones button, the search box's cross and the
/// folder list's icons, the last two again larger for the folder list's large text; and the mixer's knob caps, in
/// colour, with the dot their arcs and pointers are drawn with. Each is a PNG built into the app (see
/// Assets\draw_assets.py).
/// </summary>
sealed record Images(
    Image Logo,
    Image Played,
    Image TriangleRight,
    Image TriangleDown,
    Image TriangleLeft,
    Image Cog,
    Image Cross,
    FolderIcons Folders,
    Image LargeTriangleRight,
    Image LargeTriangleDown,
    FolderIcons LargeFolders,
    Image Headphones,
    Image Knob,
    Image LargeKnob,
    Image Dot,
    Image Pause
)
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
            Cog: atlas.AddImage(Read("cog-18.png")),
            Cross: atlas.AddImage(Read("cross-10.png")),
            Folders: FolderIcons.Load(atlas, 14),
            LargeTriangleRight: atlas.AddImage(Read("triangle-right-14.png")),
            LargeTriangleDown: atlas.AddImage(Read("triangle-down-14.png")),
            LargeFolders: FolderIcons.Load(atlas, 20),
            Headphones: atlas.AddImage(Read("headphones-18.png")),
            Knob: atlas.AddImage(Read("knob-18.png")),
            LargeKnob: atlas.AddImage(Read("knob-32.png")),
            Dot: atlas.AddImage(Read("dot-8.png")),
            Pause: atlas.AddImage(Read("pause-14.png"))
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
/// The folder list's icons, one for each kind of thing in it.
/// </summary>
sealed record FolderIcons(Image Library, Image Drive, Image Folder, Image Playlist, Image List, Image History, Image Filter)
{
    /// <summary>
    /// Adds the icons drawn <paramref name="size"/> pixels square to <paramref name="atlas"/>.
    /// </summary>
    public static FolderIcons Load(FontAtlas atlas, int size) =>
        new(
            Library: atlas.AddImage(Images.Read($"icon-library-{size}.png")),
            Drive: atlas.AddImage(Images.Read($"icon-drive-{size}.png")),
            Folder: atlas.AddImage(Images.Read($"icon-folder-{size}.png")),
            Playlist: atlas.AddImage(Images.Read($"icon-playlist-{size}.png")),
            List: atlas.AddImage(Images.Read($"icon-list-{size}.png")),
            History: atlas.AddImage(Images.Read($"icon-history-{size}.png")),
            Filter: atlas.AddImage(Images.Read($"icon-filter-{size}.png"))
        );
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
