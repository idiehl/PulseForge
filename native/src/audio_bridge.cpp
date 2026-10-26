#include "pulseforge/pulseforge.h"

#include "miniaudio.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <cwctype>
#include <memory>
#include <new>
#include <numbers>
#include <string>
#include <vector>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propvarutil.h>
#include <wrl/client.h>
#endif

namespace {

constexpr std::size_t RingCapacity = 65'536;
constexpr std::size_t ResamplerTaps = 128;
constexpr std::size_t ResamplerPhases = 512;
constexpr std::size_t MaximumCallbackFrames = 8'192;

[[nodiscard]] std::string deviceIdToUtf8(const ma_device_id& id)
{
#if defined(_WIN32)
    const int required = WideCharToMultiByte(CP_UTF8, 0, id.wasapi, -1, nullptr, 0, nullptr, nullptr);
    if (required <= 1) return {};
    std::string result(static_cast<std::size_t>(required), '\0');
    WideCharToMultiByte(CP_UTF8, 0, id.wasapi, -1, result.data(), required, nullptr, nullptr);
    result.pop_back();
    return result;
#else
    std::array<char, sizeof(ma_device_id) * 2 + 1> buffer{};
    static constexpr char Hex[] = "0123456789abcdef";
    const auto* bytes = reinterpret_cast<const unsigned char*>(&id);
    for (std::size_t index = 0; index < sizeof(ma_device_id); ++index) {
        buffer[index * 2] = Hex[bytes[index] >> 4];
        buffer[index * 2 + 1] = Hex[bytes[index] & 0x0F];
    }
    return buffer.data();
#endif
}

[[nodiscard]] bool containsPulseForge(const char* name)
{
    if (name == nullptr) return false;
    std::string lower(name);
    std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char value) { return static_cast<char>(std::tolower(value)); });
    return lower.find("pulseforge") != std::string::npos;
}

struct DeviceEntry final {
    ma_device_id nativeId{};
    std::string id;
    std::string stableId;
    std::string hardwareId;
    std::string name;
    bool isDefault = false;
    bool isVirtual = false;
};

#if defined(_WIN32)
using Microsoft::WRL::ComPtr;

[[nodiscard]] std::string wideToUtf8(const std::wstring& value)
{
    if (value.empty()) return {};
    const int required = WideCharToMultiByte(
        CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (required <= 0) return {};
    std::string result(static_cast<std::size_t>(required), '\0');
    if (WideCharToMultiByte(
            CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), required, nullptr, nullptr) != required) {
        return {};
    }
    return result;
}

[[nodiscard]] std::wstring readStringProperty(IPropertyStore* properties, const PROPERTYKEY& key)
{
    if (properties == nullptr) return {};
    PROPVARIANT value;
    PropVariantInit(&value);
    const HRESULT result = properties->GetValue(key, &value);
    std::wstring text;
    if (SUCCEEDED(result) && value.vt == VT_LPWSTR && value.pwszVal != nullptr) text = value.pwszVal;
    PropVariantClear(&value);
    return text;
}

void enrichWindowsDeviceIdentities(std::vector<DeviceEntry>& devices)
{
    const HRESULT initialization = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitialize = initialization == S_OK || initialization == S_FALSE;
    if (FAILED(initialization) && initialization != RPC_E_CHANGED_MODE) return;

    ComPtr<IMMDeviceEnumerator> enumerator;
    const HRESULT createResult = CoCreateInstance(
        __uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
    if (SUCCEEDED(createResult)) {
        for (DeviceEntry& entry : devices) {
            entry.stableId = entry.id;
            ComPtr<IMMDevice> endpoint;
            if (FAILED(enumerator->GetDevice(entry.nativeId.wasapi, &endpoint))) continue;
            ComPtr<IPropertyStore> properties;
            if (FAILED(endpoint->OpenPropertyStore(STGM_READ, &properties))) continue;

            std::wstring hardwareId = readStringProperty(properties.Get(), PKEY_Device_InstanceId);
            if (hardwareId.empty()) hardwareId = readStringProperty(properties.Get(), PKEY_Device_MatchingDeviceId);
            entry.hardwareId = wideToUtf8(hardwareId);
        }
    }
    if (uninitialize) CoUninitialize();
}
#endif

class PolyphaseRing final {
public:
    PolyphaseRing()
    {
        buildCoefficients();
    }

    void reset(std::size_t targetFillFrames) noexcept
    {
        data_.fill(0.0F);
        writeFrame_.store(0, std::memory_order_release);
        committedReadFrame_.store(0, std::memory_order_release);
        sourcePosition_ = 0.0;
        targetFill_ = std::max<std::size_t>(targetFillFrames, ResamplerTaps * 2);
        primed_ = false;
        underruns_.store(0, std::memory_order_release);
        overruns_.store(0, std::memory_order_release);
    }

    void write(const float* interleaved, std::uint32_t frameCount) noexcept
    {
        std::uint64_t write = writeFrame_.load(std::memory_order_relaxed);
        const std::uint64_t read = committedReadFrame_.load(std::memory_order_acquire);
        for (std::uint32_t frame = 0; frame < frameCount; ++frame) {
            if (write - read >= RingCapacity - 1) {
                overruns_.fetch_add(frameCount - frame, std::memory_order_relaxed);
                break;
            }
            const std::size_t slot = static_cast<std::size_t>(write % RingCapacity) * 2;
            const std::size_t input = static_cast<std::size_t>(frame) * 2;
            data_[slot] = interleaved[input];
            data_[slot + 1] = interleaved[input + 1];
            ++write;
        }
        writeFrame_.store(write, std::memory_order_release);
    }

    void render(float* output, std::uint32_t frameCount) noexcept
    {
        std::fill_n(output, static_cast<std::size_t>(frameCount) * 2, 0.0F);
        const std::uint64_t write = writeFrame_.load(std::memory_order_acquire);
        if (!primed_) {
            if (write < targetFill_ + ResamplerTaps) return;
            sourcePosition_ = static_cast<double>(write - targetFill_);
            sourcePosition_ = std::max(sourcePosition_, static_cast<double>(ResamplerTaps / 2));
            primed_ = true;
        }
        const double fill = static_cast<double>(write) - sourcePosition_;
        const double error = (fill - static_cast<double>(targetFill_)) / static_cast<double>(targetFill_);
        // The transport clocks are independent. A bounded proportional correction
        // keeps the ring centred while remaining far below an audible pitch step.
        const double ratio = 1.0 + std::clamp(error * 0.005, -0.002, 0.002);
        for (std::uint32_t frame = 0; frame < frameCount; ++frame) {
            const std::int64_t center = static_cast<std::int64_t>(std::floor(sourcePosition_));
            const std::int64_t first = center - static_cast<std::int64_t>(ResamplerTaps / 2) + 1;
            if (first < 0 || static_cast<std::uint64_t>(center + static_cast<std::int64_t>(ResamplerTaps / 2)) >= write) {
                underruns_.fetch_add(1, std::memory_order_relaxed);
                primed_ = false;
                break;
            }
            const double fraction = sourcePosition_ - static_cast<double>(center);
            const std::size_t phase = std::min<std::size_t>(
                static_cast<std::size_t>(fraction * static_cast<double>(ResamplerPhases)), ResamplerPhases - 1);
            double left = 0.0;
            double right = 0.0;
            for (std::size_t tap = 0; tap < ResamplerTaps; ++tap) {
                const std::uint64_t absolute = static_cast<std::uint64_t>(first + static_cast<std::int64_t>(tap));
                const std::size_t slot = static_cast<std::size_t>(absolute % RingCapacity) * 2;
                const double coefficient = coefficients_[phase * ResamplerTaps + tap];
                left += static_cast<double>(data_[slot]) * coefficient;
                right += static_cast<double>(data_[slot + 1]) * coefficient;
            }
            const std::size_t destination = static_cast<std::size_t>(frame) * 2;
            output[destination] = static_cast<float>(left);
            output[destination + 1] = static_cast<float>(right);
            sourcePosition_ += ratio;
        }
        const double commitPosition = std::max(0.0, sourcePosition_ - static_cast<double>(ResamplerTaps));
        committedReadFrame_.store(static_cast<std::uint64_t>(commitPosition), std::memory_order_release);
    }

    [[nodiscard]] std::uint64_t underruns() const noexcept { return underruns_.load(std::memory_order_acquire); }
    [[nodiscard]] std::uint64_t overruns() const noexcept { return overruns_.load(std::memory_order_acquire); }

private:
    std::array<float, RingCapacity * 2> data_{};
    std::array<double, ResamplerPhases * ResamplerTaps> coefficients_{};
    std::atomic<std::uint64_t> writeFrame_{0};
    std::atomic<std::uint64_t> committedReadFrame_{0};
    std::atomic<std::uint64_t> underruns_{0};
    std::atomic<std::uint64_t> overruns_{0};
    double sourcePosition_ = 0.0;
    std::size_t targetFill_ = 512;
    bool primed_ = false;

    void buildCoefficients() noexcept
    {
        constexpr double cutoff = 0.47;
        for (std::size_t phase = 0; phase < ResamplerPhases; ++phase) {
            const double fraction = static_cast<double>(phase) / static_cast<double>(ResamplerPhases);
            double sum = 0.0;
            for (std::size_t tap = 0; tap < ResamplerTaps; ++tap) {
                const double centered = static_cast<double>(tap) - static_cast<double>(ResamplerTaps / 2 - 1) - fraction;
                const double argument = 2.0 * cutoff * centered;
                const double sinc = std::abs(argument) < 1.0e-12 ? 1.0 : std::sin(std::numbers::pi * argument) / (std::numbers::pi * argument);
                const double windowPosition = static_cast<double>(tap) / static_cast<double>(ResamplerTaps - 1);
                const double window = 0.42 - 0.5 * std::cos(2.0 * std::numbers::pi * windowPosition) + 0.08 * std::cos(4.0 * std::numbers::pi * windowPosition);
                const double coefficient = 2.0 * cutoff * sinc * window;
                coefficients_[phase * ResamplerTaps + tap] = coefficient;
                sum += coefficient;
            }
            if (std::abs(sum) > 1.0e-12) {
                for (std::size_t tap = 0; tap < ResamplerTaps; ++tap) coefficients_[phase * ResamplerTaps + tap] /= sum;
            }
        }
    }
};

} // namespace

struct pf_audio_context final {
    ma_context context{};
    bool contextInitialized = false;
    std::vector<DeviceEntry> devices;
    pf_engine* engine = nullptr;
    ma_device captureDevice{};
    ma_device playbackDevice{};
    bool captureInitialized = false;
    bool playbackInitialized = false;
    std::atomic<bool> running{false};
    std::atomic<bool> dspFaultBypass{false};
    std::atomic<bool> endpointFault{false};
    std::atomic<int> failureCode{0};
    std::uint32_t sampleRate = 48'000;
    std::uint32_t periodFrames = 128;
    std::uint32_t requestedPeriodFrames = 128;
    std::uint32_t sourcePeriodFrames = 0;
    std::uint32_t outputPeriodFrames = 0;
    std::uint32_t bufferedFrames = 0;
    std::uint32_t transport = PF_TRANSPORT_RAW_SHARED;
    std::uint32_t bufferProfile = PF_BUFFER_BALANCED;
    double addedLatencyMs = 0.0;
    PolyphaseRing ring;
    std::array<float, MaximumCallbackFrames * 2> processScratch{};
    double bypassFade = 0.0;
    double outputFade = 0.0;
};

namespace {

void captureCallback(ma_device* device, void*, const void* input, ma_uint32 frameCount)
{
    auto* context = static_cast<pf_audio_context*>(device->pUserData);
    if (context == nullptr || input == nullptr || !context->running.load(std::memory_order_acquire)) return;
    const auto* source = static_cast<const float*>(input);
    std::uint32_t offset = 0;
    while (offset < frameCount) {
        const std::uint32_t count = std::min<std::uint32_t>(frameCount - offset, MaximumCallbackFrames);
        const float* block = source + static_cast<std::size_t>(offset) * 2;
        if (!context->dspFaultBypass.load(std::memory_order_acquire)) {
            const pf_result result = pf_engine_process(context->engine, block, context->processScratch.data(), count);
            if (result != PF_SUCCESS) {
                context->failureCode.store(static_cast<int>(result), std::memory_order_release);
                context->dspFaultBypass.store(true, std::memory_order_release);
                context->bypassFade = 0.0;
            }
        }
        if (context->dspFaultBypass.load(std::memory_order_acquire)) {
            const double step = 1.0 / std::max(1.0, static_cast<double>(context->sampleRate) * 0.005);
            for (std::uint32_t frame = 0; frame < count; ++frame) {
                context->bypassFade = std::min(1.0, context->bypassFade + step);
                context->processScratch[static_cast<std::size_t>(frame) * 2] = static_cast<float>(static_cast<double>(block[static_cast<std::size_t>(frame) * 2]) * context->bypassFade);
                context->processScratch[static_cast<std::size_t>(frame) * 2 + 1] = static_cast<float>(static_cast<double>(block[static_cast<std::size_t>(frame) * 2 + 1]) * context->bypassFade);
            }
        }
        context->ring.write(context->processScratch.data(), count);
        offset += count;
    }
}

void playbackCallback(ma_device* device, void* output, const void*, ma_uint32 frameCount)
{
    auto* context = static_cast<pf_audio_context*>(device->pUserData);
    if (context == nullptr || output == nullptr) return;
    if (!context->running.load(std::memory_order_acquire)) {
        std::memset(output, 0, static_cast<std::size_t>(frameCount) * 2 * sizeof(float));
        return;
    }
    context->ring.render(static_cast<float*>(output), frameCount);
    auto* samples = static_cast<float*>(output);
    if (context->endpointFault.load(std::memory_order_acquire)) {
        const double step = 1.0 / std::max(1.0, static_cast<double>(context->sampleRate) * 0.005);
        for (std::uint32_t frame = 0; frame < frameCount; ++frame) {
            context->outputFade = std::max(0.0, context->outputFade - step);
            samples[static_cast<std::size_t>(frame) * 2] = static_cast<float>(static_cast<double>(samples[static_cast<std::size_t>(frame) * 2]) * context->outputFade);
            samples[static_cast<std::size_t>(frame) * 2 + 1] = static_cast<float>(static_cast<double>(samples[static_cast<std::size_t>(frame) * 2 + 1]) * context->outputFade);
        }
    } else if (context->outputFade < 1.0) {
        // Never expose a newly opened physical endpoint at full level on its
        // first callback. A bounded 50 ms ramp suppresses startup transients
        // without adding steady-state latency or work outside the callback.
        const double step = 1.0 / std::max(1.0, static_cast<double>(context->sampleRate) * 0.050);
        for (std::uint32_t frame = 0; frame < frameCount; ++frame) {
            context->outputFade = std::min(1.0, context->outputFade + step);
            samples[static_cast<std::size_t>(frame) * 2] = static_cast<float>(static_cast<double>(samples[static_cast<std::size_t>(frame) * 2]) * context->outputFade);
            samples[static_cast<std::size_t>(frame) * 2 + 1] = static_cast<float>(static_cast<double>(samples[static_cast<std::size_t>(frame) * 2 + 1]) * context->outputFade);
        }
    }
}

void notificationCallback(const ma_device_notification* notification)
{
    if (notification == nullptr || notification->pDevice == nullptr) return;
    auto* context = static_cast<pf_audio_context*>(notification->pDevice->pUserData);
    if (context == nullptr) return;
    // miniaudio reports a stopped notification while an intentionally stopped
    // device is being uninitialized. stopBridge() clears running before that
    // teardown begins, so only an unsolicited stop on a live route is a fault.
    if (!context->running.load(std::memory_order_acquire)) return;
    if (notification->type == ma_device_notification_type_stopped || notification->type == ma_device_notification_type_interruption_began) {
        context->endpointFault.store(true, std::memory_order_release);
        context->failureCode.store(PF_AUDIO_ERROR, std::memory_order_release);
    }
}

void copyString(char* destination, std::size_t capacity, const std::string& source) noexcept
{
    if (destination == nullptr || capacity == 0) return;
    const std::size_t count = std::min(capacity - 1, source.size());
    std::memcpy(destination, source.data(), count);
    destination[count] = '\0';
}

void stopBridge(pf_audio_context& context) noexcept
{
    context.running.store(false, std::memory_order_release);
    if (context.captureInitialized) {
        ma_device_uninit(&context.captureDevice);
        context.captureInitialized = false;
    }
    if (context.playbackInitialized) {
        ma_device_uninit(&context.playbackDevice);
        context.playbackInitialized = false;
    }
}

#if defined(_WIN32)
constexpr std::uint32_t DiagnosticSampleRate = 48'000;
constexpr std::uint32_t DiagnosticDurationSeconds = 6;
constexpr std::uint64_t DiagnosticFrameCapacity =
    static_cast<std::uint64_t>(DiagnosticSampleRate) * DiagnosticDurationSeconds;
constexpr std::uint64_t DiagnosticLeftStartFrame = DiagnosticSampleRate;
constexpr std::uint64_t DiagnosticLeftEndFrame = DiagnosticLeftStartFrame + DiagnosticSampleRate * 3 / 2;
constexpr std::uint64_t DiagnosticRightStartFrame = DiagnosticSampleRate * 13 / 4;
constexpr std::uint64_t DiagnosticRightEndFrame = DiagnosticRightStartFrame + DiagnosticSampleRate * 3 / 2;
constexpr std::uint64_t DiagnosticFadeFrames = DiagnosticSampleRate / 50;
constexpr double DiagnosticLeftFrequency = 997.0;
constexpr double DiagnosticRightFrequency = 1'603.0;
constexpr double DiagnosticUnexpectedToneFrequency = 3'000.0;

[[nodiscard]] double diagnosticEnvelope(
    std::uint64_t frame,
    std::uint64_t startFrame,
    std::uint64_t endFrame) noexcept
{
    if (frame < startFrame || frame >= endFrame) return 0.0;
    const std::uint64_t fadeInFrames = std::min(DiagnosticFadeFrames, endFrame - startFrame);
    const std::uint64_t fadeOutStart = endFrame - fadeInFrames;
    if (frame < startFrame + fadeInFrames) {
        return static_cast<double>(frame - startFrame) / static_cast<double>(fadeInFrames);
    }
    if (frame >= fadeOutStart) {
        return static_cast<double>(endFrame - frame) / static_cast<double>(fadeInFrames);
    }
    return 1.0;
}

struct VirtualLoopbackDiagnosticState final {
    float* captured = nullptr;
    std::uint64_t captureCapacityFrames = 0;
    std::atomic<std::uint64_t> capturedFrames{0};
    std::atomic<std::uint64_t> renderedFrames{0};
};

void diagnosticPlaybackCallback(ma_device* device, void* output, const void*, ma_uint32 frameCount)
{
    auto* state = static_cast<VirtualLoopbackDiagnosticState*>(device->pUserData);
    if (state == nullptr || output == nullptr) return;
    auto* samples = static_cast<float*>(output);
    const std::uint64_t firstFrame = state->renderedFrames.fetch_add(frameCount, std::memory_order_relaxed);
    for (ma_uint32 frame = 0; frame < frameCount; ++frame) {
        const std::uint64_t absoluteFrame = firstFrame + frame;
        const double time = static_cast<double>(absoluteFrame) / static_cast<double>(DiagnosticSampleRate);
        const double leftEnvelope = diagnosticEnvelope(absoluteFrame, DiagnosticLeftStartFrame, DiagnosticLeftEndFrame);
        const double rightEnvelope = diagnosticEnvelope(absoluteFrame, DiagnosticRightStartFrame, DiagnosticRightEndFrame);
        const std::size_t offset = static_cast<std::size_t>(frame) * 2;
        samples[offset] = static_cast<float>(
            0.02 * leftEnvelope * std::sin(2.0 * std::numbers::pi * DiagnosticLeftFrequency * time));
        samples[offset + 1] = static_cast<float>(
            0.02 * rightEnvelope * std::sin(2.0 * std::numbers::pi * DiagnosticRightFrequency * time));
    }
}

void diagnosticCaptureCallback(ma_device* device, void*, const void* input, ma_uint32 frameCount)
{
    auto* state = static_cast<VirtualLoopbackDiagnosticState*>(device->pUserData);
    if (state == nullptr || input == nullptr || state->captured == nullptr) return;
    const std::uint64_t firstFrame = state->capturedFrames.fetch_add(frameCount, std::memory_order_relaxed);
    if (firstFrame >= state->captureCapacityFrames) return;
    const std::uint64_t writable = std::min<std::uint64_t>(frameCount, state->captureCapacityFrames - firstFrame);
    std::memcpy(
        state->captured + static_cast<std::size_t>(firstFrame) * 2,
        input,
        static_cast<std::size_t>(writable) * 2 * sizeof(float));
}

[[nodiscard]] double diagnosticRmsDb(const float* samples, std::uint64_t frames) noexcept
{
    if (samples == nullptr || frames == 0) return -120.0;
    double energy = 0.0;
    for (std::uint64_t frame = 0; frame < frames; ++frame) {
        const double left = samples[static_cast<std::size_t>(frame) * 2];
        const double right = samples[static_cast<std::size_t>(frame) * 2 + 1];
        energy += left * left + right * right;
    }
    const double rms = std::sqrt(energy / static_cast<double>(frames * 2));
    return 20.0 * std::log10(std::max(rms, 1.0e-6));
}

[[nodiscard]] double diagnosticToneDb(
    const float* samples,
    std::uint64_t frames,
    std::uint32_t channel,
    double frequency) noexcept
{
    if (samples == nullptr || frames == 0 || channel > 1) return -120.0;
    constexpr std::uint64_t WindowFrames = DiagnosticSampleRate / 4;
    double maximumAmplitude = 0.0;
    for (std::uint64_t firstFrame = 0; firstFrame < frames; firstFrame += WindowFrames) {
        const std::uint64_t count = std::min<std::uint64_t>(WindowFrames, frames - firstFrame);
        if (count < WindowFrames / 2) continue;
        double real = 0.0;
        double imaginary = 0.0;
        for (std::uint64_t offset = 0; offset < count; ++offset) {
            const std::uint64_t frame = firstFrame + offset;
            const double phase = 2.0 * std::numbers::pi * frequency * static_cast<double>(frame) /
                static_cast<double>(DiagnosticSampleRate);
            const double sample = samples[static_cast<std::size_t>(frame) * 2 + channel];
            real += sample * std::cos(phase);
            imaginary -= sample * std::sin(phase);
        }
        const double amplitude = 2.0 * std::sqrt(real * real + imaginary * imaginary) / static_cast<double>(count);
        maximumAmplitude = std::max(maximumAmplitude, amplitude);
    }
    return 20.0 * std::log10(std::max(maximumAmplitude, 1.0e-6));
}
#endif

} // namespace

extern "C" {

pf_result PF_CALL pf_audio_context_create(pf_audio_context** output, pf_engine* engine)
{
    if (output == nullptr || engine == nullptr) return PF_INVALID_ARGUMENT;
    *output = nullptr;
    auto* context = new (std::nothrow) pf_audio_context{};
    if (context == nullptr) return PF_OUT_OF_MEMORY;
    const ma_backend backends[] = { ma_backend_wasapi };
    const ma_result result = ma_context_init(backends, 1, nullptr, &context->context);
    if (result != MA_SUCCESS) {
        delete context;
        return PF_AUDIO_ERROR;
    }
    context->contextInitialized = true;
    context->engine = engine;
    *output = context;
    return pf_audio_refresh_devices(context);
}

void PF_CALL pf_audio_context_destroy(pf_audio_context* context)
{
    if (context == nullptr) return;
    stopBridge(*context);
    if (context->contextInitialized) ma_context_uninit(&context->context);
    delete context;
}

pf_result PF_CALL pf_audio_refresh_devices(pf_audio_context* context)
{
    if (context == nullptr) return PF_INVALID_ARGUMENT;
    if (context->running.load(std::memory_order_acquire)) return PF_BUSY;
    ma_device_info* playbackInfos = nullptr;
    ma_uint32 playbackCount = 0;
    const ma_result result = ma_context_get_devices(&context->context, &playbackInfos, &playbackCount, nullptr, nullptr);
    if (result != MA_SUCCESS) return PF_AUDIO_ERROR;
    context->devices.clear();
    context->devices.reserve(playbackCount);
    for (ma_uint32 index = 0; index < playbackCount; ++index) {
        DeviceEntry entry{};
        entry.nativeId = playbackInfos[index].id;
        entry.id = deviceIdToUtf8(playbackInfos[index].id);
        entry.stableId = entry.id;
        entry.name = playbackInfos[index].name;
        entry.isDefault = playbackInfos[index].isDefault != 0;
        entry.isVirtual = containsPulseForge(playbackInfos[index].name);
        context->devices.push_back(std::move(entry));
    }
#if defined(_WIN32)
    enrichWindowsDeviceIdentities(context->devices);
#endif
    return PF_SUCCESS;
}

uint32_t PF_CALL pf_audio_get_device_count(const pf_audio_context* context)
{
    return context == nullptr ? 0u : static_cast<uint32_t>(context->devices.size());
}

pf_result PF_CALL pf_audio_get_device(const pf_audio_context* context, uint32_t index, pf_audio_device_v1* device)
{
    if (context == nullptr || device == nullptr || device->size < sizeof(pf_audio_device_v1) || device->version != PF_ABI_VERSION) return PF_INVALID_ARGUMENT;
    if (index >= context->devices.size()) return PF_NOT_FOUND;
    const DeviceEntry& entry = context->devices[index];
    std::memset(device, 0, sizeof(*device));
    device->size = sizeof(*device);
    device->version = PF_ABI_VERSION;
    copyString(device->id, sizeof(device->id), entry.id);
    copyString(device->name, sizeof(device->name), entry.name);
    device->is_default = entry.isDefault ? 1u : 0u;
    device->is_pulseforge_virtual = entry.isVirtual ? 1u : 0u;
    copyString(device->stable_id, sizeof(device->stable_id), entry.stableId);
    copyString(device->hardware_id, sizeof(device->hardware_id), entry.hardwareId);
    return PF_SUCCESS;
}

pf_result PF_CALL pf_audio_start_bridge(pf_audio_context* context, const char* sourceDeviceId, const char* targetDeviceId, const pf_bridge_config_v1* config)
{
    if (context == nullptr || sourceDeviceId == nullptr || targetDeviceId == nullptr || config == nullptr || config->size < sizeof(pf_bridge_config_v1) || config->version != PF_ABI_VERSION) return PF_INVALID_ARGUMENT;
    stopBridge(*context);
    const auto source = std::find_if(context->devices.begin(), context->devices.end(), [sourceDeviceId](const DeviceEntry& entry) { return entry.id == sourceDeviceId; });
    const auto target = std::find_if(context->devices.begin(), context->devices.end(), [targetDeviceId](const DeviceEntry& entry) { return entry.id == targetDeviceId; });
    if (source == context->devices.end() || target == context->devices.end()) return PF_NOT_FOUND;
    if (source->id == target->id || !source->isVirtual || target->isVirtual) return PF_INVALID_ARGUMENT;
    if (config->preferred_sample_rate < 44'100 || config->preferred_sample_rate > 192'000 || config->buffer_profile > PF_BUFFER_SAFE || config->transport > PF_TRANSPORT_COMPATIBLE_SHARED) return PF_INVALID_CONFIG;

    context->sampleRate = config->preferred_sample_rate;
    context->requestedPeriodFrames = config->buffer_profile == PF_BUFFER_LOW_LATENCY ? 64u : config->buffer_profile == PF_BUFFER_SAFE ? 256u : 128u;
    context->periodFrames = context->requestedPeriodFrames;
    context->sourcePeriodFrames = 0;
    context->outputPeriodFrames = 0;
    context->bufferedFrames = 0;
    context->transport = config->transport;
    context->bufferProfile = config->buffer_profile;
    context->failureCode.store(0, std::memory_order_release);
    context->dspFaultBypass.store(false, std::memory_order_release);
    context->endpointFault.store(false, std::memory_order_release);
    context->bypassFade = 0.0;
    context->outputFade = 0.0;
    const pf_result prepareResult = pf_engine_prepare(context->engine, context->sampleRate, MaximumCallbackFrames);
    if (prepareResult != PF_SUCCESS) return prepareResult;

    ma_device_config playbackConfig = ma_device_config_init(ma_device_type_playback);
    playbackConfig.playback.pDeviceID = &target->nativeId;
    playbackConfig.playback.format = ma_format_f32;
    playbackConfig.playback.channels = 2;
    playbackConfig.playback.shareMode = config->transport == PF_TRANSPORT_EXCLUSIVE ? ma_share_mode_exclusive : ma_share_mode_shared;
    playbackConfig.sampleRate = context->sampleRate;
    playbackConfig.periodSizeInFrames = context->periodFrames;
    playbackConfig.performanceProfile = ma_performance_profile_low_latency;
    playbackConfig.noFixedSizedCallback = 1;
    playbackConfig.dataCallback = playbackCallback;
    playbackConfig.notificationCallback = notificationCallback;
    playbackConfig.pUserData = context;
    playbackConfig.noClip = 1;
    playbackConfig.wasapi.usage = ma_wasapi_usage_pro_audio;
    playbackConfig.wasapi.noAutoStreamRouting = 1;
    playbackConfig.wasapi.noHardwareOffloading = 1;
    if (config->transport == PF_TRANSPORT_RAW_SHARED) {
        playbackConfig.wasapi.rawShared = 1;
        playbackConfig.wasapi.noAutoConvertSRC = 1;
        playbackConfig.wasapi.noDefaultQualitySRC = 1;
    }
    if (ma_device_init(&context->context, &playbackConfig, &context->playbackDevice) != MA_SUCCESS) return PF_AUDIO_ERROR;
    context->playbackInitialized = true;

    ma_device_config captureConfig = ma_device_config_init(ma_device_type_loopback);
    captureConfig.capture.pDeviceID = &source->nativeId;
    captureConfig.capture.format = ma_format_f32;
    captureConfig.capture.channels = 2;
    captureConfig.capture.shareMode = ma_share_mode_shared;
    captureConfig.sampleRate = context->sampleRate;
    captureConfig.periodSizeInFrames = context->periodFrames;
    captureConfig.performanceProfile = ma_performance_profile_low_latency;
    captureConfig.noFixedSizedCallback = 1;
    captureConfig.dataCallback = captureCallback;
    captureConfig.notificationCallback = notificationCallback;
    captureConfig.pUserData = context;
    captureConfig.wasapi.usage = ma_wasapi_usage_pro_audio;
    captureConfig.wasapi.noAutoStreamRouting = 1;
    captureConfig.wasapi.noHardwareOffloading = 1;
    if (ma_device_init(&context->context, &captureConfig, &context->captureDevice) != MA_SUCCESS) {
        stopBridge(*context);
        return PF_AUDIO_ERROR;
    }
    context->captureInitialized = true;
    const std::uint32_t playbackPeriod = std::max(1u, context->playbackDevice.playback.internalPeriodSizeInFrames);
    const std::uint32_t capturePeriod = std::max(1u, context->captureDevice.capture.internalPeriodSizeInFrames);
    context->sourcePeriodFrames = capturePeriod;
    context->outputPeriodFrames = playbackPeriod;
    context->periodFrames = std::max(playbackPeriod, capturePeriod);
    const std::size_t ringPeriods = config->buffer_profile == PF_BUFFER_SAFE ? 3u : 2u;
    const std::size_t targetFillFrames = static_cast<std::size_t>(context->periodFrames) * ringPeriods;
    context->bufferedFrames = static_cast<std::uint32_t>(targetFillFrames);
    context->ring.reset(targetFillFrames);
    context->running.store(true, std::memory_order_release);
    if (ma_device_start(&context->playbackDevice) != MA_SUCCESS || ma_device_start(&context->captureDevice) != MA_SUCCESS) {
        stopBridge(*context);
        return PF_AUDIO_ERROR;
    }
    context->addedLatencyMs = (static_cast<double>(targetFillFrames + ResamplerTaps / 2) * 1'000.0 / static_cast<double>(context->sampleRate)) + 2.0;
    return PF_SUCCESS;
}

void PF_CALL pf_audio_stop_bridge(pf_audio_context* context)
{
    if (context != nullptr) stopBridge(*context);
}

pf_result PF_CALL pf_audio_get_bridge_status(const pf_audio_context* context, pf_bridge_status_v1* status)
{
    if (context == nullptr || status == nullptr || status->size < sizeof(pf_bridge_status_v1) || status->version != PF_ABI_VERSION) return PF_INVALID_ARGUMENT;
    const int failureCode = context->failureCode.load(std::memory_order_acquire);
    std::memset(status, 0, sizeof(*status));
    status->size = sizeof(*status);
    status->version = PF_ABI_VERSION;
    status->running = context->running.load(std::memory_order_acquire) ? 1u : 0u;
    status->sample_rate = context->sampleRate;
    status->period_frames = context->periodFrames;
    status->added_latency_ms = context->addedLatencyMs;
    status->underruns = context->ring.underruns();
    status->overruns = context->ring.overruns();
    if (failureCode != 0) copyString(status->failure, sizeof(status->failure), pf_result_description(static_cast<pf_result>(failureCode)));
    status->requested_period_frames = context->requestedPeriodFrames;
    status->source_period_frames = context->sourcePeriodFrames;
    status->output_period_frames = context->outputPeriodFrames;
    status->buffered_frames = context->bufferedFrames;
    status->transport = context->transport;
    status->buffer_profile = context->bufferProfile;
    return PF_SUCCESS;
}

pf_result PF_CALL pf_audio_run_resampler_diagnostics(pf_resampler_diagnostics_v1* diagnostics)
{
    if (diagnostics == nullptr || diagnostics->size < sizeof(pf_resampler_diagnostics_v1) || diagnostics->version != PF_ABI_VERSION) return PF_INVALID_ARGUMENT;
    try {
        const auto measure = [](double frequency) {
            auto ring = std::make_unique<PolyphaseRing>();
            ring->reset(256);
            std::uint64_t sourceFrame = 0;
            const auto generate = [&sourceFrame, frequency](std::uint32_t frames) {
                std::vector<float> samples(static_cast<std::size_t>(frames) * 2);
                for (std::uint32_t frame = 0; frame < frames; ++frame) {
                    const float value = static_cast<float>(0.5 * std::sin(2.0 * std::numbers::pi * frequency * static_cast<double>(sourceFrame++) / 48'000.0));
                    samples[static_cast<std::size_t>(frame) * 2] = value;
                    samples[static_cast<std::size_t>(frame) * 2 + 1] = value;
                }
                return samples;
            };
            auto initial = generate(512);
            ring->write(initial.data(), 512);
            std::vector<float> output(128 * 2);
            double energy = 0.0;
            std::uint64_t count = 0;
            for (int block = 0; block < 128; ++block) {
                auto input = generate(128);
                ring->write(input.data(), 128);
                ring->render(output.data(), 128);
                if (block > 4) {
                    for (std::size_t frame = 0; frame < 128; ++frame) {
                        const double sample = output[frame * 2];
                        energy += sample * sample;
                        ++count;
                    }
                }
            }
            return std::sqrt(energy / static_cast<double>(count));
        };

        const auto drift = [](double partsPerMillion) {
            auto ring = std::make_unique<PolyphaseRing>();
            ring->reset(256);
            std::uint64_t sourceFrame = 0;
            double fractionalFrames = 0.0;
            const auto writeFrames = [&ring, &sourceFrame](std::uint32_t frames) {
                std::array<float, 129 * 2> input{};
                for (std::uint32_t frame = 0; frame < frames; ++frame) {
                    const float value = static_cast<float>(0.2 * std::sin(2.0 * std::numbers::pi * 997.0 * static_cast<double>(sourceFrame++) / 48'000.0));
                    input[static_cast<std::size_t>(frame) * 2] = value;
                    input[static_cast<std::size_t>(frame) * 2 + 1] = value;
                }
                ring->write(input.data(), frames);
            };
            writeFrames(128);
            writeFrames(128);
            writeFrames(128);
            writeFrames(128);
            std::array<float, 128 * 2> output{};
            for (int block = 0; block < 4'000; ++block) {
                fractionalFrames += partsPerMillion * 128.0 / 1'000'000.0;
                std::uint32_t frames = 128;
                if (fractionalFrames >= 1.0) {
                    ++frames;
                    fractionalFrames -= 1.0;
                } else if (fractionalFrames <= -1.0) {
                    --frames;
                    fractionalFrames += 1.0;
                }
                writeFrames(frames);
                ring->render(output.data(), 128);
            }
            return std::pair{ ring->underruns(), ring->overruns() };
        };

        const double passband = measure(1'000.0);
        const double stopband = measure(23'900.0);
        const auto positive = drift(500.0);
        const auto negative = drift(-500.0);
        std::memset(diagnostics, 0, sizeof(*diagnostics));
        diagnostics->size = sizeof(*diagnostics);
        diagnostics->version = PF_ABI_VERSION;
        diagnostics->passband_gain_db = 20.0 * std::log10(std::max(passband, 1.0e-15) / (0.5 / std::sqrt(2.0)));
        diagnostics->stopband_rejection_db = 20.0 * std::log10(std::max(stopband, 1.0e-15) / std::max(passband, 1.0e-15));
        diagnostics->drift_underruns = positive.first + negative.first;
        diagnostics->drift_overruns = positive.second + negative.second;
        return PF_SUCCESS;
    } catch (const std::bad_alloc&) {
        return PF_OUT_OF_MEMORY;
    }
}

pf_result PF_CALL pf_audio_run_virtual_loopback_diagnostics(
    const char* sourceDeviceId,
    pf_virtual_loopback_diagnostics_v1* diagnostics)
{
    if (sourceDeviceId == nullptr || diagnostics == nullptr ||
        diagnostics->size < sizeof(pf_virtual_loopback_diagnostics_v1) ||
        diagnostics->version != PF_ABI_VERSION)
    {
        return PF_INVALID_ARGUMENT;
    }

    const std::uint32_t requestedSize = diagnostics->size;
    std::memset(diagnostics, 0, sizeof(*diagnostics));
    diagnostics->size = requestedSize;
    diagnostics->version = PF_ABI_VERSION;
    diagnostics->sample_rate = 48'000;
    diagnostics->captured_rms_dbfs = -120.0;
    diagnostics->left_probe_dbfs = -120.0;
    diagnostics->right_probe_dbfs = -120.0;
    diagnostics->unexpected_3000hz_dbfs = -120.0;

#if !defined(_WIN32)
    copyString(diagnostics->failure, sizeof(diagnostics->failure), "Virtual endpoint diagnostics require Windows WASAPI.");
    return PF_UNSUPPORTED;
#else
    ma_context context{};
    ma_device playback{};
    ma_device capture{};
    bool contextInitialized = false;
    bool playbackInitialized = false;
    bool captureInitialized = false;
    bool playbackStarted = false;
    bool captureStarted = false;
    const auto cleanup = [&]() noexcept {
        if (playbackStarted) {
            ma_device_stop(&playback);
            playbackStarted = false;
        }
        if (captureStarted) {
            ma_device_stop(&capture);
            captureStarted = false;
        }
        if (captureInitialized) {
            ma_device_uninit(&capture);
            captureInitialized = false;
        }
        if (playbackInitialized) {
            ma_device_uninit(&playback);
            playbackInitialized = false;
        }
        if (contextInitialized) {
            ma_context_uninit(&context);
            contextInitialized = false;
        }
    };

    try {
        std::vector<float> captured(static_cast<std::size_t>(DiagnosticFrameCapacity) * 2, 0.0F);
        VirtualLoopbackDiagnosticState state{};
        state.captured = captured.data();
        state.captureCapacityFrames = DiagnosticFrameCapacity;

        const ma_backend backends[] = { ma_backend_wasapi };
        if (ma_context_init(backends, 1, nullptr, &context) != MA_SUCCESS) {
            copyString(diagnostics->failure, sizeof(diagnostics->failure), "Unable to initialize the WASAPI diagnostic context.");
            return PF_AUDIO_ERROR;
        }
        contextInitialized = true;

        ma_device_info* playbackInfos = nullptr;
        ma_uint32 playbackCount = 0;
        if (ma_context_get_devices(&context, &playbackInfos, &playbackCount, nullptr, nullptr) != MA_SUCCESS) {
            copyString(diagnostics->failure, sizeof(diagnostics->failure), "Unable to enumerate WASAPI render endpoints.");
            cleanup();
            return PF_AUDIO_ERROR;
        }
        const ma_device_info* source = nullptr;
        for (ma_uint32 index = 0; index < playbackCount; ++index) {
            if (deviceIdToUtf8(playbackInfos[index].id) == sourceDeviceId && containsPulseForge(playbackInfos[index].name)) {
                source = &playbackInfos[index];
                break;
            }
        }
        if (source == nullptr) {
            copyString(diagnostics->failure, sizeof(diagnostics->failure), "The requested PulseForge virtual endpoint was not found.");
            cleanup();
            return PF_NOT_FOUND;
        }

        ma_device_config playbackConfig = ma_device_config_init(ma_device_type_playback);
        playbackConfig.playback.pDeviceID = &source->id;
        playbackConfig.playback.format = ma_format_f32;
        playbackConfig.playback.channels = 2;
        playbackConfig.playback.shareMode = ma_share_mode_shared;
        playbackConfig.sampleRate = DiagnosticSampleRate;
        playbackConfig.periodSizeInFrames = 480;
        playbackConfig.performanceProfile = ma_performance_profile_low_latency;
        playbackConfig.noFixedSizedCallback = 1;
        playbackConfig.dataCallback = diagnosticPlaybackCallback;
        playbackConfig.pUserData = &state;
        playbackConfig.noClip = 1;
        playbackConfig.wasapi.noAutoStreamRouting = 1;
        playbackConfig.wasapi.noHardwareOffloading = 1;
        if (ma_device_init(&context, &playbackConfig, &playback) != MA_SUCCESS) {
            copyString(diagnostics->failure, sizeof(diagnostics->failure), "Unable to open the PulseForge virtual render endpoint for the diagnostic probe.");
            cleanup();
            return PF_AUDIO_ERROR;
        }
        playbackInitialized = true;

        ma_device_config captureConfig = ma_device_config_init(ma_device_type_loopback);
        captureConfig.capture.pDeviceID = &source->id;
        captureConfig.capture.format = ma_format_f32;
        captureConfig.capture.channels = 2;
        captureConfig.capture.shareMode = ma_share_mode_shared;
        captureConfig.sampleRate = DiagnosticSampleRate;
        captureConfig.periodSizeInFrames = 480;
        captureConfig.performanceProfile = ma_performance_profile_low_latency;
        captureConfig.noFixedSizedCallback = 1;
        captureConfig.dataCallback = diagnosticCaptureCallback;
        captureConfig.pUserData = &state;
        captureConfig.wasapi.noAutoStreamRouting = 1;
        captureConfig.wasapi.noHardwareOffloading = 1;
        if (ma_device_init(&context, &captureConfig, &capture) != MA_SUCCESS) {
            copyString(diagnostics->failure, sizeof(diagnostics->failure), "Unable to open PulseForge loopback capture for the diagnostic probe.");
            cleanup();
            return PF_AUDIO_ERROR;
        }
        captureInitialized = true;

        if (ma_device_start(&capture) != MA_SUCCESS) {
            copyString(diagnostics->failure, sizeof(diagnostics->failure), "Unable to start PulseForge loopback capture for the diagnostic probe.");
            cleanup();
            return PF_AUDIO_ERROR;
        }
        captureStarted = true;
        if (ma_device_start(&playback) != MA_SUCCESS) {
            copyString(diagnostics->failure, sizeof(diagnostics->failure), "Unable to start the PulseForge virtual render diagnostic probe.");
            cleanup();
            return PF_AUDIO_ERROR;
        }
        playbackStarted = true;

        Sleep(DiagnosticDurationSeconds * 1'000);

        if (playbackStarted) {
            ma_device_stop(&playback);
            playbackStarted = false;
        }
        if (captureStarted) {
            ma_device_stop(&capture);
            captureStarted = false;
        }

        const std::uint64_t capturedFrames = std::min<std::uint64_t>(
            state.capturedFrames.load(std::memory_order_acquire),
            DiagnosticFrameCapacity);
        diagnostics->frames_captured = static_cast<std::uint32_t>(capturedFrames);
        diagnostics->captured_rms_dbfs = diagnosticRmsDb(captured.data(), capturedFrames);
        diagnostics->left_probe_dbfs = diagnosticToneDb(captured.data(), capturedFrames, 0, DiagnosticLeftFrequency);
        diagnostics->right_probe_dbfs = diagnosticToneDb(captured.data(), capturedFrames, 1, DiagnosticRightFrequency);
        const double unexpectedLeft = diagnosticToneDb(captured.data(), capturedFrames, 0, DiagnosticUnexpectedToneFrequency);
        const double unexpectedRight = diagnosticToneDb(captured.data(), capturedFrames, 1, DiagnosticUnexpectedToneFrequency);
        diagnostics->unexpected_3000hz_dbfs = std::max(unexpectedLeft, unexpectedRight);

        const bool enoughFrames = capturedFrames >= DiagnosticSampleRate * 2;
        const bool probesPresent = diagnostics->left_probe_dbfs > -50.0 && diagnostics->right_probe_dbfs > -52.0;
        const bool sampleToneAbsent = diagnostics->unexpected_3000hz_dbfs < -55.0;
        diagnostics->passed = enoughFrames && probesPresent && sampleToneAbsent ? 1u : 0u;
        if (!enoughFrames) {
            copyString(diagnostics->failure, sizeof(diagnostics->failure), "Loopback returned too few frames for a bounded diagnostic.");
        } else if (!sampleToneAbsent) {
            copyString(diagnostics->failure, sizeof(diagnostics->failure), "A strong 3 kHz SysVAD sample tone is still present; physical routing is unsafe.");
        } else if (!probesPresent) {
            copyString(diagnostics->failure, sizeof(diagnostics->failure), "The injected stereo probes were not returned by virtual loopback.");
        }
        cleanup();
        return PF_SUCCESS;
    } catch (...) {
        cleanup();
        copyString(diagnostics->failure, sizeof(diagnostics->failure), "The virtual loopback diagnostic encountered an unexpected failure.");
        return PF_AUDIO_ERROR;
    }
#endif
}

} // extern "C"
