# next_tag.ps1 -Owner <name> : prints the next free tag and reserves it atomically (retries on a race). Use this tag for your build/deploy, never type one.
param([string]$Owner = 'anon', [string]$Prefix = 'p')
. "$PSScriptRoot\_tag_reserve.ps1"
for ($i = 0; $i -lt 6; $i++) { $t = Get-NextTag -Prefix $Prefix; try { $null = Reserve-Tag -Tag $t -Owner $Owner; $t; exit 0 } catch { Start-Sleep -Milliseconds (80 * ($i + 1)) } }
throw 'could not reserve a tag'
