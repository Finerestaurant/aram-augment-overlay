using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using AramOverlay.Core;

namespace AramOverlay.App;

/// <summary>
/// Looks strings up for XAML, and tells every binding to re-read when the
/// language changes.
///
/// Bindings go through an indexer rather than one property per string, so a new
/// string needs an entry in <see cref="Strings"/> and nothing else. Raising
/// "Item[]" invalidates all of them at once, which is what makes the language
/// switch live instead of needing the window rebuilt.
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Current { get; } = new();

    private LocSource() { }

    public string this[string key] => Strings.Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>Usage: <c>Text="{app:Loc Nav.Status}"</c>.</summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = LocSource.Current,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}
