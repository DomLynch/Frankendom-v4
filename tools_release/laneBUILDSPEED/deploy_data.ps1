# deploy_data.ps1 - laneBUILDSPEED. DATA-ONLY hot deploy: no Unity, no build. DRY RUN by default; -Apply writes.
#   powershell -NoProfile -File _agents\laneBUILDSPEED\deploy_data.ps1 -v v109k                 plan only (what would change, which base, whether a real build is needed)
#   powershell -NoProfile -File _agents\laneBUILDSPEED\deploy_data.ps1 -v v109k -Apply          make Builds\HDRP_v109k, then run the data gates
#   -Base HDRP_v109j     base build folder (default: newest Builds\HDRP_* whose build_info.json says Succeeded)
#   -IgnoreAssetChanges  deploy even when Assets outside Data/StreamingAssets (scripts, scenes, glb, textures) changed since the last REAL build (they will NOT be in the new folder)
#   -SkipGates           do not run the gates   -OutRoot <dir>  parent folder of the new build (default Builds)
# What it does: new build folder = HARD LINKS to every file of the base build (no disk copy, no 7 GB write), except StreamingAssets: every *.json there is a REAL copy (an in-place edit
# of a json in the new build can never touch the base build), and the files whose content differs from "what the next build would ship" are replaced by that content.
# "What the next build would ship" = BuildCfg.CopyDataToStreaming + Unity's StreamingAssets copy: Assets\StreamingAssets (any file, no .meta) overlaid by Assets\Data\*.json (except build.editorOnlyData).
# Audit fix 18 Nov (Prove deletions): the candidate is compared by the COMPLETE relative-path set AND sha256, both directions:
#   removed  = in the base build, gone from the desired set -> NOT carried into the candidate (report line "removed <rel>"; deploy_data.json "keepFromBase" = safe-list of player-only files, none today)
#   excluded = backup-named files (.bak, _backup, .orig, ~, or a 'backup' directory) are never shipped; they stay in the repo ("excluded <rel>")
#   same-size files are ALWAYS hashed (parallel sha256; base hashes come from the base's sa_manifest.json when size+mtime still match, else hashed); a same-size same-timestamp binary swap is caught ("content").
#   Immutable unchanged binaries are still hard-linked. Each candidate gets sa_manifest.json (rel -> size, mtime, sha256) next to build_info.json.
# Never writes Assets, never deletes anything, never touches the base build. Hard-linked binaries must never be edited in place (write a new file and rename).
param(
    [Parameter(Mandatory = $true)][string]$v,
    [string]$Base = '',
    [string]$OutRoot = '',
    [switch]$Apply,
    [switch]$IgnoreAssetChanges,
    [switch]$SkipGates
)
$ErrorActionPreference = 'Stop'
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$P = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$cfg = Get-Content -Raw (Join-Path $P 'Tools\build_config.json') | ConvertFrom-Json
$skipJson = @($cfg.build.editorOnlyData) | ForEach-Object { $_.ToLower() }
$buildsRoot = if ($OutRoot) { $OutRoot } else { Join-Path $P ($cfg.build.buildRoot) }
$prefix = [string]$cfg.build.buildDirPrefix
$exeName = [string]$cfg.build.exeName
$dataDirName = [System.IO.Path]::GetFileNameWithoutExtension($exeName) + '_Data'
if (-not $v.StartsWith('v')) { $v = "v$v" }
$outDir = Join-Path $buildsRoot ($prefix + $v)
$log = Join-Path $P ("TestOut\" + $v + "_deploy.log"); $null = New-Item -ItemType Directory -Force (Join-Path $P 'TestOut')
function Say([string]$s) { $l = (Get-Date -Format HH:mm:ss) + ' ' + $s; Write-Host $l; if ($Apply) { Add-Content -Path $log -Value $l } }

# ---- 1. base build: newest build whose build_info says Succeeded
function Read-Info([string]$dir) { $f = Join-Path $dir 'build_info.json'; if (Test-Path $f) { try { return (Get-Content -Raw $f | ConvertFrom-Json) } catch { return $null } } return $null }
if ($Base) { $baseDir = Join-Path $P ('Builds\' + $Base) }
else {
    $cand = Get-ChildItem (Join-Path $P 'Builds') -Directory -Filter ($prefix + '*') | Where-Object { (Test-Path (Join-Path $_.FullName $exeName)) -and (Test-Path (Join-Path $_.FullName 'build_info.json')) } |
        Where-Object { $i = Read-Info $_.FullName; $i -and $i.result -eq 'Succeeded' } | Sort-Object { (Get-Item (Join-Path $_.FullName 'build_info.json')).LastWriteTime } -Descending
    if (-not $cand) { throw 'no good base build found under Builds (needs HadesSlice.exe + build_info.json result Succeeded)' }
    $baseDir = @($cand)[0].FullName
}
$bi = Read-Info $baseDir
if (-not $bi -or $bi.result -ne 'Succeeded') { throw "base $baseDir has no build_info.json with result Succeeded" }
$baseSA = Join-Path $baseDir "$dataDirName\StreamingAssets"
if (-not (Test-Path $baseSA)) { throw "base has no $dataDirName\StreamingAssets" }
if ($v -notmatch '^v109[a-z]{2,3}$') { throw "tag must look like v109xx (got '$v'): get it from Tools\next_tag.ps1" }
if ($Apply) { $jbad = @(); foreach ($d in @((Join-Path $P 'Assets\Data'), (Join-Path $P 'Assets\StreamingAssets'))) { foreach ($j in Get-ChildItem $d -Filter *.json -File -EA SilentlyContinue) { if ($j.Length -lt 3) { $jbad += "$($j.Name) EMPTY" } else { try { $null = (Get-Content $j.FullName -Raw).TrimStart([char]0xFEFF) | ConvertFrom-Json -ErrorAction Stop } catch { $jbad += "$($j.Name) does not parse" } } } }; if ($jbad.Count) { throw ("DEPLOY REFUSED (json guard, COO 08:38): " + ($jbad -join " | ")) } }
if ($Apply) { . (Join-Path $P 'Tools\_tag_reserve.ps1'); $null = Reserve-Tag -Tag $v -Owner (Get-TagOwnerDefault) -Root $P }
if (Test-Path $outDir) { throw "$outDir already exists (this script never overwrites or deletes). Pick another -v." }
Say ("BASE " + $baseDir + " (build_info time " + $bi.time + ")")

# ---- 2. what the next build would ship in StreamingAssets
$srcSA = Join-Path $P 'Assets\StreamingAssets'; $srcData = Join-Path $P 'Assets\Data'
$desired = @{}   # relative path (backslashes, lower-case key) -> full source path
Get-ChildItem $srcSA -Recurse -File | Where-Object { $_.Extension -ne '.meta' } | ForEach-Object { $desired[$_.FullName.Substring($srcSA.Length + 1).ToLower()] = $_.FullName }
$editorOnlyHit = @()
Get-ChildItem $srcData -Filter '*.json' -File | ForEach-Object {
    if ($skipJson -contains $_.Name.ToLower()) { $editorOnlyHit += $_.Name } else { $desired[$_.Name.ToLower()] = $_.FullName }
}

# backup-named files never ship (audit 18 Nov): names containing .bak, _backup, .orig, ~, or any directory named *backup*. They stay in the repo.
function IsBackupName([string]$rel) { $parts = $rel -split '\\'; if ($parts[-1] -match '(?i)(\.bak|_backup|\.orig|~)') { return $true }; foreach ($d in $parts[0..([math]::Max(0, $parts.Count - 2))]) { if ($parts.Count -gt 1 -and $d -match '(?i)backup') { return $true } }; return $false }
$excluded = @($desired.Keys | Where-Object { IsBackupName $_ } | Sort-Object)
foreach ($k in $excluded) { $desired.Remove($k) }
Say ("DESIRED StreamingAssets files " + $desired.Count + " (Assets\StreamingAssets overlaid by Assets\Data\*.json, editor-only skipped: " + $editorOnlyHit.Count + ", backup-named excluded: " + $excluded.Count + ")")
$excluded | ForEach-Object { Say ("  excluded " + $_) }

# parallel sha256 (same-size files are always hashed; 8 threads)
if (-not ('FkHash' -as [type])) { Add-Type -TypeDefinition 'using System; using System.IO; using System.Security.Cryptography; using System.Threading.Tasks; public static class FkHash { public static string[] Sha(string[] p) { var r = new string[p.Length]; Parallel.For(0, p.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => { try { using (var h = SHA256.Create()) using (var s = new FileStream(p[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20)) r[i] = BitConverter.ToString(h.ComputeHash(s)); } catch (Exception e) { r[i] = "ERR:" + e.Message; } }); return r; } }' }
function FileSha([string]$p) { return ([FkHash]::Sha(@($p)))[0] }
$safeKeep = @(); $dcfg0 = Join-Path $PSScriptRoot 'deploy_data.json'; if (Test-Path $dcfg0) { $x = Get-Content -Raw $dcfg0 | ConvertFrom-Json; if ($x.PSObject.Properties['keepFromBase']) { $safeKeep = @($x.keepFromBase) } }

# ---- 3. changed = new / size / content (sha256 of every same-size file, both sides; base side taken from the base's sa_manifest.json when size+mtime still match)
$baseMan = @{}; $bmf = Join-Path $baseDir 'sa_manifest.json'
if (Test-Path $bmf) { try { $o = Get-Content -Raw $bmf | ConvertFrom-Json; foreach ($pp in $o.files.PSObject.Properties) { $baseMan[$pp.Name] = $pp.Value } } catch { $baseMan = @{} } }
$changed = @(); $bytes = 0; $needHash = New-Object System.Collections.Generic.List[object]
$srcInfo = @{}
foreach ($k in $desired.Keys) {
    $src = Get-Item $desired[$k]; $srcInfo[$k] = $src; $dst = Join-Path $baseSA $k
    if (-not (Test-Path $dst)) { $changed += [pscustomobject]@{ rel = $k; src = $src.FullName; why = 'new' }; $bytes += $src.Length; continue }
    $d = Get-Item $dst
    if ($src.Length -ne $d.Length) { $changed += [pscustomobject]@{ rel = $k; src = $src.FullName; why = 'size' }; $bytes += $src.Length; continue }
    $needHash.Add([pscustomobject]@{ rel = $k; src = $src; dst = $d })
}
$srcH = [FkHash]::Sha(@($needHash | ForEach-Object { $_.src.FullName }))
$dstIdx = New-Object System.Collections.Generic.List[int]; $dstHash = @{}
for ($i = 0; $i -lt $needHash.Count; $i++) { $m = $baseMan[$needHash[$i].rel]; if ($m -and $m.size -eq $needHash[$i].dst.Length -and $m.mtime -eq $needHash[$i].dst.LastWriteTimeUtc.Ticks) { $dstHash[$i] = $m.sha } else { $dstIdx.Add($i) } }
$dh = [FkHash]::Sha(@($dstIdx | ForEach-Object { $needHash[$_].dst.FullName })); for ($j = 0; $j -lt $dstIdx.Count; $j++) { $dstHash[$dstIdx[$j]] = $dh[$j] }
for ($i = 0; $i -lt $needHash.Count; $i++) { if ($srcH[$i] -ne $dstHash[$i]) { $changed += [pscustomobject]@{ rel = $needHash[$i].rel; src = $needHash[$i].src.FullName; why = 'content' }; $bytes += $needHash[$i].src.Length } }
$srcSha = @{}; for ($i = 0; $i -lt $needHash.Count; $i++) { $srcSha[$needHash[$i].rel] = $srcH[$i] }
Say ("HASHED " + $needHash.Count + " same-size file(s) (base hashes from manifest: " + ($needHash.Count - $dstIdx.Count) + ", hashed fresh: " + $dstIdx.Count + ")")
# removed = in the base, not in the desired set (and not on the player-only safe-list) -> not carried
$removed = @(); $kept0 = 0
foreach ($bf in [System.IO.Directory]::EnumerateFiles($baseSA, '*', [System.IO.SearchOption]::AllDirectories)) {
    $brel = $bf.Substring($baseSA.Length + 1).ToLower()
    if ($desired.ContainsKey($brel)) { continue }
    $safe = $false; foreach ($pat in $safeKeep) { if ($brel -like ([string]$pat).ToLower()) { $safe = $true; break } }
    if ($safe) { $kept0++ } else { $removed += $brel }
}
$removed = @($removed | Sort-Object)
$removedSet = @{}; foreach ($r in $removed) { $removedSet[$r] = 1 }
Say ("CHANGED " + $changed.Count + " StreamingAssets file(s), " + [math]::Round($bytes / 1MB, 2) + " MB; REMOVED " + $removed.Count + " (in base, not in desired source; kept player-only safe-list: " + $kept0 + ")")
$changed | Select-Object -First 40 | ForEach-Object { Say ("  " + $_.why + " " + $_.rel) }
$removed | ForEach-Object { Say ("  removed " + $_) }

# ---- 4. does anything that needs a real build differ? (Assets outside Data/StreamingAssets, ProjectSettings, manifest) newer than the last REAL build
$since = if ($bi.realBuildTime) { [datetime]$bi.realBuildTime } elseif ($bi.time) { [datetime]$bi.time } else { (Get-Item (Join-Path $baseDir 'build_info.json')).LastWriteTime }
$needs = New-Object System.Collections.Generic.List[string]
$skipTop = @('Data', 'StreamingAssets', 'Library', 'Temp')
$roots = @()
Get-ChildItem (Join-Path $P 'Assets') -Directory | Where-Object { $skipTop -notcontains $_.Name } | ForEach-Object { $roots += $_.FullName }
$roots += (Join-Path $P 'ProjectSettings')
foreach ($r in $roots) {
    foreach ($f in ([System.IO.Directory]::EnumerateFiles($r, '*', [System.IO.SearchOption]::AllDirectories))) {
        if ($f.EndsWith('.meta')) { continue }
        if ([System.IO.File]::GetLastWriteTime($f) -gt $since) { $needs.Add($f.Substring($P.Length + 1)) }
    }
}
$man = Join-Path $P 'Packages\manifest.json'; if ([System.IO.File]::GetLastWriteTime($man) -gt $since) { $needs.Add('Packages\manifest.json') }
$dcfg = Get-Content -Raw (Join-Path $PSScriptRoot 'deploy_data.json') | ConvertFrom-Json
$kept = New-Object System.Collections.Generic.List[string]; $ign = 0
foreach ($n in $needs) { $hit = $false; foreach ($pat in @($dcfg.ignoreRealBuildCheck)) { if ($n -like $pat) { $hit = $true; break } }; if ($hit) { $ign++ } else { $kept.Add($n) } }
$needs = $kept
Say ("REAL-BUILD CHECK: (ignored " + $ign + " chain-regenerated files, see deploy_data.json) " + $needs.Count + " file(s) outside Data/StreamingAssets changed since the last real build (" + $since.ToString('yyyy-MM-dd HH:mm:ss') + ")")
$needs | Select-Object -First 15 | ForEach-Object { Say ("  needs build: " + $_) }
if ($needs.Count -gt 0 -and -not $IgnoreAssetChanges) { Say 'STOP: a data deploy would NOT carry those. Run a real build, or pass -IgnoreAssetChanges if they are meant to wait.'; if ($Apply) { exit 2 } }

if (-not $Apply) { Say ("DRY RUN done in " + [math]::Round($sw.Elapsed.TotalSeconds, 1) + " s. Nothing written. Add -Apply to create " + $outDir); exit 0 }

# ---- 5. make the new folder: hard links, real json copies, changed files replaced
Add-Type -Namespace Native -Name Link -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)] public static extern bool CreateHardLinkW(string newName, string existing, IntPtr sec);'
$keepTop = @($exeName, 'UnityPlayer.dll', 'UnityCrashHandler64.exe', 'D3D12', 'MonoBleedingEdge', $dataDirName, 'build_info.json')
$chg = @{}; foreach ($c in $changed) { $chg[$c.rel] = $c.src }
$linked = 0; $copied = 0; $replaced = 0; $failedLinks = 0; $dropped = 0
New-Item -ItemType Directory -Path $outDir | Out-Null
foreach ($e in (Get-ChildItem $baseDir)) {
    $isKeep = ($keepTop -contains $e.Name) -or ($e.Name -like '*.dll' -and -not $e.PSIsContainer)
    if (-not $isKeep) { continue }
    if ($e.Name -eq 'build_info.json') { continue }
    $files = if ($e.PSIsContainer) { [System.IO.Directory]::EnumerateFiles($e.FullName, '*', [System.IO.SearchOption]::AllDirectories) } else { @($e.FullName) }
    foreach ($f in $files) {
        $rel = $f.Substring($baseDir.Length + 1); $dst = Join-Path $outDir $rel
        $saRel = $null; $prefixSA = "$dataDirName\StreamingAssets\"
        if ($rel.StartsWith($prefixSA)) { $saRel = $rel.Substring($prefixSA.Length).ToLower() }
        if ($saRel -and $removedSet.ContainsKey($saRel)) { $dropped++; continue }   # removed from the desired source (or backup-named): never rides into the candidate
        $dd = Split-Path $dst -Parent; if (-not (Test-Path $dd)) { New-Item -ItemType Directory -Path $dd -Force | Out-Null }
        if ($saRel -and $chg.ContainsKey($saRel)) { [System.IO.File]::Copy($chg[$saRel], $dst); $replaced++; $chg.Remove($saRel); continue }
        if ($saRel -and $f.EndsWith('.json')) { [System.IO.File]::Copy($f, $dst); $copied++; continue }
        if ([Native.Link]::CreateHardLinkW($dst, $f, [IntPtr]::Zero)) { $linked++ } else { $failedLinks++; [System.IO.File]::Copy($f, $dst); $copied++ }
    }
}
foreach ($k in @($chg.Keys)) {   # new files that did not exist in the base
    $dst = Join-Path $outDir ("$dataDirName\StreamingAssets\" + $k); $dd = Split-Path $dst -Parent
    if (-not (Test-Path $dd)) { New-Item -ItemType Directory -Path $dd -Force | Out-Null }
    [System.IO.File]::Copy($chg[$k], $dst); $replaced++
}
Say ("TREE linked " + $linked + ", copied " + $copied + " (json + link fallbacks " + $failedLinks + "), changed/new written " + $replaced + ", removed/not carried " + $dropped)

# ---- 6. build_info.json with the source
$o = $bi | ConvertTo-Json -Depth 6 | ConvertFrom-Json
$real = if ($bi.realBuildTime) { $bi.realBuildTime } else { $bi.time }
foreach ($pair in @(@('time', (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')), @('version', $v), @('source', 'deploy_data'), @('base', (Split-Path $baseDir -Leaf)), @('realBuildTime', $real), @('changedCount', $changed.Count))) {
    if ($o.PSObject.Properties[$pair[0]]) { $o.($pair[0]) = $pair[1] } else { $o | Add-Member -NotePropertyName $pair[0] -NotePropertyValue $pair[1] }
}
$o | Add-Member -NotePropertyName changedFiles -NotePropertyValue @($changed | ForEach-Object { $_.rel }) -Force
[System.IO.File]::WriteAllText((Join-Path $outDir 'build_info.json'), ($o | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
Say ("BUILD_INFO written (source deploy_data, base " + (Split-Path $baseDir -Leaf) + ")")

# ---- 7. check the new folder equals what the next build would ship, then the data gates (all read-only)
$newSA = Join-Path $outDir "$dataDirName\StreamingAssets"; $bad = 0
$candFiles = @{}; foreach ($cf in [System.IO.Directory]::EnumerateFiles($newSA, '*', [System.IO.SearchOption]::AllDirectories)) { $candFiles[$cf.Substring($newSA.Length + 1).ToLower()] = $cf }
foreach ($k in $desired.Keys) { if (-not $candFiles.ContainsKey($k)) { $bad++; Say ("  MISMATCH missing " + $k) } }
foreach ($k in @($candFiles.Keys)) { if (-not $desired.ContainsKey($k)) { $isSafe = $false; foreach ($pat in $safeKeep) { if ($k -like ([string]$pat).ToLower()) { $isSafe = $true } }; if (-not $isSafe) { $bad++; Say ("  MISMATCH extra (not in desired) " + $k) } } }
$vk = @($desired.Keys | Where-Object { $candFiles.ContainsKey($_) } | Sort-Object)
$vh = [FkHash]::Sha(@($vk | ForEach-Object { $candFiles[$_] })); $manFiles = [ordered]@{}
for ($i = 0; $i -lt $vk.Count; $i++) {
    $k = $vk[$i]; $want = $srcSha[$k]; if (-not $want) { $want = ([FkHash]::Sha(@($desired[$k])))[0] }   # changed/new files were not hashed above
    if ($vh[$i] -ne $want) { $bad++; Say ("  HASH MISMATCH " + $k) }
    $ci = Get-Item $candFiles[$k]; $manFiles[$k] = [ordered]@{ size = $ci.Length; mtime = $ci.LastWriteTimeUtc.Ticks; sha = $vh[$i] }
}
[System.IO.File]::WriteAllText((Join-Path $outDir 'sa_manifest.json'), ([ordered]@{ version = $v; files = $manFiles } | ConvertTo-Json -Depth 5 -Compress), (New-Object System.Text.UTF8Encoding($false)))
Say ("VERIFY new StreamingAssets == next-build content (complete path set + sha256 of " + $vk.Count + " files; removed " + $removed.Count + ", excluded " + $excluded.Count + "): " + $(if ($bad -eq 0) { 'PASS' } else { "FAIL ($bad)" }))
$gateFail = 0
if (-not $SkipGates) {
    foreach ($g in @(@('data_gates', @('Tools\data_gates.py')), @('missing_keys', @('Tools\missing_keys.py')))) {
        $out = & python -I (Join-Path $P $g[1][0]) 2>&1 | Out-String; $rc = $LASTEXITCODE
        Say ("GATE " + $g[0] + " exit " + $rc + " :: " + (($out -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 2) -join ' | '))
        if ($rc -ne 0) { $gateFail++ }
    }
}
$res = if ($bad -eq 0 -and $gateFail -eq 0) { 'READY' } else { 'CHECK FAILED' }
Say ("RESULT " + $res + " " + $outDir + " in " + [math]::Round($sw.Elapsed.TotalSeconds, 1) + " s; run the game with -savePath <sandbox> to check; nothing else was touched")
Say ("PATH HOT (forcing asset: none, data + runtime-loaded files only) " + $outDir); Add-Content -Path (Join-Path $P '_agents\COORDINATION.md') -Value ("`n" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + " PATH HOT frankendom " + (Split-Path $outDir -Leaf) + " (deploy_data, no asset import)")
if ($res -ne 'READY') { exit 1 }



