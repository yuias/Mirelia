using System.Reflection;
using Mirelia.AudioBridge;
using Mirelia.Library.Database;
using Mirelia.Library.Export;
using Mirelia.Library.Import;
using Mirelia.Library.Playlists;
using Mirelia.Library.Querying;
using Mirelia.Library.Scanning;
using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;

return args switch
{
    ["--version"] or ["-v"] => PrintVersion(),
    ["native-smoke"] => NativeSmoke(),
    ["scan", var dir, .. var rest] => Scan(dir, rest),
    ["query", var dsl, .. var rest] => RunQuery(dsl, ParseDbOption(rest)),
    ["play", var file] => Play(file, exclusive: false),
    ["play", var file, "--exclusive"] => Play(file, exclusive: true),
    ["seed", var count, .. var rest] => Seed(count, ParseDbOption(rest)),
    ["playlist", .. var plArgs] => PlaylistCommand(plArgs),
    ["export", var name, .. var rest] => Export(name, rest),
    ["import", "musicbee", var folder, .. var rest] => ImportMusicBee(folder, rest),
    ["rgscan", .. var rest] => RgScan(ParseDbOption(rest)),
    [] or ["--help"] or ["-h"] => PrintHelp(0),
    _ => PrintHelp(2),
};

static string? ParseDbOption(string[] rest) => rest switch
{
    ["--db", var path] => path,
    [] => null,
    _ => throw new ArgumentException($"Unknown arguments: {string.Join(' ', rest)}"),
};

static string DefaultDbPath() => Mirelia.Library.LibraryPaths.DefaultDatabasePath;

static int Seed(string countText, string? dbPath)
{
    if (!int.TryParse(countText, out int count) || count <= 0)
    {
        Console.Error.WriteLine($"seed: invalid count: {countText}");
        return 2;
    }

    string db = dbPath ?? DefaultDbPath();
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    using var database = LibraryDatabase.Open(db);
    Mirelia.Cli.Seeder.Seed(database, count);
    stopwatch.Stop();
    Console.WriteLine(
        $"seeded {count} synthetic tracks into {db} in {stopwatch.Elapsed.TotalSeconds:F1}s");
    return 0;
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

static int Scan(string dir, string[] rest)
{
    (rest, string? dbPath) = ExtractDbOption(rest);
    bool force = rest.Contains("--force");
    if (rest.Any(a => a != "--force"))
    {
        Console.Error.WriteLine($"scan: unknown arguments: {string.Join(' ', rest)}");
        return 2;
    }

    if (!Directory.Exists(dir))
    {
        Console.Error.WriteLine($"scan: directory not found: {dir}");
        return 1;
    }

    string db = dbPath ?? DefaultDbPath();
    Console.WriteLine($"scanning {dir} into {db}{(force ? " (force)" : string.Empty)}");

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
    }), force);
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

    using var database = LibraryDatabase.Open(db);

    CompiledQuery compiled;
    try
    {
        // The store is what lets 'playlist =' see a smart playlist.
        compiled = SqlCompiler.Compile(
            QueryParser.Parse(dsl), now: null, new PlaylistStore(database).SmartPlaylistResolver);
    }
    catch (Exception ex) when (ex is QueryParseException or QueryCompileException)
    {
        Console.Error.WriteLine($"query: {ex.Message}");
        return 1;
    }

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

static int Play(string file, bool exclusive)
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

        (uint rate, ushort ch) = engine.OpenDevice(exclusive: exclusive);
        engine.Load(AudioSlot.Current, path, userToken: 1);
        engine.Play();
        Console.WriteLine(
            $"device: {(exclusive ? "exclusive" : "shared")} @ {rate} Hz {ch} ch");
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

static int PlaylistCommand(string[] args)
{
    (args, string? dbPath) = ExtractDbOption(args);
    string db = dbPath ?? DefaultDbPath();
    if (!File.Exists(db))
    {
        Console.Error.WriteLine($"playlist: database not found: {db} (run scan first)");
        return 1;
    }

    using var database = LibraryDatabase.Open(db);
    var store = new PlaylistStore(database);
    try
    {
        switch (args)
        {
            case ["create", var name]:
                store.CreateStatic(name);
                Console.WriteLine($"created static playlist '{name}'");
                return 0;

            case ["create", var name, "--smart", var dsl]:
                PlaylistInfo smart = store.CreateSmart(name, dsl);
                Console.WriteLine($"created smart playlist '{name}': {smart.QueryText}");
                return 0;

            case ["list"]:
                foreach (PlaylistInfo p in store.List())
                {
                    string detail = p.Kind == PlaylistKind.Smart
                        ? $"smart: {p.QueryText}"
                        : $"{store.Evaluate(p).Length} track(s)";
                    Console.WriteLine($"{p.Name}  [{detail}]");
                }

                return 0;

            case ["show", var name]:
            {
                PlaylistInfo p = RequirePlaylist(store, name);
                long[] ids = store.Evaluate(p);
                foreach (TrackSummary row in TrackQueryRunner.Hydrate(database, ids))
                {
                    Console.WriteLine($"{row.Artist ?? "?"} - {row.Title ?? "?"} [{row.Album ?? "?"}]");
                }

                Console.WriteLine($"{ids.Length} track(s)");
                return 0;
            }

            case ["add", var name, var dsl]:
            {
                PlaylistInfo p = RequirePlaylist(store, name);
                CompiledQuery compiled = SqlCompiler.Compile(
                    QueryParser.Parse(dsl), now: null, store.SmartPlaylistResolver);
                List<long> ids = TrackQueryRunner.Run(database, compiled);
                store.AddTracks(p.Id, ids);
                Console.WriteLine($"added {ids.Count} track(s) to '{name}'");
                return 0;
            }

            case ["delete", var name]:
                store.Delete(RequirePlaylist(store, name).Id);
                Console.WriteLine($"deleted '{name}'");
                return 0;

            default:
                Console.Error.WriteLine(
                    "usage: mirelia-cli playlist <create|list|show|add|delete> ... [--db <path>]");
                return 2;
        }
    }
    catch (Exception ex) when (ex is InvalidOperationException
        or QueryParseException
        or QueryCompileException)
    {
        Console.Error.WriteLine($"playlist: {ex.Message}");
        return 1;
    }
}

static int Export(string name, string[] rest)
{
    (rest, string? dbPath) = ExtractDbOption(rest);
    string db = dbPath ?? DefaultDbPath();
    string? outPath = null;
    var options = new M3u8Options();

    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--out" when i + 1 < rest.Length:
                outPath = rest[++i];
                break;
            case "--relative":
                options = options with { RelativePaths = true };
                break;
            case "--copy":
                options = options with { CopyFiles = true };
                break;
            case "--rewrite" when i + 1 < rest.Length && rest[i + 1].Contains('='):
                string[] parts = rest[++i].Split('=', 2);
                options = options with { PathRewrite = (parts[0], parts[1]) };
                break;
            default:
                Console.Error.WriteLine($"export: unknown argument: {rest[i]}");
                return 2;
        }
    }

    if (outPath is null)
    {
        Console.Error.WriteLine(
            "usage: mirelia-cli export <playlist> --out <file.m3u8> [--relative] [--copy] [--rewrite from=to] [--db <path>]");
        return 2;
    }

    if (!File.Exists(db))
    {
        Console.Error.WriteLine($"export: database not found: {db}");
        return 1;
    }

    using var database = LibraryDatabase.Open(db);
    var store = new PlaylistStore(database);
    try
    {
        PlaylistInfo playlist = RequirePlaylist(store, name);
        ExportResult result = M3u8Exporter.Export(
            database, store.Evaluate(playlist), outPath, options);
        Console.WriteLine(
            $"exported {result.Written} track(s) to {outPath}" +
            (result.Copied > 0 ? $", copied {result.Copied} file(s)" : string.Empty) +
            (result.Missing > 0 ? $", {result.Missing} missing" : string.Empty));
        return result.Missing > 0 ? 1 : 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException)
    {
        Console.Error.WriteLine($"export: {ex.Message}");
        return 1;
    }
}

static int ImportMusicBee(string folder, string[] rest)
{
    (rest, string? dbPath) = ExtractDbOption(rest);
    string db = dbPath ?? DefaultDbPath();
    var options = new MusicBeeImportOptions();

    foreach (string argument in rest)
    {
        switch (argument)
        {
            case "--no-scan":
                options = options with { ScanRoots = false };
                break;
            case "--no-playlists":
                options = options with { ImportPlaylists = false };
                break;
            case "--merge":
                options = options with { Merge = true };
                break;
            default:
                Console.Error.WriteLine($"import: unknown argument: {argument}");
                return 2;
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(db)!);
    using var database = LibraryDatabase.Open(db);

    // One line per stage rather than a live counter: the scan dominates and
    // its own progress is already per-file.
    string lastPhase = string.Empty;
    var progress = new Progress<MusicBeeImportProgress>(p =>
    {
        if (p.Phase != lastPhase)
        {
            lastPhase = p.Phase;
            Console.WriteLine($"  {p.Phase}…");
        }
    });

    try
    {
        MusicBeeImportReport report = new MusicBeeImporter(database)
            .Import(folder, options, progress);

        Console.WriteLine(
            $"tracks: {report.TracksMatched} of {report.TracksInExport} matched"
            + (report.TracksMissing > 0 ? $", {report.TracksMissing} missing" : string.Empty));
        Console.WriteLine(
            $"playlists: {report.StaticPlaylists} static, {report.SmartPlaylists} smart, "
            + $"{report.SnapshotPlaylists} frozen, {report.FoldersCreated} folders"
            + (report.PlaylistsReplaced > 0 ? $", {report.PlaylistsReplaced} replaced" : string.Empty));
        foreach (string note in report.Notes)
        {
            Console.WriteLine(note.StartsWith(' ') ? note : $"note: {note}");
        }

        return 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException)
    {
        Console.Error.WriteLine($"import: {ex.Message}");
        return 1;
    }
}

static int RgScan(string? dbPath)
{
    string db = dbPath ?? DefaultDbPath();
    if (!File.Exists(db))
    {
        Console.Error.WriteLine($"rgscan: database not found: {db}");
        return 1;
    }

    using var database = LibraryDatabase.Open(db);
    List<(long Id, string Path)> pending = TrackPager.GetTracksMissingReplayGain(database);
    if (pending.Count == 0)
    {
        Console.WriteLine("rgscan: nothing to scan");
        return 0;
    }

    using var engine = AudioEngine.Create();
    int done = 0, failed = 0;
    foreach ((long id, string path) in pending)
    {
        try
        {
            (float gain, float peak) = engine.ScanLoudness(path);
            TrackPager.SetTrackReplayGain(database, id, gain, peak);
            done++;
        }
        catch (AudioEngineException)
        {
            failed++;
        }

        if ((done + failed) % 50 == 0)
        {
            Console.WriteLine($"  {done + failed}/{pending.Count}...");
        }
    }

    Console.WriteLine($"rgscan: {done} scanned, {failed} failed");
    return 0;
}

static PlaylistInfo RequirePlaylist(PlaylistStore store, string name)
{
    return store.FindByName(name)
        ?? throw new InvalidOperationException($"no playlist named '{name}'");
}

static (string[] Remaining, string? DbPath) ExtractDbOption(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--db")
        {
            return ([.. args[..i], .. args[(i + 2)..]], args[i + 1]);
        }
    }

    return (args, null);
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
          mirelia-cli playlist create <name> [--smart <dsl>]
          mirelia-cli playlist list | show <name> | delete <name>
          mirelia-cli playlist add <name> <dsl>    Add query results to a static playlist.
          mirelia-cli export <name> --out <file.m3u8> [--relative] [--copy] [--rewrite from=to]
          mirelia-cli import musicbee <library folder> [--no-scan] [--no-playlists] [--merge]
                                                   --merge replaces same-named playlists
                                                   instead of importing a second copy.
          mirelia-cli native-smoke                 Load the native audio backend.
          mirelia-cli --version                    Print the version and exit.

        The default database is %APPDATA%/Mirelia/library.db.
        Query DSL reference: docs/query-dsl.md
        """);
    return exitCode;
}
