using System.Dynamic;
using System.Text;
using Neoeter.Scripting.Interop;

namespace Neoeter.Scripting;

/// <summary>Entry point to the engine's native modules (app, net, wndMgr, ...).</summary>
public static class Native
{
    private static readonly Dictionary<string, NativeModule> s_modules = new(StringComparer.Ordinal);

    /// <summary>
    /// Encoding of script strings. Native strings are raw bytes (the client
    /// locale code page); Latin-1 round-trips every byte unchanged. Set it to
    /// the locale encoding (e.g. after registering CodePagesEncodingProvider)
    /// to get readable text.
    /// </summary>
    public static Encoding Encoding { get; set; } = Encoding.Latin1;

    /// <summary>True when running inside the engine (CDotNetRuntime).</summary>
    public static bool IsHosted => Runtime.IsHosted;

    /// <summary>Returns the module, like Python's <c>import name</c>.</summary>
    /// <exception cref="KeyNotFoundException">No native module has that name.</exception>
    public static NativeModule Import(string name)
    {
        return TryImport(name) ?? throw new KeyNotFoundException($"No native module named '{name}'");
    }

    /// <summary>Returns the module as <c>dynamic</c>: <c>Native.ImportDynamic("net").SendChatPacket("hi", 0)</c>.</summary>
    public static dynamic ImportDynamic(string name) => Import(name);

    public static unsafe NativeModule? TryImport(string name)
    {
        if (s_modules.TryGetValue(name, out NativeModule? cached))
            return cached;

        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        nint handle;
        fixed (byte* p = bytes)
            handle = Runtime.Api->FindModule(p);
        if (handle == 0)
            return null;

        NativeModule module = new(name, handle);
        s_modules[name] = module;
        return module;
    }

    /// <summary>Names of every registered native module.</summary>
    public static unsafe IReadOnlyList<string> ModuleNames
    {
        get
        {
            NativeApi* api = Runtime.Api;
            int count = api->ModuleCount();
            List<string> names = new(count);
            for (int i = 0; i < count; i++)
                names.Add(NativeText.FromNative(api->ModuleName(api->ModuleAt(i)))!);
            return names;
        }
    }

    /// <summary>Releases native wrappers of collected objects. The engine calls it every frame.</summary>
    public static void Pump() => Runtime.Pump();

    public static class Log
    {
        public static void Trace(string message) => Runtime.Log(0, message);
        public static void Warning(string message) => Runtime.Log(1, message);
        public static void Error(string message) => Runtime.Log(2, message);

        /// <summary>Reports an exception like an unhandled script error (dbg/exception sender).</summary>
        public static void Exception(Exception exception) => Runtime.ReportException(exception);
    }

    internal static void Reset() => s_modules.Clear();
}

/// <summary>A native module. Supports both <see cref="Call(string, object?[])"/> and <c>dynamic</c> access.</summary>
public sealed unsafe class NativeModule : DynamicObject
{
    private readonly Dictionary<string, nint> _methods = new(StringComparer.Ordinal);
    private int _methodCount = -1;

    internal NativeModule(string name, nint handle)
    {
        Name = name;
        Handle = handle;
    }

    public string Name { get; }

    internal nint Handle { get; }

    internal static NativeModule? FromModuleName(string? name) => name == null ? null : Native.TryImport(name);

    public IReadOnlyList<string> Functions
    {
        get
        {
            NativeApi* api = Runtime.Api;
            int count = api->MethodCount(Handle);
            List<string> names = new(count);
            for (int i = 0; i < count; i++)
                names.Add(NativeText.FromNative(api->MethodName(api->MethodAt(Handle, i)))!);
            return names;
        }
    }

    public IReadOnlyList<string> ConstantNames
    {
        get
        {
            NativeApi* api = Runtime.Api;
            int count = api->ConstantCount(Handle);
            List<string> names = new(count);
            for (int i = 0; i < count; i++)
                names.Add(NativeText.FromNative(api->ConstantName(Handle, i))!);
            return names;
        }
    }

    public bool HasFunction(string name) => FindMethod(name) != 0;

    public bool HasConstant(string name) => FindConstant(name) != 0;

    /// <summary>Calls a module function. Native errors throw <see cref="ScriptException"/>.</summary>
    public object? Call(string function, params object?[] args)
    {
        nint method = FindMethod(function);
        if (method == 0)
            throw new MissingMethodException(Name, function);

        Runtime.Pump();
        NativeApi* api = Runtime.Api;
        nint tuple = Converter.BuildTuple(args);
        nint result = api->Invoke(method, tuple);
        api->DecRef(tuple);

        // A function may return a value *and* set an error (Script_BuildException).
        ScriptException? error = Runtime.FetchError();
        if (error != null || result == 0)
        {
            if (result != 0)
                api->DecRef(result);
            throw error ?? new ScriptException(ScriptErrorType.SystemError, $"{Name}.{function} failed");
        }
        return Converter.TakeFromNative(result);
    }

    public T Call<T>(string function, params object?[] args) => Converter.ConvertTo<T>(Call(function, args));

    public object? Constant(string name)
    {
        nint value = FindConstant(name);
        if (value == 0)
            throw new KeyNotFoundException($"{Name} has no constant '{name}'");
        return Converter.FromNative(value);
    }

    public T Constant<T>(string name) => Converter.ConvertTo<T>(Constant(name));

    public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
    {
        if (FindMethod(binder.Name) == 0)
        {
            result = null;
            return false;
        }
        result = Call(binder.Name, args ?? []);
        return true;
    }

    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        nint value = FindConstant(binder.Name);
        if (value != 0)
        {
            result = Converter.FromNative(value);
            return true;
        }
        if (FindMethod(binder.Name) != 0)
        {
            string function = binder.Name;
            result = new Func<object?[], object?>(args => Call(function, args));
            return true;
        }
        result = null;
        return false;
    }

    public override IEnumerable<string> GetDynamicMemberNames() => Functions.Concat(ConstantNames);

    public override string ToString() => $"<native module '{Name}'>";

    private nint FindMethod(string name)
    {
        // Method pointers stay valid until the module's table grows.
        int count = Runtime.Api->MethodCount(Handle);
        if (count != _methodCount)
        {
            _methods.Clear();
            _methodCount = count;
        }
        if (_methods.TryGetValue(name, out nint cached))
            return cached;

        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        nint method;
        fixed (byte* p = bytes)
            method = Runtime.Api->FindMethod(Handle, p);
        if (method != 0)
            _methods[name] = method;
        return method;
    }

    private nint FindConstant(string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes)
            return Runtime.Api->GetConstant(Handle, p);
    }
}
