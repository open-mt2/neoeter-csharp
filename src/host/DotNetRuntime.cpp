#include <Neoeter/DotNet/DotNetRuntime.h>
#include <Neoeter/DotNet/NativeAbi.h>

#include <Neoeter/IO/EterPackManager.h>
#include <Neoeter/Script/Script.h>
#include <Neoeter/Script/ScriptExceptionSender.h>

#include <nethost.h>
#include <hostfxr.h>
#include <coreclr_delegates.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <set>
#include <string>
#include <vector>

#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#include <unistd.h>
#endif

namespace fs = std::filesystem;

namespace
{
	// -----------------------------------------------------------------------
	// Platform helpers
	// -----------------------------------------------------------------------
	using CharString = std::basic_string<char_t>;

	CharString ToCharT(const std::string& value)
	{
		return fs::path(value).native();
	}

	void* LoadLibraryPath(const char_t* path)
	{
#if defined(_WIN32)
		return reinterpret_cast<void*>(::LoadLibraryW(path));
#else
		return ::dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
	}

	void* GetExport(void* library, const char* name)
	{
#if defined(_WIN32)
		return reinterpret_cast<void*>(::GetProcAddress(static_cast<HMODULE>(library), name));
#else
		return ::dlsym(library, name);
#endif
	}

	void Log(int level, const std::string& message)
	{
		std::fprintf(level >= 1 ? stderr : stdout, "[dotnet] %s\n", message.c_str());
	}

	int32_t CopyOut(const std::string& text, uint8_t* buffer, int32_t capacity)
	{
		if (buffer && capacity > 0)
		{
			const std::size_t n = std::min<std::size_t>(text.size(), static_cast<std::size_t>(capacity - 1));
			std::memcpy(buffer, text.data(), n);
			buffer[n] = 0;
		}
		return static_cast<int32_t>(text.size());
	}

	ScriptObject* AsObject(NeoeterValue value)
	{
		return static_cast<ScriptObject*>(value);
	}

	// A managed object seen by the engine. `refs` counts native references;
	// the managed side keeps the object strongly reachable while refs > 0.
	struct ManagedRef
	{
			intptr_t handle = 0;
			int32_t refs = 0;
	};

	// -----------------------------------------------------------------------
	// Native API (called from C#)
	// -----------------------------------------------------------------------
	NeoeterManagedApi g_managed{};
	std::size_t g_liveManagedObjects = 0;

	int32_t Api_TypeOf(NeoeterValue value)
	{
		return value ? static_cast<int32_t>(AsObject(value)->Type()) : NEOETER_VALUE_NONE;
	}

	void Api_IncRef(NeoeterValue value)
	{
		if (value)
			Script_IncRef(AsObject(value));
	}

	void Api_DecRef(NeoeterValue value)
	{
		if (value)
			Script_DecRef(AsObject(value));
	}

	NeoeterValue Api_NewNone() { return Script_BuildNone(); }
	NeoeterValue Api_NewBool(int32_t value) { return ScriptBool_New(value != 0); }
	NeoeterValue Api_NewInt(int64_t value) { return ScriptInt_New(value); }
	NeoeterValue Api_NewFloat(double value) { return ScriptFloat_New(value); }

	NeoeterValue Api_NewString(const uint8_t* data, int32_t length)
	{
		return ScriptString_NewSize(reinterpret_cast<const char*>(data), length > 0 ? static_cast<std::size_t>(length) : 0);
	}

	NeoeterValue Api_NewTuple(int32_t size) { return ScriptTuple_New(size > 0 ? size : 0); }
	NeoeterValue Api_NewList(int32_t size) { return ScriptList_New(size > 0 ? size : 0); }
	NeoeterValue Api_NewDict() { return ScriptDict_New(); }

	int32_t Api_SetItem(NeoeterValue sequence, int32_t index, NeoeterValue value)
	{
		return ScriptTuple_SetItem(AsObject(sequence), index, AsObject(value)) ? 1 : 0;
	}

	// Steals key and value (unlike ScriptDict_SetItem) to simplify marshaling.
	int32_t Api_DictSet(NeoeterValue dict, NeoeterValue key, NeoeterValue value)
	{
		const bool ok = ScriptDict_SetItem(AsObject(dict), AsObject(key), AsObject(value));
		Api_DecRef(key);
		Api_DecRef(value);
		return ok ? 1 : 0;
	}

	int32_t Api_GetBool(NeoeterValue value) { return Script_IsTrue(AsObject(value)) ? 1 : 0; }
	int64_t Api_GetInt(NeoeterValue value) { return Script_AsLongLong(AsObject(value)); }
	double Api_GetFloat(NeoeterValue value) { return Script_AsDouble(AsObject(value)); }

	const uint8_t* Api_GetString(NeoeterValue value, int32_t* length)
	{
		ScriptObject* object = AsObject(value);
		if (!ScriptString_Check(object))
		{
			if (length)
				*length = 0;
			return nullptr;
		}
		if (length)
			*length = static_cast<int32_t>(object->Bytes().size());
		return reinterpret_cast<const uint8_t*>(object->Bytes().data());
	}

	int32_t Api_Size(NeoeterValue value)
	{
		ScriptObject* object = AsObject(value);
		switch (object->Type())
		{
			case ScriptType::String:
				return static_cast<int32_t>(object->Bytes().size());
			case ScriptType::Tuple:
			case ScriptType::List:
				return static_cast<int32_t>(object->Items().size());
			case ScriptType::Dict:
				return static_cast<int32_t>(object->Entries().size());
			default:
				return 0;
		}
	}

	NeoeterValue Api_GetItem(NeoeterValue sequence, int32_t index)
	{
		ScriptObject* object = AsObject(sequence);
		if ((!ScriptTuple_Check(object) && !ScriptList_Check(object)) || index < 0 ||
			static_cast<std::size_t>(index) >= object->Items().size())
			return nullptr;
		return object->Items()[static_cast<std::size_t>(index)];
	}

	int32_t Api_DictEntry(NeoeterValue dict, int32_t index, NeoeterValue* key, NeoeterValue* value)
	{
		ScriptObject* object = AsObject(dict);
		if (!ScriptDict_Check(object) || index < 0 || static_cast<std::size_t>(index) >= object->Entries().size())
			return 0;
		const auto& entry = object->Entries()[static_cast<std::size_t>(index)];
		*key = entry.first;
		*value = entry.second;
		return 1;
	}

	const char* Api_ModuleNameOf(NeoeterValue value)
	{
		ScriptObject* object = AsObject(value);
		return object->Type() == ScriptType::Module && object->Module() ? object->Module()->name.c_str() : nullptr;
	}

	NeoeterValue Api_NewManaged(intptr_t handle)
	{
		auto* ref = new ManagedRef{handle, 0};
		++g_liveManagedObjects;
		ScriptObject* object = ScriptObject_NewForeign(&CDotNetRuntime::Instance(), ref);
		Script_IncRef(object);
		return object;
	}

	ManagedRef* ManagedRefOf(ScriptObject* object)
	{
		if (!object || object->Type() != ScriptType::Object || object->Backend() != &CDotNetRuntime::Instance())
			return nullptr;
		return static_cast<ManagedRef*>(object->Handle());
	}

	intptr_t Api_GetManaged(NeoeterValue value)
	{
		ManagedRef* ref = ManagedRefOf(AsObject(value));
		return ref ? ref->handle : 0;
	}

	void Api_FreeManaged(NeoeterValue value)
	{
		ScriptObject* object = AsObject(value);
		ManagedRef* ref = ManagedRefOf(object);
		if (!ref)
			return;
		delete ref;
		--g_liveManagedObjects;
		object->SetHandle(nullptr);
		ScriptObject_FreeForeign(object);
	}

	int32_t Api_Repr(NeoeterValue value, uint8_t* buffer, int32_t capacity)
	{
		ScriptObject* object = AsObject(value);
		std::string text = object->Type() == ScriptType::Object && object->Backend() ? object->Backend()->Repr(object) : std::string("<value>");
		return CopyOut(text, buffer, capacity);
	}

	int32_t Api_CallMethod(NeoeterValue object, const char* name, NeoeterValue args, NeoeterValue* result)
	{
		ScriptObject* target = AsObject(object);
		*result = nullptr;
		if (!target || target->Type() != ScriptType::Object || !target->Backend())
			return NEOETER_CALL_NO_METHOD;

		ScriptObject* argTuple = args ? AsObject(args) : Script_BuildEmptyTuple();
		ScriptObject* out = nullptr;
		const ScriptCallStatus status = target->Backend()->CallMethod(target, name, argTuple, &out);
		if (!args)
			Script_DecRef(argTuple);
		*result = out;
		return static_cast<int32_t>(status);
	}

	int32_t Api_HasAttr(NeoeterValue object, const char* name)
	{
		return Script_HasAttr(AsObject(object), name) ? 1 : 0;
	}

	void Api_ErrSet(int32_t type, const uint8_t* message, int32_t length)
	{
		std::string text(reinterpret_cast<const char*>(message), length > 0 ? static_cast<std::size_t>(length) : 0);
		ScriptErr_SetString(static_cast<ScriptErrorType>(type), text.c_str());
	}

	int32_t Api_ErrFetch(int32_t* type, uint8_t* buffer, int32_t capacity)
	{
		ScriptErrorType errorType{};
		std::string message;
		if (!ScriptErr_Fetch(&errorType, &message))
			return -1;
		if (type)
			*type = static_cast<int32_t>(errorType);
		return CopyOut(message, buffer, capacity);
	}

	void Api_ErrClear() { ScriptErr_Clear(); }

	NeoeterModule Api_FindModule(const char* name) { return ScriptModule_Find(name); }

	ScriptModuleData* AsModule(NeoeterModule module) { return static_cast<ScriptModuleData*>(module); }

	NeoeterValue Api_ModuleObject(NeoeterModule module) { return AsModule(module)->object; }

	int32_t Api_ModuleCount() { return static_cast<int32_t>(ScriptModule_All().size()); }

	NeoeterModule Api_ModuleAt(int32_t index)
	{
		const auto& all = ScriptModule_All();
		return index >= 0 && static_cast<std::size_t>(index) < all.size() ? all[static_cast<std::size_t>(index)] : nullptr;
	}

	const char* Api_ModuleName(NeoeterModule module) { return AsModule(module)->name.c_str(); }

	NeoeterMethod Api_FindMethod(NeoeterModule module, const char* name)
	{
		return const_cast<ScriptMethodDef*>(AsModule(module)->FindMethod(name));
	}

	int32_t Api_MethodCount(NeoeterModule module) { return static_cast<int32_t>(AsModule(module)->methods.size()); }

	NeoeterMethod Api_MethodAt(NeoeterModule module, int32_t index)
	{
		auto& methods = AsModule(module)->methods;
		return index >= 0 && static_cast<std::size_t>(index) < methods.size() ? &methods[static_cast<std::size_t>(index)] : nullptr;
	}

	const char* Api_MethodName(NeoeterMethod method) { return static_cast<ScriptMethodDef*>(method)->name; }

	NeoeterValue Api_Invoke(NeoeterMethod method, NeoeterValue args)
	{
		ScriptErr_Clear();
		return ScriptModule_Invoke(static_cast<ScriptMethodDef*>(method), AsObject(args));
	}

	NeoeterValue Api_GetConstant(NeoeterModule module, const char* name) { return AsModule(module)->FindConstant(name); }

	int32_t Api_ConstantCount(NeoeterModule module) { return static_cast<int32_t>(AsModule(module)->constants.size()); }

	const char* Api_ConstantName(NeoeterModule module, int32_t index)
	{
		auto& constants = AsModule(module)->constants;
		return index >= 0 && static_cast<std::size_t>(index) < constants.size() ? constants[static_cast<std::size_t>(index)].first.c_str() : nullptr;
	}

	NeoeterValue Api_ConstantAt(NeoeterModule module, int32_t index)
	{
		auto& constants = AsModule(module)->constants;
		return index >= 0 && static_cast<std::size_t>(index) < constants.size() ? constants[static_cast<std::size_t>(index)].second : nullptr;
	}

	void Api_Log(int32_t level, const uint8_t* message, int32_t length)
	{
		Log(level, std::string(reinterpret_cast<const char*>(message), length > 0 ? static_cast<std::size_t>(length) : 0));
	}

	void Api_ReportException(const uint8_t* text, int32_t length)
	{
		std::string message(reinterpret_cast<const char*>(text), length > 0 ? static_cast<std::size_t>(length) : 0);
		Log(2, message);
		Script_ReportException(message.c_str());
	}

	// Packs -------------------------------------------------------------------
	using Blob = CEterPackManager::TPackDataPtr;

	CEterPackManager* Packs() { return CEterPackManager::InstancePtr(); }

	int32_t Api_PackExists(const char* name)
	{
		CEterPackManager* packs = Packs();
		return packs && packs->isExist(name) ? 1 : 0;
	}

	NeoeterBlob Api_PackRead(const char* name, const uint8_t** data, int64_t* length)
	{
		CEterPackManager* packs = Packs();
		Blob blob;
		if (!packs || !packs->Get(name, blob) || !blob)
			return nullptr;
		*data = reinterpret_cast<const uint8_t*>(blob->data());
		*length = static_cast<int64_t>(blob->size());
		return new Blob(std::move(blob));
	}

	NeoeterBlob Api_PackCreateBlob(const uint8_t* data, int64_t length)
	{
		auto bytes = std::make_shared<std::vector<char>>(reinterpret_cast<const char*>(data), reinterpret_cast<const char*>(data) + (length > 0 ? length : 0));
		return new Blob(std::move(bytes));
	}

	void Api_PackReleaseBlob(NeoeterBlob blob) { delete static_cast<Blob*>(blob); }

	int32_t Api_PackSetRedirect(const char* from, const char* to)
	{
		if (!Packs())
			return 0;
		Packs()->SetRedirect(from, to);
		return 1;
	}

	int32_t Api_PackRemoveRedirect(const char* from) { return Packs() && Packs()->RemoveRedirect(from) ? 1 : 0; }

	int32_t Api_PackSetOverride(const char* name, const uint8_t* data, int64_t length)
	{
		if (!Packs())
			return 0;
		Blob* blob = static_cast<Blob*>(Api_PackCreateBlob(data, length));
		Packs()->SetOverride(name, *blob);
		delete blob;
		return 1;
	}

	int32_t Api_PackRemoveOverride(const char* name) { return Packs() && Packs()->RemoveOverride(name) ? 1 : 0; }

	int32_t Api_PackResolveName(const char* name, uint8_t* buffer, int32_t capacity)
	{
		return CopyOut(Packs() ? Packs()->ResolveName(name) : std::string(name), buffer, capacity);
	}

	int32_t Api_PackEnableResolveHook(int32_t enable)
	{
		if (!Packs())
			return 0;
		if (!enable)
		{
			Packs()->SetResolveHook(nullptr);
			return 1;
		}
		Packs()->SetResolveHook([](const std::string& name, Blob* out) -> bool
								{
			if (!g_managed.ResolvePack)
				return false;
			NeoeterBlob blob = nullptr;
			if (!g_managed.ResolvePack(name.c_str(), out ? &blob : nullptr))
				return false;
			if (out)
			{
				if (!blob)
					return false;
				*out = *static_cast<Blob*>(blob);
				Api_PackReleaseBlob(blob);
			}
			return true; });
		return 1;
	}

	int32_t Api_PackEnumerate(const char* prefix, NeoeterPackEnumerateCallback callback, void* context)
	{
		CEterPackManager* packs = Packs();
		if (!packs || !callback)
			return 0;
		const std::string normalized = packs->ConvertFileName(prefix ? prefix : "");
		std::set<std::string> seen;
		int32_t count = 0;
		for (const auto& pack : packs->GetPackInfos())
		{
			for (const auto& file : packs->GetPackFiles(pack.name))
			{
				if (file.name.compare(0, normalized.size(), normalized) != 0 || !seen.insert(file.name).second)
					continue;
				callback(context, file.name.c_str(), static_cast<int64_t>(file.size));
				++count;
			}
		}
		return count;
	}

	NeoeterNativeApi MakeNativeApi()
	{
		NeoeterNativeApi api{};
		api.version = NEOETER_DOTNET_ABI_VERSION;
		api.size = sizeof(NeoeterNativeApi);
		api.TypeOf = Api_TypeOf;
		api.IncRef = Api_IncRef;
		api.DecRef = Api_DecRef;
		api.NewNone = Api_NewNone;
		api.NewBool = Api_NewBool;
		api.NewInt = Api_NewInt;
		api.NewFloat = Api_NewFloat;
		api.NewString = Api_NewString;
		api.NewTuple = Api_NewTuple;
		api.NewList = Api_NewList;
		api.NewDict = Api_NewDict;
		api.SetItem = Api_SetItem;
		api.DictSet = Api_DictSet;
		api.GetBool = Api_GetBool;
		api.GetInt = Api_GetInt;
		api.GetFloat = Api_GetFloat;
		api.GetString = Api_GetString;
		api.Size = Api_Size;
		api.GetItem = Api_GetItem;
		api.DictEntry = Api_DictEntry;
		api.ModuleNameOf = Api_ModuleNameOf;
		api.NewManaged = Api_NewManaged;
		api.GetManaged = Api_GetManaged;
		api.FreeManaged = Api_FreeManaged;
		api.Repr = Api_Repr;
		api.CallMethod = Api_CallMethod;
		api.HasAttr = Api_HasAttr;
		api.ErrSet = Api_ErrSet;
		api.ErrFetch = Api_ErrFetch;
		api.ErrClear = Api_ErrClear;
		api.FindModule = Api_FindModule;
		api.ModuleObject = Api_ModuleObject;
		api.ModuleCount = Api_ModuleCount;
		api.ModuleAt = Api_ModuleAt;
		api.ModuleName = Api_ModuleName;
		api.FindMethod = Api_FindMethod;
		api.MethodCount = Api_MethodCount;
		api.MethodAt = Api_MethodAt;
		api.MethodName = Api_MethodName;
		api.Invoke = Api_Invoke;
		api.GetConstant = Api_GetConstant;
		api.ConstantCount = Api_ConstantCount;
		api.ConstantName = Api_ConstantName;
		api.ConstantAt = Api_ConstantAt;
		api.Log = Api_Log;
		api.ReportException = Api_ReportException;
		api.PackExists = Api_PackExists;
		api.PackRead = Api_PackRead;
		api.PackCreateBlob = Api_PackCreateBlob;
		api.PackReleaseBlob = Api_PackReleaseBlob;
		api.PackSetRedirect = Api_PackSetRedirect;
		api.PackRemoveRedirect = Api_PackRemoveRedirect;
		api.PackSetOverride = Api_PackSetOverride;
		api.PackRemoveOverride = Api_PackRemoveOverride;
		api.PackResolveName = Api_PackResolveName;
		api.PackEnableResolveHook = Api_PackEnableResolveHook;
		api.PackEnumerate = Api_PackEnumerate;
		return api;
	}

	const NeoeterNativeApi g_native = MakeNativeApi();

	std::string ExecutableDirectory()
	{
#if defined(_WIN32)
		wchar_t buffer[MAX_PATH];
		const DWORD n = ::GetModuleFileNameW(nullptr, buffer, MAX_PATH);
		return fs::path(std::wstring(buffer, n)).parent_path().string();
#else
		std::error_code ec;
		fs::path exe = fs::read_symlink("/proc/self/exe", ec);
		return ec ? std::string(".") : exe.parent_path().string();
#endif
	}
}

struct CDotNetRuntime::Impl
{
		bool initialized = false;
};

CDotNetRuntime& CDotNetRuntime::Instance()
{
	static CDotNetRuntime instance;
	return instance;
}

CDotNetRuntime::CDotNetRuntime() : m_impl(std::make_unique<Impl>()) {}

CDotNetRuntime::~CDotNetRuntime() = default;

std::string CDotNetRuntime::DefaultHostDirectory()
{
	return (fs::path(ExecutableDirectory()) / "dotnet").string();
}

bool CDotNetRuntime::IsInitialized() const
{
	return m_impl->initialized;
}

bool CDotNetRuntime::Initialize(const DotNetRuntimeOptions& options, std::string* error)
{
	auto fail = [error](const std::string& message)
	{
		if (error)
			*error = message;
		Log(2, message);
		return false;
	};

	if (m_impl->initialized)
		return true;

	const fs::path hostDir = options.hostAssemblyDirectory.empty() ? fs::path(DefaultHostDirectory()) : fs::path(options.hostAssemblyDirectory);
	const fs::path assembly = hostDir / "Neoeter.Scripting.dll";
	const fs::path runtimeConfig = hostDir / "Neoeter.Scripting.runtimeconfig.json";
	std::error_code ec;
	if (!fs::exists(assembly, ec) || !fs::exists(runtimeConfig, ec))
		return fail("Neoeter.Scripting.dll or its runtimeconfig.json is missing in " + hostDir.string());

	// 1. Locate and load hostfxr (bundled runtime first when configured).
	CharString dotnetRoot = options.dotnetRoot.empty() ? CharString() : ToCharT(options.dotnetRoot);
	CharString assemblyPathT = ToCharT(assembly.string());
	get_hostfxr_parameters fxrParams{sizeof(get_hostfxr_parameters), assemblyPathT.c_str(), dotnetRoot.empty() ? nullptr : dotnetRoot.c_str()};
	char_t fxrPath[4096];
	size_t fxrPathSize = sizeof(fxrPath) / sizeof(char_t);
	if (get_hostfxr_path(fxrPath, &fxrPathSize, &fxrParams) != 0)
		return fail(options.dotnetRoot.empty()
						? "No .NET 10 runtime found (install it, set DOTNET_ROOT or bundle it in <client>/dotnet/runtime)"
						: "No .NET 10 runtime found under " + options.dotnetRoot);

	void* fxr = LoadLibraryPath(fxrPath);
	if (!fxr)
		return fail("Failed to load hostfxr");

	auto initFn = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(GetExport(fxr, "hostfxr_initialize_for_runtime_config"));
	auto getDelegateFn = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(GetExport(fxr, "hostfxr_get_runtime_delegate"));
	auto closeFn = reinterpret_cast<hostfxr_close_fn>(GetExport(fxr, "hostfxr_close"));
	if (!initFn || !getDelegateFn || !closeFn)
		return fail("hostfxr exports are missing");

	// 2. Start the runtime described by Neoeter.Scripting.runtimeconfig.json.
	CharString configT = ToCharT(runtimeConfig.string());
	hostfxr_initialize_parameters initParams{sizeof(hostfxr_initialize_parameters), assemblyPathT.c_str(), dotnetRoot.empty() ? nullptr : dotnetRoot.c_str()};
	hostfxr_handle context = nullptr;
	const int rc = initFn(configT.c_str(), &initParams, &context);
	if (rc < 0 || !context)
	{
		char buffer[32];
		std::snprintf(buffer, sizeof(buffer), "0x%08x", static_cast<unsigned>(rc));
		if (context)
			closeFn(context);
		return fail(std::string("hostfxr_initialize_for_runtime_config failed: ") + buffer);
	}

	load_assembly_fn loadAssembly = nullptr;
	get_function_pointer_fn getFunctionPointer = nullptr;
	getDelegateFn(context, hdt_load_assembly, reinterpret_cast<void**>(&loadAssembly));
	getDelegateFn(context, hdt_get_function_pointer, reinterpret_cast<void**>(&getFunctionPointer));
	closeFn(context);
	if (!loadAssembly || !getFunctionPointer)
		return fail("hostfxr did not provide the load_assembly/get_function_pointer delegates");

	// 3. Load Neoeter.Scripting into the default load context and bootstrap it.
	if (loadAssembly(assemblyPathT.c_str(), nullptr, nullptr) != 0)
		return fail("Failed to load " + assembly.string());

	NeoeterBootstrapFn bootstrap = nullptr;
	const CharString typeName = ToCharT("Neoeter.Scripting.Interop.Bootstrap, Neoeter.Scripting");
	const CharString methodName = ToCharT("Initialize");
	if (getFunctionPointer(typeName.c_str(), methodName.c_str(), UNMANAGEDCALLERSONLY_METHOD, nullptr, nullptr, reinterpret_cast<void**>(&bootstrap)) != 0 || !bootstrap)
		return fail("Neoeter.Scripting.Interop.Bootstrap.Initialize not found");

	g_managed = NeoeterManagedApi{};
	if (bootstrap(&g_native, &g_managed) != 0 || g_managed.version != NEOETER_DOTNET_ABI_VERSION)
		return fail("Neoeter.Scripting rejected the native ABI (version mismatch?)");

	m_impl->initialized = true;
	Log(0, "CoreCLR started (" + assembly.string() + ")");
	return true;
}

bool CDotNetRuntime::RunEntry(const std::string& assemblyPath, const std::vector<std::string>& args)
{
	if (!m_impl->initialized)
		return false;
	std::vector<const char*> argv;
	argv.reserve(args.size());
	for (const auto& arg : args)
		argv.push_back(arg.c_str());
	return g_managed.RunEntry(assemblyPath.c_str(), argv.data(), static_cast<int32_t>(argv.size())) == 0;
}

bool CDotNetRuntime::LoadAssembly(const std::string& assemblyPath)
{
	return m_impl->initialized && g_managed.LoadAssembly(assemblyPath.c_str()) == 0;
}

void CDotNetRuntime::Pump()
{
	if (m_impl->initialized)
		g_managed.Pump();
}

void CDotNetRuntime::Shutdown()
{
	if (!m_impl->initialized)
		return;
	if (Packs())
		Packs()->SetResolveHook(nullptr);
	g_managed.Shutdown();
	m_impl->initialized = false;
}

bool CDotNetRuntime::RunFile(const char* path)
{
	return path && RunEntry(path);
}

void CDotNetRuntime::Update()
{
	Pump();
}

std::size_t CDotNetRuntime::LiveObjectCount() const
{
	return g_liveManagedObjects;
}

const char* CDotNetRuntime::Name() const
{
	return "dotnet";
}

void CDotNetRuntime::IncRef(ScriptObject* object)
{
	ManagedRef* ref = ManagedRefOf(object);
	if (ref && ++ref->refs == 1 && g_managed.SetStrong)
		g_managed.SetStrong(ref->handle, 1);
}

void CDotNetRuntime::DecRef(ScriptObject* object)
{
	ManagedRef* ref = ManagedRefOf(object);
	if (ref && ref->refs > 0 && --ref->refs == 0 && g_managed.SetStrong)
		g_managed.SetStrong(ref->handle, 0);
}

bool CDotNetRuntime::HasAttr(ScriptObject* object, const char* name)
{
	ManagedRef* ref = ManagedRefOf(object);
	return ref && g_managed.HasAttr && g_managed.HasAttr(ref->handle, name) != 0;
}

ScriptCallStatus CDotNetRuntime::CallMethod(ScriptObject* object, const char* name, ScriptObject* args, ScriptObject** result)
{
	*result = nullptr;
	ManagedRef* ref = ManagedRefOf(object);
	if (!ref || !g_managed.CallMethod)
		return ScriptCallStatus::NoMethod;
	NeoeterValue out = nullptr;
	const int32_t status = g_managed.CallMethod(ref->handle, name, args, &out);
	*result = AsObject(out);
	return static_cast<ScriptCallStatus>(status);
}

std::string CDotNetRuntime::Repr(ScriptObject* object)
{
	ManagedRef* ref = ManagedRefOf(object);
	if (!ref || !g_managed.Repr)
		return "<dotnet object>";
	uint8_t buffer[256];
	const int32_t length = g_managed.Repr(ref->handle, buffer, sizeof(buffer));
	return std::string(reinterpret_cast<const char*>(buffer), static_cast<std::size_t>(std::min<int32_t>(length, sizeof(buffer) - 1)));
}
