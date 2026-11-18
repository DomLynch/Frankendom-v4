# Frankendom: audit brief for a new developer (18 Nov 2026)

Repo: https://github.com/DomLynch/Frankendom-v4 (branch `main`). Unity 6, HDRP, Windows. Third-person melee action game: Joan of Arc against five bosses (Pitborn, Sobek, Grendel, Humbaba, Erlking) plus bandit groups, in seven zones, with two markets and gear that carries over. Target: a free Steam alpha. Max-quality HDRP is the bar; no visible quality cuts for fps without Dom's say.

## What to read first (in this order)
1. `AGENTS.md`: the working rules, release paths (HOT / CODE / CHAIN), build guards, and the data traps. Points 8 to 12 are the newest.
2. `docs/AUDIT_BRIEF.md` (this file).
3. The handover bundle you were sent with this brief (not in the repo): `HANDOVER_LEAD_2026-11-17_NIGHT.md`, `DATA_LAYER_PLAN`, `content_path_table.md`, `new_body_path.md`, `balance_weight_old_vs_new.md`.

## How the game is organised
- Code: `Assets/Scripts/` (combat in `Combat/`, zones in `Zones/`, bows in `Combat/BowVisual.cs`, `BanditArcher.cs`, `NpcBowAnim.cs`; quality and upscaling in `QualityTiers.cs`; foot IK in `FootGround.cs`; runtime look swap in `MonsterLook.cs`; runtime props in `Zones/PropsRuntime.cs`).
- Data (everything tuneable is JSON): `Assets/Data/*.json`, mirrored into `Assets/StreamingAssets/` (the player reads StreamingAssets). Keep both copies identical.
- Rule: nothing hard-coded where a data row can carry it. Dom's standing rule is "everything on the data layer".

## How a change ships
- HOT = data only, copied into a build folder in about a minute (`deploy_data.ps1` then `flip.ps1`). CODE = a compile-only build of Assembly-CSharp.dll (`Tools/build_compileonly.ps1`, about 80 s, no new serialized fields). CHAIN = Editor bake or full player build (15 to 60 min), only for baked assets.
- Release first, test after. Every build and flip prints `PATH HOT | CODE | FULL`. Roll-back is `PLAY_PREV.bat`.
- Traps already found: a JSON edit can do nothing at runtime. Example: `move-defs.json` does not change moves already baked in `Moves.asset` unless the row carries `"overrides": true` (`MoveDefsOverlay.cs`). Always find the reader and look for the log line that proves a change applied.

## State on 18 Nov (live build HDRP_v109tq)
Done recently: heavier combat timings (light wind-up 24, heavy 42, heavy damage 21, longer heavy hit-stop); enemy attack clip speeds capped near 1.5x; STP upscaling at 77% on by default (menu: native 100 / 77 / 67); runtime prop layer (off by default); alias plus MonsterLook path for new enemies on an existing rig (pilot proven in log); NPC archers with real draw, hold and release animation; foot grounding fixes for Cedar, Root Druid and Humbaba.

Measured numbers you can check: STP at z1 gave about 47 fps against 34 fps native; at z5 it gave about +4% (z5 runs near 20 fps at either scale, so z5 is not resolution-bound). The 100+ ms frame spikes seen in QA traces are mostly the screenshot capture in the test harness; without capture only 1 to 2% of frames exceed 80 ms, and their source is not yet named. BalanceAuto, old versus new combat weight (4 seeds each): Pitborn fights 39% longer, Sobek 22% longer, Reed Skirmisher unchanged.

## Known weak points and open items (good audit targets)
- Performance: z5 near 20 fps and not resolution-bound; unexplained frame spikes outside game scripts; about 10.7 GB of textures resident, 9 GB of it on no active renderer. A per-zone split (plan in the bundle) is the only real memory lever and waits for Dom's word.
- Combat feel: new timings are live; Dom has not yet signed them off. BalanceAuto needs 12 to 20 seeds per boss before any balance change.
- Archers: the Huntsman's draw hand sits on his head; the bow arm flails at release.
- z5 visuals: a streaked ground-texture band across the middle and bone claws clipping the screen edge (cause not named).
- Apothecary vendor: wants an existing body in place of the Tripo one; tint rows did not take effect on the replacement.
- Weapon design: Dom dislikes the current great-axe (`Assets/StreamingAssets/Gear/Erlking_axe.glb`, second hooked blade and an awkward hold); a new one is being designed.
- Data layer: still baked today: `props_layout_*.json` rows, LOD swaps, `lod_tex_map.json`, terrain, camp structure rows, a new enemy with its own skeleton, new Joan clips. Runtime audio is not audited.

## What we would like from an audit
1. Performance: name the biggest CPU and GPU costs at z1 and z5 from a clean run, and the source of the non-capture frame spikes.
2. Data-layer gaps: anything still hard-coded or baked that could be a data row; any other JSON that looks authoritative but is not (see the `Moves.asset` trap).
3. Code health: dead features (the texture-share and texture-compress experiments), duplicated systems, risky serialized fields.
4. Combat and AI quality against the bar "AAA, weighty, readable".
5. Release process: gaps in the build guards and QA harness (`_agents/laneHOTSHIP/gateproof.ps1` is not in the repo; ask for it).

## Notes for the auditor
- Large assets and builds are not in the repo (builds live under `Builds/` on the owner's laptop); audio, art and some tooling are not here. Ask for anything you need.
- Do not push to `main` without agreeing it with Dom. Keep credentials and private reference material out of the public repo.
