# restore.ps1 - put the versioned release tooling back where the scripts expect it on a fresh clone.
#   . .\tools_release\env.ps1 ; .\tools_release\restore.ps1            dry run: lists what would be copied
#   .\tools_release\restore.ps1 -Apply                                 copy (never overwrites an existing file unless -Force)
# Mapping: Tools\ -> Tools\ ; lane*/unitydev/pool/agents_root -> _agents\<name>\ (agents_root -> _agents\) ; Builds_launchers\ -> Builds\
param([switch]$Apply,[switch]$Force)
$src = $PSScriptRoot; $root = (Resolve-Path (Join-Path $src '..')).Path; $n = 0
foreach ($f in Get-ChildItem $src -Recurse -File | Where-Object { $_.Name -notin 'restore.ps1','env.ps1','README.md','SANITIZE_REPORT.md' }) {
  $rel = $f.FullName.Substring($src.Length + 1); $top, $rest = $rel -split '\\', 2
  $dst = switch ($top) { 'Tools' { "$root\Tools\$rest" } 'agents_root' { "$root\_agents\$rest" } 'Builds_launchers' { "$root\Builds\$rest" } default { "$root\_agents\$top\$rest" } }
  $exists = Test-Path $dst
  if ($exists -and -not $Force) { "keep (exists) $dst"; continue }
  $n++; if ($Apply) { New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null; Copy-Item $f.FullName $dst -Force; "copied  $dst" } else { "would copy $dst" }
}
"$n file(s) " + $(if ($Apply) { 'copied' } else { 'to copy (dry run; add -Apply)' })
