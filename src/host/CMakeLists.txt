set(_managed_sources)
file(GLOB_RECURSE _managed_sources CONFIGURE_DEPENDS
    "${NEOETER_DOTNET_MANAGED_DIR}/Neoeter.Scripting/*.cs"
    "${NEOETER_DOTNET_MANAGED_DIR}/Neoeter.Scripting/*.csproj"
    "${NEOETER_DOTNET_MANAGED_DIR}/Directory.Build.props")

# Neoeter.Scripting.dll + runtimeconfig.json, loaded by CDotNetRuntime.
set(_managed_stamp "${NEOETER_DOTNET_OUTPUT_DIR}/Neoeter.Scripting.dll")
add_custom_command(
    OUTPUT "${_managed_stamp}"
    COMMAND "${DOTNET_EXECUTABLE}" build
            "${NEOETER_DOTNET_MANAGED_DIR}/Neoeter.Scripting/Neoeter.Scripting.csproj"
            -c ${NEOETER_DOTNET_CONFIGURATION} -o "${NEOETER_DOTNET_OUTPUT_DIR}"
            --nologo -v quiet
    DEPENDS ${_managed_sources}
    COMMENT "Building Neoeter.Scripting (C#)"
    VERBATIM)
add_custom_target(neoeter_dotnet_managed ALL DEPENDS "${_managed_stamp}")

add_library(neoeter_dotnet_host STATIC
    DotNetRuntime.cpp
    "${NEOETER_DOTNET_INCLUDE_DIR}/Neoeter/DotNet/DotNetRuntime.h"
    "${NEOETER_DOTNET_INCLUDE_DIR}/Neoeter/DotNet/NativeAbi.h")
add_library(Neoeter::DotNet ALIAS neoeter_dotnet_host)

target_include_directories(neoeter_dotnet_host PUBLIC "${NEOETER_DOTNET_INCLUDE_DIR}")
target_link_libraries(neoeter_dotnet_host
    PUBLIC
        neoeter_script
    PRIVATE
        neoeter_io
        NetHost::NetHost)
set_target_properties(neoeter_dotnet_host PROPERTIES POSITION_INDEPENDENT_CODE ON)
add_dependencies(neoeter_dotnet_host neoeter_dotnet_managed)

target_compile_definitions(neoeter_dotnet_host
    PUBLIC NEOETER_SCRIPT_DOTNET=1)

if(BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests)
endif()
