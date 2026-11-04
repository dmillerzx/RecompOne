namespace RecompOne.Runtime.Modding;

/// <summary>
/// What a <see cref="ModOption"/> asks the host to draw.
/// </summary>
public enum ModOptionKind
{
    /// <summary>On/off row.</summary>
    Toggle,
    /// <summary>One of <see cref="ModOption.Choices"/>.</summary>
    Choice,
    /// <summary>A number between <see cref="ModOption.Min"/> and <see cref="ModOption.Max"/>.</summary>
    Slider,
    /// <summary>A row that just runs something when picked.</summary>
    Action,
}

/// <summary>
/// One setting a mod wants exposed, described rather than drawn.
///
/// Mods used to expose settings only through <see cref="IMod.DrawSettings"/>, which is raw
/// ImGui. That works on desktop and is invisible on Android, where the menu is native views
/// and no ImGui surface exists - so no mod could be configured on a phone at all. Describing
/// an option instead of drawing it lets each host render it in its own idiom: ImGui widgets on
/// desktop, MenuSheet rows on Android.
///
/// Values are read and written through delegates rather than stored here, so the mod stays the
/// single source of truth and can persist however it likes.
/// </summary>
public sealed class ModOption
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Description { get; init; }

    /// <summary>Optional heading this option is filed under. Consecutive options sharing a section are grouped.</summary>
    public string? Section { get; init; }

    public ModOptionKind Kind { get; init; }

    /// <summary>Greys the option out when it returns false. Null means always available.</summary>
    public Func<bool>? IsAvailable { get; init; }

    // Toggle
    public Func<bool>? GetBool { get; init; }
    public Action<bool>? SetBool { get; init; }

    // Choice
    public string[] Choices { get; init; } = [];
    public Func<int>? GetIndex { get; init; }
    public Action<int>? SetIndex { get; init; }

    // Slider
    public float Min { get; init; }
    public float Max { get; init; }
    public float Step { get; init; } = 1f;
    public Func<float>? GetFloat { get; init; }
    public Action<float>? SetFloat { get; init; }
    /// <summary>Printf-style format for the slider readout, e.g. "%.2f" or "%.0f%%".</summary>
    public string Format { get; init; } = "%.2f";

    // Action
    public Action? Invoke { get; init; }

    public bool Available => IsAvailable?.Invoke() ?? true;

    public static ModOption MakeToggle(string id, string label, Func<bool> get, Action<bool> set,
        string? description = null, string? section = null, Func<bool>? isAvailable = null) => new()
    {
        Id = id, Label = label, Description = description, Section = section,
        Kind = ModOptionKind.Toggle, GetBool = get, SetBool = set, IsAvailable = isAvailable,
    };

    public static ModOption MakeChoice(string id, string label, string[] choices, Func<int> get, Action<int> set,
        string? description = null, string? section = null, Func<bool>? isAvailable = null) => new()
    {
        Id = id, Label = label, Description = description, Section = section,
        Kind = ModOptionKind.Choice, Choices = choices, GetIndex = get, SetIndex = set, IsAvailable = isAvailable,
    };

    public static ModOption MakeSlider(string id, string label, float min, float max, float step,
        Func<float> get, Action<float> set, string format = "%.2f",
        string? description = null, string? section = null, Func<bool>? isAvailable = null) => new()
    {
        Id = id, Label = label, Description = description, Section = section,
        Kind = ModOptionKind.Slider, Min = min, Max = max, Step = step,
        GetFloat = get, SetFloat = set, Format = format, IsAvailable = isAvailable,
    };

    public static ModOption MakeAction(string id, string label, Action invoke,
        string? description = null, string? section = null, Func<bool>? isAvailable = null) => new()
    {
        Id = id, Label = label, Description = description, Section = section,
        Kind = ModOptionKind.Action, Invoke = invoke, IsAvailable = isAvailable,
    };

    /// <summary>Reads the current value as display text, for hosts that show a row's value inline.</summary>
    public string ValueText()
    {
        try
        {
            switch (Kind)
            {
                case ModOptionKind.Toggle:
                    return (GetBool?.Invoke() ?? false) ? "On" : "Off";
                case ModOptionKind.Choice:
                {
                    int i = GetIndex?.Invoke() ?? 0;
                    return i >= 0 && i < Choices.Length ? Choices[i] : "";
                }
                case ModOptionKind.Slider:
                    return FormatValue(GetFloat?.Invoke() ?? 0f);
                default:
                    return "";
            }
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Renders a slider value using <see cref="Format"/>. Only the handful of printf forms
    /// ImGui sliders actually use are honoured; anything else falls back to plain round-tripping.
    /// </summary>
    public string FormatValue(float value)
    {
        var f = Format;
        bool percent = f.EndsWith("%%", StringComparison.Ordinal) || f.EndsWith("%", StringComparison.Ordinal) && !f.EndsWith("%f", StringComparison.Ordinal);
        int decimals = 2;
        int dot = f.IndexOf('.');
        if (dot >= 0 && dot + 1 < f.Length && char.IsDigit(f[dot + 1])) decimals = f[dot + 1] - '0';
        var text = value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
        return percent ? text + "%" : text;
    }
}

/// <summary>
/// Implement alongside <see cref="IMod"/> to expose settings that every host can draw -
/// including Android, which cannot draw <see cref="IMod.DrawSettings"/>.
///
/// A mod may implement both: hosts render these options first, then call DrawSettings for
/// anything too bespoke to describe.
/// </summary>
public interface IModOptions
{
    IReadOnlyList<ModOption> GetOptions();
}
