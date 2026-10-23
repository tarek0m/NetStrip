using NetStrip.Core;

namespace NetStrip.Core.Tests.Fakes;

public sealed class FakeInterfaceTable : IInterfaceTable
{
    public List<InterfaceCounters> Counters { get; } = new();
    public string? BestId { get; set; }
    public int BestCalls { get; private set; }

    public IReadOnlyList<InterfaceCounters> Read() => Counters.ToArray();

    public string? BestInterfaceId()
    {
        BestCalls++;
        return BestId;
    }

    /// <summary>Adds or replaces the adapter with <paramref name="id"/>.</summary>
    public void Set(string id, ulong inOctets, ulong outOctets, bool included = true,
        AdapterKind kind = AdapterKind.Ethernet, string? ipv4 = null)
    {
        Remove(id);
        Counters.Add(new InterfaceCounters(id, "Adapter " + id, kind, ipv4, inOctets, outOctets, included));
    }

    public void Remove(string id) => Counters.RemoveAll(c => c.Id == id);
}
