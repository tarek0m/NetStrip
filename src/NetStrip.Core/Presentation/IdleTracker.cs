namespace NetStrip.Core.Presentation;

/// <summary>
/// Reports idle once <paramref name="idleSeconds"/> consecutive samples have both directions below
/// <paramref name="thresholdBytes"/>. A single active sample clears the idle state.
/// </summary>
public sealed class IdleTracker(int idleSeconds = 5, long thresholdBytes = 1024)
{
    private int _quietRun;

    public bool IsIdle { get; private set; }

    /// <returns>The new <see cref="IsIdle"/> value.</returns>
    public bool Update(Sample s)
    {
        if (s.UpBytesPerSec < thresholdBytes && s.DownBytesPerSec < thresholdBytes)
        {
            if (_quietRun < idleSeconds) _quietRun++;
        }
        else
        {
            _quietRun = 0;
        }
        IsIdle = _quietRun >= idleSeconds;
        return IsIdle;
    }
}
