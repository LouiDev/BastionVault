using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BastionVault.Core;

/// <summary>
/// The answer of the KDF memory pre-flight (FORMAT.md §3.1 step 9) for one parameter set on this machine.
/// </summary>
/// <param name="Fits">True when the derivation stays within the budget and will not be refused.</param>
/// <param name="RequiredBytes">Memory the derivation claims.</param>
/// <param name="BudgetBytes">The most a derivation may claim here: <see cref="Format.VaultLimits.KdfMemoryFractionOfInstalled"/> of installed memory, or 0 when nothing could be measured.</param>
/// <param name="InstalledBytes">Physical memory installed, or 0 when nothing could be measured.</param>
public sealed record KdfPreflightResult(bool Fits, long RequiredBytes, long BudgetBytes, long InstalledBytes);

/// <summary>
/// The KDF memory pre-flight of FORMAT.md §3.1 step 9, exposed as a question rather than only as the
/// refusal Open, Unlock and Create raise. It measures installed memory, not free memory: what is free moves
/// with whatever else the machine is doing this second, and measuring it refused the default preset on a
/// large machine during a busy moment. The pre-flight rejects a header no machine of this size could ever
/// serve, and that question has a stable answer a UI can show before the button is pressed.
/// </summary>
public static partial class KdfPreflight
{
    /// <summary>Answers whether <paramref name="parameters"/> would pass the pre-flight on this machine.</summary>
    /// <param name="parameters">Argon2id parameters, from a header or a preset.</param>
    /// <returns>The verdict with the figures behind it. An unmeasurable machine always fits.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    public static KdfPreflightResult Check(KdfParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Check(parameters, InstalledPhysicalMemoryBytes());
    }

    /// <summary>
    /// The pre-flight as a pure function of the parameters and a machine size, so a test or a scripted
    /// host can ask about a machine other than this one. <paramref name="installedBytes"/> of 0 or less
    /// means "nothing could be measured" and fits.
    /// </summary>
    /// <param name="parameters">Argon2id parameters, from a header or a preset.</param>
    /// <param name="installedBytes">Physical memory of the machine in question.</param>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    public static KdfPreflightResult Check(KdfParameters parameters, long installedBytes)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        long required = parameters.MemoryBytes;
        if (installedBytes <= 0)
        {
            return new KdfPreflightResult(Fits: true, required, BudgetBytes: 0, InstalledBytes: 0);
        }

        long budget = (long)(installedBytes * Format.VaultLimits.KdfMemoryFractionOfInstalled);
        return new KdfPreflightResult(required <= budget, required, budget, installedBytes);
    }

    /// <summary>
    /// The most expensive named preset that passes the pre-flight here, or <see langword="null"/> when
    /// even <see cref="KdfPreset.Fast"/> does not fit.
    /// </summary>
    public static KdfPreset? LargestFittingPreset()
    {
        foreach (KdfPreset preset in new[] { KdfPreset.Strong, KdfPreset.Standard, KdfPreset.Fast })
        {
            if (Check(KdfParameters.FromPreset(preset)).Fits)
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>
    /// Installed physical memory in bytes, which is what FORMAT.md section 3.1 step 9 measures:
    /// <c>GlobalMemoryStatusEx.ullTotalPhys</c>, falling back to
    /// <see cref="GCMemoryInfo.TotalAvailableMemoryBytes"/> (the machine's or the container's total)
    /// where that call is unavailable.
    /// </summary>
    /// <returns>Installed physical memory, or 0 when nothing can be measured.</returns>
    internal static long InstalledPhysicalMemoryBytes()
    {
        if (OperatingSystem.IsWindows() && TryQuery(out MemoryStatusEx status) && status.TotalPhysical is > 0 and <= long.MaxValue)
        {
            return (long)status.TotalPhysical;
        }

        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return total > 0 ? total : 0;
    }

    /// <summary>
    /// Physical memory free at this instant, for the message that follows a failed allocation. Never
    /// consulted by the pre-flight itself.
    /// </summary>
    /// <returns>Free physical memory, or 0 when nothing can be measured.</returns>
    internal static long AvailablePhysicalMemoryBytes()
    {
        if (OperatingSystem.IsWindows() && TryQuery(out MemoryStatusEx status) && status.AvailablePhysical <= long.MaxValue)
        {
            return (long)status.AvailablePhysical;
        }

        return 0;
    }

    /// <summary>Fills a <c>MEMORYSTATUSEX</c>; no allocation, so it is safe to call right after a failed one.</summary>
    /// <param name="status">The structure to fill.</param>
    [SupportedOSPlatform("windows")]
    private static bool TryQuery(out MemoryStatusEx status)
    {
        status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status);
    }

    /// <summary>The subset of <c>MEMORYSTATUSEX</c> the pre-flight needs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        /// <summary>Size of the structure in bytes; set by the caller.</summary>
        public uint Length;

        /// <summary>Percentage of physical memory in use.</summary>
        public uint MemoryLoad;

        /// <summary>Total physical memory: the quantity section 3.1 step 9 asks for.</summary>
        public ulong TotalPhysical;

        /// <summary>Free physical memory; only used to describe a failed allocation.</summary>
        public ulong AvailablePhysical;

        /// <summary>Committed memory limit.</summary>
        public ulong TotalPageFile;

        /// <summary>Remaining commit charge.</summary>
        public ulong AvailablePageFile;

        /// <summary>Size of the process virtual address space.</summary>
        public ulong TotalVirtual;

        /// <summary>Unreserved address space.</summary>
        public ulong AvailableVirtual;

        /// <summary>Reserved; always zero.</summary>
        public ulong AvailableExtendedVirtual;
    }

    /// <summary>Queries the machine's memory state.</summary>
    /// <param name="buffer">Structure to fill; its <c>Length</c> must be set.</param>
    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
