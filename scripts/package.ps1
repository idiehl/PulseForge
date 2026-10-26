[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(ParameterSetName = 'Build')]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [Parameter(ParameterSetName = 'Build')]
    [switch]$NoBuild,
    [Parameter(ParameterSetName = 'Build')]
    [switch]$StageOnly,
    [Parameter(Mandatory, ParameterSetName = 'Promote')]
    [string]$StagedPackagePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$packageBase = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'out\package'))
$packageRoot = Join-Path $packageBase 'PulseForge-alpha-win-x64'
$allowedPrefix = $packageBase.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Assert-PackagePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing package operation outside $packageBase`: $fullPath."
    }
    # Check every existing ancestor; Resolve-Path does not reliably expose junction destinations.
    $ancestor = $fullPath
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing package operation through reparse point $ancestor."
            }
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    if (Test-Path -LiteralPath $fullPath -PathType Container) {
        $linkedItem = Get-ChildItem -LiteralPath $fullPath -Recurse -Force |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1
        if ($linkedItem) { throw "Refusing package containing reparse point $($linkedItem.FullName)." }
    }
    return $fullPath
}

function Assert-PackageStopped([string]$Path) {
    $prefix = $Path.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $running = @(Get-CimInstance Win32_Process | Where-Object {
        ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) -or
        ($_.Name -match '^dotnet(?:\.exe)?$' -and $_.CommandLine -and
            $_.CommandLine.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) -or
        ($_.Name -match '^PulseForge\.(Editor|AudioHost)(?:\.exe)?$' -and -not $_.ExecutablePath)
    })
    if ($running.Count) {
        $details = ($running | ForEach-Object { "$($_.Name) (PID $($_.ProcessId))" }) -join ', '
        throw "Package is in use by $details. Close these processes before promoting the staged package. Nothing was replaced."
    }
}

$packageRoot = Assert-PackagePath $packageRoot
$nativeDll = $null
if ($PSCmdlet.ParameterSetName -eq 'Promote') {
    $stagingRoot = Assert-PackagePath $StagedPackagePath
    if ((Split-Path -Parent $stagingRoot) -ne $packageBase -or
        (Split-Path -Leaf $stagingRoot) -notlike 'PulseForge-alpha-win-x64.staging-*') {
        throw 'Only a direct PulseForge staging directory under out/package can be promoted.'
    }
}
else {
    if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration }
    $stageId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $stagingRoot = Assert-PackagePath (Join-Path $packageBase "PulseForge-alpha-win-x64.staging-$stageId")
    if (Test-Path -LiteralPath $stagingRoot) { throw "Staging path already exists: $stagingRoot." }
    $hostRoot = Join-Path $stagingRoot 'AudioHost'
    New-Item -ItemType Directory -Path $hostRoot | Out-Null

    $editorProject = Join-Path $repositoryRoot 'managed\PulseForge.Editor\PulseForge.Editor.csproj'
    $hostProject = Join-Path $repositoryRoot 'managed\PulseForge.AudioHost\PulseForge.AudioHost.csproj'
    dotnet restore $editorProject -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'PulseForge workstation runtime restore failed; existing package was preserved.' }
    dotnet restore $hostProject -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'PulseForge Audio Host runtime restore failed; existing package was preserved.' }

    # Runtime-specific publishing must compile its restored assets target even after an ordinary build.
    dotnet publish $editorProject -c $Configuration --no-restore -r win-x64 --self-contained false -o $stagingRoot
    if ($LASTEXITCODE -ne 0) { throw 'PulseForge workstation publish failed; existing package was preserved.' }
    dotnet publish $hostProject -c $Configuration --no-restore -r win-x64 --self-contained false -o $hostRoot
    if ($LASTEXITCODE -ne 0) { throw 'PulseForge Audio Host publish failed; existing package was preserved.' }

    $nativeDll = Join-Path $repositoryRoot "out\native\windows-$($Configuration.ToLowerInvariant())\native\PulseForge.Engine.dll"
    if (-not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) { throw 'The native real-time engine is missing from the selected configuration.' }
    Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $stagingRoot 'PulseForge.Engine.dll')
    Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $hostRoot 'PulseForge.Engine.dll')

    $files = [ordered]@{}
    Get-ChildItem -LiteralPath $stagingRoot -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($stagingRoot, $_.FullName).Replace('\', '/')
        $files[$relative] = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        packageId = 'PulseForge.Desktop/0.1.0-alpha'
        runtime = 'win-x64 framework-dependent net10.0'
        configuration = $Configuration
        createdUtc = [DateTime]::UtcNow.ToString('O')
        nativeEngineSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $nativeDll).Hash.ToLowerInvariant()
        virtualDriverIncluded = $false
        defaultDeviceChanged = $false
        files = $files
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stagingRoot 'package-manifest.json') -Encoding utf8NoBOM
}

& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageRoot $stagingRoot -NativeEnginePath $nativeDll
if ($StageOnly) {
    Write-Output "PULSEFORGE_PACKAGE_STAGED path='$stagingRoot' existing-package=preserved"
    return
}

# All publishing and validation complete before touching the current package.
$packageRoot = Assert-PackagePath $packageRoot
$stagingRoot = Assert-PackagePath $stagingRoot
Assert-PackageStopped $packageRoot
Assert-PackageStopped $stagingRoot
$rollbackRoot = $null
if (Test-Path -LiteralPath $packageRoot) {
    $rollbackId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $rollbackRoot = Assert-PackagePath (Join-Path $packageBase "PulseForge-alpha-win-x64.rollback-$rollbackId")
    if (Test-Path -LiteralPath $rollbackRoot) { throw "Rollback path already exists: $rollbackRoot." }
    Move-Item -LiteralPath $packageRoot -Destination $rollbackRoot
}
try {
    Move-Item -LiteralPath $stagingRoot -Destination $packageRoot
}
catch {
    if ($rollbackRoot -and -not (Test-Path -LiteralPath $packageRoot)) {
        $null = Assert-PackagePath $rollbackRoot
        $null = Assert-PackagePath $packageRoot
        Move-Item -LiteralPath $rollbackRoot -Destination $packageRoot
    }
    throw
}
& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackageRoot $packageRoot -NativeEnginePath $nativeDll
Write-Output "PULSEFORGE_PACKAGE_OK path='$packageRoot' rollback='$rollbackRoot' driver=included-no"
