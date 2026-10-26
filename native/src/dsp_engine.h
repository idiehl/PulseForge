#ifndef PULSEFORGE_DSP_ENGINE_H
#define PULSEFORGE_DSP_ENGINE_H

#include "pulseforge/pulseforge.h"

#include <array>
#include <atomic>
#include <cstdint>

namespace pulseforge {

class DspEngine final {
public:
    DspEngine();
    ~DspEngine();
    DspEngine(const DspEngine&) = delete;
    DspEngine& operator=(const DspEngine&) = delete;

    pf_result prepare(std::uint32_t sampleRate, std::uint32_t maximumBlockFrames) noexcept;
    pf_result applyConfig(const pf_config_v1& config) noexcept;
    pf_result process(const float* input, float* output, std::uint32_t frameCount) noexcept;
    pf_result readTelemetry(pf_telemetry_v1& telemetry) noexcept;
    void reset() noexcept;
    [[nodiscard]] std::uint32_t sampleRate() const noexcept { return sampleRate_; }

private:
    struct Impl;
    Impl* impl_;
    std::uint32_t sampleRate_ = 0;
    std::uint32_t maximumBlockFrames_ = 0;
};

pf_result validateConfig(const pf_config_v1& config, std::uint32_t sampleRate) noexcept;
void initializeConfig(pf_config_v1& config) noexcept;

} // namespace pulseforge

#endif
