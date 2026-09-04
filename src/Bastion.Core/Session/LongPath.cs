using IoPath = System.IO.Path;

namespace Bastion.Core.Session;

/// <summary>
/// Extended-length paths (FORMAT.md section 6.4). Windows only honours the manifest's
/// <c>longPathAware</c> flag when <c>HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled</c>
/// is also set, which is not the default, so any fully qualified path that can pass <c>MAX_PATH</c> is
/// handed to the file system with the <c>\\?\</c> prefix instead. The plain path stays the one shown to
/// the user: the prefix is an argument to the API, never text for a report.
/// </summary>
internal static class LongPath
{
    /// <summary>The longest path Win32 accepts without the prefix, excluding the terminating NUL.</summary>
    private const int MaxPath = 259;

    /// <summary>The longest path Win32 accepts with the prefix, excluding the terminating NUL.</summary>
    public const int MaxExtendedPath = 32767;

    /// <summary>The extended-length prefix for a drive-rooted path.</summary>
    private const string DevicePrefix = @"\\?\";

    /// <summary>The extended-length prefix for a UNC path; the two leading separators are dropped.</summary>
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Returns the form of a path that should be passed to a file-system call: unchanged when it is
    /// short enough, already prefixed, not fully qualified, or the platform is not Windows.
    /// </summary>
    /// <param name="fullPath">A path, normally the output of <see cref="IoPath.GetFullPath(string)"/>.</param>
    public static string ForIo(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) ||
            fullPath.Length <= MaxPath ||
            !OperatingSystem.IsWindows() ||
            fullPath.StartsWith(DevicePrefix, StringComparison.Ordinal) ||
            fullPath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            !IoPath.IsPathFullyQualified(fullPath))
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? string.Concat(UncPrefix, fullPath.AsSpan(2))
            : string.Concat(DevicePrefix, fullPath);
    }
}
