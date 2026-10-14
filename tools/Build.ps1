[CmdletBinding()]
param([string]$DotnetPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = '0.1.0'
if (-not $DotnetPath) {
    $bundledDotnet = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $bundledDotnet) { $bundledDotnet } else { 'dotnet' }
}
$dotnet = (Get-Command -Name $DotnetPath -ErrorAction Stop).Source
$iscc = $env:ISCC
if (-not $iscc) {
    $installedIscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($installedIscc) { $iscc = $installedIscc.Source }
    else {
        foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe")) {
            if (Test-Path -LiteralPath $candidate) { $iscc = $candidate; break }
        }
    }
}
if (-not $iscc -or -not (Test-Path -LiteralPath $iscc -PathType Leaf)) {
    throw 'Inno Setup ISCC.exe was not found. Install Inno Setup or set the ISCC environment variable to its full path.'
}

Push-Location $repoRoot
try {
    & $dotnet build SPDScheduler.slnx --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
    & $dotnet run --project tests/SPDScheduler.Tests/SPDScheduler.Tests.csproj --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Console tests failed ($LASTEXITCODE)." }

    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
    $publishDir = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'publish'))
    if (-not $publishDir.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish output must stay inside the artifacts directory.'
    }
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    & $dotnet publish src/SPDScheduler.App/SPDScheduler.App.csproj --configuration Release --runtime win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
    & $iscc "/DAppVersion=$version" installer/SPDScheduler.iss
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed ($LASTEXITCODE)." }

    $installer = Join-Path $artifactsRoot "installer/SPD-Scheduler-Setup-$version.exe"
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    $sourceCommit = (& git rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { $sourceCommit = 'uncommitted' }
    $dirty = @(& git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { $sourceCommit = 'uncommitted' }
    [ordered]@{ sourceCommit = $sourceCommit; version = $version; installerSha256 = $hash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path $installer) 'build-manifest.json') -Encoding utf8
    Write-Host "Installer: $installer"
    Write-Host "SHA256: $hash"
    Write-Host "Source: $sourceCommit"
}
finally { Pop-Location }
