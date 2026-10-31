using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Brutality.Audio;

/// <summary>
/// Reads a file's title and artist from the Windows property system, the same place File Explorer gets them. It
/// covers tag formats SDL_mixer doesn't read, such as the iTunes-style tags in .m4a files.
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

    [DllImport("ole32.dll", ExactSpelling = true)]
    static extern int PropVariantClear(IntPtr value);

    /// <summary>
    /// The title and artist, each null if the file doesn't have one or can't be read.
    /// </summary>
    public static (string? Title, string? Artist) Read(string path)
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
            return (null, null);

        try
        {
            return (GetString(store, Title), GetString(store, Artist) ?? GetString(store, AlbumArtist));
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    static string? GetString(IPropertyStore store, PropertyKey key)
    {
        var value = Marshal.AllocCoTaskMem(PROPVARIANT_SIZE);
        try
        {
            new Span<byte>((void*)value, PROPVARIANT_SIZE).Clear();
            if (store.GetValue(key, value) < 0)
                return null;

            try
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
}
