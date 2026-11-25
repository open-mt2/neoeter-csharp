using System.Collections.Concurrent;
using System.Reflection;

namespace Neoeter.Scripting.Interop;

// Resolves engine callbacks (Script_CallMethod(obj, "OnUpdate", args)) to
// public instance methods. Exact name first, then a case-insensitive match;
// among overloads, the one accepting the argument count wins (optional and
// `params` parameters are honoured).
internal static class Dispatcher
{
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo[]> s_methods = new();

    private static MethodInfo[] Candidates(Type type, string name)
    {
        return s_methods.GetOrAdd((type, name), static key =>
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            MethodInfo[] all = key.Item1.GetMethods(flags);
            MethodInfo[] exact = all.Where(m => m.Name == key.Item2 && !m.IsGenericMethodDefinition).ToArray();
            if (exact.Length > 0)
                return exact;
            return all.Where(m => string.Equals(m.Name, key.Item2, StringComparison.OrdinalIgnoreCase) && !m.IsGenericMethodDefinition).ToArray();
        });
    }

    public static bool HasMethod(object target, string name) => Candidates(target.GetType(), name).Length > 0;

    public static bool TryInvoke(object target, string name, object?[] args, out object? result)
    {
        result = null;
        MethodInfo[] candidates = Candidates(target.GetType(), name);
        if (candidates.Length == 0)
            return false;

        MethodInfo? method = null;
        foreach (MethodInfo candidate in candidates.OrderByDescending(m => m.GetParameters().Length == args.Length))
        {
            if (Accepts(candidate.GetParameters(), args.Length))
            {
                method = candidate;
                break;
            }
        }
        if (method == null)
            throw new TargetParameterCountException($"{target.GetType().Name}.{name} does not accept {args.Length} argument(s)");

        result = method.Invoke(target, BindArguments(method.GetParameters(), args));
        return true;
    }

    private static bool Accepts(ParameterInfo[] parameters, int count)
    {
        if (parameters.Length > 0 && IsParams(parameters[^1]))
            return count >= parameters.Length - 1;
        int required = parameters.Count(p => !p.IsOptional);
        return count >= required && count <= parameters.Length;
    }

    private static bool IsParams(ParameterInfo parameter) => parameter.IsDefined(typeof(ParamArrayAttribute), false);

    private static object?[] BindArguments(ParameterInfo[] parameters, object?[] args)
    {
        object?[] bound = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo parameter = parameters[i];
            if (i == parameters.Length - 1 && IsParams(parameter))
            {
                Type element = parameter.ParameterType.GetElementType()!;
                int rest = Math.Max(0, args.Length - i);
                Array values = Array.CreateInstance(element, rest);
                for (int j = 0; j < rest; j++)
                    values.SetValue(Converter.ConvertTo(args[i + j], element), j);
                bound[i] = values;
            }
            else if (i < args.Length)
            {
                bound[i] = Converter.ConvertTo(args[i], parameter.ParameterType);
            }
            else
            {
                bound[i] = parameter.DefaultValue is DBNull ? null : parameter.DefaultValue;
            }
        }
        return bound;
    }
}
