[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectFile = Join-Path $projectRoot 'src/CloseMeansExit.Windows/CloseMeansExit.Windows.csproj'
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$publishDirectory = Join-Path $artifactsRoot "publish-$Runtime"
$packageDirectory = Join-Path $artifactsRoot "CloseMeansExit-$Runtime"
$archivePath = Join-Path $artifactsRoot "CloseMeansExit-$Runtime.zip"
$artifactsPrefix = $artifactsRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw "The project Version must be an explicit semantic version: $version"
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
if ((Get-Item -LiteralPath $artifactsRoot -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    throw 'The artifacts directory must not redirect to another location.'
}
foreach ($directory in @($publishDirectory, $packageDirectory)) {
    $absoluteDirectory = [System.IO.Path]::GetFullPath($directory)
    if (-not $absoluteDirectory.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a directory outside project artifacts: $absoluteDirectory"
    }
    if (Test-Path -LiteralPath $absoluteDirectory) {
        if ((Get-Item -LiteralPath $absoluteDirectory -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to clean a redirected directory: $absoluteDirectory"
        }
        Remove-Item -LiteralPath $absoluteDirectory -Recurse -Force
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
    Copy-Item -LiteralPath $executable -Destination $packageDirectory
    foreach ($document in @('README.md', 'README.en.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $packageDirectory
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $packageDirectory -Recurse

    $sourceCommit = 'unavailable'
    $sourceState = 'unavailable'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $repositoryRoot = & git -C $projectRoot rev-parse --show-toplevel 2>$null
        if ($LASTEXITCODE -eq 0 -and
            [System.IO.Path]::GetFullPath([string]$repositoryRoot).TrimEnd('\', '/') -eq $projectRoot.TrimEnd('\', '/')) {
            $revision = & git -C $projectRoot rev-parse HEAD 2>$null
            if ($LASTEXITCODE -eq 0 -and $revision -match '^[0-9a-fA-F]{40,64}$') {
                $sourceCommit = $revision
                $sourceState = if (@(& git -C $projectRoot status --porcelain --untracked-files=normal).Count -gt 0) {
                    'modified'
                } else {
                    'clean'
                }
            }
        }
    }
    $sdkVersion = & dotnet --version
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to record the .NET SDK version.'
    }
    $executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    $buildInfo = @(
        "CloseMeansExit $version - Beta test release / Beta 测试版",
        'Only basic functionality is implemented. Compatibility and the overall experience are still being improved.',
        '目前仅实现基本功能，兼容性和使用体验仍在完善中。',
        'If you find it useful, please give the project a Star! / 觉得好用，别忘了点个 Star！',
        "Version tag: v$version",
        "Runtime: $Runtime; self-contained single-file executable",
        "Built at (UTC): $([DateTimeOffset]::UtcNow.ToString('O'))",
        ".NET SDK: $sdkVersion",
        "Source commit: $sourceCommit",
        "Source state: $sourceState",
        "CloseMeansExit.exe SHA-256: $executableHash",
        'Validation scope: docs/VALIDATION-0.2.2.md and docs/TESTING.md; see GitHub Actions for this build.',
        'Packaging does not itself run acceptance tests. ARM64 and real-app physical input require separate verification.',
        'Preview-Notification.cmd uses synthetic data and does not monitor, end processes, or save settings.'
    )
    [System.IO.File]::WriteAllLines(
        (Join-Path $packageDirectory 'BUILD-INFO.txt'), $buildInfo, [System.Text.UTF8Encoding]::new($false)
    )
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
