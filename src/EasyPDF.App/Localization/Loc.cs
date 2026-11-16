using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace EasyPDF.App.Localization;

/// <summary>
/// Provides UI texts from <c>Resources/Strings*.resx</c>. XAML binds through <c>{loc:Tr Key}</c>; changing the
/// culture raises <c>PropertyChanged</c> so all bound texts update live. <c>{App}</c> in a text stands for the shown
/// product name (<see cref="AppName"/>).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager _resources = new("EasyPDF.App.Resources.Strings", typeof(Loc).Assembly);

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => T(key);

    /// <summary>The product name the user sees, set once in the brand profile (<c>AppDisplayName</c>).</summary>
    public static string AppName => Core.AppInfo.Name;

    public static string T(string key) =>
        _resources.GetString(key, CultureInfo.CurrentUICulture)?.Replace("{App}", AppName) ?? $"!{key}!";

    /// <summary>Formats a text with placeholders ({0}, {1}); sentences are never concatenated in code.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentUICulture, T(key), args);

    /// <summary>A count with its noun in the right number ("1 page", "2 pages"), to fill a placeholder of a longer text.</summary>
    public static string Pages(int count) => F(count == 1 ? "CountPage" : "CountPages", count);

    /// <inheritdoc cref="Pages"/>
    public static string Files(int count) => F(count == 1 ? "CountFile" : "CountFiles", count);

    /// <inheritdoc cref="Pages"/>
    public static string Words(int count) => F(count == 1 ? "CountWord" : "CountWords", count);

    /// <inheritdoc cref="Pages"/>
    public static string Characters(int count) => F(count == 1 ? "CountCharacter" : "CountCharacters", count);

    public static void SetCulture(CultureInfo culture)
    {
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        lock (_strings)
            foreach (var text in _strings.Values)
                text.Refresh();
    }

    // One shared instance per key (strongly held: bindings only keep their source weakly)
    private static readonly Dictionary<string, LocString> _strings = new();

    internal static LocString For(string key)
    {
        lock (_strings)
        {
            if (!_strings.TryGetValue(key, out var text))
                _strings[key] = text = new LocString(key);
            return text;
        }
    }
}

/// <summary>One localized text as bindable property; refreshes itself when the culture changes.</summary>
public sealed class LocString : INotifyPropertyChanged
{
    private readonly string _key;

    internal LocString(string key) => _key = key;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Value => Loc.T(_key);

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}

/// <summary>XAML markup extension: <c>Text="{loc:Tr Open}"</c>.</summary>
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CompiledBinding.Create((LocString s) => s.Value, source: Loc.For(Key), mode: BindingMode.OneWay);
}
