using Microsoft.Data.Sqlite;

namespace CoaMcp.Server.Tests;

/// <summary>The store holds written text, so on Unix it and its journal files are never open to others (production runs on Linux).</summary>
public sealed class WriteStoreModeTests : IDisposable
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coamcp-mode-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_dir, "store.sqlite");

    public WriteStoreModeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* temp data */ }
    }

    private static void Add(WriteStore s) =>
        s.Add(new NewEntry("test:mode", "note", new[] { "unknown" }, CoaMcp.Core.Trust.Memory, "text", "{}", null, null, null, "Ann"));

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void AssertNoWiderThanPrivate(string file) =>
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(file) & ~Private);

    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Store_NewFile_IsPrivate()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes don't exist on Windows; production runs on Linux.");
        using var s = new WriteStore(StorePath);
        Add(s);

        AssertNoWiderThanPrivate(StorePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StorePath) & (UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }

    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Store_ExecutableFile_LosesExecute()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes don't exist on Windows; production runs on Linux.");
        using (var first = new WriteStore(StorePath)) Add(first);
        SqliteConnection.ClearAllPools();
        File.SetUnixFileMode(StorePath, (UnixFileMode)0b111_101_101);   // 755

        using var s = new WriteStore(StorePath);
        Assert.Equal(1, s.Status().Live);

        Assert.Equal(Private, File.GetUnixFileMode(StorePath));
    }

    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Store_NarrowerFile_IsNeverWidened()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes don't exist on Windows; production runs on Linux.");
        using (var first = new WriteStore(StorePath)) Add(first);
        SqliteConnection.ClearAllPools();
        var narrow = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(StorePath, narrow);

        using var s = new WriteStore(StorePath);
        Assert.Equal(1, s.Status().Live);

        Assert.Equal(narrow, File.GetUnixFileMode(StorePath));
    }

    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Store_ExistingWideFile_IsTightened()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes don't exist on Windows; production runs on Linux.");
        using (var first = new WriteStore(StorePath)) Add(first);
        SqliteConnection.ClearAllPools();
        File.SetUnixFileMode(StorePath, (UnixFileMode)0b110_100_100);   // 644, as on production

        using var s = new WriteStore(StorePath);
        Assert.Equal(1, s.Status().Live);   // a read alone tightens it

        AssertNoWiderThanPrivate(StorePath);
    }

    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Store_WalAndShm_ArePrivate()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes don't exist on Windows; production runs on Linux.");
        using var s = new WriteStore(StorePath);
        Add(s);
        // Other tests clear the shared connection pool, so a connection of its own keeps the WAL files in place.
        using var keep = new SqliteConnection($"Data Source={StorePath};Mode=ReadWrite;Pooling=False");
        keep.Open();
        using (var q = keep.CreateCommand()) { q.CommandText = "SELECT COUNT(*) FROM entries"; q.ExecuteScalar(); }
        var wal = StorePath + "-wal";
        var shm = StorePath + "-shm";
        Assert.True(File.Exists(wal) && File.Exists(shm), "An open WAL connection should keep the WAL files in place.");
        File.SetUnixFileMode(wal, (UnixFileMode)0b110_100_100);
        File.SetUnixFileMode(shm, (UnixFileMode)0b110_100_100);

        Add(s);   // the next open tightens wider ones that exist

        AssertNoWiderThanPrivate(wal);
        AssertNoWiderThanPrivate(shm);
        AssertNoWiderThanPrivate(StorePath);
    }
}
