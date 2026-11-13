using System;
using System.Collections.Generic;

// POTIONS v1: the carried quantity of every stackable item (potions). Saved by LootSave as `stacks` (loot_save.json version 3); gear stays in `owned` (one copy per id), stacks never enter `owned`,
// so the equip screen, the gear catalog and the old market sell list are untouched.
// An old (version 1 / 2) save has no `stacks`: JsonUtility leaves the empty default, Load() sees nothing = no potions. Quantities are kept even while the master switch is off (switching it off again must not eat the potions).
[Serializable] public class StackRow { public string id = ""; public int qty; }

public static class PotionStore
{
    static readonly Dictionary<string, int> q = new Dictionary<string, int>();
    static readonly List<string> order = new List<string>();   // insertion order = save order, so the file is stable

    /// <summary>True when `id` is a stackable item the market and the use key handle: master on, a known row, an implemented effect.</summary>
    public static bool IsStack(string id) { return PotionCfg.On && PotionCfg.Implemented(PotionCfg.Find(id)); }
    public static int Qty(string id) { int n; return !string.IsNullOrEmpty(id) && q.TryGetValue(id, out n) ? n : 0; }
    public static void Clear() { q.Clear(); order.Clear(); }

    /// <summary>Replace the state from a saved array. Damaged rows (no id, qty below 1, a repeated id) are dropped; a quantity is cut to the row's maxCarry when the row is known.</summary>
    public static void Load(StackRow[] rows)
    {
        Clear(); if (rows == null) return;
        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i]; if (r == null || string.IsNullOrEmpty(r.id) || r.qty < 1 || q.ContainsKey(r.id)) continue;
            var p = PotionCfg.Find(r.id); int n = r.qty; if (p != null && p.maxCarry >= 1 && n > p.maxCarry) n = p.maxCarry;
            q[r.id] = n; order.Add(r.id);
        }
    }
    public static StackRow[] ToRows()
    {
        var l = new List<StackRow>(); for (int i = 0; i < order.Count; i++) { int n = Qty(order[i]); if (n > 0) l.Add(new StackRow { id = order[i], qty = n }); }
        return l.ToArray();
    }
    public static void Set(string id, int n)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (n <= 0) { q.Remove(id); order.Remove(id); return; }
        if (!q.ContainsKey(id)) order.Add(id);
        q[id] = n;
    }
    /// <summary>STARTSTACKS v1: a NEW save (no file / New game) starts with consumables.json startStacks. Only known, implemented potions; the quantity is cut to maxCarry. Empty list or potions off = nothing.</summary>
    public static void GrantStart()
    {
        if (!PotionCfg.On) return; var rows = PotionCfg.Get().startStacks; if (rows == null) return;
        foreach (var r in rows) { if (r == null || string.IsNullOrEmpty(r.id) || r.qty < 1) continue; var p = PotionCfg.Find(r.id); if (!PotionCfg.Implemented(p)) continue; Set(r.id, p.maxCarry >= 1 ? Math.Min(r.qty, p.maxCarry) : r.qty); }
    }
    public static void Add(string id, int n) { Set(id, Qty(id) + n); }
    public static bool CanAdd(string id, int n, int maxCarry) { return n >= 1 && (long)Qty(id) + n <= Math.Max(1, maxCarry); }
    /// <summary>Take one. False when none is carried.</summary>
    public static bool Take(string id) { int n = Qty(id); if (n < 1) return false; Set(id, n - 1); return true; }
}
