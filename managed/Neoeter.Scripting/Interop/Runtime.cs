using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Neoeter.Scripting.Interop;

internal static unsafe class Runtime
{
    private static NativeApi* s_api;
    private static readonly ConcurrentQueue<Action> s_mainThreadWork = new();

    public static NativeApi* Api
    {
        get
        {
            if (s_api == null)
                throw new InvalidOperationException("Neoeter.Scripting is not hosted by the engine (CDotNetRuntime).");
            return s_api;
        }
    }

    public static bool IsHosted => s_api != null;

    public static void Attach(NativeApi* api) => s_api = api;

    public static void Detach() => s_api = null;

    // Finalizers run on the GC thread; native objects may only be touched on
    // the main thread, so their release is deferred to Pump().
    public static void Defer(Action action) => s_mainThreadWork.Enqueue(action);

    public static void Pump()
    {
        while (s_mainThreadWork.TryDequeue(out Action? action))
        {
            if (s_api != null)
                action();
        }
    }

    // Converts the pending native error (if any) into a ScriptException.
    public static ScriptException? FetchError()
    {
        const int capacity = 4096;
        byte* buffer = stackalloc byte[capacity];
        int type;
        int length = Api->ErrFetch(&type, buffer, capacity);
        if (length < 0)
            return null;
        return new ScriptException((ScriptErrorType)type, NativeText.FromNative(buffer, Math.Min(length, capacity - 1)));
    }

    public static void SetError(ScriptErrorType type, string message)
    {
        byte[] bytes = NativeText.ToNative(message);
        fixed (byte* p = bytes)
            Api->ErrSet((int)type, p, bytes.Length - 1);
    }

    public static void Log(int level, string message)
    {
        if (s_api == null)
        {
            (level > 0 ? Console.Error : Console.Out).WriteLine($"[dotnet] {message}");
            return;
        }
        byte[] bytes = NativeText.ToNative(message);
        fixed (byte* p = bytes)
            s_api->Log(level, p, bytes.Length - 1);
    }

    public static void ReportException(Exception exception)
    {
        string text = exception.ToString();
        if (s_api == null)
        {
            Console.Error.WriteLine(text);
            return;
        }
        byte[] bytes = NativeText.ToNative(text);
        fixed (byte* p = bytes)
            s_api->ReportException(p, bytes.Length - 1);
    }
}

// Managed objects handed to the engine. Each object gets one native wrapper
// (stable pointer identity), created on first use and freed after the object
// is collected. The wrapper references the object through a weak GCHandle;
// SetStrong pins it while native code holds references (Script_INCREF).
internal static class ManagedObjects
{
    private sealed class Entry
    {
        public nint Wrapper;
        public GCHandle Weak;

        ~Entry()
        {
            nint wrapper = Wrapper;
            GCHandle weak = Weak;
            Runtime.Defer(() =>
            {
                unsafe { Runtime.Api->FreeManaged(wrapper); }
                weak.Free();
            });
        }
    }

    private static readonly ConditionalWeakTable<object, Entry> s_entries = new();
    private static readonly Dictionary<nint, GCHandle> s_strong = new();
    private static readonly object s_lock = new();

    // Returns a new native reference to the wrapper of `value`.
    public static unsafe nint ToNative(object value)
    {
        if (s_entries.TryGetValue(value, out Entry? entry))
        {
            Runtime.Api->IncRef(entry.Wrapper);
            return entry.Wrapper;
        }

        GCHandle weak = GCHandle.Alloc(value, GCHandleType.Weak);
        nint wrapper = Runtime.Api->NewManaged(GCHandle.ToIntPtr(weak));
        s_entries.Add(value, new Entry { Wrapper = wrapper, Weak = weak });
        return wrapper;
    }

    public static object? FromHandle(nint handle)
    {
        return handle == 0 ? null : GCHandle.FromIntPtr(handle).Target;
    }

    public static void SetStrong(nint handle, bool strong)
    {
        lock (s_lock)
        {
            if (strong)
            {
                object? target = FromHandle(handle);
                if (target != null && !s_strong.ContainsKey(handle))
                    s_strong[handle] = GCHandle.Alloc(target, GCHandleType.Normal);
            }
            else if (s_strong.Remove(handle, out GCHandle pinned))
            {
                pinned.Free();
            }
        }
    }

    public static void ReleaseAll()
    {
        lock (s_lock)
        {
            foreach (GCHandle handle in s_strong.Values)
                handle.Free();
            s_strong.Clear();
        }
    }
}
