using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Bastion.App.Services;

/// <summary>
/// The OS clipboard. Bastion puts only "Copy path" and "Copy details" text here, and every write
/// carries the three opt-out formats so clipboard history, cloud clipboard and clipboard
/// monitors leave it alone (UI-CONTRACT.md section 1.11).
/// </summary>
public sealed class OsClipboard : IOsClipboard
{
    private const string ExcludeFromMonitorProcessing = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";

    private readonly ILog? _log;

    /// <summary>Creates the clipboard wrapper.</summary>
    /// <param name="log">Optional log for clipboard failures.</param>
    public OsClipboard(ILog? log = null) => _log = log;

    /// <inheritdoc />
    public bool HasFileDrop
    {
        get
        {
            try
            {
                return Clipboard.ContainsFileDropList();
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var data = new DataObject();
        data.SetText(text);

        // A zero DWORD in each of these formats is the documented opt-out.
        data.SetData(ExcludeFromMonitorProcessing, ZeroDword());
        data.SetData(CanIncludeInHistory, ZeroDword());
        data.SetData(CanUploadToCloud, ZeroDword());

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException ex) when (attempt < 2)
            {
                _log?.Warn("Clipboard was busy; retrying.", ex);
                Thread.Sleep(30);
            }
            catch (COMException ex)
            {
                _log?.Warn("Text could not be placed on the clipboard.", ex);
                return;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string>? GetFileDropList()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                return null;
            }

            System.Collections.Specialized.StringCollection files = Clipboard.GetFileDropList();
            var result = new List<string>(files.Count);
            foreach (string? file in files)
            {
                if (!string.IsNullOrEmpty(file))
                {
                    result.Add(file);
                }
            }

            return result.Count == 0 ? null : result;
        }
        catch (COMException ex)
        {
            _log?.Warn("Clipboard file list could not be read.", ex);
            return null;
        }
    }

    private static MemoryStream ZeroDword() => new(new byte[] { 0, 0, 0, 0 });
}
