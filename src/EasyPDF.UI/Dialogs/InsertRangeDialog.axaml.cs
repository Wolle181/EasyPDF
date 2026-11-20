using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EasyPDF.App.Services;
using EasyPDF.App.ViewModels;

namespace EasyPDF.App.Dialogs;

public partial class InsertRangeDialogView : DialogView
{
    public InsertRangeDialogView() => InitializeComponent();

    public InsertRangeDialogView(InsertRangeContext context) : this() => DataContext = new InsertRangeDialogViewModel(context);

    private void OnOk(object? sender, RoutedEventArgs e) => Close(((InsertRangeDialogViewModel)DataContext!).BuildResult());

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((IReadOnlyList<int>?)null);
}

/// <summary>The dialog as a window (desktop).</summary>
public sealed class InsertRangeDialog : DialogWindow
{
    public InsertRangeDialog(InsertRangeContext context) : base(new InsertRangeDialogView(context))
    {
    }
}
