namespace AramOverlay.Core;

/// <summary>
/// One log line goes to every listener: the console when there is one, the
/// status window when it is up, and always a file.
///
/// The file exists because the log is the only account of why an augment went
/// unrecorded, and by the time someone notices, the window has usually scrolled
/// or a new game has cleared it. It is truncated at startup so it stays the
/// record of the current run rather than growing forever.
/// </summary>
public static class Log
{
    private static readonly List<Action<string>> Sinks = new();
    private static readonly object FileGate = new();
    private static string? _path;

    public static event Action<string>? Line;

    public static string? FilePath => _path;

    /// <summary>Starts a fresh log file next to the exe. Failure is not fatal.</summary>
    public static void StartFile()
    {
        try
        {
            Directory.CreateDirectory(Config.State);
            string path = Path.Combine(Config.State, "overlay.log");
            File.WriteAllText(path, $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            _path = path;
        }
        catch
        {
            _path = null;                 // read-only folder: carry on without it
        }
    }

    public static void Add(Action<string> sink)
    {
        lock (Sinks) Sinks.Add(sink);
    }

    public static void Write(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);

        if (_path is not null)
        {
            try
            {
                lock (FileGate) File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // A locked or full disk must not take the detection loop down.
            }
        }

        Action<string>[] sinks;
        lock (Sinks) sinks = Sinks.ToArray();
        foreach (var sink in sinks)
        {
            try { sink(line); }
            catch { /* a broken listener must not stop the loop */ }
        }
        Line?.Invoke(line);
    }
}
