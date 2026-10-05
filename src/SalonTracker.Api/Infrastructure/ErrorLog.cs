namespace SalonTracker.Api.Infrastructure;

public sealed record LoggedError(string Time, string Method, string Url, string Message);

/// <summary>The last server errors, newest first, shown on the admin System page.</summary>
public sealed class ErrorLog
{
    private const int Capacity = 50;
    private const int MaxMessageLength = 500;

    private readonly Queue<LoggedError> _entries = new();
    private readonly Lock _lock = new();

    public void Add(LoggedError entry)
    {
        var trimmed = entry.Message.Length > MaxMessageLength
            ? entry with { Message = entry.Message[..MaxMessageLength] }
            : entry;
        lock (_lock)
        {
            _entries.Enqueue(trimmed);
            if (_entries.Count > Capacity) _entries.Dequeue();
        }
    }

    public IReadOnlyList<LoggedError> Recent()
    {
        lock (_lock)
        {
            return _entries.Reverse().ToList();
        }
    }
}
