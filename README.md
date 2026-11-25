# neoeter-dotnet

.NET (CoreCLR) scripting runtime for Neoeter. It sits next to
[`neoeter-python`](../neoeter-python): both backends expose the **same**
native modules registered through `Neoeter/Script` (`app`, `chr`, `net`,
`player`, `wndMgr`, `pack`, …), so game scripts can be migrated from Python to
C# one piece at a time while the Python runtime keeps working.

## Layout

```
include/Neoeter/DotNet/NativeAbi.h      C ABI shared by C++ and C# (function tables)
include/Neoeter/DotNet/DotNetRuntime.h  Host API (Initialize, RunEntry, Pump, Shutdown)
src/host/                               CoreCLR host (hostfxr) + ScriptBackend impl
managed/Neoeter.Scripting/              C# library loaded by the host
managed/tests/                          Managed half of the integration test
cmake/FindNetHost.cmake                 Locates the dotnet SDK and nethost
```

### How it works

1. `DotNetRuntime::Initialize` loads `hostfxr` (from `DOTNET_ROOT`, a bundled
   `<client>/dotnet/runtime`, or the global install), starts CoreCLR with
   `Neoeter.Scripting.runtimeconfig.json` and calls the managed
   `Bootstrap.Initialize` with a `NeoeterNativeApi` table.
2. The managed side fills a `NeoeterManagedApi` table (dispatch, pack resolver,
   pump, entry-point runner). All calls are blittable `UnmanagedCallersOnly`
   function pointers; no COM or P/Invoke marshalling.
3. The runtime registers itself as the active `ScriptBackend`, so
   `Script_RunFile` (e.g. `app.RunPythonFile`) and the per-frame
   `Script_UpdateActiveBackend` reach C#.
4. `RunEntry("scripts/OpenMT2.Scripts.dll")` loads the assembly **from the
   packs** in a `PackAssemblyLoadContext` (dependencies resolve from the same
   pack directory) and calls the `[assembly: ScriptEntryPoint(typeof(T))]`
   type's `Main`, or the assembly's regular entry point.

CoreCLR cannot be unloaded: `Initialize` runs once per process and `Shutdown`
only detaches the backend.

## C# API (`Neoeter.Scripting`)

```csharp
using Neoeter.Scripting;
using Neoeter.Scripting.Packs;

NativeModule app = Native.Import("app");
app.Call("SetMouseHandler", handler);        // C# objects become handlers
string locale = app.Call<string>("GetLocalePath");
dynamic chr = Native.ImportDynamic("chr");   // chr.SelectInstance(vid)
string name = Native.Import("scriptHost").Constant<string>("NAME");

// Packs: read, redirect, override and add resolvers (thread-safe)
byte[] data = Pack.ReadAllBytes("locale/en/locale_game.txt");
Pack.Redirect("d:/ymir work/ui/login.tga", "mods/ui/login.tga");
Pack.Override("locale/en/item_list.txt", File.ReadAllBytes("item_list.txt"));
using var mods = Pack.AddResolver(new DirectoryPackResolver("mods"));
using var gen  = Pack.AddResolver(path => path.EndsWith(".gen") ? Build(path) : null);
```

* `ScriptException` carries the native error type (`RuntimeError`,
  `TypeError`, …) and message, mirroring the Python exceptions.
* Handler objects are **borrowed** by the engine (same as Python): keep a
  reference to them for as long as they are registered.
* Strings cross the boundary with `Native.Encoding` (Latin-1 by default, like
  the original client).
* Unhandled exceptions in callbacks are reported through `Native.Log.Exception`
  instead of crashing the process.

## Building

Requires CMake ≥ 3.20 and the .NET 10 SDK (`dotnet` in `PATH`). The project is
normally built as part of the open-mt2 workspace (see `devkit`). The managed
output is staged in `${CMAKE_BINARY_DIR}/dotnet` (`NEOETER_DOTNET_OUTPUT_DIR`)
and must be shipped next to the client as `<client>/dotnet`. Set
`OPENMT2_SCRIPT_DOTNET=OFF` in the client to build without it.

## Tests

`src/host/tests/test_dotnet_host` (CTest) starts CoreCLR, registers a native
test module, loads `Neoeter.Scripting.HostTests.dll` through a pack override and
checks module calls, constants, errors, callbacks (native → C#), pack
redirects/overrides/resolvers (including from another thread) and object
lifetime.

```bash
ctest --test-dir build/src/dotnet-host/tests --output-on-failure
```
