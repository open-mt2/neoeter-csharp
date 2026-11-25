using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Neoeter.Scripting.Interop;

// Script values <-> C# values.
//
//   None -> null           Bool -> bool        Int -> long     Float -> double
//   String -> string (Native.Encoding)         Tuple -> object?[]
//   List -> List<object?>  Dict -> Dictionary<object, object?>
//   Module -> NativeModule Object -> the managed object, or a ForeignObject
//
// In the other direction any integral type, enum, nint and char become Int,
// byte[] becomes a String with the raw bytes, ITuple/arrays become Tuples,
// IDictionary becomes a Dict, other IEnumerables a List, and every other
// object is passed by reference (the engine can call its methods back).
internal static unsafe class Converter
{
    public static nint ToNative(object? value)
    {
        NativeApi* api = Runtime.Api;
        switch (value)
        {
            case null:
                return api->NewNone();
            case bool b:
                return api->NewBool(b ? 1 : 0);
            case int or long or short or sbyte or byte or ushort or uint:
                return api->NewInt(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong ul:
                return api->NewInt(unchecked((long)ul));
            case nint ni:
                return api->NewInt(ni);
            case nuint nu:
                return api->NewInt(unchecked((long)nu));
            case char c:
                return api->NewInt(c);
            case Enum e:
                return api->NewInt(Convert.ToInt64(e, CultureInfo.InvariantCulture));
            case float f:
                return api->NewFloat(f);
            case double d:
                return api->NewFloat(d);
            case decimal m:
                return api->NewFloat((double)m);
            case string s:
                return NewString(Native.Encoding.GetBytes(s));
            case byte[] bytes:
                return NewString(bytes);
            case NativeModule module:
                return api->ModuleObject(module.Handle);
            case ForeignObject foreign:
                return foreign.ToNative();
            case object?[] array:
                return BuildTuple(array);
            case ITuple tuple:
            {
                object?[] items = new object?[tuple.Length];
                for (int i = 0; i < items.Length; i++)
                    items[i] = tuple[i];
                return BuildTuple(items);
            }
            case IDictionary dictionary:
            {
                nint dict = api->NewDict();
                foreach (DictionaryEntry entry in dictionary)
                    api->DictSet(dict, ToNative(entry.Key), ToNative(entry.Value));
                return dict;
            }
            case Array array:
            {
                object?[] items = new object?[array.Length];
                array.CopyTo(items, 0);
                return BuildTuple(items);
            }
            case ICollection collection:
            {
                List<object?> items = collection.Cast<object?>().ToList();
                nint list = api->NewList(items.Count);
                for (int i = 0; i < items.Count; i++)
                    api->SetItem(list, i, ToNative(items[i]));
                return list;
            }
            default:
                return ManagedObjects.ToNative(value);
        }
    }

    private static nint NewString(byte[] bytes)
    {
        fixed (byte* p = bytes)
            return Runtime.Api->NewString(p, bytes.Length);
    }

    public static nint BuildTuple(object?[]? items)
    {
        NativeApi* api = Runtime.Api;
        int count = items?.Length ?? 0;
        nint tuple = api->NewTuple(count);
        for (int i = 0; i < count; i++)
            api->SetItem(tuple, i, ToNative(items![i]));
        return tuple;
    }

    // Converts a borrowed native value.
    public static object? FromNative(nint value)
    {
        if (value == 0)
            return null;

        NativeApi* api = Runtime.Api;
        switch ((NativeValueType)api->TypeOf(value))
        {
            case NativeValueType.None:
                return null;
            case NativeValueType.Bool:
                return api->GetBool(value) != 0;
            case NativeValueType.Int:
                return api->GetInt(value);
            case NativeValueType.Float:
                return api->GetFloat(value);
            case NativeValueType.String:
            {
                int length;
                byte* data = api->GetString(value, &length);
                return NativeText.FromNative(data, length);
            }
            case NativeValueType.Tuple:
            {
                int count = api->Size_(value);
                object?[] items = new object?[count];
                for (int i = 0; i < count; i++)
                    items[i] = FromNative(api->GetItem(value, i));
                return items;
            }
            case NativeValueType.List:
            {
                int count = api->Size_(value);
                List<object?> items = new(count);
                for (int i = 0; i < count; i++)
                    items.Add(FromNative(api->GetItem(value, i)));
                return items;
            }
            case NativeValueType.Dict:
            {
                int count = api->Size_(value);
                Dictionary<object, object?> items = new(count);
                for (int i = 0; i < count; i++)
                {
                    nint key, item;
                    if (api->DictEntry(value, i, &key, &item) != 0)
                        items[FromNative(key) ?? NoneKey.Instance] = FromNative(item);
                }
                return items;
            }
            case NativeValueType.Module:
                return NativeModule.FromModuleName(NativeText.FromNative(api->ModuleNameOf(value)));
            case NativeValueType.Object:
            {
                nint handle = api->GetManaged(value);
                return handle != 0 ? ManagedObjects.FromHandle(handle) : new ForeignObject(value);
            }
            default:
                return null;
        }
    }

    // Converts and releases a new native reference.
    public static object? TakeFromNative(nint value)
    {
        try
        {
            return FromNative(value);
        }
        finally
        {
            if (value != 0)
                Runtime.Api->DecRef(value);
        }
    }

    public static object?[] ArgsFromTuple(nint tuple)
    {
        return tuple == 0 ? [] : FromNative(tuple) as object?[] ?? [FromNative(tuple)];
    }

    public static T ConvertTo<T>(object? value) => (T)ConvertTo(value, typeof(T))!;

    // Coerces a script value to a C# parameter/return type.
    public static object? ConvertTo(object? value, Type type)
    {
        if (type == typeof(object) || type == typeof(void))
            return value;

        Type? nullable = Nullable.GetUnderlyingType(type);
        if (value == null)
        {
            if (!type.IsValueType || nullable != null)
                return null;
            throw new InvalidCastException($"None cannot be converted to {type.Name}");
        }

        Type target = nullable ?? type;
        if (target.IsInstanceOfType(value))
            return value;

        if (target.IsEnum)
            return Enum.ToObject(target, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        if (target == typeof(nint))
            return (nint)Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (target == typeof(nuint))
            return unchecked((nuint)Convert.ToUInt64(value, CultureInfo.InvariantCulture));
        if (target == typeof(string))
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        if (target == typeof(byte[]) && value is string text)
            return Native.Encoding.GetBytes(text);
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
        {
            // Script integers wrap like the C casts in the bindings (e.g. DWORD).
            if (value is long l && target == typeof(uint))
                return unchecked((uint)l);
            if (value is long l2 && target == typeof(ulong))
                return unchecked((ulong)l2);
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }

        if (target.IsArray && value is IEnumerable sequence)
        {
            Type element = target.GetElementType()!;
            List<object?> items = sequence.Cast<object?>().ToList();
            Array result = Array.CreateInstance(element, items.Count);
            for (int i = 0; i < items.Count; i++)
                result.SetValue(ConvertTo(items[i], element), i);
            return result;
        }

        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>) && value is IEnumerable list)
        {
            Type element = target.GetGenericArguments()[0];
            IList result = (IList)Activator.CreateInstance(target)!;
            foreach (object? item in list)
                result.Add(ConvertTo(item, element));
            return result;
        }

        throw new InvalidCastException($"{value.GetType().Name} cannot be converted to {type.Name}");
    }

    // Dictionary keys cannot be null; None keys (rare) map to this marker.
    internal sealed class NoneKey
    {
        public static readonly NoneKey Instance = new();
        public override string ToString() => "None";
    }
}
