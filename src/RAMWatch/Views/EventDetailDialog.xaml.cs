using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RAMWatch.Core.Decode;
using RAMWatch.Core.Models;

namespace RAMWatch.Views;

/// <summary>
/// Drill-down dialog for a single error source. Shows recent events for that
/// source and a deterministic decode (what / where / why / what to do) for
/// the selected event. Decoder logic lives in <see cref="EventDecoder"/>.
/// </summary>
public partial class EventDetailDialog : System.Windows.Window
{
    private readonly List<EventListItem> _items;

    public EventDetailDialog(string sourceName, IReadOnlyList<MonitoredEvent> events)
    {
        InitializeComponent();

        SourceHeader.Text = sourceName.ToUpperInvariant();
        EventCountLabel.Text = events.Count switch
        {
            0 => "no events captured",
            1 => "1 event",
            _ => $"{events.Count} events"
        };

        // Newest first — events arrive oldest-first from the service buffer.
        _items = events
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new EventListItem(e))
            .ToList();
        EventList.ItemsSource = _items;
        EventList.SelectionChanged += OnSelectionChanged;

        if (_items.Count > 0)
        {
            EventList.SelectedIndex = 0;
        }
        else
        {
            RenderEmpty();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is EventListItem item)
        {
            RenderDecoded(EventDecoder.Decode(item.Event));
        }
    }

    private void RenderEmpty()
    {
        DetailStack.Children.Clear();
        var msg = new TextBlock
        {
            Text = "No events recorded for this source yet.",
            FontStyle = FontStyles.Italic,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        DetailStack.Children.Add(msg);
    }

    private void RenderDecoded(DecodedEvent decoded)
    {
        DetailStack.Children.Clear();

        // Title
        DetailStack.Children.Add(new TextBlock
        {
            Text = decoded.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = (Brush)FindResource("TextPrimary"),
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        });

        AddSection("WHAT", decoded.What);
        AddSection("WHERE", decoded.Where);
        AddSection("WHY", decoded.Why);
        AddSection("WHAT TO DO", decoded.WhatToDo);

        if (decoded.Facts.Count > 0)
        {
            DetailStack.Children.Add(new TextBlock
            {
                Text = "FACTS",
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(0, 8, 0, 4)
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;
            foreach (var kv in decoded.Facts)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var name = new TextBlock
                {
                    Text = kv.Key,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    FontSize = 11,
                    Margin = new Thickness(0, 1, 8, 1)
                };
                Grid.SetRow(name, row);
                Grid.SetColumn(name, 0);

                var value = new TextBlock
                {
                    Text = kv.Value,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                Grid.SetRow(value, row);
                Grid.SetColumn(value, 1);

                grid.Children.Add(name);
                grid.Children.Add(value);
                row++;
            }

            DetailStack.Children.Add(grid);
        }
    }

    private void AddSection(string label, string body)
    {
        DetailStack.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 6, 0, 2)
        });
        DetailStack.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
            Margin = new Thickness(0, 0, 0, 0)
        });
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is not EventListItem item) return;

        var decoded = EventDecoder.Decode(item.Event);
        var sb = new StringBuilder();
        sb.AppendLine(decoded.Title);
        sb.AppendLine();
        sb.AppendLine("WHAT");
        sb.AppendLine(decoded.What);
        sb.AppendLine();
        sb.AppendLine("WHERE");
        sb.AppendLine(decoded.Where);
        sb.AppendLine();
        sb.AppendLine("WHY");
        sb.AppendLine(decoded.Why);
        sb.AppendLine();
        sb.AppendLine("WHAT TO DO");
        sb.AppendLine(decoded.WhatToDo);
        if (decoded.Facts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("FACTS");
            int width = decoded.Facts.Max(kv => kv.Key.Length);
            foreach (var kv in decoded.Facts)
                sb.AppendLine($"  {kv.Key.PadRight(width)}  {kv.Value}");
        }

        Clipboard.SetText(sb.ToString());
        CopyConfirm.Text = "Copied to clipboard.";
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class EventListItem
    {
        public MonitoredEvent Event { get; }
        public string TimestampDisplay { get; }
        public string ListSummary { get; }

        public EventListItem(MonitoredEvent evt)
        {
            Event = evt;
            TimestampDisplay = evt.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss");
            ListSummary = $"id {evt.EventId} · {evt.Summary}";
        }
    }
}
