using System.Runtime.InteropServices;
using System.Text;

namespace Neoeter.Scripting.Interop;

internal static unsafe class NativeText
{
    // NUL-terminated bytes in the script encoding.
    public static byte[] ToNative(string value)
    {
        Encoding encoding = Native.Encoding;
        byte[] bytes = new byte[encoding.GetByteCount(value) + 1];
        encoding.GetBytes(value, 0, value.Length, bytes, 0);
        return bytes;
    }

    public static string? FromNative(byte* value)
    {
        if (value == null)
            return null;
        return Native.Encoding.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value));
    }

    public static string FromNative(byte* value, int length)
    {
        return length <= 0 ? string.Empty : Native.Encoding.GetString(value, length);
    }

    public static int CopyOut(string text, byte* buffer, int capacity)
    {
        byte[] bytes = Native.Encoding.GetBytes(text);
        if (buffer != null && capacity > 0)
        {
            int n = Math.Min(bytes.Length, capacity - 1);
            bytes.AsSpan(0, n).CopyTo(new Span<byte>(buffer, n));
            buffer[n] = 0;
        }
        return bytes.Length;
    }
}
