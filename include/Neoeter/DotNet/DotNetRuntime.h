#pragma once

#include <Neoeter/Script/ScriptBackend.h>

#include <cstddef>
#include <memory>
#include <string>
#include <vector>

struct DotNetRuntimeOptions
{
		// Directory containing Neoeter.Scripting.dll and its runtimeconfig.json.
		std::string hostAssemblyDirectory;
		// Root of a bundled .NET runtime (host/fxr + shared/). Empty = use the
		// installed runtime (DOTNET_ROOT, then the system locations).
		std::string dotnetRoot;
};

// Hosts CoreCLR (via nethost/hostfxr) and exposes the runtime-neutral native
// modules (Neoeter/Script) to C#. Also the ScriptBackend that owns managed
// objects handed to the engine (UI windows, network handlers, ...).
//
// CoreCLR cannot be unloaded: Shutdown() releases the engine hooks but the
// runtime stays loaded until the process exits, so Initialize succeeds once.
class CDotNetRuntime : public ScriptBackend
{
	public:
		static CDotNetRuntime& Instance();

		// <executable dir>/dotnet: where packaged builds ship Neoeter.Scripting
		// and, optionally, the runtime itself.
		static std::string DefaultHostDirectory();

		bool Initialize(const DotNetRuntimeOptions& options, std::string* error = nullptr);
		bool IsInitialized() const;

		// Loads an assembly from the packs (or the file system) into a
		// PackAssemblyLoadContext and runs its entry point. Returns false on
		// load failure or an unhandled exception (already reported).
		bool RunEntry(const std::string& assemblyPath, const std::vector<std::string>& args = {});
		bool LoadAssembly(const std::string& assemblyPath);

		// Frees wrappers of collected managed objects. Call once per frame.
		void Pump();
		void Shutdown();

		// Native wrappers of managed objects currently alive (diagnostics).
		std::size_t LiveObjectCount() const;

		// ScriptBackend
		const char* Name() const override;
		void IncRef(ScriptObject* object) override;
		void DecRef(ScriptObject* object) override;
		bool HasAttr(ScriptObject* object, const char* name) override;
		ScriptCallStatus CallMethod(ScriptObject* object, const char* name, ScriptObject* args, ScriptObject** result) override;
		std::string Repr(ScriptObject* object) override;
		// Runs an entry assembly (*.dll) from the packs.
		bool RunFile(const char* path) override;
		// Drains finalized wrappers and deferred work; call once per frame.
		void Update() override;

		struct Impl;

	private:
		CDotNetRuntime();
		~CDotNetRuntime() override;

		std::unique_ptr<Impl> m_impl;
};
