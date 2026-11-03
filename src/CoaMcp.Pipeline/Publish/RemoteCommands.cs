namespace CoaMcp.Pipeline.Publish;

public static class Shell
{
    /// <summary>POSIX single-quoting: nothing inside is special to the remote shell, and an embedded quote is written as '\''.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}

/// <summary>
/// Every command line the publisher sends, in one place. Every path goes through <see cref="Shell.Quote"/>.
/// The "if [ -f ... ] ; then ... ; fi" forms print nothing, and succeed, when the file is absent.
/// </summary>
public static class RemoteCommands
{
    public static string MakeDirectory(string dir) => $"mkdir -p {Shell.Quote(dir)}";

    public static string Sha256IfPresent(string path) => $"if [ -f {Shell.Quote(path)} ] ; then sha256sum {Shell.Quote(path)} ; fi";

    public static string CatIfPresent(string path) => $"if [ -f {Shell.Quote(path)} ] ; then cat {Shell.Quote(path)} ; fi";

    public static string Remove(string path) => $"rm -f {Shell.Quote(path)}";

    /// <summary>Makes the file read-only, then renames it. A rename inside one folder is atomic.</summary>
    public static string LockAndRename(string from, string to) => $"chmod 444 {Shell.Quote(from)} && mv {Shell.Quote(from)} {Shell.Quote(to)}";

    /// <summary>Writes stdin to a temp file and renames it over the target, so readers never see a half-written file.</summary>
    public static string ReplaceFromStdin(string tmp, string target) =>
        $"cat > {Shell.Quote(tmp)} && chmod 644 {Shell.Quote(tmp)} && mv {Shell.Quote(tmp)} {Shell.Quote(target)}";
}
