using System;
using UnityEngine;

// HCKIT2 v93: the ONE place that decides "does this data row belong to this fighter".
// Row keys (the old `name` / `key` / `match` / `contains` / `fighter` field of every character-keyed json) may be:
//   a roster id      "Joan"      exact match against the roster id of the object (clones like "Joan (Clone)" / "Joan (1)" still resolve)
//   "@player"        the roster player,  "@enemy" the roster enemy   (so a row follows the ROLE, not the name)
//   "*"              default row: used only when no exact / role / legacy row matches (never for objects that are not roster characters)
//   anything else    LEGACY: old substring match against the object name, kept so today's data works unchanged
// Priority when several rows match: exact id / role  >  legacy substring  >  "*".  Ties keep each loader's old rule (first or last wins).
public static class RosterResolver
{
    public const string RolePlayer = "@player", RoleEnemy = "@enemy", RoleAny = "*";
    public const int NoMatch = 0, Wildcard = 1, Legacy = 2, Exact = 3;

    public static bool IsRole(string k) { return k == RolePlayer || k == RoleEnemy; }

    // "@player" -> Roster.PlayerId, "@enemy" -> Roster.EnemyId, anything else unchanged
    public static string Resolve(string key)
    {
        if (key == RolePlayer) return Roster.PlayerId;
        if (key == RoleEnemy) return Roster.EnemyId;
        return key ?? "";
    }

    public static bool InRoster(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var all = Roster.All; if (all == null) return false;
        for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].id == id) return true;
        return false;
    }

    // roster id of an object NAME: exact objectName / id (after stripping "(Clone)" and " (n)"), then the old substring rule. "" = not a roster character.
    public static string IdOfName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string n = Strip(name);
        var all = Roster.All;
        if (all != null)
            for (int i = 0; i < all.Length; i++)
            {
                var r = all[i]; if (r == null || r.id.Length == 0) continue;
                if (string.Equals(n, r.objectName, StringComparison.OrdinalIgnoreCase) || string.Equals(n, r.id, StringComparison.OrdinalIgnoreCase)) return r.id;
            }
        return Roster.IdOfName(name);   // COMPAT: the old "name contains objectName / id" rule
    }
    public static string IdOf(Transform t) { return t == null ? "" : IdOfName(t.root.name); }

    static string Strip(string n)
    {
        n = n.Trim();
        int i = n.IndexOf("(Clone)", StringComparison.Ordinal); if (i > 0) n = n.Substring(0, i).Trim();
        if (n.EndsWith(")", StringComparison.Ordinal))
        {
            int p = n.LastIndexOf(" (", StringComparison.Ordinal);
            if (p > 0) { bool digits = n.Length - p - 3 > 0; for (int k = p + 2; k < n.Length - 1; k++) if (!char.IsDigit(n[k])) digits = false; if (digits) n = n.Substring(0, p).Trim(); }
        }
        return n;
    }

    // how well does a row key fit an object name (a fighter's name or its root name)?  see the header for the tiers
    public static int Tier(string rowKey, string name, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(rowKey) || string.IsNullOrEmpty(name)) return NoMatch;
        string id = IdOfName(name);
        if (rowKey == RoleAny) return id.Length > 0 ? Wildcard : NoMatch;
        if (IsRole(rowKey)) { string r = Resolve(rowKey); return id.Length > 0 && r == id ? Exact : NoMatch; }
        if (id.Length > 0 && string.Equals(rowKey, id, StringComparison.OrdinalIgnoreCase)) return Exact;
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (name.IndexOf(rowKey, cmp) >= 0) return Legacy;                                   // COMPAT: old substring match
        string on = Roster.Get(rowKey).objectName;                                           // COMPAT: ArmHold matched on the roster objectName of the key
        if (on.Length > 0 && InRoster(rowKey) && name.IndexOf(on, cmp) >= 0) return Legacy;
        return NoMatch;
    }

    // best row for a name (highest tier). Ties: lastWins = the later row (old `row = c` loops), else the first (old `return` / `break` loops). null = none.
    public static T Pick<T>(T[] rows, Func<T, string> key, string name, bool lastWins = true, bool ignoreCase = false) where T : class
    {
        if (rows == null) return null;
        T best = null; int bt = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i]; if (r == null) continue;
            int t = Tier(key(r), name, ignoreCase);
            if (t > bt || (t == bt && t > 0 && lastWins)) { best = r; bt = t; }
        }
        return best;
    }

    // the best tier any row reaches for this name (0 = none); a loader that keeps SEVERAL rows (WeaponGrip) keeps those with Tier == Best
    public static int Best<T>(T[] rows, Func<T, string> key, string name, bool ignoreCase = false) where T : class
    {
        int bt = 0; if (rows == null) return 0;
        for (int i = 0; i < rows.Length; i++) { if (rows[i] == null) continue; int t = Tier(key(rows[i]), name, ignoreCase); if (t > bt) bt = t; }
        return bt;
    }

    // a row keyed by roster ID (poise / ai_adapt style): exact id, a role or "*". "" is NOT a match here (each loader keeps its own "any" meaning).
    public static bool KeyIs(string rowKey, string id)
    {
        if (string.IsNullOrEmpty(rowKey) || string.IsNullOrEmpty(id)) return false;
        if (rowKey == RoleAny) return true;
        return Resolve(rowKey) == id;
    }
    // exact id / role row first, else the first "*" row, else null
    public static T ByIdOrDefault<T>(T[] rows, Func<T, string> key, string id) where T : class
    {
        if (rows == null) return null; T any = null;
        for (int i = 0; i < rows.Length; i++) { var r = rows[i]; if (r == null) continue; string k = key(r); if (k == RoleAny) { if (any == null) any = r; } else if (KeyIs(k, id)) return r; }
        return any;
    }

    // additive rows that must NOT take part in "*" (AIBrain zBias): the row name is the object name (old rule), a roster id, or a role
    public static bool NameIs(string rowName, string objName)
    {
        if (string.IsNullOrEmpty(rowName) || string.IsNullOrEmpty(objName)) return false;
        if (rowName == objName) return true;                                  // COMPAT: old exact GameObject-name rule
        if (rowName == RoleAny) return false;
        return Tier(rowName, objName) == Exact;
    }

    // does a free-text filter ("@enemy", "@player", a substring, "" = anything) apply to this object name? (ClashFx targetContains)
    public static bool NameFilter(string filter, string name)
    {
        if (string.IsNullOrEmpty(filter) || filter == RoleAny) return true;
        if (IsRole(filter)) { string id = IdOfName(name); return id.Length > 0 && Resolve(filter) == id; }
        return name != null && name.Contains(filter);
    }

    // scene-object names an editor list may hold: "@player" / "@enemy" -> the roster objectName, "@all" -> every roster objectName, else as written
    public static string[] ExpandNames(string[] names)
    {
        if (names == null) return null;
        var o = new System.Collections.Generic.List<string>();
        foreach (var n in names)
        {
            if (n == "@all") { var a = Roster.All; if (a != null) foreach (var r in a) if (r != null) o.Add(r.objectName); }
            else if (IsRole(n)) o.Add(Roster.Get(Resolve(n)).objectName);
            else o.Add(n);
        }
        return o.ToArray();
    }
}
