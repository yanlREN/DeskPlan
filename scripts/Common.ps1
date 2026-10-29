$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$script:ProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCommand) {
    $script:DotNet = $dotnetCommand.Source
} else {
    $candidates = @('D:\Codex\runtimes\dotnet-10\dotnet.exe', (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'))
    $script:DotNet = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $script:DotNet) { throw '需要安装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0' }
$env:DOTNET_ROOT = Split-Path -Parent $script:DotNet
function Invoke-DotNet {
    & $script:DotNet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败（退出码 $LASTEXITCODE）。" }
}
