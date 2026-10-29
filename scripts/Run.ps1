param([string]$DataDirectory)
. (Join-Path $PSScriptRoot 'Common.ps1')
Push-Location $script:ProjectRoot
try {
    if ($DataDirectory) { Invoke-DotNet run --project src/DeskPlan.App -c Release -- --data-dir $DataDirectory }
    else { Invoke-DotNet run --project src/DeskPlan.App -c Release }
} finally { Pop-Location }
