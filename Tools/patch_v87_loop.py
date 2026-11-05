P=r"C:\Users\dom12\Documents\unity_test_hdrp\Assets\Scripts\LoopTest.cs"
b=open(P,'rb').read(); bom=b.startswith(b'\xef\xbb\xbf'); crlf=b'\r\n' in b; s=b.decode('utf-8-sig').replace('\r\n','\n')
if 'v87 loot gate' in s: print('ALREADY'); raise SystemExit
def rep(a,n):
    global s
    assert s.count(a)==1,(a[:70],s.count(a)); s=s.replace(a,n)
rep('Check(D.stamina >= D.maxStamina - 0.01f && P.stamina >= P.maxStamina - 0.01f, tag + "stamina not full','Check(D.stamina >= D.maxStamina * 0.5f && P.stamina >= P.maxStamina * 0.5f, tag + "stamina below 50% at the fight start (v87: the AI may already have swung, the check is for a fresh reset)')
rep('            lastOutcome = res;\n','''            lastOutcome = res;
            // v87 loot gate: the first victory must drop loot, a pickup must be equippable and change the slot (Goose: the loot path was ungated)
            if (res == GameLoop.Result.Victory && !lootChecked)
            {
                lootChecked = true; float l0 = Time.realtimeSinceStartup; LootPickup[] pk = new LootPickup[0];
                while (Time.realtimeSinceStartup - l0 < 6f) { pk = FindObjectsByType<LootPickup>(FindObjectsSortMode.None); if (pk.Length > 0) break; yield return null; }
                Check(LootBridge.HasDropSource, tag + "loot: no drop source registered (GearDrop missing)");
                Check(pk.Length > 0, tag + "loot: the victory spawned NO loot pickup within 6 s");
                if (pk.Length > 0)
                {
                    var pick = pk[0]; string iid = pick.itemId; var sl = LootBridge.Slots; bool eq = sl != null && sl.Equip(iid); string where = "";
                    if (sl != null) { var cs = LootCfg.Get().slots; for (int q = 0; q < cs.Length; q++) if (cs[q] != null && sl.GetEquipped(cs[q].id) == iid) where = cs[q].id; }
                    Check(eq && where.Length > 0, tag + "loot: equipping '" + iid + "' did not change any slot (Equip " + eq + ", slot '" + where + "')");
                    Log("loot gate: " + pk.Length + " pickup(s), first '" + iid + "' equipped to slot '" + where + "'");
                }
            }
''')
rep('        GameLoop.Result lastOutcome = GameLoop.Result.None;\n','        GameLoop.Result lastOutcome = GameLoop.Result.None; bool lootChecked = false;\n')
s=s.replace('\n','\r\n') if crlf else s; o=s.encode('utf-8'); o=(b'\xef\xbb\xbf'+o) if bom else o; open(P,'wb').write(o); print('patched')
