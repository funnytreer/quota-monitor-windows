using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuotaMonitor;

internal sealed class CodexResetPanel : StackPanel
{
    private readonly TextBlock _count = Visuals.Text("—", 12, bold: true);
    private readonly TextBlock _summary = Visuals.Text("", 11, Visuals.Gray);
    private readonly Grid _expirations = Visuals.Columns(-2, -1);
    private readonly Border _expirationsCard;

    public CodexResetPanel()
    {
        var heading = Visuals.Columns(-1, 0);
        var title = Visuals.Text("Codex 可用重置", 12, bold: true);
        title.Margin = new Thickness(0, 0, 6, 0);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        Visuals.Place(heading, title, 0);
        var badge = new Border
        {
            Child = _count, Background = Visuals.ThemeBrush("Accent.PrimarySoft"),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1)
        };
        RenderOptions.SetClearTypeHint(badge, ClearTypeHint.Enabled);
        Visuals.Place(heading, badge, 1);
        Children.Add(heading);
        _summary.Margin = new Thickness(0, 1, 0, 0);
        _summary.TextTrimming = TextTrimming.CharacterEllipsis;
        Children.Add(_summary);

        _expirationsCard = new Border
        {
            Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(8, 3, 8, 3),
            Background = Visuals.ThemeBrush("Surface.Subtle"), CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer
            {
                Content = _expirations, MaxHeight = 98,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        RenderOptions.SetClearTypeHint(_expirationsCard, ClearTypeHint.Enabled);
        Children.Add(_expirationsCard);
    }

    public void Present(CodexResetReading reading)
    {
        _count.Text = reading.AvailableCount is long count ? $"{count} 次" : "—";
        _expirations.Children.Clear();
        _expirations.RowDefinitions.Clear();
        _expirationsCard.Visibility = Visibility.Collapsed;

        if (reading.AvailableCount is not long available)
        {
            ShowSummary(reading.Status);
            return;
        }

        long unknown = available - reading.Expirations.Count;
        string note = Quota.LastSuccessNote(reading.LastSuccessAt, reading.Status) ?? "";
        if (reading.Expirations.Count == 0)
        {
            ShowSummary(note.Length > 0 ? note : unknown > 0 ? "暂未返回到期时间" : "");
            return;
        }

        ShowSummary(note.Length > 0 ? note : unknown > 0 ? "部分到期时间暂未返回" : "");

        AddExpiryCell(0, 0, Visuals.Text("到期日期", 10, Visuals.Gray));
        AddExpiryCell(0, 1, Visuals.Text("时间", 10, Visuals.Gray));
        foreach (var expiry in reading.Expirations.OrderBy(date => !date.HasValue).ThenBy(date => date))
        {
            int row = _expirations.RowDefinitions.Count;
            if (expiry is DateTimeOffset time)
            {
                var local = time.ToLocalTime();
                var date = Visuals.Text(local.ToString("yyyy-MM-dd"), 11);
                var hour = Visuals.Text(local.ToString("HH:mm"), 11);
                date.FontFamily = hour.FontFamily = Visuals.NumericTypeface;
                AddExpiryCell(row, 0, date);
                AddExpiryCell(row, 1, hour);
            }
            else
            {
                var label = Visuals.Text("无到期限制", 11, Visuals.Gray);
                Grid.SetColumnSpan(label, 2);
                AddExpiryCell(row, 0, label);
            }
        }
        _expirationsCard.Visibility = Visibility.Visible;
    }

    private void AddExpiryCell(int row, int column, TextBlock text)
    {
        if (_expirations.RowDefinitions.Count == row)
            _expirations.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        text.Margin = new Thickness(0, 0, 0, row == 0 ? 3 : 1);
        Grid.SetRow(text, row);
        Visuals.Place(_expirations, text, column);
    }

    private void ShowSummary(string message)
    {
        _summary.Text = message;
        _summary.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
