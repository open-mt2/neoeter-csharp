#pragma once

// C ABI between the native engine and Neoeter.Scripting (managed).
//
// The native side fills NeoeterNativeApi and hands it to the managed
// bootstrap, which fills NeoeterManagedApi in return. Both tables are plain
// function pointers so they can be mirrored 1:1 by the C# structs in
// managed/Neoeter.Scripting/Interop/NativeApi.cs; keep the field order in sync
// and bump NEOETER_DOTNET_ABI_VERSION on every change.
//
// Conventions:
//  * `NeoeterValue` is an opaque ScriptObject*. Functions named New* and
//    Invoke return a new reference; GetItem/DictEntry/GetConstant return
//    borrowed references; SetItem steals `value`.
//  * Strings are byte sequences (no encoding implied) passed with an explicit
//    length; identifiers (module/method names, pack paths) are NUL-terminated.
//  * Every call must happen on the script (main) thread, except the Pack*
//    functions and the managed ResolvePack callback, which are thread-safe.

#include <stdint.h>

#ifdef __cplusplus
extern "C"
{
#endif

#define NEOETER_DOTNET_ABI_VERSION 1

	typedef void* NeoeterValue;
	typedef void* NeoeterModule;
	typedef void* NeoeterMethod;
	typedef void* NeoeterBlob;

	enum NeoeterValueType
	{
		NEOETER_VALUE_NONE = 0,
		NEOETER_VALUE_BOOL = 1,
		NEOETER_VALUE_INT = 2,
		NEOETER_VALUE_FLOAT = 3,
		NEOETER_VALUE_STRING = 4,
		NEOETER_VALUE_TUPLE = 5,
		NEOETER_VALUE_LIST = 6,
		NEOETER_VALUE_DICT = 7,
		NEOETER_VALUE_MODULE = 8,
		NEOETER_VALUE_OBJECT = 9,
	};

	enum NeoeterCallStatus
	{
		NEOETER_CALL_OK = 0,
		NEOETER_CALL_NO_METHOD = 1,
		NEOETER_CALL_ERROR = 2,
	};

	typedef void (*NeoeterPackEnumerateCallback)(void* context, const char* name, int64_t size);

	typedef struct NeoeterNativeApi
	{
			uint32_t version;
			uint32_t size;

			// Values
			int32_t (*TypeOf)(NeoeterValue value);
			void (*IncRef)(NeoeterValue value);
			void (*DecRef)(NeoeterValue value);
			NeoeterValue (*NewNone)(void);
			NeoeterValue (*NewBool)(int32_t value);
			NeoeterValue (*NewInt)(int64_t value);
			NeoeterValue (*NewFloat)(double value);
			NeoeterValue (*NewString)(const uint8_t* data, int32_t length);
			NeoeterValue (*NewTuple)(int32_t size);
			NeoeterValue (*NewList)(int32_t size);
			NeoeterValue (*NewDict)(void);
			int32_t (*SetItem)(NeoeterValue sequence, int32_t index, NeoeterValue value);
			int32_t (*DictSet)(NeoeterValue dict, NeoeterValue key, NeoeterValue value);
			int32_t (*GetBool)(NeoeterValue value);
			int64_t (*GetInt)(NeoeterValue value);
			double (*GetFloat)(NeoeterValue value);
			const uint8_t* (*GetString)(NeoeterValue value, int32_t* length);
			int32_t (*Size)(NeoeterValue value);
			NeoeterValue (*GetItem)(NeoeterValue sequence, int32_t index);
			int32_t (*DictEntry)(NeoeterValue dict, int32_t index, NeoeterValue* key, NeoeterValue* value);
			const char* (*ModuleNameOf)(NeoeterValue moduleValue);

			// Managed objects: `handle` is a weak GCHandle owned by managed code.
			NeoeterValue (*NewManaged)(intptr_t handle);
			intptr_t (*GetManaged)(NeoeterValue value);
			void (*FreeManaged)(NeoeterValue value);
			// Objects of other backends (e.g. Python instances).
			int32_t (*Repr)(NeoeterValue value, uint8_t* buffer, int32_t capacity);
			int32_t (*CallMethod)(NeoeterValue object, const char* name, NeoeterValue args, NeoeterValue* result);
			int32_t (*HasAttr)(NeoeterValue object, const char* name);

			// Errors (thread-local pending error of the script core)
			void (*ErrSet)(int32_t type, const uint8_t* message, int32_t length);
			int32_t (*ErrFetch)(int32_t* type, uint8_t* buffer, int32_t capacity);
			void (*ErrClear)(void);

			// Native modules
			NeoeterModule (*FindModule)(const char* name);
			NeoeterValue (*ModuleObject)(NeoeterModule module);
			int32_t (*ModuleCount)(void);
			NeoeterModule (*ModuleAt)(int32_t index);
			const char* (*ModuleName)(NeoeterModule module);
			NeoeterMethod (*FindMethod)(NeoeterModule module, const char* name);
			int32_t (*MethodCount)(NeoeterModule module);
			NeoeterMethod (*MethodAt)(NeoeterModule module, int32_t index);
			const char* (*MethodName)(NeoeterMethod method);
			NeoeterValue (*Invoke)(NeoeterMethod method, NeoeterValue args);
			NeoeterValue (*GetConstant)(NeoeterModule module, const char* name);
			int32_t (*ConstantCount)(NeoeterModule module);
			const char* (*ConstantName)(NeoeterModule module, int32_t index);
			NeoeterValue (*ConstantAt)(NeoeterModule module, int32_t index);

			// Diagnostics. level: 0 = trace, 1 = warning, 2 = error.
			void (*Log)(int32_t level, const uint8_t* message, int32_t length);
			void (*ReportException)(const uint8_t* text, int32_t length);

			// Packs (CEterPackManager). Return 0 when no pack manager exists.
			int32_t (*PackExists)(const char* name);
			NeoeterBlob (*PackRead)(const char* name, const uint8_t** data, int64_t* length);
			NeoeterBlob (*PackCreateBlob)(const uint8_t* data, int64_t length);
			void (*PackReleaseBlob)(NeoeterBlob blob);
			int32_t (*PackSetRedirect)(const char* from, const char* to);
			int32_t (*PackRemoveRedirect)(const char* from);
			int32_t (*PackSetOverride)(const char* name, const uint8_t* data, int64_t length);
			int32_t (*PackRemoveOverride)(const char* name);
			int32_t (*PackResolveName)(const char* name, uint8_t* buffer, int32_t capacity);
			int32_t (*PackEnableResolveHook)(int32_t enable);
			int32_t (*PackEnumerate)(const char* prefix, NeoeterPackEnumerateCallback callback, void* context);
	} NeoeterNativeApi;

	typedef struct NeoeterManagedApi
	{
			uint32_t version;
			uint32_t size;

			int32_t (*HasAttr)(intptr_t handle, const char* name);
			int32_t (*CallMethod)(intptr_t handle, const char* name, NeoeterValue args, NeoeterValue* result);
			int32_t (*Repr)(intptr_t handle, uint8_t* buffer, int32_t capacity);
			// strong != 0 while native code holds references to the wrapper.
			void (*SetStrong)(intptr_t handle, int32_t strong);
			// Loads an assembly (pack path or file) and runs its entry point.
			int32_t (*RunEntry)(const char* assemblyPath, const char* const* args, int32_t argCount);
			int32_t (*LoadAssembly)(const char* assemblyPath);
			// Thread-safe. Returns 1 when managed resolvers serve `name`; `blob`
			// (when non-null) receives a PackCreateBlob result owned by the caller.
			int32_t (*ResolvePack)(const char* name, NeoeterBlob* blob);
			// Releases objects collected by the GC; call regularly on the main thread.
			void (*Pump)(void);
			void (*Shutdown)(void);
	} NeoeterManagedApi;

	// Managed bootstrap: Neoeter.Scripting.Interop.Bootstrap.Initialize.
	typedef int32_t (*NeoeterBootstrapFn)(const NeoeterNativeApi* native, NeoeterManagedApi* managed);

#ifdef __cplusplus
}
#endif
