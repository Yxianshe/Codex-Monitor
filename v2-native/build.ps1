param(
    [string]$Output = (Join-Path $PSScriptRoot '..\dist\CodexMonitorV2')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$bundled = Join-Path $repo '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path $bundled) { $bundled } else { (Get-Command dotnet -ErrorAction Stop).Source }
$project = Join-Path $PSScriptRoot 'CodexMonitorV2\CodexMonitorV2.csproj'

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $Output

if ($LASTEXITCODE -ne 0) { throw "V2 publish failed with exit code $LASTEXITCODE" }
Get-ChildItem $Output -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force
Write-Host "Built: $(Join-Path $Output 'CodexMonitorV2.exe')"
