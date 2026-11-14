# ARRIVALGRACE v1 (STAGED, NOT APPLIED, NOT COMPILED). Bandits dev, file-only.
Cause: ZoneRuntime.SpanEnter (seamless-world zone switch, called from ZoneSpan.cs:202 when Joan walks z4 -> z5 -> z6) sets arriveGraceUntil = 0f. The existing engage grace
(zones.json engage.graceAfterArriveSeconds 8.0) gates Engage in Poll (ZoneRuntime.cs:329) for gate/load arrivals only, so on a walked arrival the pre-boss raiders (aggro 12-13 at 4.7-12.5 m) engage on frame 1.
Fix = CODE (2 files) + DATA (1 kit), default off:
- make_patch.py reads the tree ZoneData.cs (4fdfb56af7) and ZoneRuntime.cs (45bbc7918c), writes ZoneData.cs.patched (e8626bed54), ZoneRuntime.cs.patched (20f5f3060c) + .diff. Braces balanced (50/50, 197/197). unity-dev: copy each .patched over Assets\Scripts\Zones\<name>, CODE-build.
- New zones.json engage keys: spanGraceSeconds (0 = old behaviour) and spanGraceZones (ids; empty = all zones). SpanEnter now sets arriveGraceUntil = now + spanGraceSeconds for listed zones.
- Data kit: python -I _agents\laneLG\lgkit.py spangrace apply --go  (zones.json 9ec689fe63 -> 0f092887dc: spanGraceSeconds 10.0, spanGraceZones z5_humbaba + z6_erlking). Inert until the code is built (JsonUtility ignores unknown keys). Revert: spangrace revert --go. QA copy: --target <copy StreamingAssets>.
Side effects: the grace also holds the boss engage for the same seconds (bosses stand 26-34 m away, fine); a creature already engaged is unaffected; fromGate/load arrivals keep graceAfterArriveSeconds 8.0.
Proof (needs a build): walk z4 -> z5 on a QA copy with the kit; no [Zones] engage / no raider adjacent for 10 s after the span enter, then they engage as before. Unproven in Unity.
