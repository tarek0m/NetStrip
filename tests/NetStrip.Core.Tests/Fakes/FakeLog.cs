using NetStrip.Core;

namespace NetStrip.Core.Tests.Fakes;

public sealed class FakeLog : ILog
{
    public List<string> Infos { get; } = new();
    public List<(string Message, Exception? Exception)> Warnings { get; } = new();
    public List<(string Message, Exception? Exception)> Errors { get; } = new();

    public void Info(string msg) => Infos.Add(msg);
    public void Warn(string msg, Exception? ex = null) => Warnings.Add((msg, ex));
    public void Error(string msg, Exception? ex = null) => Errors.Add((msg, ex));
}
