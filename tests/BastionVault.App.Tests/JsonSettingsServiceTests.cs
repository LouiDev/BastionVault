using BastionVault.App.Services;
using BastionVault.Core;

namespace BastionVault.App.Tests;

/// <summary>
/// Settings round-trip. The file is the only thing that survives a restart, so a write has to be
/// atomic and a damaged file has to be survivable.
/// </summary>
public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "BastionTests", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void DefaultsAreUsedWhenThereIsNoFile()
    {
        var service = new JsonSettingsService(FilePath());

        Assert.Equal(10, service.Current.AutoLockMinutes);
        Assert.True(service.Current.ExcludeFromScreenCapture);
        Assert.False(service.Current.RememberKeyFilePaths);
        Assert.Equal(KdfPreset.Standard, service.Current.DefaultKdfPreset);
    }

    [Fact]
    public void EverySettingSurvivesARoundTrip()
    {
        string path = FilePath();
        var first = new JsonSettingsService(path);

        first.Current.AutoLockMinutes = 3;
        first.Current.Theme = AppTheme.Dark;
        first.Current.RowDensity = RowDensity.Spacious;
        first.Current.DefaultKdfPreset = KdfPreset.Strong;
        first.Current.RememberKeyFilePaths = true;
        first.Current.StagingLocation = StagingLocation.Custom;
        first.Current.StagingCustomPath = @"D:\staging";
        first.Current.ExcludeFromScreenCapture = false;
        first.Current.SizeObfuscation = true;
        first.Current.ShowFirstRun = false;
        first.Current.WindowPlacement.Left = 120;
        first.Current.WindowPlacement.Width = 1000;
        first.Current.WindowPlacement.HasValue = true;
        first.Current.ColumnLayout.SortColumn = "modified";
        first.Current.ColumnLayout.SortAscending = false;
        first.Current.ColumnLayout.Columns.Add(new ColumnState { Key = "name", Width = 320, Order = 0 });
        first.Save();

        var second = new JsonSettingsService(path);

        Assert.Equal(3, second.Current.AutoLockMinutes);
        Assert.Equal(AppTheme.Dark, second.Current.Theme);
        Assert.Equal(RowDensity.Spacious, second.Current.RowDensity);
        Assert.Equal(KdfPreset.Strong, second.Current.DefaultKdfPreset);
        Assert.True(second.Current.RememberKeyFilePaths);
        Assert.Equal(StagingLocation.Custom, second.Current.StagingLocation);
        Assert.Equal(@"D:\staging", second.Current.StagingCustomPath);
        Assert.False(second.Current.ExcludeFromScreenCapture);
        Assert.True(second.Current.SizeObfuscation);
        Assert.False(second.Current.ShowFirstRun);
        Assert.Equal(120, second.Current.WindowPlacement.Left);
        Assert.Equal(1000, second.Current.WindowPlacement.Width);
        Assert.True(second.Current.WindowPlacement.HasValue);
        Assert.Equal("modified", second.Current.ColumnLayout.SortColumn);
        Assert.False(second.Current.ColumnLayout.SortAscending);
        Assert.Single(second.Current.ColumnLayout.Columns);
        Assert.Equal(320, second.Current.ColumnLayout.Columns[0].Width);
    }

    [Fact]
    public void SaveLeavesNoTempFileBehind()
    {
        string path = FilePath();
        var service = new JsonSettingsService(path);

        service.Save();
        service.Current.AutoLockMinutes = 30;
        service.Save();

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Contains("autoLockMinutes", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SaveRaisesChanged()
    {
        var service = new JsonSettingsService(FilePath());
        int raised = 0;
        service.Changed += (_, _) => raised++;

        service.Save();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ADamagedFileFallsBackToDefaults()
    {
        string path = FilePath();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ this is not json");

        var service = new JsonSettingsService(path);

        Assert.Equal(10, service.Current.AutoLockMinutes);
    }

    private string FilePath() => Path.Combine(_directory, "settings.json");
}
