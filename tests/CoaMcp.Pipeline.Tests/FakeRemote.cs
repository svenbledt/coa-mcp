using System.Text;
using CoaMcp.Pipeline.Publish;

namespace CoaMcp.Pipeline.Tests;

/// <summary>
/// An in-memory server for the publisher. It understands exactly the command shapes <see cref="RemoteCommands"/> produces
/// and parses them the way a shell would (single-quoted words), so a path that is not quoted correctly breaks the test.
/// </summary>
public sealed class FakeRemote : IRemote
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public HashSet<string> Dirs { get; } = new();
    public Dictionary<string, string> Owners { get; } = new();
    public List<string> Commands { get; } = new();
    public List<string> Uploads { get; } = new();
    public bool FailUpload { get; set; }
    public bool CorruptUpload { get; set; }
    public bool ThrowOnAnyCall { get; set; }

    public Task UploadAsync(string localPath, string remotePath, CancellationToken ct)
    {
        if (ThrowOnAnyCall) throw new InvalidOperationException("the remote must not be contacted");
        Uploads.Add(remotePath);
        if (FailUpload) throw new IOException("scp: connection lost");
        var bytes = File.ReadAllBytes(localPath);
        if (CorruptUpload) bytes[bytes.Length / 2] ^= 0xFF;
        Files[remotePath] = bytes;
        return Task.CompletedTask;
    }

    public Task DownloadAsync(string remotePath, string localPath, CancellationToken ct)
    {
        if (ThrowOnAnyCall) throw new InvalidOperationException("the remote must not be contacted");
        if (!Files.TryGetValue(remotePath, out var data)) throw new IOException("scp: no such file " + remotePath);
        File.WriteAllBytes(localPath, data);
        return Task.CompletedTask;
    }

    public Task<RemoteResult> RunAsync(string command, string? stdin, CancellationToken ct)
    {
        if (ThrowOnAnyCall) throw new InvalidOperationException("the remote must not be contacted");
        Commands.Add(command);
        var w = Words(command);
        switch (w[0])
        {
            case "mkdir" when w is ["mkdir", "-p", var d, "&&", "chmod", "755", ..]:
                Dirs.Add(d);
                return Ok("");
            case "mkdir" when w is ["mkdir", "-p", var d, "&&", "chown", var owner, var d2, "&&", "chmod", "750", var d3] && d == d2 && d == d3:
                Dirs.Add(d);
                Owners[d] = owner;
                return Ok("");
            case "rm" when w is ["rm", "-f", var p]:
                Files.Remove(p);
                return Ok("");
            case "chmod" when w is ["chmod", "644", var from, "&&", "mv", var from2, var to] && from == from2:
                return Move(from, to);
            case "if" when w is ["if", "[", "-f", var p, "]", ";", "then", var tool, var p2, ";", "fi"] && p == p2:
                if (!Files.TryGetValue(p, out var data)) return Ok("");
                return tool switch
                {
                    "sha256sum" => Ok(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)) + "  " + p + "\n"),
                    "cat" => Ok(Encoding.UTF8.GetString(data)),
                    _ => Fail($"unknown tool {tool}"),
                };
            case "cat" when w is ["cat", ">", var tmp, "&&", "chmod", "644", var tmp2, "&&", "mv", var tmp3, var dst] && tmp == tmp2 && tmp == tmp3:
                Files[tmp] = Encoding.UTF8.GetBytes(stdin ?? "");
                return Move(tmp, dst);
        }
        return Fail("unrecognised command: " + command);
    }

    private Task<RemoteResult> Move(string from, string to)
    {
        if (!Files.Remove(from, out var data)) return Fail($"mv: cannot stat '{from}'");
        Files[to] = data;
        return Ok("");
    }

    private static Task<RemoteResult> Ok(string stdout) => Task.FromResult(new RemoteResult(0, stdout, ""));
    private static Task<RemoteResult> Fail(string stderr) => Task.FromResult(new RemoteResult(1, "", stderr));

    /// <summary>Splits like a POSIX shell for the subset used here: whitespace separates, single quotes group, \' outside quotes is a quote.</summary>
    public static List<string> Words(string s)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        var has = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'')
            {
                has = true;
                var end = s.IndexOf('\'', i + 1);
                if (end < 0) throw new FormatException("unterminated quote in: " + s);
                cur.Append(s, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '\\' && i + 1 < s.Length && s[i + 1] == '\'') { has = true; cur.Append('\''); i++; }
            else if (char.IsWhiteSpace(c)) { if (has) { words.Add(cur.ToString()); cur.Clear(); has = false; } }
            else if (c is '"' or '$' or '`' or '\\' or '*' or '?' or '(' or ')' or '<' or '|')
                throw new FormatException($"unquoted shell metacharacter '{c}' in: {s}");
            else { has = true; cur.Append(c); }
        }
        if (has) words.Add(cur.ToString());
        return words;
    }
}
