# HCKIT3 v93: shared helpers for Tools\build_and_gate.ps1 and Tools\gate_unified.ps1. Dot-source it:  . "$PSScriptRoot\lib\hckit3_common.ps1"
# Nothing in here deletes anything: stale outputs are MOVED aside (Move-Aside), markers are overwritten.

function Get-ProjectRoot {
    # Tools\lib\hckit3_common.ps1 -> project root is two levels up (no absolute path in any script)
    return (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
}

function Read-JsonFile([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { throw "missing json: $path" }
    $t = [System.IO.File]::ReadAllText($path).TrimStart([char]0xFEFF)
    return ($t | ConvertFrom-Json)
}

function Get-BuildConfig {
    $root = Get-ProjectRoot
    $c = Read-JsonFile (Join-Path $root 'Tools\build_config.json')
    if ($c.paths.projectRoot) { $c | Add-Member -NotePropertyName root -NotePropertyValue ([string]$c.paths.projectRoot) -Force } else { $c | Add-Member -NotePropertyName root -NotePropertyValue $root -Force }
    return $c
}

function Get-GateConfig {
    $root = Get-ProjectRoot
    return (Read-JsonFile (Join-Path $root 'Tools\gate_config.json'))
}

# exe + data folder of one build: build_info.json (written by BuildPlayerCmd since HCKIT3) wins, older builds fall back to build_config.json build.exeName
function Get-BuildInfo([string]$buildDir, $cfg) {
    $exe = [string]$cfg.build.exeName
    $bi = Join-Path $buildDir 'build_info.json'
    if (Test-Path -LiteralPath $bi) {
        try { $j = Read-JsonFile $bi; if ($j.exe) { $exe = [string]$j.exe } } catch { }
    }
    $data = [System.IO.Path]::GetFileNameWithoutExtension($exe) + '_Data'
    if (Test-Path -LiteralPath $bi) {
        try { $j = Read-JsonFile $bi; if ($j.dataDir) { $data = [string]$j.dataDir } } catch { }
    }
    return [pscustomobject]@{ Exe = $exe; DataDir = $data; ExePath = (Join-Path $buildDir $exe); DataPath = (Join-Path $buildDir $data) }
}

# rename an existing file/dir to <name>_prev_<time> instead of deleting it
function Move-Aside([string]$path) {
    if (Test-Path -LiteralPath $path) {
        $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        $leaf = Split-Path $path -Leaf
        $ext = [System.IO.Path]::GetExtension($leaf)
        $base = if ($ext) { $leaf.Substring(0, $leaf.Length - $ext.Length) } else { $leaf }
        $new = $base + '_prev_' + $stamp + $ext
        Move-Item -LiteralPath $path -Destination (Join-Path (Split-Path $path -Parent) $new)
    }
}

function Test-BuildLogFailed([string]$logPath, [string]$marker) {
    if (-not (Test-Path -LiteralPath $logPath)) { return $false }
    return [bool](Select-String -LiteralPath $logPath -Pattern $marker -SimpleMatch -Quiet)
}
