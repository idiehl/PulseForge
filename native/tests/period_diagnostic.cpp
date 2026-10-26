#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

#include <audioclient.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propvarutil.h>
#include <wrl/client.h>

#include <cstdint>
#include <iomanip>
#include <iostream>
#include <string>

namespace {

using Microsoft::WRL::ComPtr;

[[nodiscard]] std::wstring endpointName(IMMDevice* device)
{
    ComPtr<IPropertyStore> properties;
    if (FAILED(device->OpenPropertyStore(STGM_READ, &properties))) return L"(unknown endpoint)";

    PROPVARIANT value;
    PropVariantInit(&value);
    const HRESULT result = properties->GetValue(PKEY_Device_FriendlyName, &value);
    std::wstring name = SUCCEEDED(result) && value.vt == VT_LPWSTR && value.pwszVal != nullptr
        ? value.pwszVal
        : L"(unknown endpoint)";
    PropVariantClear(&value);
    return name;
}

void printFailure(const std::wstring& name, const wchar_t* mode, const wchar_t* operation, HRESULT result)
{
    std::wcout << L"PULSEFORGE_PERIOD_CAPS"
               << L" device=\"" << name << L"\""
               << L" mode=" << mode
               << L" result=error"
               << L" operation=" << operation
               << L" hresult=0x" << std::hex << std::setw(8) << std::setfill(L'0')
               << static_cast<std::uint32_t>(result) << std::dec << std::setfill(L' ') << L'\n';
}

void queryPeriods(IMMDevice* device, const std::wstring& name, bool raw)
{
    const wchar_t* mode = raw ? L"raw" : L"default";
    ComPtr<IAudioClient3> client;
    HRESULT result = device->Activate(__uuidof(IAudioClient3), CLSCTX_ALL, nullptr, &client);
    if (FAILED(result)) {
        printFailure(name, mode, L"Activate(IAudioClient3)", result);
        return;
    }

    if (raw) {
        AudioClientProperties properties{};
        properties.cbSize = sizeof(properties);
        properties.eCategory = AudioCategory_Other;
        properties.Options = AUDCLNT_STREAMOPTIONS_RAW;
        result = client->SetClientProperties(&properties);
        if (FAILED(result)) {
            printFailure(name, mode, L"SetClientProperties(RAW)", result);
            return;
        }
    }

    WAVEFORMATEX* mixFormat = nullptr;
    result = client->GetMixFormat(&mixFormat);
    if (FAILED(result) || mixFormat == nullptr) {
        printFailure(name, mode, L"GetMixFormat", result);
        return;
    }

    UINT32 defaultFrames = 0;
    UINT32 fundamentalFrames = 0;
    UINT32 minimumFrames = 0;
    UINT32 maximumFrames = 0;
    REFERENCE_TIME defaultDevicePeriod = 0;
    REFERENCE_TIME minimumDevicePeriod = 0;
    result = client->GetDevicePeriod(&defaultDevicePeriod, &minimumDevicePeriod);
    if (FAILED(result)) {
        CoTaskMemFree(mixFormat);
        printFailure(name, mode, L"GetDevicePeriod", result);
        return;
    }

    result = client->GetSharedModeEnginePeriod(
        mixFormat,
        &defaultFrames,
        &fundamentalFrames,
        &minimumFrames,
        &maximumFrames);

    if (FAILED(result)) {
        CoTaskMemFree(mixFormat);
        printFailure(name, mode, L"GetSharedModeEnginePeriod", result);
        return;
    }

    std::wcout << L"PULSEFORGE_PERIOD_CAPS"
               << L" device=\"" << name << L"\""
               << L" mode=" << mode
               << L" result=ok"
               << L" sample-rate=" << mixFormat->nSamplesPerSec
               << L" channels=" << mixFormat->nChannels
               << L" default-frames=" << defaultFrames
               << L" fundamental-frames=" << fundamentalFrames
               << L" minimum-frames=" << minimumFrames
               << L" maximum-frames=" << maximumFrames
               << L" default-device-us=" << defaultDevicePeriod / 10
               << L" minimum-device-us=" << minimumDevicePeriod / 10
               << L'\n';
    CoTaskMemFree(mixFormat);
}

} // namespace

int wmain()
{
    const HRESULT initializeResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initializeResult)) {
        printFailure(L"(none)", L"default", L"CoInitializeEx", initializeResult);
        return 1;
    }

    ComPtr<IMMDeviceEnumerator> enumerator;
    HRESULT result = CoCreateInstance(
        __uuidof(MMDeviceEnumerator),
        nullptr,
        CLSCTX_ALL,
        IID_PPV_ARGS(&enumerator));
    if (FAILED(result)) {
        printFailure(L"(none)", L"default", L"CoCreateInstance(MMDeviceEnumerator)", result);
        CoUninitialize();
        return 1;
    }

    ComPtr<IMMDeviceCollection> devices;
    result = enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &devices);
    if (FAILED(result)) {
        printFailure(L"(none)", L"default", L"EnumAudioEndpoints", result);
        CoUninitialize();
        return 1;
    }

    UINT count = 0;
    result = devices->GetCount(&count);
    if (FAILED(result)) {
        printFailure(L"(none)", L"default", L"GetCount", result);
        CoUninitialize();
        return 1;
    }

    for (UINT index = 0; index < count; ++index) {
        ComPtr<IMMDevice> device;
        if (FAILED(devices->Item(index, &device))) continue;
        const std::wstring name = endpointName(device.Get());
        queryPeriods(device.Get(), name, false);
        queryPeriods(device.Get(), name, true);
    }

    CoUninitialize();
    return 0;
}
