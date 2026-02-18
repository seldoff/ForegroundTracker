using System.Collections.ObjectModel;

namespace ForegroundTracker;

public static class EventLog
{
    public static ObservableCollection<string> Entries { get; } = new();

    public static void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        if (App.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => Entries.Add(entry));
        else
            Entries.Add(entry);
    }

    public static void Clear() => Entries.Clear();
}
