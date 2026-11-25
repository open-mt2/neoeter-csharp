cmake_minimum_required(VERSION 3.20)
project(NeoeterDotNet VERSION 0.1.0 LANGUAGES C CXX)

# .NET (CoreCLR) scripting runtime: native host + Neoeter.Scripting (C#).
# Sits next to neoeter-python; both expose the same Neoeter::Script modules.

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

list(APPEND CMAKE_MODULE_PATH "${CMAKE_CURRENT_SOURCE_DIR}/cmake")

set(NEOETER_DOTNET_INCLUDE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/include")
set(NEOETER_DOTNET_MANAGED_DIR "${CMAKE_CURRENT_SOURCE_DIR}/managed")
set(NEOETER_DOTNET_CONFIGURATION Release CACHE STRING "dotnet build configuration")
# Managed output is staged here; packaging copies it to <dist>/dotnet.
set(NEOETER_DOTNET_OUTPUT_DIR "${CMAKE_BINARY_DIR}/dotnet" CACHE PATH "Neoeter.Scripting output directory")

find_package(NetHost REQUIRED)

# Shared with consumers (the client builds its C# scripts against this tree).
set(NEOETER_DOTNET_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}" CACHE INTERNAL "neoeter-dotnet source directory")
# The SDK's own runtime, used by the tests (the client finds an installed or
# bundled runtime at run time).
get_filename_component(_dotnet_real "${DOTNET_EXECUTABLE}" REALPATH)
get_filename_component(_dotnet_root "${_dotnet_real}" DIRECTORY)
set(NEOETER_DOTNET_ROOT "${_dotnet_root}" CACHE INTERNAL ".NET root of the SDK used to build")

add_subdirectory(src/host "${CMAKE_BINARY_DIR}/src/dotnet-host")
