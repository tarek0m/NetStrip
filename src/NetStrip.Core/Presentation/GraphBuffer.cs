using System.Collections;

namespace NetStrip.Core.Presentation;

/// <summary>Fixed-capacity ring of the most recent samples (oldest first) with a running window maximum.</summary>
public sealed class GraphBuffer(int capacity = 60)
{
    public const long MaxFloor = 1024;

    private readonly Sample[] _ring = new Sample[capacity];
    private int _start;
    private int _count;

    public IReadOnlyList<Sample> Items => _view ??= new View(this);

    /// <summary>Largest up or down value in the window, never below 1024.</summary>
    public long Max { get; private set; } = MaxFloor;

    public void Push(Sample s)
    {
        if (_count < _ring.Length)
        {
            _ring[(_start + _count) % _ring.Length] = s;
            _count++;
        }
        else
        {
            _ring[_start] = s;
            _start = (_start + 1) % _ring.Length;
        }

        var max = MaxFloor;
        for (var i = 0; i < _count; i++)
        {
            ref readonly var x = ref _ring[(_start + i) % _ring.Length];
            if (x.UpBytesPerSec > max) max = x.UpBytesPerSec;
            if (x.DownBytesPerSec > max) max = x.DownBytesPerSec;
        }
        Max = max;
    }

    private View? _view;

    private sealed class View(GraphBuffer owner) : IReadOnlyList<Sample>
    {
        public int Count => owner._count;

        public Sample this[int index]
        {
            get
            {
                if ((uint)index >= (uint)owner._count) throw new ArgumentOutOfRangeException(nameof(index));
                return owner._ring[(owner._start + index) % owner._ring.Length];
            }
        }

        public IEnumerator<Sample> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
