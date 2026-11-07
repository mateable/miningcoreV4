using System.Collections.Concurrent;
using NLog;
using NLog.Targets;

namespace Miningcore.Logging;

/// <summary>
/// Bounded in-memory ring buffer of formatted log lines, so the admin panel
/// can show recent log output without logFile/perPoolLogFile configured and
/// without SSH access to the box.
/// </summary>
[Target("Memory")]
public sealed class MemoryLogTarget : TargetWithLayout
{
    private const int MaxLines = 2000;
    private static readonly ConcurrentQueue<string> lines = new();

    protected override void Write(LogEventInfo logEvent)
    {
        lines.Enqueue(Layout.Render(logEvent));

        while(lines.Count > MaxLines)
            lines.TryDequeue(out _);
    }

    public static IEnumerable<string> Recent(int count = 300)
    {
        var snapshot = lines.ToArray();
        return snapshot.Skip(Math.Max(0, snapshot.Length - count));
    }
}
