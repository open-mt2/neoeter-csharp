using Neoeter.Scripting.Interop;

namespace Neoeter.Scripting;

/// <summary>
/// An object owned by another scripting runtime (e.g. a Python instance
/// passed through the engine). Methods can still be called on it.
/// </summary>
public sealed unsafe class ForeignObject
{
    private readonly nint _value;

    internal ForeignObject(nint value)
    {
        _value = value;
        Runtime.Api->IncRef(value);
    }

    ~ForeignObject()
    {
        nint value = _value;
        Runtime.Defer(() => Runtime.Api->DecRef(value));
    }

    // New native reference.
    internal nint ToNative()
    {
        Runtime.Api->IncRef(_value);
        return _value;
    }

    public bool HasMethod(string name)
    {
        byte[] bytes = NativeText.ToNative(name);
        fixed (byte* p = bytes)
            return Runtime.Api->HasAttr(_value, p) != 0;
    }

    public object? Call(string name, params object?[] args)
    {
        nint tuple = Converter.BuildTuple(args);
        nint result;
        int status;
        byte[] bytes = NativeText.ToNative(name);
        fixed (byte* p = bytes)
            status = Runtime.Api->CallMethod(_value, p, tuple, &result);
        Runtime.Api->DecRef(tuple);

        if (status == (int)CallStatus.NoMethod)
            throw new MissingMethodException(ToString(), name);
        if (status != (int)CallStatus.Ok || result == 0)
            throw new ScriptException(ScriptErrorType.RuntimeError, $"{ToString()}.{name} raised (see the script log)");
        return Converter.TakeFromNative(result);
    }

    public override string ToString()
    {
        const int capacity = 256;
        byte* buffer = stackalloc byte[capacity];
        int length = Runtime.Api->Repr(_value, buffer, capacity);
        return NativeText.FromNative(buffer, Math.Min(length, capacity - 1));
    }
}
