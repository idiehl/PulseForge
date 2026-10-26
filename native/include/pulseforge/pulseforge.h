#ifndef PULSEFORGE_H
#define PULSEFORGE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(PULSEFORGE_ENGINE_BUILD)
#    define PF_API __declspec(dllexport)
#  else
#    define PF_API __declspec(dllimport)
#  endif
#  define PF_CALL __cdecl
#else
#  define PF_API __attribute__((visibility("default")))
#  define PF_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define PF_ABI_VERSION 1u
#define PF_MAX_FILTERS 12u
#define PF_WAVEFORM_CAPACITY 512u
#define PF_DEVICE_ID_CAPACITY 384u
#define PF_DEVICE_NAME_CAPACITY 256u

typedef enum pf_result {
    PF_SUCCESS = 0,
    PF_INVALID_ARGUMENT = -1,
    PF_INVALID_CONFIG = -2,
    PF_OUT_OF_MEMORY = -3,
    PF_NOT_READY = -4,
    PF_BUSY = -5,
    PF_AUDIO_ERROR = -6,
    PF_NOT_FOUND = -7,
    PF_UNSUPPORTED = -8
} pf_result;

typedef enum pf_filter_shape {
    PF_FILTER_BELL = 0,
    PF_FILTER_NOTCH = 1,
    PF_FILTER_LOW_SHELF = 2,
    PF_FILTER_HIGH_SHELF = 3
} pf_filter_shape;

typedef enum pf_output_transport {
    PF_TRANSPORT_RAW_SHARED = 0,
    PF_TRANSPORT_EXCLUSIVE = 1,
    PF_TRANSPORT_COMPATIBLE_SHARED = 2
} pf_output_transport;

typedef enum pf_buffer_profile {
    PF_BUFFER_LOW_LATENCY = 0,
    PF_BUFFER_BALANCED = 1,
    PF_BUFFER_SAFE = 2
} pf_buffer_profile;

typedef struct pf_filter_v1 {
    uint32_t enabled;
    uint32_t shape;
    double frequency_hz;
    double gain_db;
    double q;
} pf_filter_v1;

typedef struct pf_pass_filter_v1 {
    uint32_t enabled;
    uint32_t slope_db_per_octave;
    double frequency_hz;
} pf_pass_filter_v1;

typedef struct pf_channel_config_v1 {
    double input_trim_db;
    uint32_t mute;
    uint32_t solo;
    uint32_t polarity_inverted;
    double delay_ms;
    pf_pass_filter_v1 high_pass;
    uint32_t filter_count;
    pf_filter_v1 filters[PF_MAX_FILTERS];
    pf_pass_filter_v1 low_pass;
    double output_trim_db;
} pf_channel_config_v1;

typedef struct pf_damping_config_v1 {
    uint32_t enabled;
    uint32_t stereo_linked;
    double sensitivity_db;
    double minimum_frequency_hz;
    double maximum_frequency_hz;
    double attack_ms;
    double release_ms;
    double maximum_reduction_db;
} pf_damping_config_v1;

typedef struct pf_stereo_config_v1 {
    uint32_t channels_linked;
    double balance;
    double width_percent;
    uint32_t mono_audition;
} pf_stereo_config_v1;

typedef struct pf_limiter_config_v1 {
    uint32_t enabled;
    uint32_t expert_bypass_confirmed;
    double ceiling_db_tp;
    double release_ms;
    double lookahead_ms;
} pf_limiter_config_v1;

typedef struct pf_config_v1 {
    uint32_t size;
    uint32_t version;
    pf_channel_config_v1 left;
    pf_channel_config_v1 right;
    pf_damping_config_v1 damping;
    pf_stereo_config_v1 stereo;
    pf_limiter_config_v1 limiter;
    double master_gain_db;
    uint32_t bypass;
} pf_config_v1;

typedef struct pf_telemetry_v1 {
    uint32_t size;
    uint32_t version;
    uint64_t sequence;
    uint64_t monotonic_nanoseconds;
    uint32_t sample_rate;
    uint32_t frame_count;
    float pre_left[PF_WAVEFORM_CAPACITY];
    float pre_right[PF_WAVEFORM_CAPACITY];
    float post_left[PF_WAVEFORM_CAPACITY];
    float post_right[PF_WAVEFORM_CAPACITY];
    float peak_left_dbfs;
    float peak_right_dbfs;
    float rms_left_dbfs;
    float rms_right_dbfs;
    float true_peak_dbtp;
    float momentary_lufs;
    float limiter_reduction_db;
    float correlation;
} pf_telemetry_v1;

typedef struct pf_audio_device_v1 {
    uint32_t size;
    uint32_t version;
    char id[PF_DEVICE_ID_CAPACITY];
    char name[PF_DEVICE_NAME_CAPACITY];
    uint32_t is_default;
    uint32_t is_pulseforge_virtual;
    char stable_id[PF_DEVICE_ID_CAPACITY];
    char hardware_id[PF_DEVICE_ID_CAPACITY];
} pf_audio_device_v1;

typedef struct pf_bridge_config_v1 {
    uint32_t size;
    uint32_t version;
    uint32_t transport;
    uint32_t buffer_profile;
    uint32_t preferred_sample_rate;
} pf_bridge_config_v1;

typedef struct pf_bridge_status_v1 {
    uint32_t size;
    uint32_t version;
    uint32_t running;
    uint32_t sample_rate;
    uint32_t period_frames;
    double added_latency_ms;
    uint64_t underruns;
    uint64_t overruns;
    char failure[PF_DEVICE_NAME_CAPACITY];
    uint32_t requested_period_frames;
    uint32_t source_period_frames;
    uint32_t output_period_frames;
    uint32_t buffered_frames;
    uint32_t transport;
    uint32_t buffer_profile;
} pf_bridge_status_v1;

typedef struct pf_resampler_diagnostics_v1 {
    uint32_t size;
    uint32_t version;
    double passband_gain_db;
    double stopband_rejection_db;
    uint64_t drift_underruns;
    uint64_t drift_overruns;
} pf_resampler_diagnostics_v1;

typedef struct pf_virtual_loopback_diagnostics_v1 {
    uint32_t size;
    uint32_t version;
    uint32_t sample_rate;
    uint32_t frames_captured;
    double captured_rms_dbfs;
    double left_probe_dbfs;
    double right_probe_dbfs;
    double unexpected_3000hz_dbfs;
    uint32_t passed;
    char failure[PF_DEVICE_NAME_CAPACITY];
} pf_virtual_loopback_diagnostics_v1;

typedef struct pf_engine pf_engine;
typedef struct pf_audio_context pf_audio_context;

PF_API void PF_CALL pf_config_init(pf_config_v1* config);
PF_API pf_result PF_CALL pf_config_validate(const pf_config_v1* config, uint32_t sample_rate);
PF_API pf_result PF_CALL pf_engine_create(pf_engine** engine);
PF_API void PF_CALL pf_engine_destroy(pf_engine* engine);
PF_API pf_result PF_CALL pf_engine_prepare(pf_engine* engine, uint32_t sample_rate, uint32_t maximum_block_frames);
PF_API pf_result PF_CALL pf_engine_apply_config(pf_engine* engine, const pf_config_v1* config);
PF_API pf_result PF_CALL pf_engine_process(pf_engine* engine, const float* input_interleaved, float* output_interleaved, uint32_t frame_count);
PF_API pf_result PF_CALL pf_engine_read_telemetry(pf_engine* engine, pf_telemetry_v1* telemetry);
PF_API void PF_CALL pf_engine_reset(pf_engine* engine);

PF_API pf_result PF_CALL pf_audio_context_create(pf_audio_context** context, pf_engine* engine);
PF_API void PF_CALL pf_audio_context_destroy(pf_audio_context* context);
PF_API pf_result PF_CALL pf_audio_refresh_devices(pf_audio_context* context);
PF_API uint32_t PF_CALL pf_audio_get_device_count(const pf_audio_context* context);
PF_API pf_result PF_CALL pf_audio_get_device(const pf_audio_context* context, uint32_t index, pf_audio_device_v1* device);
PF_API pf_result PF_CALL pf_audio_start_bridge(
    pf_audio_context* context,
    const char* source_device_id,
    const char* target_device_id,
    const pf_bridge_config_v1* config);
PF_API void PF_CALL pf_audio_stop_bridge(pf_audio_context* context);
PF_API pf_result PF_CALL pf_audio_get_bridge_status(const pf_audio_context* context, pf_bridge_status_v1* status);
PF_API pf_result PF_CALL pf_audio_run_resampler_diagnostics(pf_resampler_diagnostics_v1* diagnostics);
PF_API pf_result PF_CALL pf_audio_run_virtual_loopback_diagnostics(
    const char* source_device_id,
    pf_virtual_loopback_diagnostics_v1* diagnostics);

PF_API const char* PF_CALL pf_result_description(pf_result result);

#ifdef __cplusplus
}
#endif

#endif
