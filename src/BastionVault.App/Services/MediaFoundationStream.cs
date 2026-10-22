using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using static BastionVault.App.Services.MediaFoundationInterop;

namespace BastionVault.App.Services;

/// <summary>
/// The vault stream as a COM <c>IStream</c>, shared by the video thumbnailer and the audio prober.
/// Media Foundation reads and seeks through this on its own threads; nothing else is supported, and
/// the adapter never owns or disposes the stream. Every read and seek asks the cancellation token
/// first, which is what stops a probe when the selection moves on.
/// </summary>
internal sealed class MediaFoundationStream(Stream inner, CancellationToken ct) : IStream
{
    private const int StreamType = 2; // STGTY_STREAM

    private readonly Lock _gate = new();

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        ct.ThrowIfCancellationRequested();

        int total = 0;
        lock (_gate)
        {
            while (total < cb)
            {
                int read = inner.Read(pv, total, cb - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }
        }

        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbRead, total);
        }
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        ct.ThrowIfCancellationRequested();

        long position;
        lock (_gate)
        {
            position = inner.Seek(dlibMove, (SeekOrigin)dwOrigin);
        }

        if (plibNewPosition != IntPtr.Zero)
        {
            Marshal.WriteInt64(plibNewPosition, position);
        }
    }

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        long length;
        lock (_gate)
        {
            length = inner.Length;
        }

        pstatstg = new STATSTG { type = StreamType, cbSize = length };
    }

    public void Commit(int grfCommitFlags)
    {
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();

    public void SetSize(long libNewSize) => throw new NotSupportedException();

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();

    public void Revert() => throw new NotSupportedException();

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();

    public void Clone(out IStream ppstm) => throw new NotSupportedException();
}

/// <summary>The handful of source-reader chores the video and audio probes share.</summary>
internal static class MediaFoundationHelpers
{
    private const int Ok = 0;

    /// <summary>Tells the source resolver what the file name claimed; a lie only costs a failed probe.</summary>
    /// <param name="byteStream">The byte stream the reader will be created over.</param>
    /// <param name="contentType">MIME hint from the file name, or <see langword="null"/>.</param>
    public static void HintContentType(IMFByteStream byteStream, string? contentType)
    {
        if (string.IsNullOrEmpty(contentType) || byteStream is not IMFAttributes attributes)
        {
            return;
        }

        Guid key = ByteStreamContentType;
        attributes.SetString(ref key, contentType);
    }

    /// <summary>The presentation's running time, when the container declares one.</summary>
    /// <param name="reader">The source reader.</param>
    public static TimeSpan? Duration(IMFSourceReader reader)
    {
        Guid key = PresentationDuration;
        if (reader.GetPresentationAttribute(MediaSource, ref key, out PropVariant value) != Ok)
        {
            return null;
        }

        try
        {
            if (value.Type != VtUi8 || value.UInt64 > (ulong)TimeSpan.MaxValue.Ticks)
            {
                return null;
            }

            // Media Foundation counts 100-nanosecond units, the same as TimeSpan ticks.
            return TimeSpan.FromTicks((long)value.UInt64);
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    /// <summary>Releases a COM object right away instead of waiting for the finalizer.</summary>
    /// <param name="comObject">The object, or <see langword="null"/>.</param>
    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int PropVariantClear(ref PropVariant value);
}
