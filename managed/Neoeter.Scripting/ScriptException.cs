namespace Neoeter.Scripting;

/// <summary>Error categories raised by native modules (ScriptErrorType).</summary>
public enum ScriptErrorType
{
    RuntimeError = 0,
    TypeError = 1,
    ValueError = 2,
    IndexError = 3,
    KeyError = 4,
    OverflowError = 5,
    AttributeError = 6,
    SystemError = 7,
}

/// <summary>
/// Thrown when a native module function fails (the equivalent of the Python
/// exception raised by the same call).
/// </summary>
public sealed class ScriptException : Exception
{
    public ScriptException(ScriptErrorType errorType, string message)
        : base($"{errorType}: {message}")
    {
        ErrorType = errorType;
        NativeMessage = message;
    }

    public ScriptErrorType ErrorType { get; }

    /// <summary>The message set by the native function, without the error type.</summary>
    public string NativeMessage { get; }
}
