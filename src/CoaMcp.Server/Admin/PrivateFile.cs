namespace CoaMcp.Server;

/// <summary>Unix file modes for files that hold written data: owner read and write, group read, nothing for others. Windows has no such modes, so nothing happens there.</summary>
internal static class PrivateFile
{
    public const UnixFileMode Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    /// <summary>Creates an empty file that has never had wider access than <see cref="Mode"/> (the umask can only narrow it). Fails if the file exists.</summary>
    public static void Create(string file)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = Mode;
        new FileStream(file, options).Dispose();
    }

    /// <summary>Creates the file with <see cref="Mode"/> if it is missing. A file made by someone else in the meantime is left alone.</summary>
    public static void CreateIfMissing(string file)
    {
        if (File.Exists(file)) return;
        try { Create(file); }
        catch (IOException) when (File.Exists(file)) { }
    }

    /// <summary>Removes any access beyond <see cref="Mode"/> from a file that exists. It never adds access, and does nothing if the file is missing.</summary>
    public static void Tighten(string file)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(file)) return;
        var current = File.GetUnixFileMode(file);
        var tight = current & (Mode | UnixFileMode.UserExecute);
        if (tight != current) File.SetUnixFileMode(file, tight);
    }
}
