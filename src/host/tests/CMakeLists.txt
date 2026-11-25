set(_hosttests_dir "${CMAKE_CURRENT_BINARY_DIR}/hosttests")
set(_hosttests_project "${NEOETER_DOTNET_MANAGED_DIR}/tests/Neoeter.Scripting.HostTests/Neoeter.Scripting.HostTests.csproj")
file(GLOB _hosttests_sources CONFIGURE_DEPENDS
    "${NEOETER_DOTNET_MANAGED_DIR}/tests/Neoeter.Scripting.HostTests/*.cs"
    "${NEOETER_DOTNET_MANAGED_DIR}/tests/Neoeter.Scripting.HostTests/*.csproj")

add_custom_command(
    OUTPUT "${_hosttests_dir}/Neoeter.Scripting.HostTests.dll"
    COMMAND "${DOTNET_EXECUTABLE}" build "${_hosttests_project}"
            -c ${NEOETER_DOTNET_CONFIGURATION} -o "${_hosttests_dir}" --nologo -v quiet
    DEPENDS ${_hosttests_sources} neoeter_dotnet_managed
    COMMENT "Building Neoeter.Scripting.HostTests (C#)"
    VERBATIM)
add_custom_target(neoeter_dotnet_hosttests ALL DEPENDS "${_hosttests_dir}/Neoeter.Scripting.HostTests.dll")

add_executable(test_dotnet_host test_dotnet_host.cpp)
target_link_libraries(test_dotnet_host PRIVATE neoeter_dotnet_host neoeter_io)
add_dependencies(test_dotnet_host neoeter_dotnet_hosttests)

add_test(NAME test_dotnet_host
    COMMAND test_dotnet_host "${NEOETER_DOTNET_OUTPUT_DIR}" "${_hosttests_dir}/Neoeter.Scripting.HostTests.dll")
set_tests_properties(test_dotnet_host PROPERTIES ENVIRONMENT "DOTNET_ROOT=${NEOETER_DOTNET_ROOT}")
