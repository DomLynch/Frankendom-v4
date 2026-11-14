import os,json,hashlib,glob
R=r'C:\Users\dom12\Documents\unity_test_hdrp'
LIVE=R+r'\Builds\HDRP_v109ma\HadesSlice_Data\StreamingAssets'
HERE=os.path.dirname(os.path.abspath(__file__))
def md5(p): return hashlib.md5(open(p,'rb').read()).hexdigest() if os.path.isfile(p) else None
J=[
('look_zones.json','per-zone look rows: fog, sky, exposure, tint, light','laneLOOK/ENVKIT'),
('look.json','scene exposure, global look values','laneLOOK'),
('look_options.json','look option switches (see-through etc.)','laneLOOK'),
('accent_lights.json','accent/pool lights: candela, colour, cookie, bounce','laneENVKIT'),
('camp_lights.json','camp/hub light rows','laneENVKIT'),
('camp_backdrop.json','camp backdrop cards','laneENVKIT'),
('hdrp_lighting_ultra.json','global HDRP lighting, fog, bounce, sun','laneLOOK'),
('hdrp_fixes.json','HDRP material/light fixes, player fill','laneLOOK'),
('hdrp_furnace.json','furnace light/flipbook look','laneENVKIT'),
('hdrp_char_mats.json','HDRP character material values','laneOpenTrident'),
('hdrp_char_layer.json','character render layer','laneLOOK'),
('hdrp_water.json','water look','laneENVKIT'),
('atmosphere.json','local volumetric fog boxes, atmosphere rows','laneLOOK'),
('world_fog.json','world fog rows','laneLOOK'),
('fogssgi_tuning.json','fog / SSGI tuning','laneLOOK'),
('ground_fog_wisps.json','ground fog wisp particles','laneENVKIT'),
('zone_sky.json','per-zone sky','laneENVKIT'),
('zone_motes.json','floating motes/dust','laneENVKIT'),
('apv_zones.json','adaptive probe volume zones (bounce)','laneENVKIT'),
('doorway.json','doorway/gate look','laneENVKIT'),
('gateway.json','gateway rift look','laneENVKIT'),
('door_glow_card.json','door glow card','laneENVKIT'),
('props_layout.json','prop placements (master)','laneENVKIT'),
('props_layout_ground.json','prop placements, ground','laneENVKIT'),
('props_layout_hub.json','prop placements, hub','laneENVKIT'),
('props_layout_zone2.json','prop placements zone 2','laneENVKIT'),
('props_layout_zone5.json','prop placements zone 5','laneENVKIT'),
('props_layout_zone6.json','prop placements zone 6','laneENVKIT'),
('props_layout_zone7.json','prop placements zone 7','laneENVKIT'),
('props_layout_grendel.json','prop placements Grendel arena','laneENVKIT'),
('props_overlay.json','prop overlay edits','laneENVKIT'),
('prop_grime.json','prop grime look','laneENVKIT'),
('prop_grime_decals.json','prop grime decals','laneENVKIT'),
('zone_span.json','zone extents/span','laneENVKIT'),
('zone_ground.json','zone ground rows','laneENVKIT'),
('zone_ground_mat.json','zone ground materials','laneENVKIT'),
('ground.json','ground base','laneENVKIT'),
('ground_patches.json','ground patch decals','laneENVKIT'),
('paving_lower.json','lower paving','laneENVKIT'),
('decals.json','decal rows','laneENVKIT'),
('static_decals.json','static decal placements','laneENVKIT'),
('wet_decals.json','wet decals','laneENVKIT'),
('wetness.json','surface wetness','laneENVKIT'),
('world_mats.json','world material overrides','laneENVKIT'),
('bridge_mats.json','bridge material overrides','laneENVKIT'),
('matadjust_runtime.json','runtime material adjust (tint/smoothness)','laneLOOK'),
('character_materials.json','character material rows','laneOpenTrident'),
('char_sss.json','character SSS','laneOpenTrident'),
('char_hair.json','character hair look','laneOpenTrident'),
('char_tex_import.json','character texture import','laneOpenTrident'),
('character_sep.json','character separation light','laneLOOK'),
('pitborn_skin_tint.json','pitborn skin tint','laneOpenTrident'),
('fx_materials.json','FX materials','laneFX'),
('lod_tex_map.json','LOD texture map','laneF5/OPT'),
('quality_tiers.json','quality tier values','laneTIERS'),
('runtime_flags.json','render feature flags (SSGI, fog, clouds, scale)','laneTIERS'),
('runtime_flags_balance.json','tier flags balance','laneTIERS'),
('runtime_flags_high.json','tier flags high','laneTIERS'),
('runtime_flags_medium.json','tier flags medium','laneTIERS'),
('runtime_flags_ultra.json','tier flags ultra','laneTIERS'),
('shadow_cache.json','shadow cache','laneF5/OPT'),
('shadow_fixes.json','shadow fixes','laneF5/OPT'),
('shadow_proxy.json','shadow proxies','laneF5/OPT'),
('shadow_saver.json','shadow saver','laneF5/OPT'),
('startup_warm.json','startup warm / loading','laneF5'),
('loading_card.json','loading card look','laneF5'),
('loot_ui.json','loot UI look','laneHUD'),
('hud.json','HUD look','laneHUD'),
('hud_layout.json','HUD layout','laneHUD'),
('menu.json','menu look','laneHUD'),
('menu_layout.json','menu layout','laneHUD'),
('zone_ui.json','zone UI (cover, end screen)','laneHUD'),
('camera_framing.json','camera framing','laneCAM'),
('camera_pose.json','camera pose','laneCAM'),
('camera_free.json','free camera','laneCAM'),
('camera_reframe.json','camera reframe','laneCAM'),
('camera_impact.json','camera shake/impact','laneCAM'),
('camera_death.json','death camera','laneCAM'),
('camera_collision.json','camera collision','laneCAM'),
('camera_occlusion.json','camera occlusion fade','laneCAM'),
('boss_phase.json','boss ring / telegraph / phase flash','laneBOSS'),
('blood.json','blood FX','laneFX'),
('blood_edge.json','blood edge','laneFX'),
('blood_shapes.json','blood shapes','laneFX'),
('wounds.json','wound decals','laneFX'),
('hit_flash.json','hit flash','laneFX'),
('hit_vfx.json','hit VFX','laneFX'),
('hit_fx.json','hit FX','laneFX'),
('clash_fx.json','clash FX','laneFX'),
('arc_fx.json','swing arc FX','laneFX'),
('foliage.json','foliage','laneENVKIT'),
('birds.json','birds','laneENVKIT'),
('crack_flicker.json','crack light flicker','laneENVKIT'),
('hub.json','hub NPC tint/look','laneENVKIT'),
('vendor_reveal.json','vendor reveal','laneENVKIT'),
('geo_lod.json','geometry LOD switching','laneF5/OPT'),
('fighter_lod.json','fighter LOD','laneF5/OPT'),
('cut_lod.json','cut LOD','laneF5/OPT'),
('hero_prewarm.json','hero prewarm','laneF5'),
('texture_plan.json','texture plan','laneF5/OPT'),
('trench_setup.json','zone 5 trench layout and lava','laneENVKIT'),
('gear.json','gear materials/slots (look rows)','laneGEAR'),
('hdrp_asset_flags.txt','HDRP asset feature flags','laneLOOK'),
]
rows=[];miss=[]
for f,c,o in J:
    t=R+'\\Assets\\Data\\'+f; s=R+'\\Assets\\StreamingAssets\\'+f; l=LIVE+'\\'+f
    if not os.path.isfile(t): miss.append(f); continue
    rows.append(dict(f=f,c=c,o=o,t=md5(t),s=md5(s),l=md5(l)))
cs=['Assets/Scripts/Combat/GroundRing.cs','Assets/Scripts/StartupWarm.cs','Assets/Scripts/HUD/LoadingCard.cs','Assets/Scripts/QualityTiers.cs','Assets/Scripts/RuntimeQuality.cs','Assets/Scripts/CameraRig.cs','Assets/Scripts/LookData.cs','Assets/Scripts/LookOptions.cs','Assets/Scripts/FogSsgiTuning.cs','Assets/Scripts/MatAdjustRuntime.cs','Assets/Scripts/Zones/ZoneLook.cs','Assets/Scripts/Zones/ZoneSky.cs','Assets/Scripts/Zones/AccentLights.cs','Assets/Scripts/Zones/CampLights.cs','Assets/Scripts/Zones/ZoneMotes.cs','Assets/Scripts/Zones/ZoneGround.cs','Assets/Scripts/Zones/PropsOverlay.cs','Assets/Scripts/Vis/ZoneGroundMat.cs','Assets/Scripts/Vis/StaticDecals.cs','Assets/Scripts/Vis/VisMatPass.cs','Assets/Scripts/Vis/Foliage.cs','Assets/Scripts/CharMatOverride.cs','Assets/Scripts/Gear/GearMaterials.cs','Assets/Scripts/DoorGlowCard.cs','Assets/Scripts/GatewayRift.cs','Assets/Scripts/Combat/FxMaterials.cs','Assets/Scripts/Combat/HitFlash.cs','Assets/Scripts/Combat/BloodGround.cs','Assets/Scripts/Combat/WoundDecals.cs','Assets/Scripts/ShadowFixes.cs','Assets/Scripts/ShadowCache.cs','Assets/Scripts/ShadowProxy.cs','Assets/Scripts/CrackFlicker.cs','Assets/Scripts/FurnaceFlicker.cs','Assets/Scripts/AtmosFlicker.cs','Assets/Scripts/PerfFixes.cs']
cs+=[os.path.relpath(p,R).replace('\\','/') for p in glob.glob(R+r'\Assets\Editor\Hdrp*.cs')]
cs+=[os.path.relpath(p,R).replace('\\','/') for p in glob.glob(R+r'\Assets\HDRP\*.asset')]
cs=sorted(set(c for c in cs if os.path.isfile(R+'/'+c)))
crow=[(c,md5(R+'/'+c)) for c in cs]
tex=[]
SA=R+'\\Assets\\StreamingAssets'
for d in ['StaticDecals','Foliage','CharTex','MonsterLook']:
    for p in sorted(glob.glob(SA+'\\'+d+'\\**\\*',recursive=True)):
        if os.path.isfile(p) and not p.endswith('.meta'):
            q=os.path.relpath(p,R).replace('\\','/')
            tex.append((q,md5(p),md5(LIVE+'\\'+os.path.relpath(p,SA))))
M={}
for r in rows:
    M['Assets/Data/'+r['f']]=r['t']
    if r['s']: M['Assets/StreamingAssets/'+r['f']]=r['s']
for c,h in crow: M[c]=h
for q,h,_ in tex: M[q]=h
json.dump(M,open(HERE+'\\freeze_md5.json','w'),indent=1,sort_keys=True)
pairdiff=[r['f'] for r in rows if r['s']!=r['t']]
nolive=[r['f'] for r in rows if r['l'] is None]
livediff=[r['f'] for r in rows if r['l'] and r['l']!=r['t']]
texlive=[q for q,h,l in tex if l!=h]
o=['# FREEZE - ALPHA LOOK FROZEN list (laneF5)\n','Generated by gen_freeze.py 2026-11-14. Tree = Assets\\ ; live build = Builds\\HDRP_v109ma.\n',
"**Rule: changes to these files after ALPHA LOOK FROZEN need Dom's yes.**\n",
'## Counts\n',
f'- data files (each with Assets\\Data + StreamingAssets twin): {len(rows)}',
f'- C# / HDRP asset look files: {len(crow)}',
f'- StreamingAssets texture files (StaticDecals, Foliage, CharTex, MonsterLook): {len(tex)}',
f'- total hashed paths in freeze_md5.json: {len(M)}',
f'- twin pairs that differ: {len(pairdiff)} {pairdiff}',
f'- data files missing from live build: {len(nolive)} {nolive}',
f'- data files where tree != live build: {len(livediff)} {livediff}',
f'- texture files where tree != live build or missing: {len(texlive)} {texlive[:30]}','',
'## Data files\n','| file | controls | owner | md5 (tree) | twin | live |','|---|---|---|---|---|---|']
for r in rows:
    o.append(f"| {r['f']} | {r['c']} | {r['o']} | {r['t']} | {'EQ' if r['s']==r['t'] else ('MISSING' if not r['s'] else 'DIFF')} | {'EQ' if r['l']==r['t'] else ('MISSING' if not r['l'] else 'DIFF')} |")
o+=['','## Code and HDRP asset files (owner laneLOOK unless the file says otherwise)\n','| path | md5 |','|---|---|']+[f'| {c} | {h} |' for c,h in crow]
o+=['','Texture files: see freeze_md5.json (Assets/StreamingAssets/StaticDecals, Foliage, CharTex, MonsterLook).','',
'Not frozen (documentation / dark / gameplay): opt1_levers, fps_next, prop_cull, profile, combat/AI/clip rows. Assets/Data/_pending/hdrp_lighting_fog_patch.json is NOT applied and not listed.','',
'Verify: python -I _agents\\laneF5\\freeze\\check_freeze.py (exit 1 on any change).']
open(HERE+'\\FREEZE.md','w').write('\n'.join(o)+'\n')
print(len(rows),len(crow),len(tex),miss,pairdiff,nolive,livediff,texlive[:5])

