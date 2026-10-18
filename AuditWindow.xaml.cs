using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace UsageWidget;

public partial class AuditWindow : Window
{
    public AuditWindow()
    {
        InitializeComponent();

        LogList.ItemsSource = Audit.Entries;

        Audit.Entries.CollectionChanged += OnEntriesChanged;
        Closed += (_, _) => Audit.Entries.CollectionChanged -= OnEntriesChanged;
        Loaded += (_, _) => LogScroller.ScrollToEnd();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => LogScroller.ScrollToEnd();

    private void Clear(object sender, RoutedEventArgs e)
    {
        Audit.Entries.Clear();
        Audit.Event("Activity log cleared");
    }

    private void CopyAll(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine,
            Audit.Entries.Select(entry =>
                $"[{entry.TimeText}] {entry.KindText} {entry.Summary}\n{entry.Detail}".TrimEnd()));

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // The clipboard can be locked by another process - not worth failing over.
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
