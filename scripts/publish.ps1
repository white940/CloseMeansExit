[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src/CloseMeansExit.Windows/CloseMeansExit.Windows.csproj'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$publishDirectory = Join-Path $artifactsRoot "publish-$Runtime"
$packageDirectory = Join-Path $artifactsRoot "CloseMeansExit-$Runtime"
$archivePath = Join-Path $artifactsRoot "CloseMeansExit-$Runtime.zip"

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
foreach ($directory in @($publishDirectory, $packageDirectory)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

Push-Location $projectRoot
try {
    & dotnet publish $projectFile -c Release -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $executable = Join-Path $publishDirectory 'CloseMeansExit.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published executable was not found: $executable"
    }

    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $packageDirectory -Recurse
    foreach ($document in @('README.md', 'README.en.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $packageDirectory
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $packageDirectory 'Preview-Notification.cmd'),
        '@echo off' + "`r`n" + 'start "" "%~dp0CloseMeansExit.exe" --notification-demo' + "`r`n",
        [System.Text.Encoding]::ASCII
    )

    Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archivePath -Force
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        "$archivePath.sha256",
        "$hash  $([System.IO.Path]::GetFileName($archivePath))`n",
        [System.Text.UTF8Encoding]::new($false)
    )
    Write-Host "Created $archivePath"
    Write-Host "SHA256: $hash"
}
finally {
    Pop-Location
}
