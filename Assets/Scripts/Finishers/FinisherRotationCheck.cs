using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// V62: proof that the finisher rotation (FinisherPlayer.Pick + FinisherRotationStore) does what the design says, without a scene:
//   1 per weapon: the picks cycle through every ENABLED + eligible id (sequence mode), never the same id twice in a row (while more than one is eligible)
//   2 the two weapons (Dracula "sword" / Pitborn "maul") keep independent positions even when their kills are interleaved
//   3 persisted: finisher_rotation.json is written, and after a simulated restart (memory dropped, file re-read) the next pick continues the cycle
//   4 mode "random" (data): no immediate repeat. Rotation data = finishers.json rotation.* (enabledIds, mode, noImmediateRepeat, weapons).
// It runs on a TEMP file (FinisherRotationStore.PathOverride), never on the player's persistentDataPath file. FinisherAuto calls Run() at the start of every -finisherauto
// (summary lines "rotation check: PASS/FAIL"); `-finisherrotcheck` alone runs only this and exits.
public static class FinisherRotationCheck
{
    public static bool Run(out string report)
    {
        var sb = new StringBuilder(); bool ok = true;
        string tmp = Path.Combine(Path.GetTempPath(), "finisher_rotation_check_" + Environment.TickCount + ".json");
        bool oldTest = FinisherRotationStore.TestMode; string oldPath = FinisherRotationStore.PathOverride;
        try
        {
            FinisherRotationStore.TestMode = true; FinisherRotationStore.PathOverride = tmp; FinisherRotationStore.Clear();
            var file = FinisherConfig.Get(); var rot = file.rotation;
            sb.AppendLine("rotation data: mode=" + rot.mode + " noImmediateRepeat=" + rot.noImmediateRepeat + " enabledIds=[" + string.Join(",", rot.enabledIds ?? new string[0]) + "] weapons=" + rot.weapons.Dracula + "/" + rot.weapons.Pitborn);
            var ctxs = new[] {
                new FinisherContext { killerSide = 0, victimSide = 1, move = FinisherMove.Heavy, loc = FinisherLoc.Head },
                new FinisherContext { killerSide = 1, victimSide = 0, move = FinisherMove.Heavy, loc = FinisherLoc.Head } };
            var elig = new List<string>[2]; int emptyW = 0;   // V62B
            for (int s = 0; s < 2; s++)
            {
                FinisherPlayer.ResetRotationForTest(); FinisherRotationStore.Clear();
                elig[s] = FinisherPlayer.EligibleIds(ctxs[s]);
                string wp = s == 0 ? rot.weapons.Dracula : rot.weapons.Pitborn;
                sb.AppendLine("[" + wp + "] eligible: " + string.Join(", ", elig[s].ToArray()));
                if (elig[s].Count == 0) { sb.AppendLine("  WARN no enabled entry for this weapon (rotation.enabledIds): its kills are plain deaths until one is enabled"); emptyW++; }
            }

            if (emptyW == 2) { sb.AppendLine("  FAIL no weapon has an eligible entry (finishers would never play)"); ok = false; }
            // 1 + 2: interleaved kills, 3 full cycles each (+1)
            FinisherPlayer.ResetRotationForTest(); FinisherRotationStore.Clear();
            var seq = new List<string>[] { new List<string>(), new List<string>() };
            int rounds = 3 * Math.Max(elig[0].Count, elig[1].Count) + 1;
            for (int i = 0; i < rounds; i++)
                for (int s = 0; s < 2; s++)
                {
                    if (elig[s].Count == 0) continue;
                    var e = FinisherPlayer.Pick(ctxs[s]); seq[s].Add(e != null ? e.id : "(null)");
                }
            for (int s = 0; s < 2; s++)
            {
                string wp = s == 0 ? rot.weapons.Dracula : rot.weapons.Pitborn; int n = elig[s].Count; if (n == 0) continue;
                sb.AppendLine("[" + wp + "] picks: " + string.Join(" > ", seq[s].ToArray()));
                bool noRep = true; for (int i = 1; i < seq[s].Count; i++) if (n > 1 && seq[s][i] == seq[s][i - 1]) noRep = false;
                bool inSet = seq[s].TrueForAll(x => elig[s].Contains(x));
                bool cover = true; if (rot.mode != "random") foreach (var id in elig[s]) if (!seq[s].Contains(id)) cover = false;
                bool periodic = true; if (rot.mode != "random") for (int i = n; i < seq[s].Count; i++) if (seq[s][i] != seq[s][i - n]) periodic = false;
                sb.AppendLine("  noImmediateRepeat=" + noRep + " onlyEnabled=" + inSet + " coversAll=" + cover + " cycle=" + periodic);
                if (!(noRep && inSet && cover && periodic)) ok = false;
            }

            // 3: persisted + restart continues
            bool fileOk = File.Exists(tmp); string raw = fileOk ? File.ReadAllText(tmp) : "";
            sb.AppendLine("file written: " + fileOk + (fileOk ? " (" + raw.Length + " bytes, rows for " + (raw.Contains("\"weapon\": \"" + rot.weapons.Dracula + "\"") ? rot.weapons.Dracula : "-") + "/" + (raw.Contains("\"weapon\": \"" + rot.weapons.Pitborn + "\"") ? rot.weapons.Pitborn : "-") + ")" : ""));
            if (!fileOk) ok = false;
            for (int s = 0; s < 2; s++)
            {
                int n = elig[s].Count; if (n == 0 || seq[s].Count == 0) continue;
                string last = seq[s][seq[s].Count - 1];
                FinisherPlayer.ResetRotationForTest();     // = the game was closed: memory gone, only the file is left
                var e = FinisherPlayer.Pick(ctxs[s]); string got = e != null ? e.id : "(null)";
                string want = rot.mode == "random" ? null : elig[s][(elig[s].IndexOf(last) + 1) % n];
                bool good = rot.mode == "random" ? (n < 2 || got != last) : got == want;
                sb.AppendLine("[" + (s == 0 ? rot.weapons.Dracula : rot.weapons.Pitborn) + "] after restart: last=" + last + " next=" + got + (want != null ? " expected=" + want : "") + " -> " + (good ? "ok" : "FAIL"));
                if (!good) ok = false;
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION " + ex); ok = false; }
        finally
        {
            try { FinisherRotationStore.Clear(); } catch { }
            FinisherRotationStore.TestMode = oldTest; FinisherRotationStore.PathOverride = oldPath;
            FinisherPlayer.ResetRotationForTest();    // the real run re-reads the player's own file on its first pick
        }
        sb.AppendLine("rotation check: " + (ok ? "PASS" : "FAIL"));
        report = sb.ToString(); return ok;
    }
}
