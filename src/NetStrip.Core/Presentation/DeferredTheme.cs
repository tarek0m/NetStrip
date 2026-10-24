namespace NetStrip.Core.Presentation;

/// <summary>
/// Holds the theme the user chose and applies it only once UI that needs it is about to appear. Before
/// <see cref="Ensure"/> a choice is only remembered; afterwards every change is applied at once (spec §2).
/// </summary>
/// <remarks>
/// Loading the theme is not free (WPF's Fluent dictionaries are tens of styles), and at idle only the strip is on
/// screen, which draws with its own brushes. So the app host calls <see cref="Ensure"/> right before the flyout,
/// the settings window or the strip's menu or tooltip first appears. Not thread-safe; UI thread only.
/// </remarks>
public sealed class DeferredTheme(ThemeSetting initial, Action<ThemeSetting> apply)
{
    /// <summary>The user's current choice, applied or not.</summary>
    public ThemeSetting Desired { get; private set; } = initial;

    /// <summary>True once <see cref="Ensure"/> has run; from then on <see cref="Set"/> applies immediately.</summary>
    public bool IsApplied { get; private set; }

    /// <summary>
    /// Applies <see cref="Desired"/> the first time it is called and returns true; later calls do nothing and return
    /// false. The flag is set before applying, so a failing or re-entrant apply is never retried on every tooltip.
    /// </summary>
    public bool Ensure()
    {
        if (IsApplied) return false;
        IsApplied = true;
        apply(Desired);
        return true;
    }

    /// <summary>Records a new choice; once applied, a different choice is applied at once.</summary>
    public void Set(ThemeSetting theme)
    {
        if (theme == Desired) return;
        Desired = theme;
        if (IsApplied) apply(theme);
    }
}
