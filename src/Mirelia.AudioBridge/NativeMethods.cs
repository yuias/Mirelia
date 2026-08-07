using System.Runtime.InteropServices;

namespace Mirelia.AudioBridge;

/// <summary>
/// Raw P/Invoke surface over <c>audio_core.h</c>. Layouts and signatures
/// mirror the header exactly; the header is authoritative.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>Logical library name resolved by <see cref="NativeLibraryResolver"/>.</summary>
    internal const string LibraryName = "audio_core_rs";

    /// <summary>The ABI version this binding was written against.</summary>
    internal const uint AbiVersion = 1;

    static NativeMethods()
    {
        NativeLibraryResolver.Register(typeof(NativeMethods).Assembly);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KnEngineConfig
    {
        public uint AbiVersion;
        public uint TargetLatencyMs;
        public uint DecodeAheadMs;
        public int AllowResample;
        public int DitherToInt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KnPosition
    {
        public int State;
        public ulong UserToken;
        public long Frame;
        public long TotalFrames;
        public uint SampleRate;
        public ulong HostTimeNs;
        public float BufferFill;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KnEvent
    {
        public int Type;
        public ulong UserToken;
        public int Code;
        public ulong HostTimeNs;
    }

    [LibraryImport(LibraryName)]
    internal static partial KnResult kn_engine_create(in KnEngineConfig cfg, out nint engine);

    [LibraryImport(LibraryName)]
    internal static partial void kn_engine_destroy(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial nint kn_engine_last_error(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial KnResult kn_get_position(nint engine, out KnPosition position);

    [LibraryImport(LibraryName)]
    internal static partial nuint kn_engine_poll_event(
        nint engine,
        [Out] KnEvent[] events,
        nuint capacity);

    /// <summary>
    /// Reads the thread-local last-error string left by the most recent
    /// failing native call.
    /// </summary>
    internal static string LastError(nint engine)
    {
        return Marshal.PtrToStringUTF8(kn_engine_last_error(engine)) ?? string.Empty;
    }
}
