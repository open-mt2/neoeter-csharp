using System.Text;
using Neoeter.Scripting;
using Neoeter.Scripting.Packs;

[assembly: ScriptEntryPoint(typeof(Neoeter.Scripting.HostTests.Program))]

namespace Neoeter.Scripting.HostTests;

// Engine-side handler: the native test module calls these methods back.
public sealed class Handler
{
    public int Events { get; private set; }

    public long OnEvent(long value)
    {
        Events++;
        return value * 2;
    }

    public string Describe(string prefix, int number = 7) => $"{prefix}:{number}";

    public int Sum(params int[] values) => values.Sum();

    public bool IsReady() => true;

    public void Explode() => throw new InvalidOperationException("handler failure");
}

public static class Program
{
    // Kept alive for the native side, like a Python script keeps its windows.
    public static Handler? Current;

    private static int s_failures;

    private static void Check(bool condition, string what)
    {
        if (condition)
            return;
        s_failures++;
        Native.Log.Error($"[FAIL] {what}");
    }

    private static void Expect<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
            Check(false, $"{what}: no exception");
        }
        catch (TException)
        {
        }
        catch (Exception exception)
        {
            Check(false, $"{what}: unexpected {exception.GetType().Name}");
        }
    }

    public static int Main(string[] args)
    {
        Check(Native.IsHosted, "hosted");
        Check(args.Length == 2 && args[0] == "alpha" && args[1] == "beta", "entry arguments");

        Modules();
        Errors();
        Callbacks();
        Packs();
        Lifetime();

        Native.Log.Trace($"C# host tests finished with {s_failures} failure(s)");
        return s_failures == 0 ? 0 : 1;
    }

    private static void Modules()
    {
        Check(Native.ModuleNames.Contains("testmod"), "module listed");
        NativeModule testmod = Native.Import("testmod");
        Check(testmod.HasFunction("Add") && !testmod.HasFunction("Nope"), "HasFunction");
        Check(testmod.Call<int>("Add", 2, 3) == 5, "Add(int, int)");
        Check(testmod.Call<long>("Add", 5_000_000_000L, 1) == 5_000_000_001L, "64-bit ints");
        Check(testmod.Constant<int>("INT_CONST") == 42, "int constant");
        Check(testmod.Constant<string>("STR_CONST") == "hello", "string constant");

        // Round trips through Echo (the native side returns its argument).
        Check(testmod.Call("Echo", (object?)null) == null, "None");
        Check(testmod.Call<bool>("Echo", true), "bool");
        Check(Math.Abs(testmod.Call<double>("Echo", 1.5) - 1.5) < 1e-9, "float");
        Check(testmod.Call<string>("Echo", "caf\u00e9") == "caf\u00e9", "Latin-1 string");
        Check(testmod.Call("Echo", Encoding.Latin1.GetBytes("\u00ff\u0001")) is string raw && raw == "\u00ff\u0001", "raw bytes");
        object?[]? tuple = testmod.Call("Echo", (object)new object?[] { 1, "two", null }) as object?[];
        Check(tuple is { Length: 3 } && (long)tuple[0]! == 1 && (string)tuple[1]! == "two" && tuple[2] == null, "tuple");
        var list = testmod.Call("Echo", new List<int> { 4, 5 }) as List<object?>;
        Check(list is { Count: 2 } && (long)list[1]! == 5, "list");
        var dict = testmod.Call("Echo", new Dictionary<string, int> { ["k"] = 9 }) as Dictionary<object, object?>;
        Check(dict != null && (long)dict["k"]! == 9, "dict");
        Check(testmod.Call("GetModule") is NativeModule { Name: "testmod" }, "module value");

        dynamic dyn = Native.ImportDynamic("testmod");
        Check((long)dyn.Add(10, 20) == 30, "dynamic call");
        Check((long)dyn.INT_CONST == 42, "dynamic constant");
    }

    private static void Errors()
    {
        NativeModule testmod = Native.Import("testmod");
        try
        {
            testmod.Call("Fail");
            Check(false, "Fail did not throw");
        }
        catch (ScriptException exception)
        {
            Check(exception.ErrorType == ScriptErrorType.RuntimeError && exception.NativeMessage == "boom 7", "Script_BuildException -> ScriptException");
        }

        try
        {
            testmod.Call("Add", "x", 1);
            Check(false, "bad argument did not throw");
        }
        catch (ScriptException exception)
        {
            Check(exception.ErrorType == ScriptErrorType.TypeError, "Script_BadArgument -> TypeError");
        }

        Expect<MissingMethodException>(() => testmod.Call("Nope"), "missing function");
        Expect<KeyNotFoundException>(() => Native.Import("no_such_module"), "missing module");
        Check(testmod.Call<int>("Add", 1, 1) == 2, "error state cleared");
    }

    private static void Callbacks()
    {
        NativeModule testmod = Native.Import("testmod");
        Current = new Handler();
        testmod.Call("SetHandler", Current);

        Check(testmod.Call<bool>("HandlerHas", "OnEvent") && !testmod.Call<bool>("HandlerHas", "Missing"), "Script_HasAttr");
        Check(testmod.Call<long>("CallHandler", "OnEvent", 21) == 42 && Current.Events == 1, "Script_CallMethod -> C#");
        Check(testmod.Call<string>("CallHandler", "Describe", "x") == "x:7", "optional parameter");
        Check(testmod.Call<int>("CallHandler", "Sum", 1, 2, 3) == 6, "params parameter");
        Check(testmod.Call<string>("CallHandler", "describe", "y", 1) == "y:1", "case-insensitive fallback");
        Check(testmod.Call("CallHandler", "Missing") is false, "missing method -> false");

        int reports = testmod.Call<int>("ExceptionReports");
        Check(testmod.Call("CallHandler", "Explode") is false, "throwing handler -> false");
        Check(testmod.Call<int>("ExceptionReports") == reports + 1, "exception reported to the sender");
        Check(ReferenceEquals(testmod.Call("Echo", Current), Current), "managed object identity");
    }

    private static void Packs()
    {
        Pack.Override("virtual/hello.txt", "from C#"u8);
        Check(Pack.Exists("VIRTUAL\\HELLO.TXT"), "override exists");
        Check(Pack.ReadAllText("virtual/hello.txt") == "from C#", "override read");

        Pack.Redirect("virtual/alias.txt", "virtual/hello.txt");
        Check(Pack.ResolveName("Virtual/Alias.txt") == "virtual/hello.txt", "ResolveName");
        Check(Pack.ReadAllText("virtual/alias.txt") == "from C#", "redirect read");
        Check(Pack.RemoveRedirect("virtual/alias.txt") && !Pack.Exists("virtual/alias.txt"), "redirect removed");

        IDisposable temporary = Pack.AddResolver(path => path == "temp/x.bin" ? [1, 2, 3] : null);
        Check(Pack.ReadAllBytes("temp/x.bin").SequenceEqual(new byte[] { 1, 2, 3 }), "resolver read");
        temporary.Dispose();
        Check(!Pack.Exists("temp/x.bin"), "resolver removed");

        // Left registered: the native test reads it after Main returns.
        Pack.AddResolver(path => path == "csharp/served.txt" ? "served by C#"u8.ToArray() : null);

        Check(Pack.List("scripts/").Count >= 0, "list");
        Check(Pack.TryReadAllBytes("does/not/exist") == null, "missing file");
        Expect<FileNotFoundException>(() => Pack.ReadAllBytes("does/not/exist"), "missing file throws");
        Check(Pack.RemoveOverride("virtual/hello.txt"), "override removed");
    }

    // Separate frame so no JIT stack slot keeps the last temporary alive.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void PassTemporaries(NativeModule testmod)
    {
        for (int i = 0; i < 2000; i++)
            testmod.Call("Echo", new Handler());
    }

    private static void Lifetime()
    {
        NativeModule testmod = Native.Import("testmod");
        // Temporary objects crossing the boundary are released after collection.
        PassTemporaries(testmod);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Native.Pump();
        Check(testmod.Call<int>("LiveManagedObjects") <= 1, $"wrappers released ({testmod.Call<int>("LiveManagedObjects")} alive)");

        // The stored handler survives a collection.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Native.Pump();
        Check(testmod.Call<long>("CallHandler", "OnEvent", 1) == 2, "handler alive after GC");
    }
}
