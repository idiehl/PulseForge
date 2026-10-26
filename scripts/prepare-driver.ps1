[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$lockPath = Join-Path $repositoryRoot 'driver\upstream.lock.json'
$lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
$checkoutRoot = Join-Path $repositoryRoot '.runtime\windows-driver-samples'
$sourceRoot = Join-Path $checkoutRoot ($lock.subtree -replace '/', '\')
$driverBuildRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'driver\build'))
$preparedRoot = [IO.Path]::GetFullPath((Join-Path $driverBuildRoot 'sysvad'))
$allowedPrefix = $driverBuildRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if (-not $preparedRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to prepare driver outside $driverBuildRoot."
}

if (-not (Test-Path -LiteralPath (Join-Path $checkoutRoot '.git'))) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $checkoutRoot) -Force | Out-Null
    git clone --filter=blob:none --no-checkout $lock.upstream $checkoutRoot
    if ($LASTEXITCODE -ne 0) { throw 'Unable to clone the pinned Windows driver samples repository.' }
    git -C $checkoutRoot sparse-checkout init --cone
    if ($LASTEXITCODE -ne 0) { throw 'Unable to initialize sparse checkout.' }
    git -C $checkoutRoot sparse-checkout set $lock.subtree
    if ($LASTEXITCODE -ne 0) { throw 'Unable to select the pinned SysVAD subtree.' }
}

git -C $checkoutRoot checkout --detach $lock.commit
if ($LASTEXITCODE -ne 0) { throw "Unable to check out pinned Windows driver samples commit $($lock.commit)." }
$actualCommit = (git -C $checkoutRoot rev-parse HEAD).Trim()
$actualTree = (git -C $checkoutRoot rev-parse "HEAD:$($lock.subtree)").Trim()
if ($actualCommit -ne $lock.commit -or $actualTree -ne $lock.subtreeTree) {
    throw "Pinned SysVAD validation failed. Expected $($lock.commit)/$($lock.subtreeTree), received $actualCommit/$actualTree."
}
if (-not (Test-Path -LiteralPath $sourceRoot)) { throw "Pinned SysVAD source is missing at $sourceRoot." }

if (Test-Path -LiteralPath $preparedRoot) {
    $resolvedPrepared = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $preparedRoot))
    if (-not $resolvedPrepared.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected path $resolvedPrepared."
    }
    Remove-Item -LiteralPath $resolvedPrepared -Recurse -Force
}
New-Item -ItemType Directory -Path $preparedRoot -Force | Out-Null
Copy-Item -Path (Join-Path $sourceRoot '*') -Destination $preparedRoot -Recurse -Force

$tabletRoot = Join-Path $preparedRoot 'TabletAudioSample'
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'driver\overlay\ComponentizedAudioSample.inx') -Destination (Join-Path $tabletRoot 'ComponentizedAudioSample.inx') -Force
$loopbackHeaderPath = Join-Path $preparedRoot 'EndpointsCommon\PulseForgeLoopback.h'
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'driver\overlay\PulseForgeLoopback.h') -Destination $loopbackHeaderPath -Force
foreach ($unusedInf in ('ComponentizedApoSample.inx', 'ComponentizedAudioSampleExtension.inx')) {
    $unusedPath = Join-Path $tabletRoot $unusedInf
    if (Test-Path -LiteralPath $unusedPath) { Remove-Item -LiteralPath $unusedPath -Force }
}

$minipairsPath = Join-Path $tabletRoot 'minipairs.h'
$minipairs = Get-Content -Raw -LiteralPath $minipairsPath
$newEndpointLists = @'
static
PENDPOINT_MINIPAIR  g_RenderEndpoints[] =
{
    &SpeakerMiniports,
};

#define g_cRenderEndpoints  (SIZEOF_ARRAY(g_RenderEndpoints))

// PulseForge is render-only. Keep a non-empty backing declaration for strict
// C++ compilers while publishing a zero capture-endpoint count.
static
PENDPOINT_MINIPAIR  g_CaptureEndpoints[1] =
{
    NULL,
};

#define g_cCaptureEndpoints (0)
'@
$endpointPattern = '(?s)static\s+PENDPOINT_MINIPAIR\s+g_RenderEndpoints\[\]\s*=\s*\{.*?\};\s*#define\s+g_cRenderEndpoints\s+\(SIZEOF_ARRAY\(g_RenderEndpoints\)\)\s*//=+\s*//\s*// Capture miniport pairs\.\s*//\s*static\s+PENDPOINT_MINIPAIR\s+g_CaptureEndpoints\[\]\s*=\s*\{.*?\};\s*#define\s+g_cCaptureEndpoints\s+\(SIZEOF_ARRAY\(g_CaptureEndpoints\)\)'
$endpointMatches = [Regex]::Matches($minipairs, $endpointPattern)
if ($endpointMatches.Count -ne 1) { throw 'The pinned minipairs endpoint list no longer matches the reviewed overlay.' }
$minipairs = [Regex]::Replace($minipairs, $endpointPattern, $newEndpointLists, 1)

$speakerMarker = 'ENDPOINT_MINIPAIR SpeakerMiniports'
$nextMarker = 'ENDPOINT_MINIPAIR SpeakerHpMiniports'
$speakerStart = $minipairs.IndexOf($speakerMarker, [StringComparison]::Ordinal)
$speakerEnd = $minipairs.IndexOf($nextMarker, $speakerStart + $speakerMarker.Length, [StringComparison]::Ordinal)
if ($speakerStart -lt 0 -or $speakerEnd -le $speakerStart) { throw 'Unable to isolate the reviewed speaker miniport.' }
$speaker = $minipairs.Substring($speakerStart, $speakerEnd - $speakerStart)
$newFlags = @'
    ENDPOINT_NO_FLAGS,
    NULL,
    0,
    NULL,
'@
$flagPattern = 'ENDPOINT_OFFLOAD_SUPPORTED,\s*SpeakerModulesWaveFilter,\s*SIZEOF_ARRAY\(SpeakerModulesWaveFilter\),\s*&SpeakerModuleNotificationDeviceId,'
if (([Regex]::Matches($speaker, $flagPattern)).Count -ne 1) { throw 'The pinned speaker feature flags no longer match the reviewed overlay.' }
$speaker = [Regex]::Replace($speaker, $flagPattern, $newFlags.Trim(), 1)
$minipairs = $minipairs.Substring(0, $speakerStart) + $speaker + $minipairs.Substring($speakerEnd)
Set-Content -LiteralPath $minipairsPath -Value $minipairs -Encoding utf8NoBOM

$speakerWaveTablePath = Join-Path $preparedRoot 'EndpointsCommon\speakerwavtable.h'
$speakerWaveTable = Get-Content -Raw -LiteralPath $speakerWaveTablePath
$systemStreamPattern = '#define SPEAKER_MAX_INPUT_SYSTEM_STREAMS\s+6'
if (([Regex]::Matches($speakerWaveTable, $systemStreamPattern)).Count -ne 1) {
    throw 'The pinned speaker system-stream count no longer matches the reviewed overlay.'
}
$speakerWaveTable = [Regex]::Replace($speakerWaveTable, $systemStreamPattern, '#define SPEAKER_MAX_INPUT_SYSTEM_STREAMS            1', 1)
Set-Content -LiteralPath $speakerWaveTablePath -Value $speakerWaveTable -Encoding utf8NoBOM

$streamPath = Join-Path $preparedRoot 'EndpointsCommon\minwavertstream.cpp'
$stream = Get-Content -Raw -LiteralPath $streamPath
$includeMarker = '#include "AudioModuleHelper.h"'
if (([Regex]::Matches($stream, [Regex]::Escape($includeMarker))).Count -ne 1) {
    throw 'The pinned WaveRT stream include block no longer matches the reviewed overlay.'
}
$includeReplacement = $includeMarker + [Environment]::NewLine + '#include "PulseForgeLoopback.h"'
$stream = $stream.Replace($includeMarker, $includeReplacement)

$updateStart = $stream.IndexOf('VOID CMiniportWaveRTStream::UpdatePosition', [StringComparison]::Ordinal)
$updateEnd = $stream.IndexOf('VOID CMiniportWaveRTStream::WriteBytes', $updateStart + 1, [StringComparison]::Ordinal)
if ($updateStart -lt 0 -or $updateEnd -le $updateStart) {
    throw 'Unable to isolate the pinned WaveRT position update.'
}
$updatePosition = $stream.Substring($updateStart, $updateEnd - $updateStart)
$captureTonePattern = '(?s)    if \(m_bCapture\)\s*\{\s*// Write sine wave to buffer\.\s*WriteBytes\(ByteDisplacement\);\s*\}\s*else\s*\{'
if (([Regex]::Matches($updatePosition, $captureTonePattern)).Count -ne 1) {
    throw 'The pinned SysVAD capture-tone branch no longer matches the reviewed overlay.'
}
$captureReplacement = @'
    if (m_bCapture)
    {
        if (m_pMiniport->IsLoopbackPin(m_ulPin))
        {
            PulseForgeLoopback::Read(
                m_pDmaBuffer,
                m_ulDmaBufferSize,
                m_ullLinearPosition,
                ByteDisplacement,
                m_pWfExt);
        }
        else
        {
            WriteBytes(ByteDisplacement);
        }
    }
    else
    {
'@
$updatePosition = [Regex]::Replace($updatePosition, $captureTonePattern, $captureReplacement.TrimEnd(), 1)
$renderSavePattern = '(?m)^        if \(!g_DoNotCreateDataFiles\)\r?\n        \{'
if (([Regex]::Matches($updatePosition, $renderSavePattern)).Count -ne 1) {
    throw 'The pinned SysVAD render-consumption branch no longer matches the reviewed overlay.'
}
$publishRender = @'
        if (m_pMiniport->IsSystemRenderPin(m_ulPin))
        {
            PulseForgeLoopback::Publish(
                m_pDmaBuffer,
                m_ulDmaBufferSize,
                m_ullLinearPosition,
                ByteDisplacement,
                m_pWfExt);
        }

        if (!g_DoNotCreateDataFiles)
        {
'@
$updatePosition = [Regex]::Replace($updatePosition, $renderSavePattern, $publishRender.TrimEnd(), 1)
$stream = $stream.Substring(0, $updateStart) + $updatePosition + $stream.Substring($updateEnd)

$stateStart = $stream.IndexOf('NTSTATUS CMiniportWaveRTStream::SetState', [StringComparison]::Ordinal)
$stateEnd = $stream.IndexOf('NTSTATUS CMiniportWaveRTStream::SetFormat', $stateStart + 1, [StringComparison]::Ordinal)
if ($stateStart -lt 0 -or $stateEnd -le $stateStart) {
    throw 'Unable to isolate the pinned WaveRT state transition.'
}
$setState = $stream.Substring($stateStart, $stateEnd - $stateStart)
$runPattern = '(?m)^        case KSSTATE_RUN:\r?\n'
if (([Regex]::Matches($setState, $runPattern)).Count -ne 1) {
    throw 'The pinned WaveRT RUN transition no longer matches the reviewed overlay.'
}
$runReplacement = @'
        case KSSTATE_RUN:
            if (m_pMiniport->IsLoopbackPin(m_ulPin))
            {
                // Discard pre-capture history so a new route can never replay
                // stale render bytes from an earlier session.
                PulseForgeLoopback::ResetConsumer();
            }

'@
$setState = [Regex]::Replace($setState, $runPattern, $runReplacement.TrimEnd().Replace("`n", [Environment]::NewLine) + [Environment]::NewLine, 1)
$stream = $stream.Substring(0, $stateStart) + $setState + $stream.Substring($stateEnd)
Set-Content -LiteralPath $streamPath -Value $stream -Encoding utf8NoBOM

$adapterPath = Join-Path $preparedRoot 'adapter.cpp'
$adapter = Get-Content -Raw -LiteralPath $adapterPath
$toneDefaultPattern = 'DWORD g_DisableToneGenerator = 0;\s*// default is to generate tones\.'
if (([Regex]::Matches($adapter, $toneDefaultPattern)).Count -ne 1) {
    throw 'The pinned SysVAD tone-generator default no longer matches the reviewed overlay.'
}
$adapter = [Regex]::Replace(
    $adapter,
    $toneDefaultPattern,
    'DWORD g_DisableToneGenerator = 1;  // PulseForge fails closed: synthetic capture tones are never enabled.',
    1)
$captureLoopPattern = '(?s)    NTSTATUS\s+ntStatus;\s+PENDPOINT_MINIPAIR\* ppAeMiniports\s+= g_CaptureEndpoints;\s+PAGED_CODE\(\);\s+for\(ULONG i = 0; i < g_cCaptureEndpoints; \+\+i, \+\+ppAeMiniports\)\s+\{\s+ntStatus = InstallEndpointCaptureFilters\(_pDeviceObject, _pIrp, _pAdapterCommon, \*ppAeMiniports\);\s+IF_FAILED_JUMP\(ntStatus, Exit\);\s+\}\s+ntStatus = STATUS_SUCCESS;\s+Exit:\s+return ntStatus;'
$captureLoopReplacement = @'
    PAGED_CODE();

#if g_cCaptureEndpoints
    NTSTATUS ntStatus;
    PENDPOINT_MINIPAIR* ppAeMiniports = g_CaptureEndpoints;
    for (ULONG i = 0; i < g_cCaptureEndpoints; ++i, ++ppAeMiniports)
    {
        ntStatus = InstallEndpointCaptureFilters(_pDeviceObject, _pIrp, _pAdapterCommon, *ppAeMiniports);
        IF_FAILED_JUMP(ntStatus, Exit);
    }
    ntStatus = STATUS_SUCCESS;
Exit:
    return ntStatus;
#else
    UNREFERENCED_PARAMETER(_pDeviceObject);
    UNREFERENCED_PARAMETER(_pIrp);
    UNREFERENCED_PARAMETER(_pAdapterCommon);
    return STATUS_SUCCESS;
#endif
'@
if (([Regex]::Matches($adapter, $captureLoopPattern)).Count -ne 1) {
    throw 'The pinned capture-filter loop no longer matches the reviewed render-only overlay.'
}
$adapter = [Regex]::Replace($adapter, $captureLoopPattern, $captureLoopReplacement.TrimEnd(), 1)
Set-Content -LiteralPath $adapterPath -Value $adapter -Encoding utf8NoBOM

$preparedInf = Get-Content -Raw -LiteralPath (Join-Path $tabletRoot 'ComponentizedAudioSample.inx')
if ($preparedInf -notmatch [Regex]::Escape($lock.hardwareId) -or $preparedInf -notmatch [Regex]::Escape($lock.endpointName)) {
    throw 'Prepared INF does not contain the exact PulseForge hardware ID and endpoint name.'
}
if (([Regex]::Matches($minipairs, '&SpeakerMiniports,')).Count -ne 1 -or $minipairs -notmatch '#define g_cCaptureEndpoints \(0\)') {
    throw 'Prepared source did not reduce the published endpoint set to one render endpoint.'
}
if ($preparedInf -notmatch 'DisableToneGenerator,0x00010001,1' -or
    $preparedInf -notmatch 'PulseForgeLoopbackVersion,0x00010001,2' -or
    $adapter -notmatch 'g_DisableToneGenerator = 1' -or
    $stream -notmatch 'PulseForgeLoopback::Publish' -or
    $stream -notmatch 'PulseForgeLoopback::Read' -or
    $speakerWaveTable -notmatch 'SPEAKER_MAX_INPUT_SYSTEM_STREAMS\s+1') {
    throw 'Prepared source did not apply the PulseForge real-loopback and synthetic-tone safety overlay.'
}

$metadata = [ordered]@{
    schemaVersion = 1
    preparedUtc = [DateTime]::UtcNow.ToString('O')
    upstreamCommit = $actualCommit
    upstreamTree = $actualTree
    overlayVersion = $lock.overlayVersion
    packageId = "PulseForge.VirtualAudio/$($lock.overlayVersion)"
    hardwareId = $lock.hardwareId
    endpointName = $lock.endpointName
    loopbackImplementation = 'bounded-render-ring-v1'
    syntheticToneGenerator = 'disabled-source-and-service'
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $preparedRoot 'pulseforge-prepared.json') -Encoding utf8NoBOM
Write-Output "PULSEFORGE_DRIVER_SOURCE_OK commit=$actualCommit tree=$actualTree endpoint='$($lock.endpointName)'"
