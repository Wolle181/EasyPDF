using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using EasyPDF.App.Controls.Ribbon;
using EasyPDF.App.Localization;

namespace EasyPDF.App.Dialogs;

/// <summary>
/// Quick guide (Help ▸ Quick Guide, F1): one short section per function, each with the round icon it has in the ribbon.
/// The texts are <c>Guide&lt;Key&gt;Title</c> / <c>Guide&lt;Key&gt;Text</c>; "{0}" in a text stands for Ctrl (Cmd on macOS).
/// </summary>
public partial class QuickGuideView : DialogView
{
    /// <param name="Key">Resource key part of the texts.</param>
    /// <param name="Icon">Ribbon icon (also <c>&lt;Icon&gt;.Accent</c>).</param>
    /// <param name="Color">Icon colour, <c>Icon.Color.&lt;Color&gt;</c>; null for a button with a filled circle.</param>
    /// <param name="Emphasis">Filled circle like in the ribbon: "strong" (primary, Open) or "signal" (accent, Save).</param>
    public sealed record Topic(string Key, string Icon, string? Color, string? Emphasis = null);

    public static IReadOnlyList<Topic> Topics { get; } =
    [
        new("Open", "Icon.Open", null, "strong"),
        new("Read", "Icon.Reading", "Blue"),
        new("Organize", "Icon.Organize", "Green"),
        new("Pages", "Icon.Rotate", "Green"),
        new("Stamp", "Icon.Stamp", "Violet"),
        new("Text", "Icon.Text", "Blue"),
        new("Redact", "Icon.Redact", "Slate"),
        new("Area", "Icon.CopyArea", "Violet"),
        new("Ocr", "Icon.Ocr", "Teal"),
        new("Concatenate", "Icon.Concatenate", "Violet"),
        new("Extract", "Icon.Split", "Amber"),
        new("Reduce", "Icon.ReduceSize", "Blue"),
        new("Print", "Icon.Print", "Slate"),
        new("Mail", "Icon.Mail", "Amber"),
        new("Attachments", "Icon.Attachment", "Slate"),
        new("Save", "Icon.Save", null, "signal"),
        new("Settings", "Icon.Help", "Green"),
    ];

    /// <summary>The topics of what the web version offers (the others are hidden there).</summary>
    public static IReadOnlyList<Topic> WebTopics { get; } =
        [.. Topics.Where(t => t.Key is "Open" or "Read" or "Organize" or "Pages" or "Concatenate" or "Extract"),
         new("Share", "Icon.Share", "Amber"),
         .. Topics.Where(t => t.Key is "Attachments" or "Save")];

    public QuickGuideView()
    {
        InitializeComponent();
        var ctrl = OperatingSystem.IsMacOS() ? "⌘" : Loc.T("GuideCtrlKey");
        IntroText.Text = Loc.T("GuideIntro");
        foreach (var topic in Services.PlatformFeatures.Current.IsWeb ? WebTopics : Topics)
            TopicList.Children.Add(BuildTopic(topic, ctrl));
        // Starts at the top, also after the focus went to the Close button
        Shown += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Scroller.Offset = default, Avalonia.Threading.DispatcherPriority.Background);
    }

    private Control BuildTopic(Topic topic, string ctrl)
    {
        var icon = new RibbonButton
        {
            Icon = Find<Geometry>(topic.Icon),
            IconAccent = Find<Geometry>(topic.Icon + ".Accent"),
            IconSize = 26,
            CircleSize = 44,
            Width = 46,
            Height = 46,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Focusable = false,
        };
        if (topic.Emphasis != null)
            icon.Classes.Add(topic.Emphasis);
        if (topic.Color != null)
            icon.Bind(RibbonButton.IconColorProperty, this.GetResourceObservable("Icon.Color." + topic.Color)); // follows the theme

        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = Loc.T($"Guide{topic.Key}Title"), FontSize = 15, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = Loc.F($"Guide{topic.Key}Text", ctrl), TextWrapping = TextWrapping.Wrap, LineHeight = 21 });

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, Tag = topic.Key };
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>A resource of the app (the view is not in a window yet while it builds its rows).</summary>
    private T? Find<T>(string key) where T : class =>
        this.TryFindResource(key, out var value) || (Avalonia.Application.Current?.TryFindResource(key, out value) ?? false) ? value as T : null;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>The quick guide as a window of its own (desktop; it stays open beside the document).</summary>
public sealed class QuickGuideWindow : DialogWindow
{
    public QuickGuideWindow() : base(new QuickGuideView())
    {
        MinWidth = 460;
        MinHeight = 360;
    }

    public static IReadOnlyList<QuickGuideView.Topic> Topics => QuickGuideView.Topics;
}
