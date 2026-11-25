using System.Runtime.InteropServices;

namespace Neoeter.Scripting.Interop;

// Mirrors NeoeterNativeApi (include/Neoeter/DotNet/NativeAbi.h). Keep the
// field order identical and bump AbiVersion together with the C header.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeApi
{
    public const uint AbiVersion = 1;

    public uint Version;
    public uint Size;

    // Values
    public delegate* unmanaged<nint, int> TypeOf;
    public delegate* unmanaged<nint, void> IncRef;
    public delegate* unmanaged<nint, void> DecRef;
    public delegate* unmanaged<nint> NewNone;
    public delegate* unmanaged<int, nint> NewBool;
    public delegate* unmanaged<long, nint> NewInt;
    public delegate* unmanaged<double, nint> NewFloat;
    public delegate* unmanaged<byte*, int, nint> NewString;
    public delegate* unmanaged<int, nint> NewTuple;
    public delegate* unmanaged<int, nint> NewList;
    public delegate* unmanaged<nint> NewDict;
    public delegate* unmanaged<nint, int, nint, int> SetItem;
    public delegate* unmanaged<nint, nint, nint, int> DictSet;
    public delegate* unmanaged<nint, int> GetBool;
    public delegate* unmanaged<nint, long> GetInt;
    public delegate* unmanaged<nint, double> GetFloat;
    public delegate* unmanaged<nint, int*, byte*> GetString;
    public delegate* unmanaged<nint, int> Size_;
    public delegate* unmanaged<nint, int, nint> GetItem;
    public delegate* unmanaged<nint, int, nint*, nint*, int> DictEntry;
    public delegate* unmanaged<nint, byte*> ModuleNameOf;

    // Managed / foreign objects
    public delegate* unmanaged<nint, nint> NewManaged;
    public delegate* unmanaged<nint, nint> GetManaged;
    public delegate* unmanaged<nint, void> FreeManaged;
    public delegate* unmanaged<nint, byte*, int, int> Repr;
    public delegate* unmanaged<nint, byte*, nint, nint*, int> CallMethod;
    public delegate* unmanaged<nint, byte*, int> HasAttr;

    // Errors
    public delegate* unmanaged<int, byte*, int, void> ErrSet;
    public delegate* unmanaged<int*, byte*, int, int> ErrFetch;
    public delegate* unmanaged<void> ErrClear;

    // Native modules
    public delegate* unmanaged<byte*, nint> FindModule;
    public delegate* unmanaged<nint, nint> ModuleObject;
    public delegate* unmanaged<int> ModuleCount;
    public delegate* unmanaged<int, nint> ModuleAt;
    public delegate* unmanaged<nint, byte*> ModuleName;
    public delegate* unmanaged<nint, byte*, nint> FindMethod;
    public delegate* unmanaged<nint, int> MethodCount;
    public delegate* unmanaged<nint, int, nint> MethodAt;
    public delegate* unmanaged<nint, byte*> MethodName;
    public delegate* unmanaged<nint, nint, nint> Invoke;
    public delegate* unmanaged<nint, byte*, nint> GetConstant;
    public delegate* unmanaged<nint, int> ConstantCount;
    public delegate* unmanaged<nint, int, byte*> ConstantName;
    public delegate* unmanaged<nint, int, nint> ConstantAt;

    // Diagnostics
    public delegate* unmanaged<int, byte*, int, void> Log;
    public delegate* unmanaged<byte*, int, void> ReportException;

    // Packs
    public delegate* unmanaged<byte*, int> PackExists;
    public delegate* unmanaged<byte*, byte**, long*, nint> PackRead;
    public delegate* unmanaged<byte*, long, nint> PackCreateBlob;
    public delegate* unmanaged<nint, void> PackReleaseBlob;
    public delegate* unmanaged<byte*, byte*, int> PackSetRedirect;
    public delegate* unmanaged<byte*, int> PackRemoveRedirect;
    public delegate* unmanaged<byte*, byte*, long, int> PackSetOverride;
    public delegate* unmanaged<byte*, int> PackRemoveOverride;
    public delegate* unmanaged<byte*, byte*, int, int> PackResolveName;
    public delegate* unmanaged<int, int> PackEnableResolveHook;
    public delegate* unmanaged<byte*, delegate* unmanaged<nint, byte*, long, void>, nint, int> PackEnumerate;
}

// Mirrors NeoeterManagedApi.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ManagedApi
{
    public uint Version;
    public uint Size;

    public delegate* unmanaged<nint, byte*, int> HasAttr;
    public delegate* unmanaged<nint, byte*, nint, nint*, int> CallMethod;
    public delegate* unmanaged<nint, byte*, int, int> Repr;
    public delegate* unmanaged<nint, int, void> SetStrong;
    public delegate* unmanaged<byte*, byte**, int, int> RunEntry;
    public delegate* unmanaged<byte*, int> LoadAssembly;
    public delegate* unmanaged<byte*, nint*, int> ResolvePack;
    public delegate* unmanaged<void> Pump;
    public delegate* unmanaged<void> Shutdown;
}

internal enum NativeValueType
{
    None = 0,
    Bool = 1,
    Int = 2,
    Float = 3,
    String = 4,
    Tuple = 5,
    List = 6,
    Dict = 7,
    Module = 8,
    Object = 9,
}

internal enum CallStatus
{
    Ok = 0,
    NoMethod = 1,
    Error = 2,
}
