using System.Reflection;
using Mirelia.AudioBridge;

return args switch
{
    ["--version"] or ["-v"] => PrintVersion(),
    ["native-smoke"] => NativeSmoke(),
    [] or ["--help"] or ["-h"] => PrintHelp(0),
    _ => PrintHelp(2),
};

static int PrintVersion()
{
    string version = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
    Console.WriteLine($"mirelia-cli {version}");
    return 0;
}

static int NativeSmoke()
{
    // Proves the native backend loads, negotiates ABI v1, and answers a call.
    try
    {
        using var engine = AudioEngine.Create();
        PlaybackPosition position = engine.GetPosition();
        Console.WriteLine($"native-smoke: ok (state={position.State}, host_time_ns={position.HostTimeNs})");
        return 0;
    }
    catch (Exception ex) when (ex is AudioEngineException or DllNotFoundException)
    {
        Console.Error.WriteLine($"native-smoke: FAILED: {ex.Message}");
        return 1;
    }
}

static int PrintHelp(int exitCode)
{
    Console.WriteLine(
        """
        mirelia-cli — headless Mirelia harness

        Usage:
          mirelia-cli --version      Print the version and exit.
          mirelia-cli native-smoke   Load the native audio backend and negotiate the ABI.
          mirelia-cli --help         Show this help.

        Milestone M1 adds: scan <dir>, query '<dsl>'.
        """);
    return exitCode;
}
