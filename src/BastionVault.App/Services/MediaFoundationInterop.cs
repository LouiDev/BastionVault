using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace BastionVault.App.Services;

/// <summary>
/// The slice of Media Foundation the video thumbnailer needs, declared by hand so the app takes no
/// dependency on a Windows SDK projection. Every interface is declared with its full vtable in
/// order; a slot the app never calls is still declared, because COM dispatches by position.
/// All methods return the raw <c>HRESULT</c> (<see cref="PreserveSigAttribute"/>) so failures are
/// decisions, not exceptions, on this attacker-facing path.
/// </summary>
internal static class MediaFoundationInterop
{
    /// <summary><c>MF_VERSION</c>: SDK version 2 in the high word, API version 0x70 in the low word.</summary>
    public const uint ApiVersion = 0x0002_0070;

    public const uint FirstVideoStream = 0xFFFFFFFC;
    public const uint AllStreams = 0xFFFFFFFE;
    public const uint MediaSource = 0xFFFFFFFF;

    public const uint ReadFlagError = 0x1;
    public const uint ReadFlagEndOfStream = 0x2;
    public const uint ReadFlagStreamTick = 0x100;

    public const ushort VtI8 = 20;
    public const ushort VtUi8 = 21;

    public static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    public static readonly Guid DisableDxva = new("aa456cfd-3943-4a1e-a77d-1838c0ea2e35");
    public static readonly Guid ByteStreamContentType = new("fc358288-3cb6-460c-a424-b6681260375a");
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    public static readonly Guid PresentationDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMFByteStreamOnStream(IStream stream, out IMFByteStream byteStream);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    public static extern int MFCreateSourceReaderFromByteStream(IMFByteStream byteStream, IMFAttributes? attributes, out IMFSourceReader reader);

    /// <summary>A <c>PROPVARIANT</c> large enough for the 64-bit scalars the thumbnailer exchanges.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public long Int64;

        [FieldOffset(8)]
        public ulong UInt64;
    }

    /// <summary>Opaque: only ever passed through, never called.</summary>
    [ComImport]
    [Guid("ad4c1b00-4bf7-422f-9175-756693d9130d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFByteStream
    {
    }

    [ComImport]
    [Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFAttributes
    {
        [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType([In] ref Guid key, out uint type);
        [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes other, uint matchType, out int result);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
        [PreserveSig] int GetDouble([In] ref Guid key, out double value);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
        [PreserveSig] int GetString([In] ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
        [PreserveSig] int GetBlob([In] ref Guid key, IntPtr buffer, uint size, out uint written);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
        [PreserveSig] int SetDouble([In] ref Guid key, double value);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    /// <summary><c>IMFMediaType</c>: the 30 <c>IMFAttributes</c> slots first, then its own five.</summary>
    [ComImport]
    [Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaType
    {
        [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType([In] ref Guid key, out uint type);
        [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes other, uint matchType, out int result);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
        [PreserveSig] int GetDouble([In] ref Guid key, out double value);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
        [PreserveSig] int GetString([In] ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
        [PreserveSig] int GetBlob([In] ref Guid key, IntPtr buffer, uint size, out uint written);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
        [PreserveSig] int SetDouble([In] ref Guid key, double value);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);

        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out int compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out uint flags);
        [PreserveSig] int GetRepresentation(Guid representation, out IntPtr value);
        [PreserveSig] int FreeRepresentation(Guid representation, IntPtr value);
    }

    /// <summary><c>IMFSample</c>: the 30 <c>IMFAttributes</c> slots first, then its own fourteen.</summary>
    [ComImport]
    [Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSample
    {
        [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType([In] ref Guid key, out uint type);
        [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes other, uint matchType, out int result);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint value);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong value);
        [PreserveSig] int GetDouble([In] ref Guid key, out double value);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint length);
        [PreserveSig] int GetString([In] ref Guid key, IntPtr value, uint size, out uint length);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint size);
        [PreserveSig] int GetBlob([In] ref Guid key, IntPtr buffer, uint size, out uint written);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint value);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong value);
        [PreserveSig] int SetDouble([In] ref Guid key, double value);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
        [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In] ref Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);

        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(uint index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint length);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport]
    [Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint length);
        [PreserveSig] int SetCurrentLength(uint length);
        [PreserveSig] int GetMaxLength(out uint length);
    }

    /// <summary>The two-dimensional view of a video buffer; its pitch sign says which way up the rows are.</summary>
    [ComImport]
    [Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMF2DBuffer
    {
        [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
        [PreserveSig] int Unlock2D();
        [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
        [PreserveSig] int IsContiguousFormat(out int contiguous);
        [PreserveSig] int GetContiguousLength(out uint length);
        [PreserveSig] int ContiguousCopyTo(IntPtr destination, uint size);
        [PreserveSig] int ContiguousCopyFrom(IntPtr source, uint size);
    }

    [ComImport]
    [Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint streamIndex, out int selected);
        [PreserveSig] int SetStreamSelection(uint streamIndex, int selected);
        [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, out IMFMediaType mediaType);
        [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
        [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
        [PreserveSig] int SetCurrentPosition([In] ref Guid timeFormat, [In] ref PropVariant position);
        [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex, out uint streamFlags, out long timestamp, out IMFSample? sample);
        [PreserveSig] int Flush(uint streamIndex);
        [PreserveSig] int GetServiceForStream(uint streamIndex, [In] ref Guid service, [In] ref Guid iid, out IntPtr value);
        [PreserveSig] int GetPresentationAttribute(uint streamIndex, [In] ref Guid attribute, out PropVariant value);
    }
}
