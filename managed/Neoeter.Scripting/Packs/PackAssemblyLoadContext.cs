using System.Reflection;
using System.Runtime.Loader;

namespace Neoeter.Scripting.Packs;

/// <summary>
/// Loads script assemblies (and their private dependencies) from the packs,
/// so a client can ship and patch its C# game logic like any other asset.
/// Dependencies are looked up next to the requesting assembly
/// (<c>&lt;directory&gt;/&lt;name&gt;.dll</c>); Neoeter.Scripting and framework
/// assemblies always come from the default context so types are shared.
/// </summary>
public sealed class PackAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string _directory;

    /// <param name="directory">Pack directory holding the assemblies, e.g. "scripts".</param>
    /// <param name="isCollectible">Allow <see cref="AssemblyLoadContext.Unload"/> (hot reload).</param>
    public PackAssemblyLoadContext(string directory, bool isCollectible = false)
        : base($"pack:{directory}", isCollectible)
    {
        _directory = directory.Replace('\\', '/').TrimEnd('/');
    }

    public string Directory => _directory;

    /// <summary>Loads an assembly from a pack path (falls back to the file system).</summary>
    public Assembly LoadFromPack(string path)
    {
        byte[] image = Pack.ReadAllBytes(path);
        string pdbPath = Path.ChangeExtension(path, ".pdb");
        byte[]? symbols = Pack.Exists(pdbPath) ? Pack.TryReadAllBytes(pdbPath) : null;

        using MemoryStream assembly = new(image, writable: false);
        using MemoryStream? pdb = symbols != null ? new MemoryStream(symbols, writable: false) : null;
        return LoadFromStream(assembly, pdb);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == typeof(Native).Assembly.GetName().Name)
            return typeof(Native).Assembly;

        string candidate = _directory.Length == 0 ? $"{assemblyName.Name}.dll" : $"{_directory}/{assemblyName.Name}.dll";
        if (Pack.Exists(candidate))
            return LoadFromPack(candidate);

        // Framework and shared assemblies.
        return null;
    }
}
