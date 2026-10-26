#include "pulseforge/pulseforge.h"

#include <cstdint>
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

    std::uint32_t missingStableIds = 0;
    std::uint32_t hardwareFallbacks = 0;
    const std::uint32_t count = pf_audio_get_device_count(audio);
    for (std::uint32_t index = 0; index < count; ++index) {
        pf_audio_device_v1 device{};
        device.size = sizeof(device);
        device.version = PF_ABI_VERSION;
        if (pf_audio_get_device(audio, index, &device) != PF_SUCCESS) {
            ++missingStableIds;
            continue;
        }
        if (device.stable_id[0] == '\0') ++missingStableIds;
        if (device.hardware_id[0] != '\0') ++hardwareFallbacks;
        std::cout << "PULSEFORGE_DEVICE_IDENTITY"
                  << " name=\"" << device.name << '"'
                  << " endpoint=\"" << device.id << '"'
                  << " stable=\"" << device.stable_id << '"'
                  << " hardware=\"" << device.hardware_id << '"'
                  << " default=" << device.is_default
                  << " virtual=" << device.is_pulseforge_virtual
                  << '\n';
    }

    std::cout << "PULSEFORGE_DEVICE_IDENTITY_SUMMARY"
              << " endpoints=" << count
              << " stable-ids=" << count - missingStableIds
              << " hardware-fallbacks=" << hardwareFallbacks
              << " route-started=false audio-rendered=false\n";

    pf_audio_context_destroy(audio);
    pf_engine_destroy(engine);
    return count > 0 && missingStableIds == 0 ? 0 : 1;
}
