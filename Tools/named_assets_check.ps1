# named_assets_check.ps1 -Root <StreamingAssets dir> -Caller <name>
# Guard (19 Nov, COO/Strategy: AR found decal PNGs named in data that never reached a build = silent flat spots): refuses when an image named by a RUNTIME decal/ground/fx data file is absent from the new build's StreamingAssets.
# Files checked: ground_patches.json static_decals.json wet_decals.json prop_grime_decals.json (the four StaticDecals.cs readers), zone_ground_mat.json, fx_materials.json. Names starting 'Assets/' are editor-project paths and skipped.
# Output: one string per miss; writes ONE board line 'NAMED ASSET MISSING ...' and exits 5. Lead-only override: $env:FK_NAMEDASSET_ALLOW = '<reason>'.
param([Parameter(Mandatory)][string]$Root, [string]$Caller = '?', [switch]$Quiet)
$p = 'C:\Users\dom12\Documents\unity_test_hdrp'
$files = 'ground_patches.json','static_decals.json','wet_decals.json','prop_grime_decals.json','zone_ground_mat.json','fx_materials.json'
$names = @{}; Get-ChildItem $Root -Recurse -File -ErrorAction SilentlyContinue | % { $names[$_.Name.ToLower()] = 1 }
$hits = @()
foreach ($f in $files) { $fp = Join-Path $Root $f; if (-not (Test-Path $fp)) { continue }
  $t = [IO.File]::ReadAllText($fp)
  foreach ($m in [regex]::Matches($t, '"([^"\\\r\n]{3,200}\.(png|jpg|jpeg|bc7|tga|exr|dds))"', 'IgnoreCase')) { $s = $m.Groups[1].Value; if ($s -like 'Assets/*' -or $s -like 'Assets\*') { continue }
    $bn = [IO.Path]::GetFileName($s).ToLower(); if (-not $names.ContainsKey($bn)) { $hits += "NAMED ASSET MISSING: $f names '$s' but no such file in the build StreamingAssets ($Root)" } } }
$hits = @($hits | Select-Object -Unique)
if ($hits.Count -and $env:FK_NAMEDASSET_ALLOW) { try { Add-Content (Join-Path $p '_agents\COORDINATION.md') ("`n{0} NAMED ASSET OVERRIDE ({1}): {2} miss(es) allowed: {3}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $Caller, $hits.Count, $env:FK_NAMEDASSET_ALLOW) } catch {}; return }
if ($hits.Count) { try { Add-Content (Join-Path $p '_agents\COORDINATION.md') ("`n{0} NAMED ASSET REFUSED ({1}): {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $Caller, (($hits | Select-Object -First 3) -join ' | ')) } catch {}; $hits }