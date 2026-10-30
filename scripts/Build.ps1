#requires -Version 7.0
param([switch]$SmokeTest, [switch]$NoPackage, [string]$OutputDirectory = 'artifacts/publish/win-x64')
. (Join-Path $PSScriptRoot 'Common.ps1')
Push-Location $script:ProjectRoot
try {
    Invoke-DotNet restore DeskPlan.sln
    Invoke-DotNet build DeskPlan.sln -c Release --no-restore
    Invoke-DotNet test tests/DeskPlan.Tests -c Release --no-build
    if ($SmokeTest) {
        $testFolder = Join-Path $script:ProjectRoot ('artifacts\smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Invoke-DotNet src/DeskPlan.App/bin/Release/net10.0-windows/DeskPlan.dll --smoke-test $testFolder
        Write-Output "集成测试报告：$testFolder"
    }
    if (-not $NoPackage) {
        $releaseVersion = ([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
        $archiveName = "DeskPlan-$releaseVersion-win-x64"
        Invoke-DotNet publish src/DeskPlan.App -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' -o $OutputDirectory
        # Package from a fresh allowlisted directory, never from a directory that may contain user data.
        $stage = Join-Path $script:ProjectRoot ('artifacts/package/' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage | Out-Null
        Copy-Item -LiteralPath (Join-Path $OutputDirectory 'DeskPlan.exe') -Destination $stage
        foreach ($doc in @('README.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
            Copy-Item -LiteralPath (Join-Path $script:ProjectRoot $doc) -Destination $stage
        }
        Copy-Item -LiteralPath (Join-Path $script:ProjectRoot 'docs') -Destination $stage -Recurse
        & (Join-Path $PSScriptRoot 'Build-Manual.ps1') -Destination $stage
        $manualName = "DeskPlan-$releaseVersion-user-guide.html"
        $quickName = "DeskPlan-$releaseVersion-quick-start.html"
        Copy-Item -LiteralPath (Join-Path $stage '使用说明.html') -Destination "artifacts/$manualName"
        Copy-Item -LiteralPath (Join-Path $stage '快速入门.html') -Destination "artifacts/$quickName"
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath "artifacts/$archiveName.zip" -Force
        $hashes = foreach ($file in @("$archiveName.zip", $manualName, $quickName)) {
            $hash = Get-FileHash -LiteralPath "artifacts/$file" -Algorithm SHA256
            $hash.Hash.ToLowerInvariant() + '  ' + $file
        }
        Set-Content -LiteralPath "artifacts/$archiveName.sha256" -Value $hashes -Encoding ascii
        Write-Output "可运行程序：$OutputDirectory/DeskPlan.exe"
        Write-Output "完整发行目录：$stage"
    }
} finally { Pop-Location }
