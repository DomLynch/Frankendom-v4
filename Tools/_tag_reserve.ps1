# _tag_reserve.ps1: atomic build-tag reservation (Strategy 22:10 16 Nov: tag collisions pn/pv repeat fault; the mechanism, not a board rule).
# Reserve-Tag  : creates Builds\HDRP_v109<tag>.reserved with FileMode.CreateNew (atomic). Refuses if the tag is reserved by another owner or the build folder exists (unless the same owner reserved it).
# Get-NextTag  : next free two-letter tag after the highest of Builds\HDRP_v109??, *.reserved and tags named on the board.
# Nobody types a tag by hand: powershell -File Tools\next_tag.ps1 -Owner <name> prints and reserves the tag. deploy_data.ps1 and Assert-BuildSafe reserve too (same owner may reuse its own tag).
function Get-TagOwnerDefault { if ($env:FK_OWNER) { $env:FK_OWNER } else { 'anon' } }
function Get-NextTag {
    param([string]$Root = 'C:\Users\dom12\Documents\unity_test_hdrp', [string]$Prefix = 'p')
    $tags = @()
    $tags += Get-ChildItem "$Root\Builds" -Filter "HDRP_v109$Prefix?*" -ErrorAction SilentlyContinue | ForEach-Object { if ($_.Name -match "^HDRP_v109($Prefix[a-z])(\.reserved)?$") { $Matches[1] } }
    $b = "$Root\_agents\COORDINATION.md"
    if (Test-Path $b) { $tags += [regex]::Matches(((Get-Content $b -Tail 300) -join "`n"), "HDRP_v109($Prefix[a-z])\b") | ForEach-Object { $_.Groups[1].Value } }
    $max = ($tags | Sort-Object -Unique | Select-Object -Last 1); if (-not $max) { return "${Prefix}a" }
    $c = [char]([int][char]$max[1] + 1); if ($c -gt [char]'z') { throw "tag space $Prefix? exhausted, pick a new prefix" }
    return "$Prefix$c"
}
function Reserve-Tag {
    param([Parameter(Mandatory)][string]$Tag, [string]$Owner = (Get-TagOwnerDefault), [string]$Root = 'C:\Users\dom12\Documents\unity_test_hdrp')
    $Tag = $Tag -replace '^HDRP_', '' -replace '^v109', ''
    $res = "$Root\Builds\HDRP_v109$Tag.reserved"; $dir = "$Root\Builds\HDRP_v109$Tag"
    $mine = $false
    try {
        $fs = [System.IO.File]::Open($res, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
        $bytes = [Text.Encoding]::UTF8.GetBytes("owner=$Owner time=$(Get-Date -Format s) pid=$PID"); $fs.Write($bytes, 0, $bytes.Length); $fs.Close(); $mine = $true
    } catch [System.IO.IOException] {
        $cur = Get-Content $res -Raw -ErrorAction SilentlyContinue
        if ($cur -match "owner=$([regex]::Escape($Owner))\b") { $mine = $true } else { throw "TAG $Tag RESERVED by another owner ($cur): pick a tag with Tools\next_tag.ps1" }
    }
    if ((Test-Path $dir) -and -not (Test-Path "$res.used")) { }
    return $mine
}

