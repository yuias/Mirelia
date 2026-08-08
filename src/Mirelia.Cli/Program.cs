using System.Reflection;
using Mirelia.AudioBridge;
using Mirelia.Library.Database;
using Mirelia.Library.Querying;
using Mirelia.Library.Scanning;
using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;

return args switch
{
    ["--version"] or ["-v"] => PrintVersion(),
    ["native-smoke"] => NativeSmoke(),
    ["scan", var dir, .. var rest] => Scan(dir, ParseDbOption(rest)),
    ["query", var dsl, .. var rest] => RunQuery(dsl, ParseDbOption(rest)),
    ["play", var file] => Play(file),
    [] or ["--help"] or ["-h"] => PrintHelp(0),
    _ => PrintHelp(2),
};

static string? ParseDbOption(string[] rest) => rest switch
{
    ["--db", var path] => path,
    [] => null,
    _ => throw new ArgumentException($"Unknown arguments: {string.Join(' ', rest)}"),
};

static string DefaultDbPath()
{
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Mirelia",
        "library.db");
}

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

static int Scan(string dir, string? dbPath)
{
    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"scan: directory not found: {dir}");
        return 1;
    }

    string db = dbPath ?? DefaultDbPath();
    Console.WriteLine($"scanning {dir} into {db}");

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    using var database = LibraryDatabase.Open(db);
    var scanner = new LibraryScanner(database);
    int lastReported = 0;
    ScanResult result = scanner.Scan(dir, new Progress<int>(n =>
    {
        if (n - lastReported >= 500)
        {
            lastReported = n;
            Console.WriteLine($"  {n} files...");
        }
    }));
    stopwatch.Stop();

    Console.WriteLine(
        $"done in {stopwatch.Elapsed.TotalSeconds:F1}s: " +
        $"{result.Added} added, {result.Updated} updated, " +
        $"{result.Unchanged} unchanged, {result.Skipped} skipped");
    return 0;
}

static int RunQuery(string dsl, string? dbPath)
{
    string db = dbPath ?? DefaultDbPath();
    if (!File.Exists(db))
    {
        Console.Error.WriteLine($"query: database not found: {db} (run scan first)");
        return 1;
    }

    CompiledQuery compiled;
    try
    {
        compiled = SqlCompiler.Compile(QueryParser.Parse(dsl));
    }
    catch (Exception ex) when (ex is QueryParseException or QueryCompileException)
    {
        Console.Error.WriteLine($"query: {ex.Message}");
        return 1;
    }

    using var database = LibraryDatabase.Open(db);
    List<long> ids = TrackQueryRunner.Run(database, compiled);
    foreach (TrackSummary row in TrackQueryRunner.Hydrate(database, ids))
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(row.DurationMs);
        Console.WriteLine(
            $"{row.Artist ?? "?"} - {row.Title ?? "?"} [{row.Album ?? "?"}] " +
            $"({(int)duration.TotalMinutes}:{duration.Seconds:D2})");
    }

    Console.WriteLine($"{ids.Count} track(s)");
    return 0;
}

static int Play(string file)
{
    if (!File.Exists(file))
    {
        Console.Error.WriteLine($"play: file not found: {file}");
        return 1;
    }

    string path = Path.GetFullPath(file);
    try
    {
        using var engine = AudioEngine.Create();
        AudioProbeResult probe = engine.Probe(path);
        Console.WriteLine(
            $"{Path.GetFileName(path)}: {probe.Codec}, {probe.SampleRate} Hz, " +
            $"{probe.Channels} ch" +
            (probe.BitDepth is { } bits ? $", {bits} bit" : string.Empty) +
            (probe.BitrateKbps is { } kbps ? $", {kbps} kbps" : string.Empty));

        engine.OpenDevice();
        engine.Load(AudioSlot.Current, path, userToken: 1);
        engine.Play();
        Console.WriteLine("[space] pause/resume, [←/→] seek 5s, [q] quit");

        bool interactive = !Console.IsInputRedirected;
        bool paused = false;
        while (true)
        {
            foreach (AudioEvent ev in engine.PollEvents())
            {
                switch (ev.Type)
                {
                    case AudioEventType.TrackEnded:
                        Console.WriteLine();
                        Console.WriteLine("done");
                        return 0;
                    case AudioEventType.Error:
                        Console.WriteLine();
                        Console.Error.WriteLine($"play: engine error: {ev.Code}");
                        return 1;
                }
            }

            PlaybackPosition pos = engine.GetPosition();
            if (pos.SampleRate > 0)
            {
                double seconds = (double)pos.Frame / pos.SampleRate;
                double total = pos.TotalFrames > 0 ? (double)pos.TotalFrames / pos.SampleRate : 0;
                Console.Write(
                    $"\r{seconds,7:F1}s / {total:F1}s  {pos.State}  buffer {pos.BufferFill:P0}   ");
            }

            if (interactive && Console.KeyAvailable)
            {
                switch (Console.ReadKey(intercept: true).Key)
                {
                    case ConsoleKey.Q:
                        Console.WriteLine();
                        return 0;
                    case ConsoleKey.Spacebar:
                        paused = !paused;
                        if (paused)
                        {
                            engine.Pause();
                        }
                        else
                        {
                            engine.Play();
                        }

                        break;
                    case ConsoleKey.LeftArrow:
                        engine.Seek(Math.Max(0, pos.Frame - 5L * pos.SampleRate));
                        break;
                    case ConsoleKey.RightArrow:
                        engine.Seek(pos.Frame + 5L * pos.SampleRate);
                        break;
                }
            }

            Thread.Sleep(100);
        }
    }
    catch (Exception ex) when (ex is AudioEngineException or DllNotFoundException)
    {
        Console.Error.WriteLine($"play: {ex.Message}");
        return 1;
    }
}

static int PrintHelp(int exitCode)
{
    Console.WriteLine(
        """
        mirelia-cli — headless Mirelia harness

        Usage:
          mirelia-cli scan <dir> [--db <path>]     Scan a folder into the library.
          mirelia-cli query <dsl> [--db <path>]    Run a smart-playlist query.
          mirelia-cli play <file>                  Play an audio file (WASAPI shared).
          mirelia-cli native-smoke                 Load the native audio backend.
          mirelia-cli --version                    Print the version and exit.

        The default database is %APPDATA%/Mirelia/library.db.
        Query DSL reference: docs/query-dsl.md
        """);
    return exitCode;
}
