[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$EvidencePath,
    [string]$NotesPath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = '0.1.1'
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$manifestFile = Join-Path (Split-Path $installer) 'build-manifest.json'
$hashFile = "$installer.sha256"
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
$evidence = if ($EvidencePath) { Get-Content -LiteralPath (Resolve-Path -LiteralPath $EvidencePath).Path -Raw | ConvertFrom-Json } else { $null }
$changeNotes = if ($NotesPath) { Get-Content -LiteralPath $NotesPath -Raw } else { "SPD Scheduler $version" }
$actualHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()

Push-Location $repoRoot
try {
    $headCommit = (& git rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $headCommit -notmatch '^[0-9a-f]{40}$') { throw 'A committed Git HEAD is required.' }
    $dirty = @(& git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'The repository must be clean. Commit the tested source and rebuild before publishing.' }
    if ($manifest.version -cne $version -or [IO.Path]::GetFileName($installer) -cne "SPD-Scheduler-Setup-$version.exe") {
        throw 'The installer filename and build manifest version must match the release version.'
    }
    if ($manifest.sourceCommit -cne $headCommit) {
        throw 'The build manifest must identify the exact current Git HEAD.'
    }
    if ($manifest.installerSha256 -ine $actualHash) {
        throw 'The installer SHA256 must match the build manifest.'
    }
    if ((Get-Content -LiteralPath $hashFile -Raw).Trim() -cne "$actualHash  $([IO.Path]::GetFileName($installer))") {
        throw 'The installer SHA256 sidecar does not match the installer.'
    }
    $validationNote = 'PowerSI integration validation is pending. This release is published for installation and updates; it does not certify all PowerSI workflows.'
    if ($null -ne $evidence) {
        if ($evidence.sourceCommit -cne $headCommit -or $evidence.installerSha256 -ine $actualHash) {
            throw 'Other-PC evidence must match the exact source commit and installer SHA256.'
        }
        foreach ($check in @('testedOnOtherPc', 'existingPowerSi', 'failedStartSkip', 'exitRecovery', 'ownershipIsolation')) {
            if ($evidence.$check -isnot [bool] -or $evidence.$check -ne $true) { throw "Other-PC validation is missing: $check." }
        }
        if ($evidence.powersiVersion -isnot [string] -or [string]::IsNullOrWhiteSpace($evidence.powersiVersion)) {
            throw 'The tested PowerSI version is required.'
        }
        $workflows = @($evidence.workflows)
        if ($workflows.Count -eq 0) { throw 'At least one tested PowerSI workflow is required.' }
        foreach ($workflow in $workflows) {
            if ($workflow.name -isnot [string] -or [string]::IsNullOrWhiteSpace($workflow.name)) { throw 'Every tested workflow needs a name.' }
            foreach ($check in @('gui', 'start', 'completion', 'save', 'close')) {
                if ($workflow.$check -isnot [bool] -or $workflow.$check -ne $true) { throw "Workflow '$($workflow.name)' has not passed: $check." }
            }
        }
        $validationNote = "Other-PC PowerSI validation passed for $($evidence.powersiVersion): $($workflows.name -join ', ')."
    }
    Write-Host "Package verification passed for v$version ($headCommit)."
    if ($ValidateOnly) { return }

    $notesFile = New-TemporaryFile
    try {
        @"
$changeNotes

Windows 10/11 x64 user-level installer with the .NET 10 runtime included.
$validationNote
Source commit: $headCommit
Installer SHA256: $actualHash

The installer is unsigned. Windows may show an unknown-publisher or SmartScreen warning.
"@ | Set-Content -LiteralPath $notesFile.FullName -Encoding utf8
        & gh release create "v$version" $installer $hashFile --repo yunhyok/SPD-Scheduler --target $headCommit --title "SPD Scheduler v$version" --notes-file $notesFile.FullName --latest
        if ($LASTEXITCODE -ne 0) { throw "GitHub release creation failed ($LASTEXITCODE)." }
    }
    finally { Remove-Item -LiteralPath $notesFile.FullName -Force }
}
finally { Pop-Location }
