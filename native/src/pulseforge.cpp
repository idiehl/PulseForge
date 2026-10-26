#include "dsp_engine.h"
#include "pulseforge/pulseforge.h"

#include <new>

struct pf_engine final {
    pulseforge::DspEngine implementation;
};

extern "C" {

void PF_CALL pf_config_init(pf_config_v1* config)
{
    if (config != nullptr) pulseforge::initializeConfig(*config);
}

pf_result PF_CALL pf_config_validate(const pf_config_v1* config, uint32_t sample_rate)
{
    return config == nullptr ? PF_INVALID_ARGUMENT : pulseforge::validateConfig(*config, sample_rate);
}

pf_result PF_CALL pf_engine_create(pf_engine** engine)
{
    if (engine == nullptr) return PF_INVALID_ARGUMENT;
    *engine = new (std::nothrow) pf_engine{};
    return *engine == nullptr ? PF_OUT_OF_MEMORY : PF_SUCCESS;
}

void PF_CALL pf_engine_destroy(pf_engine* engine)
{
    delete engine;
}

pf_result PF_CALL pf_engine_prepare(pf_engine* engine, uint32_t sample_rate, uint32_t maximum_block_frames)
{
    return engine == nullptr ? PF_INVALID_ARGUMENT : engine->implementation.prepare(sample_rate, maximum_block_frames);
}

pf_result PF_CALL pf_engine_apply_config(pf_engine* engine, const pf_config_v1* config)
{
    return engine == nullptr || config == nullptr ? PF_INVALID_ARGUMENT : engine->implementation.applyConfig(*config);
}

pf_result PF_CALL pf_engine_process(pf_engine* engine, const float* input_interleaved, float* output_interleaved, uint32_t frame_count)
{
    return engine == nullptr ? PF_INVALID_ARGUMENT : engine->implementation.process(input_interleaved, output_interleaved, frame_count);
}

pf_result PF_CALL pf_engine_read_telemetry(pf_engine* engine, pf_telemetry_v1* telemetry)
{
    return engine == nullptr || telemetry == nullptr ? PF_INVALID_ARGUMENT : engine->implementation.readTelemetry(*telemetry);
}

void PF_CALL pf_engine_reset(pf_engine* engine)
{
    if (engine != nullptr) engine->implementation.reset();
}

const char* PF_CALL pf_result_description(pf_result result)
{
    switch (result) {
    case PF_SUCCESS: return "success";
    case PF_INVALID_ARGUMENT: return "invalid argument";
    case PF_INVALID_CONFIG: return "invalid DSP configuration";
    case PF_OUT_OF_MEMORY: return "out of memory";
    case PF_NOT_READY: return "engine not prepared";
    case PF_BUSY: return "engine busy applying another snapshot";
    case PF_AUDIO_ERROR: return "audio transport error";
    case PF_NOT_FOUND: return "audio endpoint not found";
    case PF_UNSUPPORTED: return "operation unsupported by this endpoint";
    default: return "unknown PulseForge error";
    }
}

} // extern "C"
