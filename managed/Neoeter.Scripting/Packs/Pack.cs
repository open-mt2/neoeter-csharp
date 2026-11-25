using System.Collections.Immutable;
using System.Text;
using Neoeter.Scripting.Interop;

namespace Neoeter.Scripting.Packs;

/// <summary>A file listed by <see cref="Pack.List"/>.</summary>
public readonly record struct PackEntry(string Path, long Size);

/// <summary>
/// Serves pack files from C#. Resolvers are consulted after redirects and
/// overrides, before the packs and loose files. They are called from loader
/// threads too, so implementations must be thread-safe.
/// </summary>
public interface IPackResolver
{
    /// <param name="path">Normalized path (lowercase, '/' separators).</param>
    bool Exists(string path);

    /// <returns>The file contents, or null to fall through to the packs.</returns>
    byte[]? Read(string path);
}

/// <summary>
/// The engine's virtual file system (CEterPackManager): every lookup made by
/// the engine (models, textures, UI scripts, assemblies) goes through it.
/// </summary>
public static unsafe class Pack
{
    private static ImmutableArray<IPackResolver> s_resolvers = ImmutableArray<IPackResolver>.Empty;
    private static readonly object s_lock = new();

    /// <summary>True if the file exists in a pack, as a loose file, or through a redirect/override/resolver.</summary>
    public static bool Exists(string path)
    {
        if (!Runtime.IsHosted)
            return File.Exists(path);
        byte[] name = NativeText.ToNative(path);
        fixed (byte* p = name)
            return Runtime.Api->PackExists(p) != 0;
    }

    /// <summary>Reads a file, or returns null when it does not exist.</summary>
    public static byte[]? TryReadAllBytes(string path)
    {
        if (Runtime.IsHosted)
        {
            byte[] name = NativeText.ToNative(path);
            byte* data;
            long length;
            nint blob;
            fixed (byte* p = name)
                blob = Runtime.Api->PackRead(p, &data, &length);
            if (blob != 0)
            {
                try
                {
                    return new ReadOnlySpan<byte>(data, checked((int)length)).ToArray();
                }
                finally
                {
                    Runtime.Api->PackReleaseBlob(blob);
                }
            }
        }
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static byte[] ReadAllBytes(string path)
    {
        return TryReadAllBytes(path) ?? throw new FileNotFoundException("File not found in packs", path);
    }

    /// <summary>Reads a text file (default encoding: <see cref="Native.Encoding"/>).</summary>
    public static string ReadAllText(string path, Encoding? encoding = null)
    {
        return (encoding ?? Native.Encoding).GetString(ReadAllBytes(path));
    }

    public static Stream OpenRead(string path) => new MemoryStream(ReadAllBytes(path), writable: false);

    /// <summary>Makes every lookup of <paramref name="from"/> load <paramref name="to"/> instead.</summary>
    public static void Redirect(string from, string to)
    {
        byte[] a = NativeText.ToNative(from);
        byte[] b = NativeText.ToNative(to);
        fixed (byte* pa = a)
        fixed (byte* pb = b)
            EnsureManager(Runtime.Api->PackSetRedirect(pa, pb));
    }

    public static bool RemoveRedirect(string from)
    {
        byte[] a = NativeText.ToNative(from);
        fixed (byte* pa = a)
            return Runtime.Api->PackRemoveRedirect(pa) != 0;
    }

    /// <summary>Serves <paramref name="data"/> for <paramref name="path"/> (replacing or adding a file).</summary>
    public static void Override(string path, ReadOnlySpan<byte> data)
    {
        byte[] name = NativeText.ToNative(path);
        fixed (byte* p = name)
        fixed (byte* d = data)
            EnsureManager(Runtime.Api->PackSetOverride(p, d, data.Length));
    }

    public static bool RemoveOverride(string path)
    {
        byte[] name = NativeText.ToNative(path);
        fixed (byte* p = name)
            return Runtime.Api->PackRemoveOverride(p) != 0;
    }

    /// <summary>The normalized path a lookup ends up at after redirects.</summary>
    public static string ResolveName(string path)
    {
        byte[] name = NativeText.ToNative(path);
        const int capacity = 1024;
        byte* buffer = stackalloc byte[capacity];
        int length;
        fixed (byte* p = name)
            length = Runtime.Api->PackResolveName(p, buffer, capacity);
        return NativeText.FromNative(buffer, Math.Min(length, capacity - 1));
    }

    /// <summary>Files whose normalized path starts with <paramref name="prefix"/>, across all packs.</summary>
    public static IReadOnlyList<PackEntry> List(string prefix = "")
    {
        List<PackEntry> entries = new();
        System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(entries);
        try
        {
            byte[] name = NativeText.ToNative(prefix);
            fixed (byte* p = name)
                Runtime.Api->PackEnumerate(p, &OnEntry, System.Runtime.InteropServices.GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return entries;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void OnEntry(nint context, byte* name, long size)
    {
        var entries = (List<PackEntry>)System.Runtime.InteropServices.GCHandle.FromIntPtr(context).Target!;
        entries.Add(new PackEntry(NativeText.FromNative(name)!, size));
    }

    /// <summary>Registers a resolver. Dispose the result to remove it.</summary>
    public static IDisposable AddResolver(IPackResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (s_lock)
        {
            if (s_resolvers.IsEmpty)
                EnsureManager(Runtime.Api->PackEnableResolveHook(1));
            s_resolvers = s_resolvers.Add(resolver);
        }
        return new Registration(resolver);
    }

    /// <summary>Registers a resolver from a function returning the bytes (or null).</summary>
    public static IDisposable AddResolver(Func<string, byte[]?> read) => AddResolver(new FuncResolver(read));

    internal static void ClearResolvers()
    {
        lock (s_lock)
            s_resolvers = ImmutableArray<IPackResolver>.Empty;
    }

    // Called by the native resolve hook (any thread).
    internal static bool Resolve(string path, bool wantData, out byte[]? data)
    {
        data = null;
        foreach (IPackResolver resolver in s_resolvers)
        {
            try
            {
                if (!wantData)
                {
                    if (resolver.Exists(path))
                        return true;
                    continue;
                }
                data = resolver.Read(path);
                if (data != null)
                    return true;
            }
            catch (Exception exception)
            {
                Native.Log.Error($"Pack resolver {resolver.GetType().Name} failed for '{path}': {exception}");
            }
        }
        return false;
    }

    private static void EnsureManager(int ok)
    {
        if (ok == 0)
            throw new InvalidOperationException("The engine has no pack manager (CEterPackManager) yet.");
    }

    private sealed class Registration(IPackResolver resolver) : IDisposable
    {
        private IPackResolver? _resolver = resolver;

        public void Dispose()
        {
            lock (s_lock)
            {
                if (_resolver == null)
                    return;
                s_resolvers = s_resolvers.Remove(_resolver);
                _resolver = null;
                if (s_resolvers.IsEmpty && Runtime.IsHosted)
                    Runtime.Api->PackEnableResolveHook(0);
            }
        }
    }

    private sealed class FuncResolver(Func<string, byte[]?> read) : IPackResolver
    {
        public bool Exists(string path) => read(path) != null;
        public byte[]? Read(string path) => read(path);
    }
}

/// <summary>Serves files from a directory on disk (mods, development overrides).</summary>
public sealed class DirectoryPackResolver(string root, string prefix = "") : IPackResolver
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
    private readonly string _prefix = prefix.Replace('\\', '/').ToLowerInvariant();

    public bool Exists(string path) => Locate(path) != null;

    public byte[]? Read(string path)
    {
        string? file = Locate(path);
        return file != null ? File.ReadAllBytes(file) : null;
    }

    private string? Locate(string path)
    {
        if (!path.StartsWith(_prefix, StringComparison.Ordinal))
            return null;
        string relative = path[_prefix.Length..];
        string full = Path.GetFullPath(Path.Combine(_root, relative));
        // Never escape the root through "..".
        if (!full.StartsWith(_root, StringComparison.Ordinal))
            return null;
        return File.Exists(full) ? full : null;
    }
}
