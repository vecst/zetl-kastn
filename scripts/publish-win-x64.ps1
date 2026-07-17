[CmdletBinding()]
param(
    [switch]$AllowDirty,
    [switch]$RunArtifactChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-VerifiedChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Parent,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = ([System.IO.Path]::GetFullPath($Parent)).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $parentPrefix = $fullParent + [System.IO.Path]::DirectorySeparatorChar
    if (!$fullPath.StartsWith(
            $parentPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must remain beneath $fullParent. Resolved path: $fullPath"
    }

    return $fullPath
}

function Invoke-DotNetPublish {
    param(
        [Parameter(Mandatory)]
        [string]$Project,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    $destinationProperty = $Destination + [System.IO.Path]::DirectorySeparatorChar
    & dotnet publish $Project `
        -p:PublishProfile=win-x64 `
        "-p:PublishDir=$destinationProperty" `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project with exit code $LASTEXITCODE."
    }
}

function Get-ProductVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Executable
    )

    return [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Executable).ProductVersion
}

function Invoke-WaitableExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,
        [Parameter(Mandatory)]
        [string[]]$ArgumentList,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $process = Start-Process `
        -FilePath $Executable `
        -ArgumentList $ArgumentList `
        -Wait `
        -PassThru `
        -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "$Label failed with exit code $($process.ExitCode)."
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$publishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\publish'))
$finalDirectory = Get-VerifiedChildPath `
    -Path (Join-Path $publishRoot 'win-x64') `
    -Parent $publishRoot `
    -Label 'Final publish directory'
$stagingDirectory = Get-VerifiedChildPath `
    -Path (Join-Path $publishRoot ".win-x64-staging-$PID") `
    -Parent $publishRoot `
    -Label 'Staging publish directory'
$backupDirectory = Get-VerifiedChildPath `
    -Path (Join-Path $publishRoot ".win-x64-previous-$PID") `
    -Parent $publishRoot `
    -Label 'Backup publish directory'

Push-Location $repositoryRoot
try {
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
        throw 'Could not determine the Git commit for the release bundle.'
    }

    $dirtyEntries = @(& git status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not inspect the Git worktree before publishing.'
    }

    if (!$AllowDirty -and $dirtyEntries.Count -gt 0) {
        throw @"
The release worktree is not clean. Commit or remove these changes before publishing:
$($dirtyEntries -join [Environment]::NewLine)
Use -AllowDirty only while validating changes to this script; never for an RC bundle.
"@
    }

    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    foreach ($temporaryDirectory in @($stagingDirectory, $backupDirectory)) {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
        }
    }
    New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

    Write-Host "Publishing Zetl and Kastn from commit $commit..."
    Invoke-DotNetPublish `
        -Project (Join-Path $repositoryRoot 'Zetl.App\Zetl.App.csproj') `
        -Destination $stagingDirectory
    Invoke-DotNetPublish `
        -Project (Join-Path $repositoryRoot 'Kastn.App\Kastn.App.csproj') `
        -Destination $stagingDirectory

    $licenseSource = Join-Path $repositoryRoot 'LICENSE'
    $licenseDocument = Join-Path $stagingDirectory 'LICENSE'
    Copy-Item -LiteralPath $licenseSource -Destination $licenseDocument

    $zetlExecutable = Join-Path $stagingDirectory 'Zetl.exe'
    $kastnExecutable = Join-Path $stagingDirectory 'Kastn.exe'
    $hotkeyConfiguration = Join-Path $stagingDirectory 'hotkeys.json'
    foreach ($requiredFile in @(
        $zetlExecutable,
        $kastnExecutable,
        $hotkeyConfiguration,
        $licenseDocument)) {
        if (!(Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "The release bundle is missing $requiredFile."
        }
    }
    if ((Get-FileHash -LiteralPath $licenseSource -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $licenseDocument -Algorithm SHA256).Hash) {
        throw 'The bundled GPLv3 license does not match the repository LICENSE.'
    }

    $zetlVersion = Get-ProductVersion $zetlExecutable
    $kastnVersion = Get-ProductVersion $kastnExecutable
    $expectedRevision = "+$commit"
    if (!$zetlVersion.EndsWith(
            $expectedRevision,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Zetl.exe identifies '$zetlVersion', not commit '$commit'."
    }
    if (!$kastnVersion.EndsWith(
            $expectedRevision,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Kastn.exe identifies '$kastnVersion', not commit '$commit'."
    }
    if (![string]::Equals(
            $zetlVersion,
            $kastnVersion,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Bundle version mismatch: Zetl '$zetlVersion', Kastn '$kastnVersion'."
    }

    if ($RunArtifactChecks) {
        Write-Host 'Running Windows clipboard self-tests from the staged Zetl.exe...'
        Invoke-WaitableExecutable `
            -Executable $zetlExecutable `
            -ArgumentList @('--self-test') `
            -Label 'Published Windows self-tests'

        $parityDirectory = Join-Path `
            ([System.IO.Path]::GetTempPath()) `
            "zetl-parity-$PID-$([Guid]::NewGuid().ToString('N'))"
        try {
            Write-Host 'Running the staged persistence parity scenario...'
            Invoke-WaitableExecutable `
                -Executable $zetlExecutable `
                -ArgumentList @("--parity-smoke=$parityDirectory") `
                -Label 'Published persistence parity scenario'
            if (!(Test-Path -LiteralPath `
                    (Join-Path $parityDirectory 'parity-snapshot.json') `
                    -PathType Leaf)) {
                throw 'The published persistence parity scenario produced no snapshot.'
            }
        }
        finally {
            if (Test-Path -LiteralPath $parityDirectory) {
                Remove-Item -LiteralPath $parityDirectory -Recurse -Force
            }
        }
    }

    # Only replace the existing bundle after both publishes and all requested
    # artifact checks succeed. The backup keeps the previous complete bundle
    # recoverable if the directory swap itself fails.
    if (Test-Path -LiteralPath $finalDirectory) {
        Move-Item -LiteralPath $finalDirectory -Destination $backupDirectory
    }
    try {
        Move-Item -LiteralPath $stagingDirectory -Destination $finalDirectory
    }
    catch {
        if ((!(Test-Path -LiteralPath $finalDirectory)) -and
            (Test-Path -LiteralPath $backupDirectory)) {
            Move-Item -LiteralPath $backupDirectory -Destination $finalDirectory
        }
        throw
    }
    if (Test-Path -LiteralPath $backupDirectory) {
        Remove-Item -LiteralPath $backupDirectory -Recurse -Force
    }

    $zetlHash = (Get-FileHash `
        -LiteralPath (Join-Path $finalDirectory 'Zetl.exe') `
        -Algorithm SHA256).Hash
    $kastnHash = (Get-FileHash `
        -LiteralPath (Join-Path $finalDirectory 'Kastn.exe') `
        -Algorithm SHA256).Hash
    Write-Host ''
    Write-Host "Windows release bundle ready: $finalDirectory"
    Write-Host "Version: $zetlVersion"
    Write-Host "Zetl.exe SHA256:  $zetlHash"
    Write-Host "Kastn.exe SHA256: $kastnHash"
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
