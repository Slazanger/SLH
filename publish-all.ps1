<#
.SYNOPSIS
  Publishes SLH for win-x64, linux-x64, and osx-x64 (self-contained and framework-dependent).
  Each output is a single-file app (native libraries self-extract at runtime where needed).
  After each publish, the folder contents are zipped to the output root (e.g. SLH-win-x64-self-contained.zip).

.PARAMETER OutputRoot
  Root folder under the repo for publish output. Default: dist

.PARAMETER SkipSelfContained
  Skip self-contained builds.

.PARAMETER SkipFrameworkDependent
  Skip framework-dependent builds.

.PARAMETER SkipZip
  Do not create zip archives in the output root after each publish.
#>
[CmdletBinding()]
param(
    [string] $OutputRoot = "dist",
    [switch] $SkipSelfContained,
    [switch] $SkipFrameworkDependent,
    [switch] $SkipZip
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$project = Join-Path (Join-Path $repoRoot "SLH") "SLH.csproj"
if (-not (Test-Path -LiteralPath $project)) {
    throw "Project not found: $project"
}

$outBase = Join-Path $repoRoot $OutputRoot
$rids = @("win-x64", "linux-x64", "osx-x64")

function Invoke-SlhPublish {
    param(
        [string] $Rid,
        [string] $Destination,
        [bool] $SelfContained
    )
    # `-p:SelfContained=...` is reliable; CLI `--self-contained false` does not disable SC; splatting here reached MSBuild as per-character args.
    $scLabel = if ($SelfContained) { "true" } else { "false" }
    Write-Host "Publishing $Rid (self-contained=$scLabel) -> $Destination"
    $scProp = if ($SelfContained) { "true" } else { "false" }
    dotnet publish $project `
        -c Release `
        -r $Rid `
        "-p:SelfContained=$scProp" `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $Destination
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Compress-SlhPublishFolder {
    param(
        [string] $SourceDir,
        [string] $ZipBaseName
    )
    $zipPath = Join-Path $outBase "$ZipBaseName.zip"
    Write-Host "Zipping -> $zipPath"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    $items = @(Get-ChildItem -LiteralPath $SourceDir -Force | ForEach-Object { $_.FullName })
    if ($items.Count -eq 0) {
        throw "Nothing to zip in $SourceDir"
    }
    Compress-Archive -LiteralPath $items -DestinationPath $zipPath
}

if (-not $SkipSelfContained) {
    foreach ($rid in $rids) {
        $dest = Join-Path (Join-Path $outBase "self-contained") $rid
        Invoke-SlhPublish -Rid $rid -Destination $dest -SelfContained $true
        if (-not $SkipZip) {
            $zipName = "SLH-$rid-self-contained"
            Compress-SlhPublishFolder -SourceDir $dest -ZipBaseName $zipName
        }
    }
}

if (-not $SkipFrameworkDependent) {
    foreach ($rid in $rids) {
        $dest = Join-Path (Join-Path $outBase "framework-dependent") $rid
        Invoke-SlhPublish -Rid $rid -Destination $dest -SelfContained $false
        if (-not $SkipZip) {
            $zipName = "SLH-$rid-framework-dependent"
            Compress-SlhPublishFolder -SourceDir $dest -ZipBaseName $zipName
        }
    }
}

Write-Host "Done. Output: $outBase"
