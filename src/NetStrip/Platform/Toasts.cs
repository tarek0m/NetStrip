using Microsoft.Toolkit.Uwp.Notifications;
using NetStrip.Core;
using NetStrip.Core.Formatting;

namespace NetStrip.Platform;

public static class Toasts
{
    internal static string CapMessage(CapLevel level, long used, long cap)
    {
        var u = SpeedFormatter.FormatTotal(used);
        var c = SpeedFormatter.FormatTotal(cap);
        return level == CapLevel.Reached100
            ? $"You've reached your monthly data cap ({u} of {c})"
            : $"You've used 80% of your monthly data ({u} of {c})";
    }

    public static void CapWarning(CapLevel level, long used, long cap)
    {
        if (level == CapLevel.None) return;
        try
        {
            new ToastContentBuilder()
                .AddText("NetStrip")
                .AddText(CapMessage(level, used, cap))
                .Show();
        }
        catch (Exception e)
        {
            // Toasts are best effort (e.g. notifications disabled or no shell registration for an unpackaged app).
            App.Log.Warn("Could not show cap toast", e);
        }
    }
}
