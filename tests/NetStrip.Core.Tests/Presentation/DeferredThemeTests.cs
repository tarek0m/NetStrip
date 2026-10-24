using NetStrip.Core.Presentation;

namespace NetStrip.Core.Tests.Presentation;

public class DeferredThemeTests
{
    private readonly List<ThemeSetting> _applied = [];

    private DeferredTheme Create(ThemeSetting initial = ThemeSetting.System) => new(initial, _applied.Add);

    [Fact]
    public void NothingIsApplied_UntilEnsure()
    {
        var t = Create(ThemeSetting.Dark);
        t.Set(ThemeSetting.Light);
        t.Set(ThemeSetting.System);

        Assert.Empty(_applied);
        Assert.False(t.IsApplied);
        Assert.Equal(ThemeSetting.System, t.Desired);
    }

    [Fact]
    public void Ensure_AppliesTheLatestChoiceOnce()
    {
        var t = Create(ThemeSetting.System);
        t.Set(ThemeSetting.Dark);

        Assert.True(t.Ensure());
        Assert.False(t.Ensure());
        Assert.False(t.Ensure());

        Assert.Equal([ThemeSetting.Dark], _applied);
        Assert.True(t.IsApplied);
    }

    [Fact]
    public void AfterEnsure_ChangesApplyImmediately()
    {
        var t = Create(ThemeSetting.Light);
        t.Ensure();
        t.Set(ThemeSetting.Dark);
        t.Set(ThemeSetting.System);

        Assert.Equal([ThemeSetting.Light, ThemeSetting.Dark, ThemeSetting.System], _applied);
    }

    [Fact]
    public void AfterEnsure_AnUnchangedChoiceIsNotReapplied()
    {
        // Every settings change (units, adapter, ...) re-sends the theme; only a real change may reload it.
        var t = Create(ThemeSetting.Dark);
        t.Ensure();
        t.Set(ThemeSetting.Dark);

        Assert.Equal([ThemeSetting.Dark], _applied);
    }

    [Fact]
    public void Ensure_FailingApply_IsNotRetried()
    {
        var calls = 0;
        var t = new DeferredTheme(ThemeSetting.System, _ =>
        {
            calls++;
            throw new InvalidOperationException("theme failed to load");
        });

        Assert.Throws<InvalidOperationException>(() => t.Ensure());
        Assert.False(t.Ensure());
        Assert.Equal(1, calls);
        Assert.True(t.IsApplied);
    }
}
