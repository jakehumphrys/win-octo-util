using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace UsageWidget;

public enum AuditKind { Event, Api }

/// <summary>A single entry in the activity log.</summary>
public class AuditEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public AuditKind Kind { get; init; }
    public string Summary { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsError { get; init; }

    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string KindText => Kind == AuditKind.Api ? "API" : "EVENT";
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public Brush Accent => IsError ? Audit.Red
        : Kind == AuditKind.Api ? Audit.Blue
        : Audit.Green;
}

/// <summary>
/// Session-scoped activity log shared by the whole app. Entries can be added from
/// background threads (the API calls run off the UI thread), so every mutation is
/// marshalled onto the dispatcher.
/// </summary>
public static class Audit
{
    private const int MaxEntries = 500;

    internal static readonly Brush Green = Brush("#3DDC84");
    internal static readonly Brush Blue  = Brush("#6FA8FF");
    internal static readonly Brush Red   = Brush("#EB5757");

    public static ObservableCollection<AuditEntry> Entries { get; } = new();

    /// <summary>Records an application event (start-up, refresh, settings change, ...).</summary>
    public static void Event(string summary, string detail = "", bool error = false)
        => Add(new AuditEntry { Kind = AuditKind.Event, Summary = summary, Detail = detail, IsError = error });

    /// <summary>
    /// Records an API call. The request, status and (truncated) response body are captured.
    /// The Authorization header - which carries the API key - is deliberately never logged.
    /// </summary>
    public static void Api(string summary, string detail, bool error)
        => Add(new AuditEntry { Kind = AuditKind.Api, Summary = summary, Detail = detail, IsError = error });

    private static void Add(AuditEntry entry)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
        {
            Append(entry);
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(() => Append(entry)));
    }

    private static void Append(AuditEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    private static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}
