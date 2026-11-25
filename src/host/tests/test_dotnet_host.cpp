#include <Neoeter/DotNet/DotNetRuntime.h>
#include <Neoeter/IO/EterPackManager.h>
#include <Neoeter/Script/Script.h>
#include <Neoeter/Script/ScriptExceptionSender.h>

#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iterator>
#include <string>
#include <thread>
#include <vector>

#define TEST_ASSERT(cond)                                                          \
	do {                                                                           \
		if (!(cond))                                                               \
		{                                                                          \
			std::fprintf(stderr, "[FAIL] %s:%d: %s\n", __FILE__, __LINE__, #cond); \
			std::exit(1);                                                          \
		}                                                                          \
	} while (0)

// Boots CoreCLR, exposes a native test module and runs the C# assertions in
// Neoeter.Scripting.HostTests, loaded from the pack VFS (an in-memory override).
namespace
{
	ScriptObject* g_handler = nullptr; // borrowed, like the engine's handlers

	class CountingSender : public IScriptExceptionSender
	{
		public:
			void Send() override
			{
				++sent;
				last = m_strExceptionString;
			}

			int sent = 0;
			std::string last;
	};

	CountingSender g_sender;

	ScriptObject* testmod_Add(ScriptObject*, ScriptObject* args)
	{
		long a = 0;
		long b = 0;
		if (!ScriptTuple_GetLong(args, 0, &a) || !ScriptTuple_GetLong(args, 1, &b))
			return Script_BadArgument();
		return Script_BuildValue("L", static_cast<long long>(a) + b);
	}

	ScriptObject* testmod_Echo(ScriptObject*, ScriptObject* args)
	{
		ScriptObject* value = nullptr;
		if (!ScriptTuple_GetObject(args, 0, &value))
			return Script_BadArgument();
		Script_INCREF(value);
		return value;
	}

	ScriptObject* testmod_Fail(ScriptObject*, ScriptObject*)
	{
		return Script_BuildException("boom %d", 7);
	}

	ScriptObject* testmod_GetModule(ScriptObject*, ScriptObject*)
	{
		ScriptObject* module = ScriptModule_Find("testmod")->object;
		Script_INCREF(module);
		return module;
	}

	ScriptObject* testmod_SetHandler(ScriptObject*, ScriptObject* args)
	{
		if (!ScriptTuple_GetObject(args, 0, &g_handler))
			return Script_BadArgument();
		return Script_BuildNone();
	}

	ScriptObject* testmod_HandlerHas(ScriptObject*, ScriptObject* args)
	{
		char* name = nullptr;
		if (!ScriptTuple_GetString(args, 0, &name))
			return Script_BadArgument();
		return ScriptBool_New(Script_HasAttr(g_handler, name));
	}

	ScriptObject* testmod_CallHandler(ScriptObject*, ScriptObject* args)
	{
		char* name = nullptr;
		if (!ScriptTuple_GetString(args, 0, &name))
			return Script_BadArgument();

		const long count = ScriptTuple_Size(args);
		ScriptObject* callArgs = ScriptTuple_New(static_cast<std::size_t>(count - 1));
		for (long i = 1; i < count; ++i)
		{
			ScriptObject* item = ScriptTuple_GetItem(args, i);
			Script_INCREF(item);
			ScriptTuple_SetItem(callArgs, i - 1, item);
		}

		ScriptObject* result = Script_CallMethodObject(g_handler, name, callArgs);
		if (!result)
		{
			ScriptErr_Clear();
			return ScriptBool_New(false);
		}
		return result;
	}

	ScriptObject* testmod_ExceptionReports(ScriptObject*, ScriptObject*)
	{
		return Script_BuildValue("i", g_sender.sent);
	}

	ScriptObject* testmod_LiveManagedObjects(ScriptObject*, ScriptObject*)
	{
		return Script_BuildValue("i", static_cast<int>(CDotNetRuntime::Instance().LiveObjectCount()));
	}

	void RegisterTestModule()
	{
		static ScriptMethodDef methods[] = {
			{"Add", testmod_Add, SCRIPT_VARARGS, nullptr},
			{"Echo", testmod_Echo, SCRIPT_VARARGS, nullptr},
			{"Fail", testmod_Fail, SCRIPT_VARARGS, nullptr},
			{"GetModule", testmod_GetModule, SCRIPT_VARARGS, nullptr},
			{"SetHandler", testmod_SetHandler, SCRIPT_VARARGS, nullptr},
			{"HandlerHas", testmod_HandlerHas, SCRIPT_VARARGS, nullptr},
			{"CallHandler", testmod_CallHandler, SCRIPT_VARARGS, nullptr},
			{"ExceptionReports", testmod_ExceptionReports, SCRIPT_VARARGS, nullptr},
			{"LiveManagedObjects", testmod_LiveManagedObjects, SCRIPT_VARARGS, nullptr},
			{nullptr, nullptr, 0, nullptr},
		};
		ScriptObject* module = ScriptModule_Create("testmod", methods);
		ScriptModule_AddIntConstant(module, "INT_CONST", 42);
		ScriptModule_AddStringConstant(module, "STR_CONST", "hello");
	}

	std::vector<char> ReadFile(const std::string& path)
	{
		std::ifstream file(path, std::ios::binary);
		TEST_ASSERT(file.is_open());
		return std::vector<char>(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
	}

	std::string AsString(const CEterPackManager::TPackDataPtr& data)
	{
		return std::string(data->begin(), data->end());
	}
}

int main(int argc, char** argv)
{
	TEST_ASSERT(argc == 3); // <Neoeter.Scripting dir> <HostTests.dll>
	SetExceptionSender(&g_sender);
	RegisterTestModule();

	CEterPackManager packs;

	CDotNetRuntime& runtime = CDotNetRuntime::Instance();
	std::string error;
	TEST_ASSERT(runtime.Initialize({argv[1], ""}, &error));
	TEST_ASSERT(runtime.IsInitialized());
	TEST_ASSERT(runtime.Initialize({argv[1], ""})); // idempotent

	// The script assembly only exists inside the VFS.
	packs.SetOverride("scripts/neoeter.scripting.hosttests.dll", std::make_shared<std::vector<char>>(ReadFile(argv[2])));
	TEST_ASSERT(!runtime.RunEntry("scripts/missing.dll"));
	TEST_ASSERT(g_sender.sent == 1 && g_sender.last.find("FileNotFoundException") != std::string::npos);

	TEST_ASSERT(runtime.RunEntry("scripts/Neoeter.Scripting.HostTests.dll", {"alpha", "beta"}));
	std::printf("  [PASS] C# assertions (modules, errors, callbacks, packs, lifetime)\n");

	// The handler stored by C# is callable from native code after Main returned.
	TEST_ASSERT(g_handler && ScriptForeign_Check(g_handler));
	TEST_ASSERT(Script_HasAttr(g_handler, "IsReady"));
	bool ready = false;
	TEST_ASSERT(Script_CallMethod(g_handler, "IsReady", nullptr, &ready) && ready);
	long doubled = 0;
	TEST_ASSERT(Script_CallMethod(g_handler, "OnEvent", Script_BuildValue("(i)", 50), &doubled) && doubled == 100);
	TEST_ASSERT(runtime.Repr(g_handler).find("Neoeter.Scripting.HostTests.Handler") != std::string::npos);
	std::printf("  [PASS] native -> C# callbacks\n");

	// The C# resolver serves engine lookups, including from loader threads.
	CEterPackManager::TPackDataPtr data;
	TEST_ASSERT(packs.isExist("CSharp\\Served.txt"));
	TEST_ASSERT(packs.Get("csharp/served.txt", data) && AsString(data) == "served by C#");
	bool threadOk = false;
	std::thread loader([&]
					   {
		CEterPackManager::TPackDataPtr threaded;
		threadOk = packs.Get("csharp/served.txt", threaded) && AsString(threaded) == "served by C#"; });
	loader.join();
	TEST_ASSERT(threadOk);
	TEST_ASSERT(!packs.isExist("csharp/other.txt"));
	std::printf("  [PASS] C# pack resolver\n");

	runtime.Pump();
	runtime.Shutdown();
	TEST_ASSERT(!runtime.IsInitialized());
	TEST_ASSERT(!packs.isExist("csharp/served.txt"));

	std::printf("[test_dotnet_host] All tests passed\n");
	return 0;
}
