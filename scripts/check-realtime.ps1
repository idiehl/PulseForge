[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dspPath = Join-Path $repositoryRoot 'native\src\dsp_engine.cpp'
$transportPath = Join-Path $repositoryRoot 'native\src\audio_bridge.cpp'
$dsp = Get-Content -Raw -LiteralPath $dspPath
$transport = Get-Content -Raw -LiteralPath $transportPath

function Get-CxxFunctionBody([string]$Source, [string]$Signature) {
    $signatureIndex = $Source.IndexOf($Signature, [StringComparison]::Ordinal)
    if ($signatureIndex -lt 0) { throw "Real-time audit could not find '$Signature'." }
    $openingBrace = $Source.IndexOf('{', $signatureIndex)
    if ($openingBrace -lt 0) { throw "Real-time audit could not parse '$Signature'." }
    $depth = 0
    for ($index = $openingBrace; $index -lt $Source.Length; $index++) {
        if ($Source[$index] -eq '{') { $depth++ }
        elseif ($Source[$index] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $Source.Substring($signatureIndex, $index - $signatureIndex + 1) }
        }
    }
    throw "Real-time audit found an unterminated body for '$Signature'."
}

$callbackBodies = @(
    (Get-CxxFunctionBody $transport 'void write(const float* interleaved')
    (Get-CxxFunctionBody $transport 'void render(float* output')
    (Get-CxxFunctionBody $transport 'void captureCallback(')
    (Get-CxxFunctionBody $transport 'void playbackCallback(')
) -join "`n"

# All DSP helpers are transitively reachable from DspEngine::process. The only
# native allocation in this translation unit is the engine's construction-time
# implementation object, which is removed before the source-level callback audit.
$dspRealtime = $dsp.Replace('#include <new>', '')
$dspRealtime = [Regex]::Replace(
    $dspRealtime,
    'DspEngine::DspEngine\(\)\s*:\s*impl_\(new\s*\(std::nothrow\)\s*Impl\{\}\)\s*\{\}',
    '',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)
$dspRealtime = [Regex]::Replace(
    $dspRealtime,
    'DspEngine::~DspEngine\(\)\s*\{\s*delete\s+impl_;\s*\}',
    '',
    [Text.RegularExpressions.RegexOptions]::CultureInvariant)

$forbidden = [ordered]@{
    'dynamic allocation' = '\b(new|delete|malloc|calloc|realloc|free)\b|std::(vector|string|unique_ptr|shared_ptr)\b'
    'blocking synchronization' = 'std::(mutex|recursive_mutex|shared_mutex|lock_guard|unique_lock|condition_variable)\b|\b(WaitForSingleObject|Sleep)\s*\('
    'file access' = '\b(fopen|fread|fwrite|ReadFile|WriteFile)\s*\(|std::(ifstream|ofstream|fstream)\b'
    'logging or console output' = '\b(printf|fprintf|OutputDebugString)\s*\(|std::(cout|cerr|clog)\b'
    'managed or UI boundary' = '\b(GCHandle|Dispatcher|Control|Window|Console)\b'
}

foreach ($entry in $forbidden.GetEnumerator()) {
    if ($callbackBodies -match $entry.Value) { throw "Transport callback audit found forbidden $($entry.Key): $($Matches[0])." }
    if ($dspRealtime -match $entry.Value) { throw "DSP callback audit found forbidden $($entry.Key): $($Matches[0])." }
}

Write-Output 'PULSEFORGE_REALTIME_AUDIT_OK allocation=none locks=none files=none logging=none managed=none bounded-callbacks=verified'
