[CmdletBinding()]
param(
    [string]$PackageRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'out\package\PulseForge-alpha-win-x64'),
    [AllowNull()]
    [AllowEmptyString()]
    [string]$NativeEnginePath
)

$ErrorActionPreference = 'Stop'
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
if (-not (Test-Path -LiteralPath $PackageRoot -PathType Container)) { throw "Package directory is missing: $PackageRoot." }
$ancestor = $PackageRoot
while ($ancestor) {
    $item = Get-Item -LiteralPath $ancestor -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package uses reparse point $ancestor." }
    $ancestor = Split-Path -Parent $ancestor
}
$items = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -Force)
$linkedItem = $items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1
if ($linkedItem) { throw "Package contains reparse point $($linkedItem.FullName)." }
$manifestPath = Join-Path $PackageRoot 'package-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Package manifest is missing.' }
$document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($manifestPath))
try {
    $root = $document.RootElement
    $rootKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($property in $root.EnumerateObject()) {
        if (-not $rootKeys.Add($property.Name)) { throw "Duplicate manifest property $($property.Name)." }
    }
    if ($root.GetProperty('schemaVersion').GetInt32() -ne 1 -or
        $root.GetProperty('packageId').GetString() -ne 'PulseForge.Desktop/0.1.0-alpha' -or
        $root.GetProperty('runtime').GetString() -ne 'win-x64 framework-dependent net10.0' -or
        $root.GetProperty('virtualDriverIncluded').GetBoolean() -or
        $root.GetProperty('defaultDeviceChanged').GetBoolean()) { throw 'Unsupported package manifest metadata.' }

    $expected = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $prefix = $PackageRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($file in $root.GetProperty('files').EnumerateObject()) {
        $relative = $file.Name
        # Canonical forward-slash relative paths only: reject ADS, traversal and Windows aliases.
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative -match '(^/|\\|:|[<>"|?*\x00-\x1f])' -or
            @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_ -match '[ .]$' }).Count -gt 0 -or
            $relative -ieq 'package-manifest.json') { throw "Unsafe manifest path: $relative." }
        $fullPath = [IO.Path]::GetFullPath((Join-Path $PackageRoot $relative))
        if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Manifest path escapes package: $relative." }
        $hash = $file.Value.GetString()
        if ($hash -cnotmatch '^[0-9a-f]{64}$') { throw "Invalid SHA-256 for $relative." }
        if (-not $expected.TryAdd($relative, $hash)) { throw "Duplicate package path: $relative." }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Package file is missing: $relative." }
        if ((Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash -ine $hash) { throw "Package file was modified: $relative." }
    }
    foreach ($item in $items | Where-Object { -not $_.PSIsContainer }) {
        $relative = [IO.Path]::GetRelativePath($PackageRoot, $item.FullName).Replace('\', '/')
        if ($relative -ne 'package-manifest.json' -and -not $expected.ContainsKey($relative)) { throw "Unexpected package file: $relative." }
    }
    $required = @(
        'PulseForge.Editor.exe', 'PulseForge.Editor.dll', 'PulseForge.Editor.deps.json', 'PulseForge.Editor.runtimeconfig.json',
        'PulseForge.Engine.dll', 'AudioHost/PulseForge.AudioHost.exe', 'AudioHost/PulseForge.AudioHost.dll',
        'AudioHost/PulseForge.AudioHost.deps.json', 'AudioHost/PulseForge.AudioHost.runtimeconfig.json', 'AudioHost/PulseForge.Engine.dll'
    )
    foreach ($relative in $required) {
        if (-not $expected.ContainsKey($relative)) { throw "Manifest omits required file: $relative." }
    }
    if ($expected['PulseForge.Engine.dll'] -ne $expected['AudioHost/PulseForge.Engine.dll']) { throw 'Editor and host native engine hashes differ.' }
    if ($rootKeys.Contains('nativeEngineSha256') -and $root.GetProperty('nativeEngineSha256').GetString() -ne $expected['PulseForge.Engine.dll']) {
        throw 'Native engine does not match manifest build identity.'
    }
    if ($NativeEnginePath) {
        if (-not (Test-Path -LiteralPath $NativeEnginePath -PathType Leaf)) { throw 'Expected native engine source is missing.' }
        if ((Get-FileHash -LiteralPath $NativeEnginePath -Algorithm SHA256).Hash -ine $expected['PulseForge.Engine.dll']) {
            throw 'Packaged native engine differs from the selected build.'
        }
    }
    Write-Output "PULSEFORGE_PACKAGE_VERIFY_OK path='$PackageRoot' files=$($expected.Count) hashes=passed payload=exact engine=matched"
}
finally {
    $document.Dispose()
}
