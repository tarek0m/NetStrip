namespace NetStrip.Core.Presentation;

/// <summary>Pure flyout positioning: anchored to the strip on the taskbar side, then clamped into the monitor work area.</summary>
public static class FlyoutPlacement
{
    /// <returns>Top-left of the flyout in screen pixels.</returns>
    public static PixelPoint Compute(PixelRect strip, TaskbarEdge edge, int width, int height,
        PixelRect work, int gap, int margin)
    {
        int x, y;
        switch (edge)
        {
            case TaskbarEdge.Top:
                x = strip.X + strip.Width / 2 - width / 2;
                y = strip.Y + strip.Height + gap;
                break;
            case TaskbarEdge.Left:
                x = strip.X + strip.Width + gap;
                y = strip.Y + strip.Height / 2 - height / 2;
                break;
            case TaskbarEdge.Right:
                x = strip.X - width - gap;
                y = strip.Y + strip.Height / 2 - height / 2;
                break;
            default: // Bottom
                x = strip.X + strip.Width / 2 - width / 2;
                y = strip.Y - height - gap;
                break;
        }

        return new PixelPoint(
            Clamp(x, work.X + margin, work.X + work.Width - width - margin),
            Clamp(y, work.Y + margin, work.Y + work.Height - height - margin));
    }

    // If the window does not fit at all, pin to the low bound (top-left margin).
    private static int Clamp(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);
}
