using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EasyPDF.App.Localization;

namespace EasyPDF.App.Dialogs;

/// <summary>Chooses the date of a placed stamp; the result is the date, or null if cancelled (also when closed with [X]).</summary>
public sealed class StampDateView : DialogView
{
    private readonly Calendar _calendar = new() { SelectionMode = CalendarSelectionMode.SingleDate, HorizontalAlignment = HorizontalAlignment.Center };

    public StampDateView() : this(DateTime.Today)
    {
    }

    public StampDateView(DateTime date)
    {
        Title = Loc.T("StampDateTitle");
        DialogWidth = 320;

        _calendar.SelectedDate = date.Date;
        _calendar.DisplayDate = date.Date;

        var ok = new Button { Content = Loc.T("Ok"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Content = Loc.T("Cancel"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        // Ctrl+click can clear the selection in the calendar: that leaves the date as it was
        ok.Click += (_, _) => Close((DateTime?)(_calendar.SelectedDate ?? date.Date));
        cancel.Click += (_, _) => Close((DateTime?)null);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                _calendar,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
            },
        };
    }

    /// <summary>The calendar shown.</summary>
    public Calendar Calendar => _calendar;
}

/// <summary>The date dialog as a window (desktop).</summary>
public sealed class StampDateDialog : DialogWindow
{
    public StampDateDialog(DateTime date) : base(new StampDateView(date))
    {
    }

    /// <inheritdoc cref="StampDateView.Calendar"/>
    public Calendar Calendar => ((StampDateView)DialogContent).Calendar;
}
