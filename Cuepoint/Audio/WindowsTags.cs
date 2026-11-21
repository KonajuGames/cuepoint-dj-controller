using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cuepoint.Audio;

/// <summary>
/// A song's details as tagged; each is null if the file doesn't have it.
/// </summary>
readonly record struct SongTags(string? Title, string? Artist, string? Album, TimeSpan? Duration);

/// <summary>
/// Reads a file's tags from the Windows property system, the same place File Explorer gets them. It covers tag
/// formats SDL_mixer doesn't read, such as the iTunes-style tags in .m4a files, and gives a length without decoding.
/// Safe to call from any thread.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe class WindowsTags
{
    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    // From propkey.h.
    static readonly PropertyKey Title = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);
    static readonly PropertyKey Artist = new(new Guid("56A3372E-CE9C-11D2-9F0E-006097C686F6"), 2);
    static readonly PropertyKey AlbumArtist = new(new Guid("56A3372E-CE9C-11D2-9F0E-006097C686F6"), 13);
    static readonly PropertyKey Album = new(new Guid("56A3372E-CE9C-11D2-9F0E-006097C686F6"), 4);

    /// <summary>
    /// A UInt64 in 100-nanosecond units, the same as a TimeSpan's ticks.
    /// </summary>
    static readonly PropertyKey Duration = new(new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);

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

        [PreserveSig]
        int SetValue(in PropertyKey key, IntPtr value);

        [PreserveSig]
        int Commit();
    }

    const int GPS_DEFAULT = 0;

    /// <summary>
    /// A PROPVARIANT is 16 bytes on 32-bit Windows and 24 on 64-bit; this covers both.
    /// </summary>
    const int PROPVARIANT_SIZE = 24;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int SHGetPropertyStoreFromParsingName(
        string path,
        IntPtr bindContext,
        int flags,
        in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store
    );

    /// <summary>
    /// Converts any PROPVARIANT to text; a list of artists comes back joined with "; ".
    /// </summary>
    [DllImport("propsys.dll", ExactSpelling = true)]
    static extern int PropVariantToStringAlloc(IntPtr value, out IntPtr text);

    [DllImport("propsys.dll", ExactSpelling = true)]
    static extern int PropVariantToUInt64(IntPtr value, out ulong number);

    [DllImport("ole32.dll", ExactSpelling = true)]
    static extern int PropVariantClear(IntPtr value);

    /// <summary>
    /// The file's tags; all null if it can't be read.
    /// </summary>
    public static SongTags Read(string path)
    {
        // The shell wants a full path with backslashes.
        var hr = SHGetPropertyStoreFromParsingName(
            Path.GetFullPath(path),
            IntPtr.Zero,
            GPS_DEFAULT,
            typeof(IPropertyStore).GUID,
            out var store
        );
        if (hr < 0)
            return default;

        try
        {
            var ticks = GetValue(store, Duration, ToTicks);
            return new SongTags(
                GetValue(store, Title, ToText),
                GetValue(store, Artist, ToText) ?? GetValue(store, AlbumArtist, ToText),
                GetValue(store, Album, ToText),
                ticks is > 0 ? TimeSpan.FromTicks(ticks.Value) : null
            );
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// Reads one property and converts it with <paramref name="convert"/>; null if it's missing or won't convert.
    /// </summary>
    static T? GetValue<T>(IPropertyStore store, PropertyKey key, Func<IntPtr, T?> convert)
    {
        var value = Marshal.AllocCoTaskMem(PROPVARIANT_SIZE);
        try
        {
            new Span<byte>((void*)value, PROPVARIANT_SIZE).Clear();
            if (store.GetValue(key, value) < 0)
                return default;

            try
            {
                return convert(value);
            }
            finally
            {
                PropVariantClear(value);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    static string? ToText(IntPtr value)
    {
        if (PropVariantToStringAlloc(value, out var text) < 0)
            return null;
        try
        {
            var result = Marshal.PtrToStringUni(text);
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    static long? ToTicks(IntPtr value) => PropVariantToUInt64(value, out var ticks) < 0 ? null : (long)ticks;

    const int GPS_READWRITE = 2;
    const ushort VT_LPWSTR = 31;
    const ushort VT_VECTOR = 0x1000;

    /// <summary>
    /// Writes <paramref name="title"/>, <paramref name="artist"/> and <paramref name="album"/> into the file's tags,
    /// as File Explorer's details pane does; any that's blank is left as it was. Returns why it couldn't, if it
    /// couldn't: a kind of file Windows can't write tags to, or one that's read-only or in use.
    /// </summary>
    public static string? Write(string path, string? title, string? artist, string? album)
    {
        var hr = SHGetPropertyStoreFromParsingName(
            Path.GetFullPath(path),
            IntPtr.Zero,
            GPS_READWRITE,
            typeof(IPropertyStore).GUID,
            out var store
        );
        if (hr < 0)
            return $"Windows can't change the details of {Path.GetExtension(path)} files, or this one's read-only or in use";

        try
        {
            if (!string.IsNullOrWhiteSpace(title) && SetText(store, Title, title.Trim()) is { } titleError)
                return titleError;
            // An artist is a list of them, to Windows.
            if (!string.IsNullOrWhiteSpace(artist) && SetText(store, Artist, artist.Trim(), list: true) is { } artistError)
                return artistError;
            if (!string.IsNullOrWhiteSpace(album) && SetText(store, Album, album.Trim()) is { } albumError)
                return albumError;
            hr = store.Commit();
            return hr < 0 ? $"Couldn't save the file's details (error 0x{hr:X8}); it may be read-only or in use" : null;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// Sets one property to <paramref name="text"/>, or with <paramref name="list"/>, to a list of just it. Returns
    /// why it couldn't, if it couldn't.
    /// </summary>
    static string? SetText(IPropertyStore store, PropertyKey key, string text, bool list = false)
    {
        var value = Marshal.AllocCoTaskMem(PROPVARIANT_SIZE);
        try
        {
            // A PROPVARIANT: its type, then (8 bytes in) the string, or a count and a pointer to the strings.
            var bytes = new Span<byte>((void*)value, PROPVARIANT_SIZE);
            bytes.Clear();
            var chars = Marshal.StringToCoTaskMemUni(text);
            if (list)
            {
                var items = Marshal.AllocCoTaskMem(IntPtr.Size);
                Marshal.WriteIntPtr(items, chars);
                Marshal.WriteInt16(value, (short)(VT_VECTOR | VT_LPWSTR));
                Marshal.WriteInt32(value, 8, 1);
                Marshal.WriteIntPtr(value, 8 + IntPtr.Size, items);
            }
            else
            {
                Marshal.WriteInt16(value, (short)VT_LPWSTR);
                Marshal.WriteIntPtr(value, 8, chars);
            }
            try
            {
                var hr = store.SetValue(key, value);
                return hr < 0 ? $"Windows wouldn't take that for this file (error 0x{hr:X8})" : null;
            }
            finally
            {
                // Frees the string, and the list holding it, as they were made with CoTaskMemAlloc.
                PropVariantClear(value);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }
}
