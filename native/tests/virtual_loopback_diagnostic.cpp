#include "pulseforge/pulseforge.h"

#include <algorithm>
#include <cstring>
#include <iomanip>
#include <iostream>

int main()
{
    pf_engine* engine = nullptr;
    pf_audio_context* audio = nullptr;
    if (pf_engine_create(&engine) != PF_SUCCESS || engine == nullptr) {
        std::cerr << "Unable to create the PulseForge engine.\n";
        return 2;
    }
    if (pf_audio_context_create(&audio, engine) != PF_SUCCESS || audio == nullptr) {
        std::cerr << "Unable to create the PulseForge WASAPI context.\n";
        pf_engine_destroy(engine);
        return 2;
    }

    char virtualDeviceId[PF_DEVICE_ID_CAPACITY]{};
    const std::uint32_t count = pf_audio_get_device_count(audio);
    for (std::uint32_t index = 0; index < count; ++index) {
        pf_audio_device_v1 device{};
        device.size = sizeof(device);
        device.version = PF_ABI_VERSION;
        if (pf_audio_get_device(audio, index, &device) == PF_SUCCESS && device.is_pulseforge_virtual != 0) {
            const std::size_t length = std::min(std::strlen(device.id), sizeof(virtualDeviceId) - 1);
            std::memcpy(virtualDeviceId, device.id, length);
            virtualDeviceId[length] = '\0';
            break;
        }
    }

    if (virtualDeviceId[0] == '\0') {
        std::cerr << "The PulseForge virtual render endpoint is not installed.\n";
        pf_audio_context_destroy(audio);
        pf_engine_destroy(engine);
        return 2;
    }

    pf_virtual_loopback_diagnostics_v1 diagnostics{};
    diagnostics.size = sizeof(diagnostics);
    diagnostics.version = PF_ABI_VERSION;
    const pf_result result = pf_audio_run_virtual_loopback_diagnostics(virtualDeviceId, &diagnostics);

    std::cout << std::fixed << std::setprecision(2)
              << "PULSEFORGE_VIRTUAL_LOOPBACK_DIAGNOSTIC"
              << " result=" << static_cast<int>(result)
              << " passed=" << diagnostics.passed
              << " sequence=left-997hz_then_right-1603hz"
              << " duration-seconds=6"
              << " sample-rate=" << diagnostics.sample_rate
              << " captured-frames=" << diagnostics.frames_captured
              << " rms-dbfs=" << diagnostics.captured_rms_dbfs
              << " left-997hz-dbfs=" << diagnostics.left_probe_dbfs
              << " right-1603hz-dbfs=" << diagnostics.right_probe_dbfs
              << " unexpected-3000hz-dbfs=" << diagnostics.unexpected_3000hz_dbfs;
    if (diagnostics.failure[0] != '\0') std::cout << " failure=\"" << diagnostics.failure << '"';
    std::cout << '\n';

    pf_audio_context_destroy(audio);
    pf_engine_destroy(engine);
    return result == PF_SUCCESS && diagnostics.passed != 0 ? 0 : 1;
}
