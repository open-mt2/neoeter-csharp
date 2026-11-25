using System.Reflection;
using Neoeter.Scripting.Packs;

namespace Neoeter.Scripting;

/// <summary>
/// Marks the type whose <c>public static Main()</c> (optionally taking
/// <c>string[] args</c>, optionally returning <c>int</c>) the engine runs when
/// it starts the assembly — the C# counterpart of <c>system.py</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ScriptEntryPointAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
}

/// <summary>Loads script assemblies from the packs and runs their entry point.</summary>
public static class ScriptHost
{
    private static readonly Dictionary<string, PackAssemblyLoadContext> s_contexts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Assembly> s_assemblies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Load context for a pack directory (one per directory).</summary>
    public static PackAssemblyLoadContext ContextFor(string directory)
    {
        directory = directory.Replace('\\', '/').TrimEnd('/');
        if (!s_contexts.TryGetValue(directory, out PackAssemblyLoadContext? context))
        {
            context = new PackAssemblyLoadContext(directory);
            s_contexts[directory] = context;
        }
        return context;
    }

    /// <summary>Loads (once) an assembly from a pack path, e.g. "scripts/OpenMT2.Game.dll".</summary>
    public static Assembly LoadAssembly(string path)
    {
        string key = path.Replace('\\', '/');
        if (s_assemblies.TryGetValue(key, out Assembly? loaded))
            return loaded;

        string directory = Path.GetDirectoryName(key)?.Replace('\\', '/') ?? string.Empty;
        Assembly assembly = ContextFor(directory).LoadFromPack(key);
        s_assemblies[key] = assembly;
        return assembly;
    }

    /// <summary>Runs the assembly's entry point and returns its exit code.</summary>
    public static int Run(Assembly assembly, string[] args)
    {
        MethodInfo entry = FindEntryPoint(assembly)
            ?? throw new EntryPointNotFoundException($"{assembly.GetName().Name} has no [assembly: ScriptEntryPoint(typeof(...))] with a static Main");

        object?[] parameters = entry.GetParameters().Length == 1 ? [args] : [];
        object? result;
        try
        {
            result = entry.Invoke(null, parameters);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        return result is int code ? code : 0;
    }

    private static MethodInfo? FindEntryPoint(Assembly assembly)
    {
        Type? type = assembly.GetCustomAttribute<ScriptEntryPointAttribute>()?.Type;
        if (type == null)
            return assembly.EntryPoint;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        return type.GetMethods(flags).FirstOrDefault(m => m.Name == "Main" && IsEntrySignature(m));
    }

    private static bool IsEntrySignature(MethodInfo method)
    {
        ParameterInfo[] parameters = method.GetParameters();
        bool parametersOk = parameters.Length == 0 || (parameters.Length == 1 && parameters[0].ParameterType == typeof(string[]));
        return parametersOk && (method.ReturnType == typeof(void) || method.ReturnType == typeof(int));
    }

    internal static void Reset()
    {
        s_assemblies.Clear();
        s_contexts.Clear();
    }
}
