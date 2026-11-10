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
    internal struct MireliaEngineConfig
    {
        public uint AbiVersion;
        public uint TargetLatencyMs;
        public uint DecodeAheadMs;
        public int AllowResample;
        public int DitherToInt;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MireliaPosition
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
    internal struct MireliaEvent
    {
        public int Type;
        public ulong UserToken;
        public int Code;
        public ulong HostTimeNs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MireliaStreamFormat
    {
        public uint SampleRate;
        public ushort Channels;
        public int Format;
        public uint ChannelMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MireliaLoadParams
    {
        public nint Path;
        public long StartFrame;
        public long EndFrame;
        public uint EncoderDelay;
        public uint EncoderPadding;
        public float GainDb;
        public float Peak;
        public ulong UserToken;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MireliaProbeResult
    {
        public uint SampleRate;
        public ushort Channels;
        public ushort BitDepth;
        public long TotalFrames;
        public uint EncoderDelay;
        public uint EncoderPadding;
        public uint BitrateKbps;
        public fixed byte Codec[16];
    }

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaEngineCreate(
        in MireliaEngineConfig cfg,
        out nint engine);

    [LibraryImport(LibraryName)]
    internal static partial void mireliaEngineDestroy(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial nint mireliaEngineLastError(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaGetPosition(
        nint engine,
        out MireliaPosition position);

    [LibraryImport(LibraryName)]
    internal static partial nuint mireliaEnginePollEvent(
        nint engine,
        [Out] MireliaEvent[] events,
        nuint capacity);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial MireliaResult mireliaEngineOpenDevice(
        nint engine,
        string? deviceId,
        int mode,
        nint desired,
        out MireliaStreamFormat actual);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaEngineCloseDevice(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaLoad(
        nint engine,
        int slot,
        in MireliaLoadParams p);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaClear(nint engine, int slot);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaPlay(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaPause(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaStop(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaSeek(nint engine, long frame);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaSkipToNext(nint engine);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaSetVolume(nint engine, float gain);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial MireliaResult mireliaProbe(
        nint engine, string path, out MireliaProbeResult result);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MireliaDeviceInfo
    {
        public nint Id;
        public nint Name;
        public nint Backend;
        public int IsDefault;
        public uint NativeRate;
        public ushort NativeChannels;
        public uint SupportedRateMask;
        public uint SupportedFormatMask;
        public int SupportsExclusive;
        public uint MinPeriodFrames;
        public uint DefaultPeriodFrames;
    }

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaDevicesEnumerate(
        nint engine, out nint devices, out nuint count);

    [LibraryImport(LibraryName)]
    internal static partial void mireliaDevicesFree(nint engine);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MireliaDspInfo
    {
        public nint Id;
        public nint Name;
        public int Kind;
    }

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial MireliaResult mireliaRegisterDspLibrary(nint engine, string? dllPath);

    [LibraryImport(LibraryName)]
    internal static partial MireliaResult mireliaDspEnumerate(
        nint engine, out nint infos, out nuint count);

    [LibraryImport(LibraryName)]
    internal static partial void mireliaDspFree(nint engine);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial MireliaResult mireliaEngineSetResampler(nint engine, string? pluginId);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial MireliaResult mireliaScanLoudness(
        nint engine, string path, out float gainDb, out float peak);

    /// <summary>
    /// Reads the thread-local last-error string left by the most recent
    /// failing native call.
    /// </summary>
    internal static string LastError(nint engine)
    {
        return Marshal.PtrToStringUTF8(mireliaEngineLastError(engine)) ?? string.Empty;
    }
}
