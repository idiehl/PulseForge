#include "pulseforge/pulseforge.h"

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <limits>
#include <numbers>
#include <string_view>
#include <vector>

namespace {

void require(bool condition, std::string_view message)
{
    if (!condition) {
        std::cerr << "FAILED: " << message << '\n';
        std::exit(EXIT_FAILURE);
    }
}

struct Engine final {
    pf_engine* value = nullptr;
    Engine()
    {
        require(pf_engine_create(&value) == PF_SUCCESS, "engine creation");
        require(pf_engine_prepare(value, 48'000, 8'192) == PF_SUCCESS, "engine preparation");
    }
    ~Engine() { pf_engine_destroy(value); }
    Engine(const Engine&) = delete;
    Engine& operator=(const Engine&) = delete;
};

void settle(Engine& engine)
{
    std::vector<float> silence(1'024 * 2);
    require(pf_engine_process(engine.value, silence.data(), silence.data(), 1'024) == PF_SUCCESS, "settle processing");
}

std::vector<float> sine(double frequency, double amplitude, std::size_t frames, bool left = true, bool right = true)
{
    std::vector<float> samples(frames * 2);
    for (std::size_t frame = 0; frame < frames; ++frame) {
        const float sample = static_cast<float>(amplitude * std::sin(2.0 * std::numbers::pi * frequency * static_cast<double>(frame) / 48'000.0));
        samples[frame * 2] = left ? sample : 0.0F;
        samples[frame * 2 + 1] = right ? sample : 0.0F;
    }
    return samples;
}

double rms(const std::vector<float>& samples, std::size_t channel, std::size_t skipFrames = 512)
{
    double sum = 0.0;
    std::size_t count = 0;
    for (std::size_t frame = skipFrames; frame < samples.size() / 2; ++frame) {
        const double sample = samples[frame * 2 + channel];
        sum += sample * sample;
        ++count;
    }
    return count == 0 ? 0.0 : std::sqrt(sum / static_cast<double>(count));
}

double rmsRange(const std::vector<float>& samples, std::size_t channel, std::size_t firstFrame, std::size_t lastFrame)
{
    const std::size_t available = samples.size() / 2;
    firstFrame = std::min(firstFrame, available);
    lastFrame = std::min(std::max(lastFrame, firstFrame), available);
    double sum = 0.0;
    for (std::size_t frame = firstFrame; frame < lastFrame; ++frame) {
        const double sample = samples[frame * 2 + channel];
        sum += sample * sample;
    }
    const std::size_t count = lastFrame - firstFrame;
    return count == 0 ? 0.0 : std::sqrt(sum / static_cast<double>(count));
}

void processInBlocks(Engine& engine, const std::vector<float>& input, std::vector<float>& output, std::string_view label)
{
    require(input.size() == output.size() && input.size() % 2 == 0, "block processing buffer sizes");
    const std::size_t frames = input.size() / 2;
    std::size_t offset = 0;
    while (offset < frames) {
        const std::uint32_t count = static_cast<std::uint32_t>(std::min<std::size_t>(8'192, frames - offset));
        require(pf_engine_process(engine.value, input.data() + offset * 2, output.data() + offset * 2, count) == PF_SUCCESS, label);
        offset += count;
    }
}

void testConfigValidation()
{
    pf_config_v1 config{};
    pf_config_init(&config);
    require(pf_config_validate(&config, 48'000) == PF_SUCCESS, "default config validates");
    config.master_gain_db = 18.0;
    config.limiter.enabled = 0;
    require(pf_config_validate(&config, 48'000) == PF_INVALID_CONFIG, "unguarded positive boost rejected");
    config.limiter.expert_bypass_confirmed = 1;
    require(pf_config_validate(&config, 48'000) == PF_SUCCESS, "confirmed expert bypass accepted");
    config.left.filters[0].q = std::nan("");
    require(pf_config_validate(&config, 48'000) == PF_INVALID_CONFIG, "non-finite filter rejected");
}

void testChannelIsolationAndParametricGain()
{
    Engine engine;
    pf_config_v1 config{};
    pf_config_init(&config);
    config.limiter.enabled = 0;
    config.left.filter_count = 1;
    config.left.filters[0] = {1, PF_FILTER_BELL, 1'000.0, 12.0, 2.0};
    config.right.filter_count = 0;
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "apply channel EQ");
    settle(engine);
    auto input = sine(1'000.0, 0.05, 8'192, true, false);
    std::vector<float> output(input.size());
    require(pf_engine_process(engine.value, input.data(), output.data(), 8'192) == PF_SUCCESS, "process channel EQ");
    require(rms(output, 0) > rms(input, 0) * 2.8, "left bell produces expected boost");
    require(rms(output, 1) < 1.0e-8, "right channel remains isolated");
}

void testHighPassSlope()
{
    Engine engine;
    pf_config_v1 config{};
    pf_config_init(&config);
    config.limiter.enabled = 0;
    config.left.filter_count = 0;
    config.right.filter_count = 0;
    config.left.high_pass = {1, 48, 200.0};
    config.right.high_pass = config.left.high_pass;
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "apply high-pass");
    settle(engine);
    auto lowInput = sine(50.0, 0.2, 8'192);
    std::vector<float> lowOutput(lowInput.size());
    require(pf_engine_process(engine.value, lowInput.data(), lowOutput.data(), 8'192) == PF_SUCCESS, "process low tone");
    pf_engine_reset(engine.value);
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "reapply high-pass");
    settle(engine);
    auto highInput = sine(1'000.0, 0.2, 8'192);
    std::vector<float> highOutput(highInput.size());
    require(pf_engine_process(engine.value, highInput.data(), highOutput.data(), 8'192) == PF_SUCCESS, "process high tone");
    require(rms(lowOutput, 0) < rms(highOutput, 0) * 0.03, "48 dB high-pass strongly rejects sub-cutoff tone");
}

void testLowPassSlopeAndUnityPassband()
{
    Engine engine;
    pf_config_v1 config{};
    pf_config_init(&config);
    config.limiter.enabled = 0;
    config.left.filter_count = 0;
    config.right.filter_count = 0;
    config.left.low_pass = {1, 48, 2'000.0};
    config.right.low_pass = config.left.low_pass;
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "apply low-pass");
    settle(engine);
    auto highInput = sine(12'000.0, 0.2, 8'192);
    std::vector<float> highOutput(highInput.size());
    require(pf_engine_process(engine.value, highInput.data(), highOutput.data(), 8'192) == PF_SUCCESS, "process rejected high tone");
    pf_engine_reset(engine.value);
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "reapply low-pass");
    settle(engine);
    auto lowInput = sine(250.0, 0.2, 8'192);
    std::vector<float> lowOutput(lowInput.size());
    require(pf_engine_process(engine.value, lowInput.data(), lowOutput.data(), 8'192) == PF_SUCCESS, "process low passband tone");
    require(rms(highOutput, 0) < rms(lowOutput, 0) * 0.002, "48 dB low-pass strongly rejects high tone");
    require(std::abs(rms(lowOutput, 0) / rms(lowInput, 0) - 1.0) < 0.01, "low-pass passband remains flat");
}

void testStereoWidthMonoAndBalance()
{
    Engine engine;
    pf_config_v1 config{};
    pf_config_init(&config);
    config.limiter.enabled = 0;
    config.left.filter_count = 0;
    config.right.filter_count = 0;
    config.stereo.width_percent = 0.0;
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "apply zero width");
    settle(engine);
    auto input = sine(1'000.0, 0.2, 4'096, true, false);
    std::vector<float> output(input.size());
    require(pf_engine_process(engine.value, input.data(), output.data(), 4'096) == PF_SUCCESS, "process zero width");
    bool channelsMatch = true;
    for (std::size_t frame = 512; frame < output.size() / 2; ++frame) channelsMatch = channelsMatch && std::abs(output[frame * 2] - output[frame * 2 + 1]) < 1.0e-6F;
    require(channelsMatch, "zero width produces identical channels");

    pf_engine_reset(engine.value);
    config.stereo.width_percent = 100.0;
    config.stereo.mono_audition = 1;
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "apply mono audition");
    settle(engine);
    require(pf_engine_process(engine.value, input.data(), output.data(), 4'096) == PF_SUCCESS, "process mono audition");
    require(std::abs(rms(output, 0) / rms(input, 0) - 0.5) < 0.01 && std::abs(rms(output, 1) / rms(input, 0) - 0.5) < 0.01, "mono audition uses unity-safe fold-down");

    pf_engine_reset(engine.value);
    config.stereo.mono_audition = 0;
    config.stereo.balance = 1.0;
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "apply right balance");
    settle(engine);
    auto stereoInput = sine(1'000.0, 0.1, 4'096);
    require(pf_engine_process(engine.value, stereoInput.data(), output.data(), 4'096) == PF_SUCCESS, "process balance");
    require(rms(output, 0) < 1.0e-7 && rms(output, 1) > 0.06, "right balance attenuates only left channel");
}

void testGuardedBoostLimiter()
{
    Engine engine;
    pf_config_v1 config{};
    pf_config_init(&config);
    config.master_gain_db = 18.0;
    config.left.filter_count = 0;
    config.right.filter_count = 0;
    config.limiter.enabled = 1;
    config.limiter.ceiling_db_tp = -1.0;
    config.limiter.release_ms = 80.0;
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "apply guarded boost");
    settle(engine);
    auto input = sine(997.0, 0.8, 8'192);
    std::vector<float> output(input.size());
    require(pf_engine_process(engine.value, input.data(), output.data(), 8'192) == PF_SUCCESS, "process boosted signal");
    const auto maximum = std::max_element(output.begin() + 1'024, output.end(), [](float left, float right) { return std::abs(left) < std::abs(right); });
    const double ceiling = std::pow(10.0, -1.0 / 20.0);
    require(maximum != output.end() && std::abs(*maximum) <= ceiling + 1.0e-5, "limiter honors configured ceiling");
    pf_telemetry_v1 telemetry{};
    telemetry.size = sizeof(telemetry);
    telemetry.version = PF_ABI_VERSION;
    require(pf_engine_read_telemetry(engine.value, &telemetry) == PF_SUCCESS, "read limiter telemetry");
    require(telemetry.true_peak_dbtp <= -0.8F && telemetry.limiter_reduction_db > 10.0F, "true-peak meter reports guarded output and gain reduction");
}

void testClickFreeAutomation()
{
    Engine engine;
    pf_config_v1 flat{};
    pf_config_init(&flat);
    flat.limiter.enabled = 0;
    flat.left.filter_count = 0;
    flat.right.filter_count = 0;
    require(pf_engine_apply_config(engine.value, &flat) == PF_SUCCESS, "apply automation baseline");
    settle(engine);
    auto input = sine(1'000.0, 0.04, 2'048);
    std::vector<float> before(1'024 * 2);
    require(pf_engine_process(engine.value, input.data(), before.data(), 1'024) == PF_SUCCESS, "process automation baseline");

    pf_config_v1 edited = flat;
    edited.left.filter_count = 1;
    edited.right.filter_count = 1;
    edited.left.filters[0] = {1, PF_FILTER_BELL, 1'000.0, 18.0, 24.0};
    edited.right.filters[0] = edited.left.filters[0];
    require(pf_engine_apply_config(engine.value, &edited) == PF_SUCCESS, "queue high-Q automation");
    std::vector<float> after(1'024 * 2);
    require(pf_engine_process(engine.value, input.data() + 1'024 * 2, after.data(), 1'024) == PF_SUCCESS, "process crossfaded automation");
    double largestStep = std::abs(static_cast<double>(after[0]) - before[before.size() - 2]);
    for (std::size_t frame = 1; frame < after.size() / 2; ++frame) largestStep = std::max(largestStep, std::abs(static_cast<double>(after[frame * 2]) - after[(frame - 1) * 2]));
    require(largestStep < 0.08, "filter automation remains click-free during the 128-frame path crossfade");
}

void testRapidParameterStress()
{
    Engine engine;
    pf_config_v1 config{};
    pf_config_init(&config);
    config.left.filter_count = 1;
    config.right.filter_count = 1;
    config.limiter.enabled = 1;
    std::vector<float> input(64 * 2);
    std::vector<float> output(input.size());
    double phase = 0.0;
    float previous = 0.0F;
    double largestStep = 0.0;
    std::uint64_t busyRetries = 0;
    const auto processBlock = [&] {
        for (std::size_t frame = 0; frame < 64; ++frame) {
            const float sample = static_cast<float>(0.02 * std::sin(phase));
            phase += 2.0 * std::numbers::pi * 997.0 / 48'000.0;
            input[frame * 2] = sample;
            input[frame * 2 + 1] = sample;
        }
        require(pf_engine_process(engine.value, input.data(), output.data(), 64) == PF_SUCCESS, "rapid automation block processed");
        for (std::size_t frame = 0; frame < 64; ++frame) {
            const float sample = output[frame * 2];
            require(std::isfinite(sample) && std::isfinite(output[frame * 2 + 1]), "rapid automation remains finite");
            largestStep = std::max(largestStep, std::abs(static_cast<double>(sample) - previous));
            previous = sample;
        }
    };
    for (int update = 0; update < 2'000; ++update) {
        const double fraction = static_cast<double>(update % 199) / 198.0;
        config.left.filters[0] = {
            1,
            static_cast<std::uint32_t>(update % 4),
            20.0 * std::pow(1'000.0, fraction),
            -24.0 + 42.0 * static_cast<double>(update % 37) / 36.0,
            0.1 + 23.9 * static_cast<double>(update % 53) / 52.0,
        };
        config.right.filters[0] = config.left.filters[0];
        pf_result result = pf_engine_apply_config(engine.value, &config);
        int retries = 0;
        while (result == PF_BUSY && retries < 2) {
            ++busyRetries;
            ++retries;
            processBlock();
            result = pf_engine_apply_config(engine.value, &config);
        }
        require(result == PF_SUCCESS, "rapid automation snapshot accepted after bounded block-boundary retry");
        processBlock();
    }
    require(busyRetries < 2'000, "rapid automation retries remain bounded");
    require(largestStep < 0.35, "rapid automation remains bounded without transient instability");
}

void testDynamicDampingLinkedAttenuation()
{
    Engine dry;
    Engine damped;
    pf_config_v1 dryConfig{};
    pf_config_init(&dryConfig);
    dryConfig.limiter.enabled = 0;
    dryConfig.left.filter_count = 0;
    dryConfig.right.filter_count = 0;
    pf_config_v1 dampingConfig = dryConfig;
    dampingConfig.damping.enabled = 1;
    dampingConfig.damping.stereo_linked = 1;
    dampingConfig.damping.sensitivity_db = 0.0;
    dampingConfig.damping.minimum_frequency_hz = 500.0;
    dampingConfig.damping.maximum_frequency_hz = 10'000.0;
    dampingConfig.damping.attack_ms = 1.0;
    dampingConfig.damping.release_ms = 40.0;
    dampingConfig.damping.maximum_reduction_db = 8.0;
    require(pf_engine_apply_config(dry.value, &dryConfig) == PF_SUCCESS, "apply dry damping comparison");
    require(pf_engine_apply_config(damped.value, &dampingConfig) == PF_SUCCESS, "apply linked damping");
    settle(dry);
    settle(damped);
    auto input = sine(3'200.0, 0.12, 48'000);
    std::vector<float> dryOutput(input.size());
    std::vector<float> dampedOutput(input.size());
    processInBlocks(dry, input, dryOutput, "process dry resonance");
    processInBlocks(damped, input, dampedOutput, "process damped resonance");
    const double dryLevel = rmsRange(dryOutput, 0, 36'000, 48'000);
    const double dampedLevel = rmsRange(dampedOutput, 0, 36'000, 48'000);
    require(dampedLevel < dryLevel * 0.9, "dynamic damping attenuates a persistent narrow resonance");
    require(std::abs(rmsRange(dampedOutput, 0, 36'000, 48'000) - rmsRange(dampedOutput, 1, 36'000, 48'000)) < 1.0e-5, "linked damping preserves centered stereo position");

    std::vector<float> silence(48'000 * 2);
    std::vector<float> release(silence.size());
    processInBlocks(damped, silence, release, "release damping into silence");
    require(std::all_of(release.begin(), release.end(), [](float value) { return std::isfinite(value); }), "damping release remains finite");
    require(std::all_of(release.begin() + 72'000, release.end(), [](float value) { return std::abs(value) < 1.0e-6F; }), "damping releases cleanly without persistent residual output");
}

void testUnityPassThroughAndSilence()
{
    Engine engine;
    pf_config_v1 config{};
    pf_config_init(&config);
    config.limiter.enabled = 0;
    config.left.filter_count = 0;
    config.right.filter_count = 0;
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "apply unity path");
    settle(engine);
    auto input = sine(1'337.0, 0.2, 8'192);
    std::vector<float> output(input.size());
    require(pf_engine_process(engine.value, input.data(), output.data(), 8'192) == PF_SUCCESS, "process unity path");
    double largestError = 0.0;
    for (std::size_t index = 1'024; index < output.size(); ++index) largestError = std::max(largestError, std::abs(static_cast<double>(output[index]) - input[index]));
    require(largestError < 1.0e-6, "flat DSP path adds no measurable distortion or crosstalk");
    std::fill(input.begin(), input.end(), 0.0F);
    require(pf_engine_process(engine.value, input.data(), output.data(), 8'192) == PF_SUCCESS, "process digital silence");
    require(std::all_of(output.begin(), output.end(), [](float value) { return value == 0.0F; }), "digital silence remains exact silence");
}

void testPolarityDelayAndSanitization()
{
    Engine engine;
    pf_config_v1 config{};
    pf_config_init(&config);
    config.limiter.enabled = 0;
    config.left.filter_count = 0;
    config.right.filter_count = 0;
    config.left.polarity_inverted = 1;
    config.right.delay_ms = 1.0;
    require(pf_engine_apply_config(engine.value, &config) == PF_SUCCESS, "apply channel utilities");
    settle(engine);
    std::vector<float> input(256 * 2);
    input[0] = 0.5F;
    input[1] = 0.5F;
    input[20] = std::numeric_limits<float>::infinity();
    std::vector<float> output(input.size());
    require(pf_engine_process(engine.value, input.data(), output.data(), 256) == PF_SUCCESS, "process impulse utilities");
    require(output[0] < -0.49F, "left polarity inversion");
    require(std::abs(output[1]) < 1.0e-7F && output[48 * 2 + 1] > 0.49F, "right one-millisecond delay");
    require(std::all_of(output.begin(), output.end(), [](float value) { return std::isfinite(value); }), "non-finite input sanitized");
}

void testTelemetryAndDeterminism()
{
    Engine first;
    Engine second;
    pf_config_v1 config{};
    pf_config_init(&config);
    config.damping.enabled = 1;
    require(pf_engine_apply_config(first.value, &config) == PF_SUCCESS, "apply first deterministic config");
    require(pf_engine_apply_config(second.value, &config) == PF_SUCCESS, "apply second deterministic config");
    settle(first);
    settle(second);
    auto input = sine(2'750.0, 0.2, 8'192);
    std::vector<float> firstOutput(input.size());
    std::vector<float> secondOutput(input.size());
    require(pf_engine_process(first.value, input.data(), firstOutput.data(), 8'192) == PF_SUCCESS, "first deterministic process");
    require(pf_engine_process(second.value, input.data(), secondOutput.data(), 8'192) == PF_SUCCESS, "second deterministic process");
    require(firstOutput == secondOutput, "DSP output is deterministic");
    pf_telemetry_v1 telemetry{};
    telemetry.size = sizeof(telemetry);
    telemetry.version = PF_ABI_VERSION;
    require(pf_engine_read_telemetry(first.value, &telemetry) == PF_SUCCESS, "read telemetry");
    require(telemetry.sequence > 0 && telemetry.frame_count == PF_WAVEFORM_CAPACITY, "telemetry frame published");
    require(telemetry.peak_left_dbfs > -30.0F && telemetry.correlation > 0.99F, "telemetry values are meaningful");
}

void testAdaptivePolyphaseResampler()
{
    pf_resampler_diagnostics_v1 diagnostics{};
    diagnostics.size = sizeof(diagnostics);
    diagnostics.version = PF_ABI_VERSION;
    require(pf_audio_run_resampler_diagnostics(&diagnostics) == PF_SUCCESS, "run exact transport resampler diagnostics");
    std::cout << "RESAMPLER_DIAGNOSTICS passband_db=" << diagnostics.passband_gain_db
              << " stopband_db=" << diagnostics.stopband_rejection_db
              << " drift_underruns=" << diagnostics.drift_underruns
              << " drift_overruns=" << diagnostics.drift_overruns << '\n';
    require(std::abs(diagnostics.passband_gain_db) < 0.15, "polyphase resampler passband gain");
    require(diagnostics.stopband_rejection_db < -55.0, "polyphase resampler stopband rejection");
    require(diagnostics.drift_underruns == 0 && diagnostics.drift_overruns == 0, "adaptive ring tracks bounded positive and negative clock drift");
}

} // namespace

int main()
{
    testConfigValidation();
    testChannelIsolationAndParametricGain();
    testHighPassSlope();
    testLowPassSlopeAndUnityPassband();
    testStereoWidthMonoAndBalance();
    testGuardedBoostLimiter();
    testClickFreeAutomation();
    testRapidParameterStress();
    testDynamicDampingLinkedAttenuation();
    testUnityPassThroughAndSilence();
    testPolarityDelayAndSanitization();
    testTelemetryAndDeterminism();
    testAdaptivePolyphaseResampler();
    std::cout << "PULSEFORGE_NATIVE_TESTS_OK filters=passed stereo=passed limiter=passed telemetry=passed\n";
    return EXIT_SUCCESS;
}
