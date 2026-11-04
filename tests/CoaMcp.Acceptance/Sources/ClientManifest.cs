using Microsoft.Data.Sqlite;

namespace CoaMcp.Acceptance;

/// <summary>One archive's copy of a path. Marker means a delete marker.</summary>
public sealed record Copy(string Archive, string Origin, string? Realm, int Priority, long Size, bool Marker, int Locale);

/// <summary>A client path, its copies highest priority first, and the winner worked out here, independently of the server's layer.</summary>
public sealed class ClientPath
{
    public required string Path { get; init; }
    public required IReadOnlyList<Copy> Holders { get; init; }

    /// <summary>The rule: the highest-priority archive the default client opens (priority 0 or more, tied to no realm). A delete marker on top means the file is gone.</summary>
    public Copy? Winner => Holders.FirstOrDefault(h => h.Priority >= 0 && h.Realm is null);
    public bool Deleted => Winner is { Marker: true };
    public bool ProjectWinner => Winner is { Marker: false, Origin: "project" };

    /// <summary>The next copy below the winner the client could read that isn't from a project archive.</summary>
    public Copy? NextNonProject => Holders.Where(h => h.Priority >= 0 && h.Realm is null).Skip(1).FirstOrDefault(h => h.Origin != "project");
}

/// <summary>The restore manifest, opened read-only and immutable, with winners and counts computed by this suite.</summary>
public sealed class ClientManifest
{
    private static readonly Lazy<ClientManifest> Shared = new(() => new ClientManifest(Settings.ManifestFile));
    public static ClientManifest Instance => Shared.Value;

    public Dictionary<string, ClientPath> Paths { get; } = new(StringComparer.Ordinal);
    public long InternalSkipped { get; private set; }
    public long Unnamed { get; private set; }

    /// <summary>Entries whose stored name doesn't hash to its two recorded MPQ hashes. Should be empty: listfile names must match both hashes.</summary>
    public List<string> HashMismatches { get; } = new();
    public long HashesChecked { get; private set; }

    private ClientManifest(string file)
    {
        var uri = new Uri(System.IO.Path.GetFullPath(file)).AbsoluteUri + "?immutable=1";
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();

        var archives = new Dictionary<string, (int Priority, string Origin, string? Realm)>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT archive, priority, origin, realm FROM archives";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) archives[rd.GetString(0)] = (rd.GetInt32(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3));
        }

        var groups = new Dictionary<string, List<(Copy Copy, string Path)>>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT path_lc, path, archive, size, status, locale, name, name_source, name1, name2 FROM files";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var lc = rd.GetString(0);
                var name = rd.IsDBNull(6) ? null : rd.GetString(6);
                if (name is not null)
                {
                    HashesChecked++;
                    var (h1, h2) = MpqHash.Name(name);
                    if (h1 != (uint)rd.GetInt64(8) || h2 != (uint)rd.GetInt64(9)) HashMismatches.Add($"{rd.GetString(2)}: {name}");
                }
                var internalName = (!rd.IsDBNull(7) && rd.GetString(7) == "internal") || lc is "(listfile)" or "(attributes)" or "(signature)";
                if (internalName) { InternalSkipped++; continue; }
                if (name is null) { Unnamed++; continue; }

                var a = archives[rd.GetString(2)];
                var copy = new Copy(rd.GetString(2), a.Origin, a.Realm, a.Priority, rd.GetInt64(3), rd.GetString(4) == "delete_marker", rd.GetInt32(5));
                if (!groups.TryGetValue(lc, out var list)) groups[lc] = list = new List<(Copy, string)>(1);
                list.Add((copy, rd.GetString(1).Replace('\\', '/')));
            }
        }

        foreach (var (lc, list) in groups)
        {
            // An archive can hold several locale copies of one path: the lowest locale stands for that archive.
            var holders = list.GroupBy(c => c.Copy.Archive, StringComparer.Ordinal).Select(g => g.OrderBy(c => c.Copy.Locale).First())
                .OrderByDescending(c => c.Copy.Priority).ThenBy(c => c.Copy.Archive, StringComparer.Ordinal).ToList();
            var win = holders.FirstOrDefault(c => c.Copy.Priority >= 0 && c.Copy.Realm is null);
            Paths[lc] = new ClientPath { Path = (win.Path ?? holders[0].Path), Holders = holders.Select(h => h.Copy).ToList() };
        }
    }

    public string Restored(Copy copy, string path) => System.IO.Path.Combine(Settings.RestoredRoot, copy.Archive, path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>All DBC tables: paths directly under DBFilesClient ending in .dbc.</summary>
    public IEnumerable<ClientPath> DbcTables => Paths.Where(p => p.Key.StartsWith("dbfilesclient/", StringComparison.Ordinal) && p.Key.EndsWith(".dbc", StringComparison.Ordinal) && p.Key.IndexOf('/', 14) < 0).Select(p => p.Value);

    public ClientPath? Dbc(string table) => Paths.GetValueOrDefault("dbfilesclient/" + table.ToLowerInvariant() + ".dbc");
}

/// <summary>Blizzard's MPQ name hash, written out here so a listfile name can be checked against the archive's own two hashes.</summary>
public static class MpqHash
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[0x500];
        uint seed = 0x00100001;
        for (uint i = 0; i < 0x100; i++)
        {
            var index = i;
            for (var j = 0; j < 5; j++, index += 0x100)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                var high = (seed & 0xFFFF) << 16;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                table[index] = high | (seed & 0xFFFF);
            }
        }
        return table;
    }

    private static uint Hash(string name, uint type)
    {
        uint a = 0x7FED7FED, b = 0xEEEEEEEE;
        foreach (var raw in name)
        {
            var c = char.ToUpperInvariant(raw == '/' ? '\\' : raw);
            a = Table[type + (c & 0xFF)] ^ (a + b);
            b = (uint)(c & 0xFF) + a + b + (b << 5) + 3;
        }
        return a;
    }

    public static (uint Name1, uint Name2) Name(string name) => (Hash(name, 0x100), Hash(name, 0x200));
}
