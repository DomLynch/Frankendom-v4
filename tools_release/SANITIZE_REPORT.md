# Sanitize report

Scan of every copied file (regex): the Hugging Face / OpenAI / GitHub / AWS key prefixes (hf_, sk-, ghp_, gho_, AKIA), password/passwd, api_key, secret, token, `Bearer `, private-key headers, ssh-/id_rsa, IPv4 addresses, e-mail addresses, the owner's user-profile name, local_<uuid> session ids, "tripo", `pc@` / `ssh `.

## Result

- **Credentials: none found.** No API keys, passwords, Bearer tokens, ssh keys, IP addresses, e-mail addresses, session ids, or Tripo / other service credentials. No file was dropped for a credential.
- **Private data: only the Windows user-profile path** (`C:\Users\<owner>\...`), in the files listed below. Generalized as described. The hit lines are listed for the record; line numbers are those of the copy before sanitizing (a replacement never adds or removes lines, so they still match).
- **Left on purpose** (not secrets, not user specific): `C:\Program Files\Unity\Hub\Editor\6000.6.7f1\...`, `C:\Program Files\Blender Foundation\Blender 5.2\blender.exe`, `C:\HANDOVER-Macbook` (`Tools\paths.json`, `paths.py`), the optional `D:\` QA scratch drive in `gateproof.ps1`, `FrankendomSmoke` save dir under LocalAppData, the game identity `Dom Games` / `Frankendom`, the first name "Dom" in rule comments, sibling project folders `work\armagedom-unity` and `work\fronteirdom` (now under `$env:USERPROFILE`), references to the board file `_agents\COORDINATION.md` (the file itself is not copied).
- **Environment variables the scripts need**: `FK_ROOT` (project root; `env.ps1` sets it). Optional, as before: `FK_FLIP_ALLOW_DIFF`, `FK_FORCING_ASSET`, `FK_OWNER`, `FK_PURPOSE`, `HCKIT2_LIVE`, `HCKIT7_LIVE`, `FPS_ROOT`, other `FK_*` knobs read by `gateproof.ps1`.
- **How it was changed**: PowerShell: a whole single-quoted literal `'<repo>'` became `$env:FK_ROOT`; `'<repo>\rest'` became `"$env:FK_ROOT\rest"` (only when the literal held no `$`, quote or backtick); double-quoted or bare uses became `$($env:FK_ROOT)`; the profile root likewise with `$env:USERPROFILE`. Python: the literal became `(os.environ.get('FK_ROOT') or os.path.join(os.path.expanduser('~'), 'Documents', 'unity_test_hdrp'))`; docstrings use `%USERPROFILE%`. JSON: `C:/Users/<USERNAME>` placeholder (`Tools\paths.json`; edit it on each machine). Encodings and BOMs preserved.
- **Verified after sanitizing**: every `.ps1` parses with 0 errors (`[Management.Automation.Language.Parser]::ParseFile`); every `.py` passes `ast.parse` (`laneINT107/check107.py` carries a pre-existing invalid-escape SyntaxWarning in a docstring); a final scan finds no remaining user name.
- **Not run**: no script was executed; no build, game or QA.
- Not parse-checkable: nothing failed. Behaviour is not tested, only syntax.

## Findings (file:line, what was changed)

| Location | Action |
|---|---|
| `agents_root/qa_slot.ps1:19` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `agents_root/qa_slot.ps1:81` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneBUILDSPEED/compile_bs.ps1:2` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneBUILDSPEED/persistent_probe.ps1:2` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneBUILDSPEED/persistent_probe.ps1:17` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/flip.ps1:4` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/gateproof.ps1:8` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/gateproof.ps1:24` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/gateproof.ps1:25` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/gateproof.ps1:190` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/gateproof.ps1:198` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/gateproof.ps1:213` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/gateproof.ps1:215` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/gateproof.ps1:219` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/gateproof.ps1:224` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/mem_watch.ps1:5` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/mem_watch.ps1:6` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/mem_watch.ps1:23` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/windowcheck.ps1:2` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneHOTSHIP/windowcheck.ps1:10` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneINT107/build_chain107.py:8` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneINT107/check107.py:4` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneINT107/compile107.ps1:1` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneINT107/compile107.ps1:4` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneINT107/gates107.ps1:4` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneINT107/play_zones.ps1:12` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `laneINT107/zones_switch107.py:9` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `pool/unity_busy.py:4` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `pool/unity_busy.py:9` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/build_compileonly.ps1:11` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/build_scriptsonly.ps1:6` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/build_suspend_watch.ps1:5` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/build_suspend_watch.ps1:6` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/build_texstream.ps1:7` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/compile_check.ps1:2` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/data_gates.py:24` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/data_gates.py:130` | false positive (the word "token" in a variable, doc string or a Unity licence log phrase); no change |
| `Tools/data_gates.py:139` | false positive (the word "token" in a variable, doc string or a Unity licence log phrase); no change |
| `Tools/data_gates.py:146` | false positive (the word "token" in a variable, doc string or a Unity licence log phrase); no change |
| `Tools/data_gates.py:158` | false positive (the word "token" in a variable, doc string or a Unity licence log phrase); no change |
| `Tools/data_gates.py:516` | false positive (the word "token" in a variable, doc string or a Unity licence log phrase); no change |
| `Tools/gate.ps1:2` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/gate.ps1:20` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/gate_config.json:2` | false positive (the word "token" in a variable, doc string or a Unity licence log phrase); no change |
| `Tools/gate_lean.ps1:3` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/gate_lean.ps1:15` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/hc_gate.py:15` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/missing_keys.py:25` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/paths.json:4` | user name replaced by <USERNAME> |
| `Tools/paths.py:1` | false positive (the word "token" in a variable, doc string or a Unity licence log phrase); no change |
| `Tools/paths.py:6` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/prune_builds.ps1:4` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/unity_roots.ps1:1` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/_build_guard.ps1:14` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/_build_guard.ps1:26` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/_build_guard.ps1:103` | false positive (the word "token" in a variable, doc string or a Unity licence log phrase); no change |
| `Tools/_build_guard.ps1:130` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/_build_sig.ps1:9` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/_tag_reserve.ps1:7` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `Tools/_tag_reserve.ps1:17` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/boot_check.ps1:5` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/build_preflight.ps1:8` | false positive (the word "token" in a variable, doc string or a Unity licence log phrase); no change |
| `unitydev/build_preflight.ps1:9` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/build_preflight.ps1:13` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/build_preflight.ps1:19` | false positive (the word "token" in a variable, doc string or a Unity licence log phrase); no change |
| `unitydev/chain_data_check.py:4` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/chain_timed.ps1:12` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/compile_noharness.ps1:1` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/compile_noharness.ps1:4` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/disk_guard.ps1:3` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/gate_release.ps1:4` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/hot_vs_tree.ps1:4` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/play_all_zones.ps1:18` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/prod_data_check.ps1:3` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
| `unitydev/release.ps1:6` | user path generalized: repo root -> $env:FK_ROOT, profile -> $env:USERPROFILE (py: FK_ROOT env or ~/Documents/unity_test_hdrp) |
