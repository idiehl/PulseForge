if(NOT DEFINED MINIAUDIO_HEADER OR NOT EXISTS "${MINIAUDIO_HEADER}")
    message(FATAL_ERROR "MINIAUDIO_HEADER must identify the pinned miniaudio.h")
endif()

file(READ "${MINIAUDIO_HEADER}" _text)
if(_text MATCHES "rawShared;[ ]*/\\* PulseForge")
    return()
endif()

function(replace_required search replacement label)
    string(FIND "${_text}" "${search}" index)
    if(index EQUAL -1)
        message(FATAL_ERROR "Pinned miniaudio no longer matches reviewed PulseForge patch: ${label}")
    endif()
    string(REPLACE "${search}" "${replacement}" _updated "${_text}")
    set(_text "${_updated}" PARENT_SCOPE)
endfunction()

replace_required(
[==[        ma_bool8 noDefaultQualitySRC;       /* When set to true, disables the use of AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY. */
        ma_bool8 noAutoStreamRouting;]==]
[==[        ma_bool8 noDefaultQualitySRC;       /* When set to true, disables the use of AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY. */
        ma_bool8 rawShared;                     /* PulseForge: request AUDCLNT_STREAMOPTIONS_RAW before shared playback initialization. */
        ma_bool8 noAutoStreamRouting;]==]
"public WASAPI configuration")

replace_required(
[==[            ma_bool8 noDefaultQualitySRC;                           /* When set to true, disables the use of AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY. */
            ma_bool8 noHardwareOffloading;]==]
[==[            ma_bool8 noDefaultQualitySRC;                           /* When set to true, disables the use of AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY. */
            ma_bool8 rawShared;                                         /* PulseForge raw shared playback request. */
            ma_bool8 noHardwareOffloading;]==]
"internal WASAPI device state")

replace_required(
[==[    ma_bool32 noDefaultQualitySRC;
    ma_bool32 noHardwareOffloading;]==]
[==[    ma_bool32 noDefaultQualitySRC;
    ma_bool32 rawShared;
    ma_bool32 noHardwareOffloading;]==]
"WASAPI initialization state")

replace_required(
[==[    result = ma_context_get_IAudioClient__wasapi(pContext, deviceType, pDeviceID, pData->loopbackProcessID, pData->loopbackProcessExclude, &pData->pAudioClient, &pDeviceInterface);
    if (result != MA_SUCCESS) {
        goto done;
    }

    MA_ZERO_OBJECT(&wf);]==]
[==[    result = ma_context_get_IAudioClient__wasapi(pContext, deviceType, pDeviceID, pData->loopbackProcessID, pData->loopbackProcessExclude, &pData->pAudioClient, &pDeviceInterface);
    if (result != MA_SUCCESS) {
        goto done;
    }

    if (pData->rawShared && pData->shareMode == ma_share_mode_shared && deviceType == ma_device_type_playback) {
        hr = ma_IAudioClient_QueryInterface(pData->pAudioClient, &MA_IID_IAudioClient2, (void**)&pAudioClient2);
        if (FAILED(hr)) {
            errorMsg = "[WASAPI] Raw shared mode requires IAudioClient2.";
            result = ma_result_from_HRESULT(hr);
            goto done;
        }
        {
            ma_AudioClientProperties clientProperties;
            MA_ZERO_OBJECT(&clientProperties);
            clientProperties.cbSize = sizeof(clientProperties);
            clientProperties.bIsOffload = 0;
            clientProperties.eCategory = MA_AudioCategory_Other;
            clientProperties.Options = MA_AUDCLNT_STREAMOPTIONS_RAW;
            hr = ma_IAudioClient2_SetClientProperties(pAudioClient2, &clientProperties);
        }
        pAudioClient2->lpVtbl->Release(pAudioClient2);
        if (FAILED(hr)) {
            errorMsg = "[WASAPI] The selected endpoint rejected raw shared mode.";
            result = ma_result_from_HRESULT(hr);
            goto done;
        }
    }

    MA_ZERO_OBJECT(&wf);]==]
"raw shared client property")

replace_required(
[==[    data.noDefaultQualitySRC        = pDevice->wasapi.noDefaultQualitySRC;
    data.noHardwareOffloading       = pDevice->wasapi.noHardwareOffloading;]==]
[==[    data.noDefaultQualitySRC        = pDevice->wasapi.noDefaultQualitySRC;
    data.rawShared                  = pDevice->wasapi.rawShared;
    data.noHardwareOffloading       = pDevice->wasapi.noHardwareOffloading;]==]
"raw shared reroute state")

replace_required(
[==[    pDevice->wasapi.noDefaultQualitySRC    = pConfig->wasapi.noDefaultQualitySRC;
    pDevice->wasapi.noHardwareOffloading   = pConfig->wasapi.noHardwareOffloading;]==]
[==[    pDevice->wasapi.noDefaultQualitySRC    = pConfig->wasapi.noDefaultQualitySRC;
    pDevice->wasapi.rawShared              = pConfig->wasapi.rawShared;
    pDevice->wasapi.noHardwareOffloading   = pConfig->wasapi.noHardwareOffloading;]==]
"raw shared device configuration")

replace_required(
[==[        data.noDefaultQualitySRC        = pConfig->wasapi.noDefaultQualitySRC;
        data.noHardwareOffloading       = pConfig->wasapi.noHardwareOffloading;]==]
[==[        data.noDefaultQualitySRC        = pConfig->wasapi.noDefaultQualitySRC;
        data.rawShared                  = pConfig->wasapi.rawShared;
        data.noHardwareOffloading       = pConfig->wasapi.noHardwareOffloading;]==]
"raw shared initial configuration")

file(WRITE "${MINIAUDIO_HEADER}" "${_text}")
