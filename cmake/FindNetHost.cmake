# Locates the .NET SDK (`dotnet`) and the nethost headers/library of the
# targeted runtime pack (Microsoft.NETCore.App.Host.<rid>).
#
# Inputs (optional): DOTNET_ROOT (cache or env), NETHOST_DIR (directory with
#   nethost.h/hostfxr.h/libnethost), NEOETER_DOTNET_RUNTIME_MAJOR (default 10).
# Outputs: DOTNET_EXECUTABLE, NETHOST_INCLUDE_DIR, NETHOST_LIBRARY and the
#   imported target NetHost::NetHost.

set(NEOETER_DOTNET_RUNTIME_MAJOR 10 CACHE STRING ".NET major version to host")

set(_dotnet_hints)
if(DOTNET_ROOT)
    list(APPEND _dotnet_hints "${DOTNET_ROOT}")
endif()
if(DEFINED ENV{DOTNET_ROOT})
    list(APPEND _dotnet_hints "$ENV{DOTNET_ROOT}")
endif()
if(WIN32)
    list(APPEND _dotnet_hints "$ENV{ProgramFiles}/dotnet")
else()
    list(APPEND _dotnet_hints "$ENV{HOME}/.dotnet" /usr/share/dotnet /usr/lib/dotnet /usr/local/share/dotnet /opt/dotnet)
endif()

find_program(DOTNET_EXECUTABLE dotnet HINTS ${_dotnet_hints})

if(NOT NETHOST_DIR)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64|ARM64)$")
        set(_arch arm64)
    else()
        set(_arch x64)
    endif()
    if(WIN32)
        set(_rid "win-${_arch}")
    elseif(APPLE)
        set(_rid "osx-${_arch}")
    else()
        set(_rid "linux-${_arch}")
    endif()

    set(_candidates)
    foreach(_root IN LISTS _dotnet_hints)
        file(GLOB _found LIST_DIRECTORIES true
            "${_root}/packs/Microsoft.NETCore.App.Host.${_rid}/${NEOETER_DOTNET_RUNTIME_MAJOR}.*/runtimes/${_rid}/native")
        list(APPEND _candidates ${_found})
    endforeach()
    if(_candidates)
        list(SORT _candidates COMPARE NATURAL ORDER DESCENDING)
        list(GET _candidates 0 _nethost_dir)
        set(NETHOST_DIR "${_nethost_dir}" CACHE PATH "Directory containing nethost.h and libnethost")
    endif()
endif()

find_path(NETHOST_INCLUDE_DIR nethost.h HINTS "${NETHOST_DIR}" NO_DEFAULT_PATH)
# Prefer the static library so the client does not ship libnethost.
find_library(NETHOST_LIBRARY
    NAMES ${CMAKE_STATIC_LIBRARY_PREFIX}nethost${CMAKE_STATIC_LIBRARY_SUFFIX} libnethost nethost
    HINTS "${NETHOST_DIR}" NO_DEFAULT_PATH)

include(FindPackageHandleStandardArgs)
find_package_handle_standard_args(NetHost
    REQUIRED_VARS DOTNET_EXECUTABLE NETHOST_INCLUDE_DIR NETHOST_LIBRARY)

if(NetHost_FOUND AND NOT TARGET NetHost::NetHost)
    add_library(NetHost::NetHost UNKNOWN IMPORTED)
    set_target_properties(NetHost::NetHost PROPERTIES
        IMPORTED_LOCATION "${NETHOST_LIBRARY}"
        INTERFACE_INCLUDE_DIRECTORIES "${NETHOST_INCLUDE_DIR}")
    if(NETHOST_LIBRARY MATCHES "\\${CMAKE_STATIC_LIBRARY_SUFFIX}$")
        # Static nethost: the headers must not declare dllimport.
        set_property(TARGET NetHost::NetHost APPEND PROPERTY INTERFACE_COMPILE_DEFINITIONS NETHOST_USE_AS_STATIC)
        if(UNIX)
            set_property(TARGET NetHost::NetHost APPEND PROPERTY INTERFACE_LINK_LIBRARIES ${CMAKE_DL_LIBS})
        endif()
    endif()
endif()
