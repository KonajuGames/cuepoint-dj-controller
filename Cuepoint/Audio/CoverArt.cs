using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuepoint.Audio;

/// <summary>
/// A song's cover picture, small, as rows of sRGB RGBA pixels from the top (alpha not premultiplied), ready to hand to
/// the renderer.
/// </summary>
sealed record CoverPicture(int Width, int Height, byte[] Rgba);

/// <summary>
/// Reads the cover picture embedded in a song's tags (an MP3's or M4A's, and a FLAC's where Windows reads FLAC tags)
/// from the Windows property system, as File Explorer does, and decodes it with the Windows Imaging Component. The
/// covers are nearly always JPEGs, which the app can't decode itself. Explorer's thumbnails can't stand in: they make
/// up a picture for a song without a cover, and give none for some M4As with one. Safe to call from any thread, but
/// it reads the file, so it's for a worker thread rather than the UI's.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe class CoverArt
{
    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    /// <summary>
    /// The embedded picture's file (a JPEG or PNG) as a stream, from propkey.h.
    /// </summary>
    static readonly PropertyKey ThumbnailStream = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 27);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(in PropertyKey key, IntPtr value);
    }

    const int GPS_DEFAULT = 0;

    /// <summary>
    /// A PROPVARIANT is 16 bytes on 32-bit Windows and 24 on 64-bit; this covers both. Its type comes first, and its
    /// value (here, the stream) 8 bytes in.
    /// </summary>
    const int PROPVARIANT_SIZE = 24;

    const ushort VT_STREAM = 66;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int SHGetPropertyStoreFromParsingName(
        string path,
        IntPtr bindContext,
        int flags,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store
    );

    [DllImport("ole32.dll", ExactSpelling = true)]
    static extern int PropVariantClear(IntPtr value);

    // The Windows Imaging Component's interfaces, from wincodec.h, as far as the methods used; a method not used but
    // before one that is keeps its place with no arguments.

    [ComImport]
    [Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICImagingFactory
    {
        void CreateDecoderFromFilename();

        [PreserveSig]
        int CreateDecoderFromStream(IntPtr stream, IntPtr vendor, int options, out IWICBitmapDecoder decoder);

        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();

        [PreserveSig]
        int CreateFormatConverter(out IWICFormatConverter converter);

        [PreserveSig]
        int CreateBitmapScaler(out IWICBitmapScaler scaler);
    }

    [ComImport]
    [Guid("9EDDE9E7-8DEE-47EA-99DF-E6FAF2ED44BF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICBitmapDecoder
    {
        void QueryCapability();
        void Initialize();
        void GetContainerFormat();
        void GetDecoderInfo();
        void CopyPalette();
        void GetMetadataQueryReader();
        void GetPreview();
        void GetColorContexts();
        void GetThumbnail();
        void GetFrameCount();

        [PreserveSig]
        int GetFrame(uint index, out IWICBitmapSource frame);
    }

    /// <summary>
    /// Pixels to be had: a frame decoded, scaled or converted.
    /// </summary>
    [ComImport]
    [Guid("00000120-A8F2-4877-BA0A-FD2B6645FB94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICBitmapSource
    {
        [PreserveSig]
        int GetSize(out uint width, out uint height);

        void GetPixelFormat();
        void GetResolution();
        void CopyPalette();

        [PreserveSig]
        int CopyPixels(IntPtr rect, uint stride, uint size, byte* pixels);
    }

    [ComImport]
    [Guid("00000302-A8F2-4877-BA0A-FD2B6645FB94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICBitmapScaler
    {
        void GetSize();
        void GetPixelFormat();
        void GetResolution();
        void CopyPalette();
        void CopyPixels();

        [PreserveSig]
        int Initialize(IWICBitmapSource source, uint width, uint height, int mode);
    }

    [ComImport]
    [Guid("00000301-A8F2-4877-BA0A-FD2B6645FB94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICFormatConverter
    {
        void GetSize();
        void GetPixelFormat();
        void GetResolution();
        void CopyPalette();
        void CopyPixels();

        [PreserveSig]
        int Initialize(IWICBitmapSource source, in Guid format, int dither, IntPtr palette, double alphaThreshold, int paletteType);
    }

    static readonly Guid ImagingFactory = new("CACAF262-9370-4615-A13B-9F5539DA4C0A");

    /// <summary>
    /// GUID_WICPixelFormat32bppRGBA: 8 bits each of red, green, blue and alpha, not premultiplied.
    /// </summary>
    static readonly Guid Rgba = new("F5C7AD2D-6A8D-43DD-A7A8-A29935261AE9");

    const int WICDecodeMetadataCacheOnDemand = 0;

    /// <summary>
    /// Averages every source pixel into the one it shrinks to, which keeps a picture's detail when it's made much
    /// smaller.
    /// </summary>
    const int WICBitmapInterpolationModeFant = 3;

    const int WICBitmapDitherTypeNone = 0;
    const int WICBitmapPaletteTypeCustom = 0;

    /// <summary>
    /// The cover of the song at <paramref name="path"/>, scaled down (never up) to fit a square
    /// <paramref name="size"/> pixels a side, keeping its shape; null if it hasn't one, or it can't be read.
    /// </summary>
    public static CoverPicture? Read(string path, int size)
    {
        // The shell wants a full path with backslashes.
        if (SHGetPropertyStoreFromParsingName(Path.GetFullPath(path), IntPtr.Zero, GPS_DEFAULT, typeof(IPropertyStore).GUID, out var store) < 0)
            return null;

        var value = Marshal.AllocCoTaskMem(PROPVARIANT_SIZE);
        try
        {
            new Span<byte>((void*)value, PROPVARIANT_SIZE).Clear();
            if (store.GetValue(ThumbnailStream, value) < 0)
                return null;
            try
            {
                // Empty when the song has no cover.
                var stream = Marshal.ReadIntPtr(value, 8);
                return (ushort)Marshal.ReadInt16(value) == VT_STREAM && stream != IntPtr.Zero ? Decode(stream, size) : null;
            }
            finally
            {
                PropVariantClear(value);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// Decodes the picture file in <paramref name="stream"/> (an IStream), scaled down to fit
    /// <paramref name="size"/>, as RGBA; null if it won't decode.
    /// </summary>
    static CoverPicture? Decode(IntPtr stream, int size)
    {
        // Each object made is released at the end, whether or not it gets that far.
        var made = new List<object>();
        T Keep<T>(T item)
            where T : class
        {
            made.Add(item);
            return item;
        }

        try
        {
            var factory = Keep((IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(ImagingFactory, throwOnError: true)!)!);
            if (factory.CreateDecoderFromStream(stream, IntPtr.Zero, WICDecodeMetadataCacheOnDemand, out var decoder) < 0)
                return null;
            Keep(decoder);
            if (decoder.GetFrame(0, out var frame) < 0)
                return null;
            Keep(frame);
            if (frame.GetSize(out var width, out var height) < 0 || width == 0 || height == 0)
                return null;

            var (fitWidth, fitHeight) = Fit((int)width, (int)height, size);
            var picture = frame;
            if (fitWidth != width || fitHeight != height)
            {
                if (factory.CreateBitmapScaler(out var scaler) < 0)
                    return null;
                Keep(scaler);
                if (scaler.Initialize(frame, (uint)fitWidth, (uint)fitHeight, WICBitmapInterpolationModeFant) < 0)
                    return null;
                picture = (IWICBitmapSource)scaler;
            }

            if (factory.CreateFormatConverter(out var converter) < 0)
                return null;
            Keep(converter);
            if (converter.Initialize(picture, Rgba, WICBitmapDitherTypeNone, IntPtr.Zero, 0, WICBitmapPaletteTypeCustom) < 0)
                return null;

            var pixels = new byte[fitWidth * fitHeight * 4];
            fixed (byte* bits = pixels)
                if (((IWICBitmapSource)converter).CopyPixels(IntPtr.Zero, (uint)fitWidth * 4, (uint)pixels.Length, bits) < 0)
                    return null;
            return new CoverPicture(fitWidth, fitHeight, pixels);
        }
        finally
        {
            for (var i = made.Count - 1; i >= 0; i--)
                Marshal.ReleaseComObject(made[i]);
        }
    }

    /// <summary>
    /// A picture's size scaled down, if it's bigger, to fit a square <paramref name="size"/> pixels a side, keeping
    /// its shape.
    /// </summary>
    internal static (int Width, int Height) Fit(int width, int height, int size)
    {
        if (width <= size && height <= size)
            return (width, height);
        var scale = (double)size / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}
