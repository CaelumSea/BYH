using Avalonia.Data;
using Avalonia.Markup.Xaml;
using SelectionAssistant.Core.I18n;

namespace SelectionAssistant.UI.Markup;

/// <summary>
/// Optional Avalonia markup extension for code-behind / dynamic-composition
/// scenarios:
/// <code>
///   &lt;TextBlock Text="{Tr Key=Toolbar_Translate}" /&gt;
/// </code>
/// </summary>
/// <remarks>
/// <b>Most call sites should prefer <c>{x:Static loc:Strings.Toolbar_Translate}</c>
/// instead.</b> <c>x:Static</c> is resolved at XAML compile time against the
/// actual property, so a typo is a build error and the binding is plain
/// string literal data (fully AOT/trim-safe). Use this extension only when
/// the key is genuinely dynamic (built at runtime) — it falls back to
/// <see cref="Strings.Get"/> which returns the key itself on a miss.
/// <para>
/// Implemented as a plain <see cref="MarkupExtension"/> (not an
/// <c>IValueProvider</c> tied to a binding) because the active language is
/// fixed for the process lifetime — restart-after-toggle, not live switch —
/// so a one-shot <c>ProvideValue</c> returning a literal string is correct.
/// </para>
/// </remarks>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key)
    {
        Key = key;
    }

    /// <summary>The dictionary key to look up via <see cref="Strings.Get"/>.</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Strings.Get(Key);
}
