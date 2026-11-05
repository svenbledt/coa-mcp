namespace CoaMcp.Core;

/// <summary>
/// The structured item tables of the bisbeard layer, for filtering by realm, slot, class and stat.
/// The pages in <c>entities</c> stay the readable form; these tables answer "which items have X".
/// One row per item per BisBeard list. The main list is shared by CoA, Classic and S10, so its realm stays unproven.
/// </summary>
public static class ItemTables
{
    public const string Items = "items";
    public const string Stats = "item_stats";
    public const string Classes = "item_classes";

    public static readonly string[] AllTables = { Items, Stats, Classes };

    public static readonly string[] ItemColumns =
    {
        "item_list", "id", "entity_id", "name", "realms", "realm_proven", "in_coa_client", "slot", "type", "quality",
        "item_level", "req_level", "source_category", "source", "version", "phase", "drop_rate", "set_name",
        "required_faction", "dmg_min", "dmg_max", "dmg_speed",
    };

    public static readonly string[] StatColumns = { "item_list", "item_id", "stat", "value" };
    public static readonly string[] ClassColumns = { "item_list", "item_id", "class" };

    /// <summary>Creates the tables. Call before <see cref="LayerWriter.Finish"/>; the writer's transaction is already open.</summary>
    public static void Create(LayerWriter w)
    {
        w.Execute("""
            CREATE TABLE items(
              item_list TEXT NOT NULL, id INTEGER NOT NULL, entity_id TEXT NOT NULL, name TEXT NOT NULL,
              realms TEXT NOT NULL, realm_proven INTEGER NOT NULL, in_coa_client INTEGER,
              slot TEXT, type TEXT, quality TEXT, item_level INTEGER, req_level INTEGER,
              source_category TEXT, source TEXT, version TEXT, phase INTEGER, drop_rate TEXT, set_name TEXT,
              required_faction TEXT, dmg_min REAL, dmg_max REAL, dmg_speed REAL,
              PRIMARY KEY(item_list, id)) WITHOUT ROWID
            """);
        w.Execute("CREATE TABLE item_stats(item_list TEXT NOT NULL, item_id INTEGER NOT NULL, stat TEXT NOT NULL, value REAL NOT NULL, PRIMARY KEY(item_list, item_id, stat)) WITHOUT ROWID");
        w.Execute("CREATE TABLE item_classes(item_list TEXT NOT NULL, item_id INTEGER NOT NULL, class TEXT NOT NULL, PRIMARY KEY(item_list, item_id, class)) WITHOUT ROWID");
        w.Execute("CREATE INDEX items_realm ON items(realms, slot, quality)");
        w.Execute("CREATE INDEX items_name ON items(name COLLATE NOCASE)");
        w.Execute("CREATE INDEX item_stats_stat ON item_stats(stat, value)");
        w.Execute("CREATE INDEX item_classes_class ON item_classes(class)");
    }
}
