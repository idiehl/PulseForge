#pragma once

// SysVAD's stock loopback pin emits a synthetic validation tone. PulseForge
// instead mirrors the one shared-mode system-render stream into the loopback
// capture pin through this bounded, single-producer/single-consumer byte ring.
// The endpoint overlay intentionally exposes one 48 kHz, stereo, 16-bit PCM
// render stream, so byte-for-byte transfer is sufficient and deterministic.

namespace PulseForgeLoopback
{
    constexpr ULONG RingCapacityBytes = 1u << 20;
    constexpr ULONG PrimeBytes = 48'000u * 4u * 5u / 1'000u;
    static_assert((RingCapacityBytes & (RingCapacityBytes - 1)) == 0, "PulseForge loopback ring capacity must be a power of two.");

    __declspec(align(64)) static UCHAR Ring[RingCapacityBytes] = {};
    __declspec(align(64)) static volatile LONG64 WriteSequence = 0;
    __declspec(align(64)) static volatile LONG64 ReadSequence = 0;
    static volatile LONG FormatReady = 0;
    static volatile LONG Primed = 0;

    __forceinline bool IsExpectedFormat(_In_ const WAVEFORMATEXTENSIBLE* format) noexcept
    {
        if (format == nullptr ||
            format->Format.nChannels != 2 ||
            format->Format.nSamplesPerSec != 48'000 ||
            format->Format.nBlockAlign != 4 ||
            format->Format.wBitsPerSample != 16)
        {
            return false;
        }

        if (format->Format.wFormatTag == WAVE_FORMAT_PCM)
        {
            return true;
        }

        return format->Format.wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
            IsEqualGUID(format->SubFormat, KSDATAFORMAT_SUBTYPE_PCM);
    }

    __forceinline ULONGLONG ReadCounter(_In_ volatile LONG64* counter) noexcept
    {
        return static_cast<ULONGLONG>(InterlockedCompareExchange64(counter, 0, 0));
    }

    __forceinline void ResetConsumer() noexcept
    {
        const ULONGLONG write = ReadCounter(&WriteSequence);
        InterlockedExchange64(&ReadSequence, static_cast<LONG64>(write));
        InterlockedExchange(&Primed, 0);
    }

    __forceinline void Publish(
        _In_reads_bytes_(dmaBufferSize) const BYTE* dmaBuffer,
        _In_ ULONG dmaBufferSize,
        _In_ ULONGLONG linearPosition,
        _In_ ULONG byteCount,
        _In_ const WAVEFORMATEXTENSIBLE* format) noexcept
    {
        if (dmaBuffer == nullptr || dmaBufferSize == 0 || byteCount == 0 || !IsExpectedFormat(format))
        {
            InterlockedExchange(&FormatReady, 0);
            return;
        }

        const ULONG blockAlign = format->Format.nBlockAlign;
        byteCount -= byteCount % blockAlign;
        if (byteCount == 0 || byteCount > RingCapacityBytes)
        {
            return;
        }

        const ULONGLONG write = ReadCounter(&WriteSequence);
        const ULONGLONG read = ReadCounter(&ReadSequence);
        if (write < read || write - read > RingCapacityBytes)
        {
            return;
        }

        const ULONGLONG freeBytes = RingCapacityBytes - (write - read);
        if (byteCount > freeBytes)
        {
            // Never overwrite data the capture side may be reading. A later
            // capture underflow is safer than publishing torn audio.
            return;
        }

        ULONG sourceOffset = static_cast<ULONG>(linearPosition % dmaBufferSize);
        ULONG ringOffset = static_cast<ULONG>(write & (RingCapacityBytes - 1));
        ULONG remaining = byteCount;
        while (remaining > 0)
        {
            const ULONG sourceRun = min(remaining, dmaBufferSize - sourceOffset);
            const ULONG run = min(sourceRun, RingCapacityBytes - ringOffset);
            RtlCopyMemory(Ring + ringOffset, dmaBuffer + sourceOffset, run);
            remaining -= run;
            sourceOffset = (sourceOffset + run) % dmaBufferSize;
            ringOffset = (ringOffset + run) & (RingCapacityBytes - 1);
        }

        KeMemoryBarrier();
        InterlockedExchange(&FormatReady, 1);
        InterlockedExchange64(&WriteSequence, static_cast<LONG64>(write + byteCount));
    }

    __forceinline void Read(
        _Out_writes_bytes_(dmaBufferSize) BYTE* dmaBuffer,
        _In_ ULONG dmaBufferSize,
        _In_ ULONGLONG linearPosition,
        _In_ ULONG byteCount,
        _In_ const WAVEFORMATEXTENSIBLE* format) noexcept
    {
        if (dmaBuffer == nullptr || dmaBufferSize == 0 || byteCount == 0)
        {
            return;
        }

        ULONG destinationOffset = static_cast<ULONG>(linearPosition % dmaBufferSize);
        ULONG clearRemaining = byteCount;
        while (clearRemaining > 0)
        {
            const ULONG run = min(clearRemaining, dmaBufferSize - destinationOffset);
            RtlZeroMemory(dmaBuffer + destinationOffset, run);
            clearRemaining -= run;
            destinationOffset = (destinationOffset + run) % dmaBufferSize;
        }

        if (!IsExpectedFormat(format) || InterlockedCompareExchange(&FormatReady, 0, 0) == 0)
        {
            return;
        }

        const ULONG blockAlign = format->Format.nBlockAlign;
        byteCount -= byteCount % blockAlign;
        const ULONGLONG read = ReadCounter(&ReadSequence);
        const ULONGLONG write = ReadCounter(&WriteSequence);
        if (write < read || write - read > RingCapacityBytes)
        {
            return;
        }
        const ULONGLONG available = write - read;
        if (InterlockedCompareExchange(&Primed, 0, 0) == 0)
        {
            if (available < PrimeBytes + byteCount)
            {
                return;
            }
            InterlockedExchange(&Primed, 1);
        }
        if (available < byteCount)
        {
            // Keep the capture clock moving with silence until a complete
            // packet is available; never expose a partially updated packet.
            return;
        }

        destinationOffset = static_cast<ULONG>(linearPosition % dmaBufferSize);
        ULONG ringOffset = static_cast<ULONG>(read & (RingCapacityBytes - 1));
        ULONG remaining = byteCount;
        while (remaining > 0)
        {
            const ULONG destinationRun = min(remaining, dmaBufferSize - destinationOffset);
            const ULONG run = min(destinationRun, RingCapacityBytes - ringOffset);
            RtlCopyMemory(dmaBuffer + destinationOffset, Ring + ringOffset, run);
            remaining -= run;
            destinationOffset = (destinationOffset + run) % dmaBufferSize;
            ringOffset = (ringOffset + run) & (RingCapacityBytes - 1);
        }

        KeMemoryBarrier();
        InterlockedExchange64(&ReadSequence, static_cast<LONG64>(read + byteCount));
    }
}
