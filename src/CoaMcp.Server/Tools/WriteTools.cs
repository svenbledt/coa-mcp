using System.ComponentModel;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CoaMcp.Server.Tools;

/// <summary>The only tools that change anything. Each must stay behind [Authorize]; tests prove anonymous callers neither see nor can call them.</summary>
[McpServerToolType]
public sealed class WriteTools
{
    private readonly LayerSet _layers;
    private readonly WriteStore _store;
    private readonly WriteLimiter _limiter;
    private readonly TokenAuth _token;

    public WriteTools(LayerSet layers, WriteStore store, WriteLimiter limiter, TokenAuth token)
    {
        _layers = layers;
        _store = store;
        _limiter = limiter;
        _token = token;
    }

    private void RefuseToken(params string?[] texts)
    {
        if (texts.Any(_token.AppearsIn)) throw new McpException("This write contains the server's bearer token. Nothing was written.");
    }

    [Authorize]
    [McpServerTool(Name = "write_entry", Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Add a written entry, live at once. Generated data never changes: on a generated id the entry is stored as a note. trust is observed (evidence required: a description or http(s) url; a video needs a timestamp) or memory. client, derived and cache can't be claimed. author is shown as self-declared.")]
    public string WriteEntry(
        [Description("Your name: 1 to 64 letters, digits, spaces, . _ -")] string author,
        [Description("An existing id, or a new one: npc, dungeon, encounter, mechanic, spell, item, class, quest, note or test, then :slug in lower case. A client file is path:<its path>.")] string subject,
        [Description("observation, note, correction or page. A new page replaces the shown version; older ones stay in history.")] string kind,
        [Description("observed or memory.")] string trust,
        [Description("Markdown, up to 32 KB.")] string content,
        [Description("Required for observed: {description?, url?, timestamp?, video?}.")] EntryEvidence? evidence = null,
        [Description("unknown, or coa, wotlk, octo, classic, s10, separated by commas.")] string realm = WriteRules.UnknownRealm,
        [Description("Structured claim: field name, up to 64 characters. Needs value.")] string? field = null,
        [Description("Structured claim: value, up to 256 characters. Disagreeing values get a conflict note.")] string? value = null,
        [Description("Approximate place: {map, x, y, z} or {map, leftPct, topPct}.")] EntryPosition? position = null)
    {
        if (_limiter.WriteRefusal() is { } busy) throw new McpException(busy);
        var who = WriteRules.Author(author);
        var (id, generated) = WriteRules.Subject(subject, _layers);
        var asked = WriteRules.Kind(kind);
        var level = WriteRules.TrustLevel(trust);
        var text = WriteRules.Content(content);
        var evidenceJson = WriteRules.Evidence(evidence, level);
        var realms = WriteRules.Realms(realm);
        var (f, v) = WriteRules.FieldValue(field, value);
        var positionJson = WriteRules.Position(position);
        // The text as sent, not the stored JSON, which escapes characters such as + and / that a token can hold.
        RefuseToken(who, id, text, evidence?.Description, evidence?.Url, evidence?.Timestamp, f, v, position?.Map);

        // Generated data is never changed: whatever is said about it is a note beside it.
        var stored = generated ? "note" : asked;
        var receipt = _store.Add(new NewEntry(id, stored, realms, level, text, evidenceJson, f, v, positionJson, who, asked));

        // Replies name only the entry: the author and subject are written text, and live inside the markers wherever they are shown.
        var reply = $"Stored entry:{receipt.Id.ToString(CultureInfo.InvariantCulture)} as {stored}, trust {Render.Trust(level)}. The author is shown as self-declared.";
        if (generated) reply += $" The kind {asked} was stored as a note, because this id is generated data. The generated row is unchanged.";
        if (receipt.SupersedesId is { } old) reply += $" It replaces entry:{old.ToString(CultureInfo.InvariantCulture)} as the shown page.";
        return reply;
    }

    [Authorize]
    [McpServerTool(Name = "undo_entry", Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Hide a written entry. Nothing is deleted: who undid it and when stay on record. Undoing twice changes nothing.")]
    public string UndoEntry(
        [Description("The number n of entry:n.")] long id,
        [Description("Your name: 1 to 64 letters, digits, spaces, . _ -")] string author)
    {
        if (_limiter.UndoRefusal() is { } busy) throw new McpException(busy);
        var who = WriteRules.Author(author);
        RefuseToken(who);
        var r = _store.Undo(id, who);
        var name = "entry:" + r.Id.ToString(CultureInfo.InvariantCulture);
        return r.AlreadyUndone
            ? $"{name} was already undone at {r.UndoneAt}. Nothing changed. get({name}) shows by whom."
            : $"Undid {name} at {r.UndoneAt}. It is hidden and stays in history.";
    }
}
