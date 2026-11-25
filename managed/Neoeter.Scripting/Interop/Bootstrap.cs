using System.Reflection;
using System.Runtime.InteropServices;
using Neoeter.Scripting.Packs;

namespace Neoeter.Scripting.Interop;

// Native -> managed entry points. Every function catches all exceptions:
// an exception must never unwind into native frames.
internal static unsafe class Bootstrap
{
    [UnmanagedCallersOnly]
    public static int Initialize(NativeApi* native, ManagedApi* managed)
    {
        try
        {
            if (native == null || managed == null || native->Version != NativeApi.AbiVersion || native->Size < sizeof(NativeApi))
                return -1;

            Runtime.Attach(native);
            managed->Version = NativeApi.AbiVersion;
            managed->Size = (uint)sizeof(ManagedApi);
            managed->HasAttr = &HasAttr;
            managed->CallMethod = &CallMethod;
            managed->Repr = &Repr;
            managed->SetStrong = &SetStrong;
            managed->RunEntry = &RunEntry;
            managed->LoadAssembly = &LoadAssembly;
            managed->ResolvePack = &ResolvePack;
            managed->Pump = &Pump;
            managed->Shutdown = &Shutdown;
            return 0;
        }
        catch
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    private static int HasAttr(nint handle, byte* name)
    {
        try
        {
            object? target = ManagedObjects.FromHandle(handle);
            return target != null && Dispatcher.HasMethod(target, NativeText.FromNative(name)!) ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static int CallMethod(nint handle, byte* name, nint args, nint* result)
    {
        *result = 0;
        string method = NativeText.FromNative(name) ?? string.Empty;
        try
        {
            Runtime.Pump();
            object? target = ManagedObjects.FromHandle(handle);
            if (target == null)
                return (int)CallStatus.NoMethod;
            if (!Dispatcher.TryInvoke(target, method, Converter.ArgsFromTuple(args), out object? value))
                return (int)CallStatus.NoMethod;
            *result = Converter.ToNative(value);
            return (int)CallStatus.Ok;
        }
        catch (Exception exception)
        {
            Exception reported = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            Runtime.ReportException(reported);
            return (int)CallStatus.Error;
        }
    }

    [UnmanagedCallersOnly]
    private static int Repr(nint handle, byte* buffer, int capacity)
    {
        try
        {
            object? target = ManagedObjects.FromHandle(handle);
            string text = target == null ? "<collected>" : $"<{target.GetType().FullName} {target}>";
            return NativeText.CopyOut(text, buffer, capacity);
        }
        catch
        {
            return NativeText.CopyOut("<dotnet object>", buffer, capacity);
        }
    }

    [UnmanagedCallersOnly]
    private static void SetStrong(nint handle, int strong)
    {
        try
        {
            ManagedObjects.SetStrong(handle, strong != 0);
        }
        catch (Exception exception)
        {
            Runtime.ReportException(exception);
        }
    }

    [UnmanagedCallersOnly]
    private static int RunEntry(byte* path, byte** argv, int argc)
    {
        try
        {
            string[] args = new string[Math.Max(0, argc)];
            for (int i = 0; i < args.Length; i++)
                args[i] = NativeText.FromNative(argv[i]) ?? string.Empty;
            Assembly assembly = ScriptHost.LoadAssembly(NativeText.FromNative(path)!);
            return ScriptHost.Run(assembly, args);
        }
        catch (Exception exception)
        {
            Runtime.ReportException(exception);
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    private static int LoadAssembly(byte* path)
    {
        try
        {
            ScriptHost.LoadAssembly(NativeText.FromNative(path)!);
            return 0;
        }
        catch (Exception exception)
        {
            Runtime.ReportException(exception);
            return -1;
        }
    }

    [UnmanagedCallersOnly]
    private static int ResolvePack(byte* name, nint* blob)
    {
        try
        {
            bool wantData = blob != null;
            if (!Pack.Resolve(NativeText.FromNative(name)!, wantData, out byte[]? data))
                return 0;
            if (wantData)
            {
                fixed (byte* p = data)
                    *blob = Runtime.Api->PackCreateBlob(p, data!.Length);
            }
            return 1;
        }
        catch (Exception exception)
        {
            Native.Log.Error($"Pack resolver failed: {exception}");
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static void Pump()
    {
        try
        {
            Runtime.Pump();
        }
        catch (Exception exception)
        {
            Runtime.ReportException(exception);
        }
    }

    [UnmanagedCallersOnly]
    private static void Shutdown()
    {
        try
        {
            Pack.ClearResolvers();
            Runtime.Pump();
            ManagedObjects.ReleaseAll();
            Native.Reset();
            ScriptHost.Reset();
            Runtime.Detach();
        }
        catch
        {
        }
    }
}
