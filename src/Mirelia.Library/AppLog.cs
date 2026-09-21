using System.Diagnostics;
using System.Text;

namespace Mirelia.Library;

/// <summary>
/// Append-only text log shared by the app and the CLI. A released build has
/// no console to print to, so this file is the only thing a bug report can
/// attach.
/// </summary>
public static class AppLog
{
    /// <summary>
    /// Size at which the log is rotated to <c>.1</c>. Small enough that the
    /// file stays attachable, large enough to hold a session's worth of
    /// startup lines plus a stack trace or two.
    /// </summary>
    private const long MaxBytes = 1 * 1024 * 1024;

    private static readonly Lock Gate = new();

    // A BOM on every rotation makes the file awkward to tail or concatenate.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string? _path;

    private static int _errorCount;

    /// <summary>
    /// The file currently being written, or null until <see cref="Open"/>
    /// succeeds. Exposed so the UI can point a user at it.
    /// </summary>
    public static string? Path => _path;

    /// <summary>
    /// Points the log at <paramref name="path"/>, rotating it first if it has
    /// grown past the cap, and records one startup line. Logging is a
    /// diagnostic aid, so any failure here leaves the log disabled rather
    /// than taking the process down with it.
    /// </summary>
    public static void Open(string path, string startupLine)
    {
        lock (Gate)
        {
            try
            {
                string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Rotate(path);
                _path = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _path = null;
                return;
            }
        }

        Write("start", startupLine);
    }

    /// <summary>
    /// How many errors have been recorded. The smoke run reads it, because
    /// the UI-thread hook marks exceptions handled and they would otherwise
    /// leave no trace in the exit code.
    /// </summary>
    public static int ErrorCount => Volatile.Read(ref _errorCount);

    /// <summary>Records an informational line.</summary>
    public static void Info(string message) => Write("info", message);

    /// <summary>Records a warning line.</summary>
    public static void Warn(string message) => Write("warn", message);

    /// <summary>
    /// Records an error and, when present, the exception's full text
    /// including its stack trace and any inner exceptions.
    /// </summary>
    public static void Error(string message, Exception? exception = null)
    {
        Interlocked.Increment(ref _errorCount);
        Write("error", exception is null ? message : $"{message}{Environment.NewLine}{exception}");
    }

    private static void Write(string level, string message)
    {
        string line =
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";

        // Mirrored to the debugger/console so a dev build keeps the output it
        // had before the file existed.
        Debug.Write(line);

        lock (Gate)
        {
            if (_path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, line, Utf8NoBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written must never break the caller.
            }
        }
    }

    private static void Rotate(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes)
        {
            return;
        }

        File.Move(path, path + ".1", overwrite: true);
    }
}
