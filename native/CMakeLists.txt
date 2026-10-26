option(PULSEFORGE_BUILD_TESTS "Build native engine tests" ON)

include(FetchContent)
FetchContent_Declare(
    miniaudio
    URL https://github.com/mackron/miniaudio/archive/9634bedb5b5a2ca38c1ee7108a9358a4e233f14d.zip
    URL_HASH SHA256=d9fc392f394fba13c4d71436fea38a4af384292871d59aa7d728c11fbc4ae978
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    SOURCE_SUBDIR pulseforge_header_only
)
FetchContent_MakeAvailable(miniaudio)
execute_process(
    COMMAND ${CMAKE_COMMAND}
        -DMINIAUDIO_HEADER=${miniaudio_SOURCE_DIR}/miniaudio.h
        -P ${CMAKE_CURRENT_SOURCE_DIR}/cmake/PatchMiniaudio.cmake
    RESULT_VARIABLE pulseforge_miniaudio_patch_result
)
if(NOT pulseforge_miniaudio_patch_result EQUAL 0)
    message(FATAL_ERROR "Unable to apply the reviewed PulseForge WASAPI raw-shared extension to pinned miniaudio")
endif()

add_library(PulseForge.Engine SHARED
    src/pulseforge.cpp
    src/dsp_engine.cpp
    src/audio_bridge.cpp
    src/miniaudio_impl.c
)

target_compile_features(PulseForge.Engine PUBLIC cxx_std_20)
target_include_directories(PulseForge.Engine
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
        ${miniaudio_SOURCE_DIR}
)
target_compile_definitions(PulseForge.Engine PRIVATE
    PULSEFORGE_ENGINE_BUILD=1
    MA_NO_DECODING
    MA_NO_ENCODING
    MA_NO_RESOURCE_MANAGER
    MA_NO_GENERATION
)
set_target_properties(PulseForge.Engine PROPERTIES
    OUTPUT_NAME "PulseForge.Engine"
    VERSION ${PROJECT_VERSION}
    SOVERSION 1
)

if(WIN32)
    target_link_libraries(PulseForge.Engine PRIVATE ole32 uuid)
endif()

if(MSVC)
    target_compile_options(PulseForge.Engine PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:/W4>
        $<$<COMPILE_LANGUAGE:CXX>:/WX>
        $<$<COMPILE_LANGUAGE:CXX>:/permissive->
        $<$<COMPILE_LANGUAGE:CXX>:/EHsc>
        $<$<COMPILE_LANGUAGE:CXX>:/utf-8>
    )
else()
    target_compile_options(PulseForge.Engine PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:-Wall>
        $<$<COMPILE_LANGUAGE:CXX>:-Wextra>
        $<$<COMPILE_LANGUAGE:CXX>:-Wpedantic>
        $<$<COMPILE_LANGUAGE:CXX>:-Werror>
    )
endif()

if(PULSEFORGE_BUILD_TESTS)
    add_executable(PulseForge.Engine.Tests tests/engine_tests.cpp)
    target_compile_features(PulseForge.Engine.Tests PRIVATE cxx_std_20)
    target_link_libraries(PulseForge.Engine.Tests PRIVATE PulseForge.Engine)
    if(MSVC)
        target_compile_options(PulseForge.Engine.Tests PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
    else()
        target_compile_options(PulseForge.Engine.Tests PRIVATE -Wall -Wextra -Wpedantic -Werror)
    endif()
    add_test(NAME PulseForge.Engine.Tests COMMAND PulseForge.Engine.Tests)

    if(WIN32)
        add_executable(PulseForge.VirtualLoopbackDiagnostic tests/virtual_loopback_diagnostic.cpp)
        target_compile_features(PulseForge.VirtualLoopbackDiagnostic PRIVATE cxx_std_20)
        target_link_libraries(PulseForge.VirtualLoopbackDiagnostic PRIVATE PulseForge.Engine)
        if(MSVC)
            target_compile_options(PulseForge.VirtualLoopbackDiagnostic PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
        endif()

        add_executable(PulseForge.PeriodDiagnostic tests/period_diagnostic.cpp)
        target_compile_features(PulseForge.PeriodDiagnostic PRIVATE cxx_std_20)
        target_link_libraries(PulseForge.PeriodDiagnostic PRIVATE ole32 uuid)
        if(MSVC)
            target_compile_options(PulseForge.PeriodDiagnostic PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
        endif()

        add_executable(PulseForge.DeviceIdentityDiagnostic tests/device_identity_diagnostic.cpp)
        target_compile_features(PulseForge.DeviceIdentityDiagnostic PRIVATE cxx_std_20)
        target_link_libraries(PulseForge.DeviceIdentityDiagnostic PRIVATE PulseForge.Engine)
        if(MSVC)
            target_compile_options(PulseForge.DeviceIdentityDiagnostic PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
        endif()
    endif()
endif()

install(TARGETS PulseForge.Engine
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)
install(DIRECTORY include/ DESTINATION include)
