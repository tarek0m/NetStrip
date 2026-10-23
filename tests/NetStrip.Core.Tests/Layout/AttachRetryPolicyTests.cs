using NetStrip.Core.Layout;

namespace NetStrip.Core.Tests.Layout;

public class AttachRetryPolicyTests
{
    private static AttachRetryPolicy NewPolicy() => new(TimeSpan.FromSeconds(30), 3);
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void StartupGrace_DoesNotCountFailures()
    {
        // Login before Explorer has created the taskbar: keep waiting, don't float.
        var policy = NewPolicy();

        for (var t = 0; t < 30; t++)
        {
            policy.RecordFailure(S(t));
            Assert.False(policy.ShouldFallBack);
            Assert.Equal(S(1), policy.NextRetryDelay(S(t)));
        }

        // First failures after grace start counting from zero.
        policy.RecordFailure(S(30));
        policy.RecordFailure(S(31));
        Assert.False(policy.ShouldFallBack);
    }

    [Fact]
    public void ThreeFailuresAfterGrace_FallBack()
    {
        var policy = NewPolicy();

        policy.RecordFailure(S(31));
        policy.RecordFailure(S(32));
        Assert.False(policy.ShouldFallBack);
        Assert.Equal(S(1), policy.NextRetryDelay(S(32)));

        policy.RecordFailure(S(33));
        Assert.True(policy.ShouldFallBack);
    }

    [Fact]
    public void SuccessResets()
    {
        var policy = NewPolicy();
        policy.RecordFailure(S(40));
        policy.RecordFailure(S(41));

        policy.RecordSuccess();
        policy.RecordFailure(S(50));
        policy.RecordFailure(S(51));
        Assert.False(policy.ShouldFallBack);

        policy.RecordFailure(S(52));
        Assert.True(policy.ShouldFallBack);

        policy.RecordSuccess();
        Assert.False(policy.ShouldFallBack);
        Assert.Equal(S(1), policy.NextRetryDelay(S(60)));
    }

    [Fact]
    public void FallbackRetryEvery60s()
    {
        var policy = NewPolicy();
        policy.RecordFailure(S(31));
        policy.RecordFailure(S(32));
        policy.RecordFailure(S(33));

        Assert.Equal(S(60), policy.NextRetryDelay(S(33)));

        // Further failures while floating keep the slow cadence.
        policy.RecordFailure(S(93));
        Assert.True(policy.ShouldFallBack);
        Assert.Equal(S(60), policy.NextRetryDelay(S(93)));
    }
}
