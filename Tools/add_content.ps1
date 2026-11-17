# add_content.ps1 : ONE tool to add data-layer content on the HOT path (Lead side agent, 17 Nov 2026; Dom: everything on the data layer).
# It NEVER touches Assets\ and never flips. It writes a KIT folder _agents\laneLG\content_kits\<Kit>\ that mirrors Assets\Data and Assets\StreamingAssets (both twins), then prints the exact copy + deploy_data + flip commands. PATH HOT.
# Edits are TEXT insertions (a cloned row placed right after its donor), so the diff stays minimal and BOM / CRLF / indent of every file are kept. Nothing is invented: every new row is a clone of an existing row with only the named values replaced.
#
#   -Type alias   -Donor NpcReedSkirmisher -Id NpcReedSkirmisherB -Display 'Reed Skirmisher B' [-SpawnPick bandit_z2_a_1] [-Files a.json,b.json]
#   -Type vendor  -Donor npc_keeper -Id npc_keeper2 -Title 'Herbalist' -Spot npc_keeper -Glb Npc/vendor_x.glb -Scale 0.95 [-Shop hub_trade_d]
#   -Type texture -Donor z3_apothecary_canopy_green -Id z3_x -Zone HubZone -Material tripo_material_x -UnderName camps_c1_x -Png C:\path\x.png
#   -Type prop    -Id crate_1 -Zone hub_camp -Glb crate.glb [-GlbSource C:\path\crate.glb] -Pos '2,86' [-Yaw 0 -Scale 1 -Collider none|box|mesh]   (Glb = file under StreamingAssets\<folder>\ per props_runtime.json; needs Assets\Data\props_runtime.json from the prop-layer agent; else a stub message)
# Common: -Kit <name> (default <type>_<id>), -DryRun (prints what would change, writes nothing).
param(
    [Parameter(Mandatory = $true)][ValidateSet('alias', 'vendor', 'texture', 'prop')][string]$Type,
    [string]$Id = '', [string]$Donor = '', [string]$Display = '', [string]$SpawnPick = '', [string[]]$Files = @(),
    [string]$Title = '', [string]$Spot = '', [string]$Glb = '', [double]$Scale = 0, [string]$Shop = '',
    [string]$Zone = '', [string[]]$Material = @(), [string[]]$UnderName = @(), [string]$Png = '',
    [string]$Pos = '', [double]$Yaw = 0, [string]$Collider = 'none', [bool]$CastShadows = $true, [string]$GlbSource = '',
    [string]$Kit = '', [string]$Root = 'C:\Users\dom12\Documents\unity_test_hdrp', [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
if ($Id -eq '') { throw 'add_content: -Id is required' }
if ($Id -notmatch '^[A-Za-z0-9_#.\-]+$') { throw "add_content: -Id '$Id' has characters other than letters, digits, _ - . #" }
if ($Kit -eq '') { $Kit = "${Type}_$Id" }
$kitRoot = Join-Path $Root "_agents\laneLG\content_kits\$Kit"
$dataDir = Join-Path $Root 'Assets\Data'; $saDir = Join-Path $Root 'Assets\StreamingAssets'
$script:summary = New-Object System.Collections.Generic.List[string]
$script:written = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------- file helpers (bytes: BOM + newline style preserved)
function Read-Src([string]$name) {
    $kitFile = Join-Path $kitRoot "Assets\Data\$name"; $src = if (Test-Path $kitFile) { $kitFile } else { Join-Path $dataDir $name }   # a kit already holding the file keeps accumulating
    if (-not (Test-Path $src)) { throw "add_content: $src not found" }
    $b = [IO.File]::ReadAllBytes($src); $bom = ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)
    $t = [Text.Encoding]::UTF8.GetString($b); if ($bom) { $t = $t.Substring(1) }
    [pscustomobject]@{ Text = $t; Bom = $bom; Crlf = ($t.Contains("`r`n")); Name = $name }
}
function Write-Kit($f, [string]$newText) {
    $null = $newText | ConvertFrom-Json   # must still parse
    $enc = New-Object Text.UTF8Encoding($f.Bom)
    $twin = Test-Path (Join-Path $saDir $f.Name)
    $targets = @("Assets\Data\$($f.Name)"); if ($twin) { $targets += "Assets\StreamingAssets\$($f.Name)" }
    foreach ($tg in $targets) {
        $dst = Join-Path $kitRoot $tg
        if (-not $DryRun) { New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null; [IO.File]::WriteAllText($dst, $newText, $enc) }
        $script:written.Add($tg)
    }
}
# string-aware object spans: Start/End (index of { and }), ParentCh ('[' or '{' or ''), ParentStart
function Get-Spans([string]$t) {
    $spans = New-Object System.Collections.Generic.List[object]; $stack = New-Object System.Collections.Generic.Stack[object]; $inStr = $false; $esc = $false
    for ($i = 0; $i -lt $t.Length; $i++) {
        $c = $t[$i]
        if ($inStr) { if ($esc) { $esc = $false } elseif ($c -eq '\') { $esc = $true } elseif ($c -eq '"') { $inStr = $false }; continue }
        if ($c -eq '"') { $inStr = $true; continue }
        if ($c -eq '{' -or $c -eq '[') { $stack.Push(@{ ch = $c; s = $i }) }
        elseif ($c -eq '}' -or $c -eq ']') {
            $top = $stack.Pop()
            if ($top.ch -eq '{') { $pc = ''; $ps = -1; if ($stack.Count -gt 0) { $p = $stack.Peek(); $pc = $p.ch; $ps = $p.s }; $spans.Add([pscustomobject]@{ Start = $top.s; End = $i; ParentCh = $pc; ParentStart = $ps }) }
        }
    }
    $spans
}
function Esc-Re([string]$s) { [regex]::Escape($s) }
function Indent-Of([string]$t, [int]$pos) { $ls = $t.LastIndexOf("`n", [Math]::Max(0, $pos - 1)); $line = $t.Substring($ls + 1, $pos - $ls - 1); if ($line.Trim().Length -eq 0) { $line } else { '' } }
function Has-Key([string]$t, [string[]]$keys, [string]$val) { foreach ($k in $keys) { if ($t -match ('"' + (Esc-Re $k) + '"\s*:\s*"' + (Esc-Re $val) + '"')) { return $true } }; $false }
# the keyed-table row (object inside an array) whose direct property key is one of $keys with value $val; first match unless -All
function Find-Rows([string]$t, [string[]]$keys, [string]$val) {
    $spans = Get-Spans $t; $out = New-Object System.Collections.Generic.List[object]
    foreach ($k in $keys) {
        foreach ($m in [regex]::Matches($t, '"' + (Esc-Re $k) + '"\s*:\s*"' + (Esc-Re $val) + '"')) {
            $best = $null; foreach ($s in $spans) { if ($s.Start -lt $m.Index -and $s.End -gt $m.Index) { if ($best -eq $null -or $s.Start -gt $best.Start) { $best = $s } } }
            if ($best -ne $null -and $best.ParentCh -eq '[' -and -not ($out | Where-Object { $_.Start -eq $best.Start })) { $out.Add($best) }
        }
    }
    $out
}
# insert a clone of span $s after itself, replacing whole-string occurrences of $from with $to and applying key=value overrides inside the clone
function Make-Clone([string]$t, $s, [string]$from, [string]$to, [hashtable]$over) {
    $c = $t.Substring($s.Start, $s.End - $s.Start + 1)
    $c = $c.Replace('"' + $from + '"', '"' + $to + '"')
    foreach ($k in $over.Keys) {
        $v = $over[$k]
        if ($v -is [string]) { $c = [regex]::Replace($c, '("' + (Esc-Re $k) + '"\s*:\s*)"(?:[^"\\]|\\.)*"', { param($m) $m.Groups[1].Value + '"' + $v.Replace('\', '\\').Replace('"', '\"') + '"' }) }
        else { $c = [regex]::Replace($c, '("' + (Esc-Re $k) + '"\s*:\s*)[-0-9.eE+]+', { param($m) $m.Groups[1].Value + ([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $v)) }) }
    }
    $c
}
function Apply-Edits([string]$t, $edits) {   # edits: Pos, Len, Text ; applied from the end
    foreach ($e in ($edits | Sort-Object { $_.Pos } -Descending)) { $t = $t.Substring(0, $e.Pos) + $e.Text + $t.Substring($e.Pos + $e.Len) }
    $t
}
function Insert-After($f, [string]$t, $s, [string]$cloneText) {
    $nl = if ($f.Crlf) { "`r`n" } else { "`n" }; $ind = Indent-Of $t $s.Start
    if ($f.Crlf) { $cloneText = $cloneText -replace "(?<!`r)`n", "`r`n" }
    [pscustomobject]@{ Pos = $s.End + 1; Len = 0; Text = ',' + $nl + $ind + $cloneText }
}

# ---------------------------------------------------------------- TYPE alias
function Do-Alias {
    if ($Donor -eq '') { throw 'alias: -Donor is required' }
    $safe = @('roster.json', 'zones.json', 'foot_ground.json', 'bandits.json', 'arm_hold.json', 'creature_facing.json', 'foot_yaw.json', 'guard_turn.json', 'head_clamp.json', 'weapon_grip.json', 'posture.json', 'stamina.json', 'blood.json', 'cues.json', 'attack_clips.json', 'hdrp_char_mats.json', 'mob_drops.json', 'fight_balance.json')
    # never cloned by default: opponents.json (ladder order), boss_silver.json, enemy_swap.json (scene ladder): a clone there would change the game flow
    $list = if ($Files.Count -gt 0) { $Files } else { $safe }
    $keys = @('id', 'name', 'character', 'characterId'); $over = @{}; if ($Display -ne '') { $over['displayName'] = $Display; $over['hudName'] = $Display.ToUpperInvariant() }
    # refuse first: the new id must not exist as a keyed row anywhere in the files we would edit
    foreach ($fn in $list) { $p = Join-Path $dataDir $fn; if (-not (Test-Path $p)) { continue }; $t = (Read-Src $fn).Text; if (Has-Key $t $keys $Id) { throw "alias: '$Id' already exists as a row in $fn (refusing to overwrite)" } }
    $donorFound = $false
    foreach ($fn in $list) {
        if (-not (Test-Path (Join-Path $dataDir $fn))) { continue }
        $f = Read-Src $fn; $t = $f.Text; $rows = @(Find-Rows $t $keys $Donor); $edits = New-Object System.Collections.Generic.List[object]
        foreach ($r in $rows) {
            $ov = @{}; foreach ($k in $over.Keys) { if ($t.Substring($r.Start, $r.End - $r.Start + 1) -match ('"' + (Esc-Re $k) + '"\s*:\s*"')) { $ov[$k] = $over[$k] } }
            $edits.Add((Insert-After $f $t $r (Make-Clone $t $r $Donor $Id $ov)))
        }
        if ($fn -eq 'zones.json' -and $SpawnPick -ne '') {   # spawn pick switch: inside the spawn row whose id = SpawnPick, creature Donor -> Id
            foreach ($sr in @(Find-Rows $t @('id') $SpawnPick)) {
                $seg = $t.Substring($sr.Start, $sr.End - $sr.Start + 1); $n = [regex]::Matches($seg, '"creature"\s*:\s*"' + (Esc-Re $Donor) + '"').Count
                if ($n -gt 0) { $edits.Add([pscustomobject]@{ Pos = $sr.Start; Len = $seg.Length; Text = [regex]::Replace($seg, '("creature"\s*:\s*)"' + (Esc-Re $Donor) + '"', '$1"' + $Id + '"') }); $script:summary.Add("zones.json: spawn $SpawnPick picks $n -> $Id") }
            }
        }
        if ($rows.Count -gt 0 -or $edits.Count -gt 0) { $donorFound = $true; Write-Kit $f (Apply-Edits $t $edits); $script:summary.Add("$fn : $($rows.Count) row(s) cloned $Donor -> $Id") }
    }
    if (-not $donorFound) { throw "alias: donor '$Donor' not found as a keyed row in any of: $($list -join ', ')" }
}

# ---------------------------------------------------------------- TYPE vendor (hub.json)
function Do-Vendor {
    if ($Donor -eq '') { $Donor = 'npc_keeper' }
    foreach ($req in @(@('Title', $Title), @('Spot', $Spot), @('Glb', $Glb))) { if ($req[1] -eq '') { throw "vendor: -$($req[0]) is required" } }
    $f = Read-Src 'hub.json'; $t = $f.Text
    if (Has-Key $t @('id') $Id) { throw "vendor: '$Id' already exists in hub.json (refusing to overwrite)" }
    $rows = @(Find-Rows $t @('id') $Donor); if ($rows.Count -eq 0) { throw "vendor: donor row '$Donor' not found in hub.json" }
    $r = $rows[0]; $c = $t.Substring($r.Start, $r.End - $r.Start + 1)
    $c = [regex]::Replace($c, '("id"\s*:\s*)"[^"]*"', '$1"' + $Id + '"', 1)
    $str = @{ title = $Title; spot = $Spot; glb = $Glb; character = $Title; source = "add_content.ps1 $(Get-Date -Format 'yyyy-MM-dd'): runtime glb $Glb on the shared idle clip" }
    if ($Shop -ne '') { $str['shop'] = $Shop }
    foreach ($k in $str.Keys) { $v = $str[$k]; $c = [regex]::Replace($c, '("' + $k + '"\s*:\s*)"(?:[^"\\]|\\.)*"', { param($m) $m.Groups[1].Value + '"' + $v.Replace('\', '\\') + '"' }, 1) }
    if ($Scale -gt 0) { $c = [regex]::Replace($c, '("scale"\s*:\s*)[-0-9.eE+]+', { param($m) $m.Groups[1].Value + $Scale.ToString([Globalization.CultureInfo]::InvariantCulture) }, 1) }
    $e = @(Insert-After $f $t $r $c)
    Write-Kit $f (Apply-Edits $t $e); $script:summary.Add("hub.json : vendor row '$Id' cloned from '$Donor' (glb $Glb, spot $Spot)")
    if (-not (Test-Path (Join-Path $saDir $Glb))) { $script:summary.Add("WARNING: $Glb is not under Assets\StreamingAssets yet: copy the glb into the kit's Assets\StreamingAssets\$($Glb -replace '/','\') before deploying") }
}

# ---------------------------------------------------------------- TYPE texture (zone_ground_mat.json + png)
function Do-Texture {
    if ($Donor -eq '') { $Donor = 'z3_apothecary_canopy_green' }
    foreach ($req in @(@('Zone', $Zone), @('Png', $Png))) { if ($req[1] -eq '') { throw "texture: -$($req[0]) is required" } }
    if ($Material.Count -eq 0) { throw 'texture: -Material <runtime material name(s)> is required' }
    if (-not (Test-Path $Png)) { throw "texture: png '$Png' not found" }
    $f = Read-Src 'zone_ground_mat.json'; $t = $f.Text
    if (Has-Key $t @('id') $Id) { throw "texture: '$Id' already exists in zone_ground_mat.json (refusing to overwrite)" }
    $rows = @(Find-Rows $t @('id') $Donor); if ($rows.Count -eq 0) { throw "texture: donor row '$Donor' not found" }
    $r = $rows[0]; $c = $t.Substring($r.Start, $r.End - $r.Start + 1); $ind = (Indent-Of $t $r.Start) + '  '
    $arr = { param($vals) '[' + "`n" + (($vals | ForEach-Object { $ind + '  "' + $_ + '"' }) -join (",`n")) + "`n" + $ind + ']' }
    $c = [regex]::Replace($c, '("id"\s*:\s*)"[^"]*"', '$1"' + $Id + '"', 1)
    $c = [regex]::Replace($c, '("zoneRoot"\s*:\s*)"[^"]*"', '$1"' + $Zone + '"', 1)
    $m1 = & $arr $Material; $c = [regex]::Replace($c, '("matchMaterial"\s*:\s*)\[[^\]]*\]', { param($m) $m.Groups[1].Value + $m1 }, 1)
    if ($UnderName.Count -gt 0) { $m2 = & $arr $UnderName; $c = [regex]::Replace($c, '("onlyUnderName"\s*:\s*)\[[^\]]*\]', { param($m) $m.Groups[1].Value + $m2 }, 1) }
    $leaf = Split-Path $Png -Leaf; $c = [regex]::Replace($c, '("baseColorMap"\s*:\s*)"[^"]*"', '$1"' + $leaf + '"', 1)
    if ($f.Crlf) { $c = $c -replace "(?<!`r)`n", "`r`n" }
    $e = @(Insert-After $f $t $r $c); Write-Kit $f (Apply-Edits $t $e)
    $dstPng = Join-Path $kitRoot "Assets\StreamingAssets\groundmat\$leaf"
    if (-not $DryRun) { New-Item -ItemType Directory -Force (Split-Path $dstPng) | Out-Null; Copy-Item $Png $dstPng -Force }
    $script:written.Add("Assets\StreamingAssets\groundmat\$leaf")
    $script:summary.Add("zone_ground_mat.json : row '$Id' (zoneRoot $Zone, material $($Material -join ',')) + groundmat\$leaf")
}

# ---------------------------------------------------------------- TYPE prop (props_runtime.json, schema owned by the prop-layer agent)
function Do-Prop {
    $p = Join-Path $dataDir 'props_runtime.json'; $kp = Join-Path $kitRoot 'Assets\Data\props_runtime.json'
    if (-not (Test-Path $p) -and -not (Test-Path $kp)) { "waiting for props_runtime.json schema (Assets\Data\props_runtime.json does not exist yet; the prop-layer agent writes it). Nothing done."; return $false }
    foreach ($req in @(@('Zone', $Zone), @('Glb', $Glb), @('Pos', $Pos))) { if ($req[1] -eq '') { throw "prop: -$($req[0]) is required" } }
    $pv = @($Pos -split '[,; ]+' | Where-Object { $_ -ne '' } | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) }); if ($pv.Count -lt 2 -or $pv.Count -gt 3) { throw "prop: -Pos must be 'x,z' or 'x,y,z' (got '$Pos')" }
    if ($Collider -notin @('none', 'box', 'mesh')) { throw "prop: -Collider must be none, box or mesh" }
    $f = Read-Src 'props_runtime.json'; $t = $f.Text
    if (Has-Key $t @('id') $Id) { throw "prop: '$Id' already exists in props_runtime.json (refusing to overwrite)" }
    $inv = [Globalization.CultureInfo]::InvariantCulture; $nl = if ($f.Crlf) { "`r`n" } else { "`n" }
    $posTxt = '[' + (($pv | ForEach-Object { $_.ToString($inv) }) -join ', ') + ']'
    $sc = if ($Scale -gt 0) { $Scale } else { 1.0 }
    $spans = @(Get-Spans $t | Where-Object { $_.ParentCh -eq '[' })
    if ($spans.Count -gt 0) {   # clone the shape of the last existing row
        $r = $spans[$spans.Count - 1]; $c = $t.Substring($r.Start, $r.End - $r.Start + 1)
        $c = [regex]::Replace($c, '("id"\s*:\s*)"[^"]*"', '$1"' + $Id + '"', 1); $c = [regex]::Replace($c, '("zone"\s*:\s*)"[^"]*"', '$1"' + $Zone + '"', 1); $c = [regex]::Replace($c, '("glb"\s*:\s*)"[^"]*"', '$1"' + $Glb + '"', 1)
        $c = [regex]::Replace($c, '("pos"\s*:\s*)\[[^\]]*\]', { param($m) $m.Groups[1].Value + $posTxt }, 1); $c = [regex]::Replace($c, '("yaw"\s*:\s*)[-0-9.eE+]+', { param($m) $m.Groups[1].Value + $Yaw.ToString($inv) }, 1); $c = [regex]::Replace($c, '("scale"\s*:\s*)[-0-9.eE+]+', { param($m) $m.Groups[1].Value + $sc.ToString($inv) }, 1)
        $c = [regex]::Replace($c, '("collider"\s*:\s*)"[^"]*"', '$1"' + $Collider + '"', 1)
        $e = @(Insert-After $f $t $r $c); $nt = Apply-Edits $t $e
    } else {   # empty rows []: build the row from the documented field list in props_runtime.json _about (id, zone, glb, pos, yaw, scale, castShadows, collider, enabled)
        $m = [regex]::Match($t, '"rows"\s*:\s*\[\s*\]'); if (-not $m.Success) { throw 'prop: props_runtime.json has no "rows": [] to fill' }
        $i1 = '    '; $i2 = '      '; $cs = if ($CastShadows) { 'true' } else { 'false' }
        $row = '"rows": [' + $nl + $i1 + '{' + $nl + $i2 + '"id": "' + $Id + '",' + $nl + $i2 + '"zone": "' + $Zone + '",' + $nl + $i2 + '"glb": "' + $Glb + '",' + $nl + $i2 + '"pos": ' + $posTxt + ',' + $nl + $i2 + '"yaw": ' + $Yaw.ToString($inv) + ',' + $nl + $i2 + '"scale": ' + $sc.ToString($inv) + ',' + $nl + $i2 + '"castShadows": ' + $cs + ',' + $nl + $i2 + '"collider": "' + $Collider + '",' + $nl + $i2 + '"enabled": true' + $nl + $i1 + '}' + $nl + '  ]'
        $nt = $t.Substring(0, $m.Index) + $row + $t.Substring($m.Index + $m.Length)
    }
    Write-Kit $f $nt; $script:summary.Add("props_runtime.json : row '$Id' (zone $Zone, glb $Glb, pos $posTxt, collider $Collider)")
    $fol = 'PropsRuntime'; try { $fv = ($nt | ConvertFrom-Json).folder; if ($fv) { $fol = $fv } } catch { }
    if ($GlbSource -ne '') {
        if (-not (Test-Path $GlbSource)) { throw "prop: -GlbSource '$GlbSource' not found" }
        $dst = Join-Path $kitRoot "Assets\StreamingAssets\$fol\$Glb"
        if (-not $DryRun) { New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null; Copy-Item $GlbSource $dst -Force }
        $script:written.Add("Assets\StreamingAssets\$fol\$Glb")
    } elseif (-not (Test-Path (Join-Path $saDir "$fol\$Glb"))) { $script:summary.Add("WARNING: $fol\$Glb is not under Assets\StreamingAssets yet: pass -GlbSource <file> or copy it into the kit before deploying") }
    $true
}

# ---------------------------------------------------------------- run
$ok = $true
switch ($Type) { 'alias' { Do-Alias } 'vendor' { Do-Vendor } 'texture' { Do-Texture } 'prop' { $ok = Do-Prop } }
if ($Type -eq 'prop' -and $ok -eq $false) { return }
$live = 'HDRP_v109<live>'; try { $pl = Get-Content (Join-Path $Root 'Builds\PLAY.bat') -Raw; if ($pl -match 'HDRP_v109\w+') { $live = $Matches[0] } } catch { }
"add_content [$Type] '$Id' " + $(if ($DryRun) { 'DRY RUN (nothing written)' } else { "kit: $kitRoot" })
$script:summary | ForEach-Object { "  $_" }
"  files: " + ($script:written | Select-Object -Unique) -join '; '
if (-not $DryRun) {
    "NEXT (HOT, no Editor): 1) copy the kit over the repo:  Copy-Item -Recurse -Force '$kitRoot\Assets\*' '$Root\Assets\'"
    "      2) `$env:FK_OWNER='unitydev'; `$t = (powershell -NoProfile -File Tools\next_tag.ps1 -Owner unitydev | Select -Last 1)"
    "      3) powershell -File _agents\laneBUILDSPEED\deploy_data.ps1 -v `"109`$t`" -Base $live -Apply -IgnoreAssetChanges -SkipGates"
    "      4) powershell -File _agents\laneHOTSHIP\flip.ps1 -Build `"HDRP_v109`$t`" -Note '<what and why>'   (then prove on live; release first, test after)"
}
'PATH HOT (data rows + runtime files only, no asset import, no Editor)'
