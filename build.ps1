param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'CodexTaskMonitor.exe')
)

$ErrorActionPreference = 'Stop'

$compilerCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) {
    throw 'The .NET Framework C# compiler (csc.exe) was not found.'
}

$monitorScript = Join-Path $PSScriptRoot 'monitor.ps1'
$launcher = Join-Path $PSScriptRoot 'Launcher.cs'
$icon = Join-Path $PSScriptRoot 'Codex.ico'
$sqlite = Join-Path $PSScriptRoot 'sqlite3.exe'

foreach ($required in @($monitorScript, $launcher, $icon, $sqlite)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Missing build input: $required"
    }
}

& $compiler `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    /win32icon:"$icon" `
    /reference:System.Windows.Forms.dll `
    /resource:"$monitorScript",MonitorScript `
    /resource:"$sqlite",PortableSqlite `
    /resource:"$icon",CodexIcon `
    /out:"$OutputPath" `
    "$launcher"

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Write-Host "Built: $OutputPath"
