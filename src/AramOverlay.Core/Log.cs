namespace AramOverlay.Core;

/// <summary>
/// One log line goes to every listener: the console when there is one, and the
/// status window when it is up. Same shape as the Python side's LOG_SINKS, so
/// the UI can subscribe without the core knowing a UI exists.
/// </summary>
public static class Log
{
    private static readonly List<Action<string>> Sinks = new();

    public static event Action<string>? Line;

    public static void Add(Action<string> sink)
    {
        lock (Sinks) Sinks.Add(sink);
    }

    public static void Write(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Console.WriteLine(line);
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
