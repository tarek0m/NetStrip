using System.Runtime.InteropServices;
using NetStrip.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace NetStrip.Shell.Net;

/// <summary>
/// <see cref="IInterfaceTable"/> over <c>GetIfTable2</c> (§4). Per-adapter metadata (GUID string, name, kind, IPv4,
/// inclusion) is cached and rebuilt only after <c>NotifyIpInterfaceChange</c> fires or an unknown interface appears,
/// so a steady-state <see cref="Read"/> only allocates one record per included adapter.
/// Excluded interfaces (loopback, tunnels, non-Ethernet/Wi-Fi/WWAN/PPP types, virtual or filter interfaces) are
/// not returned; <see cref="BestInterfaceId"/> may still name one, which Core treats like any unknown best route.
/// </summary>
public sealed unsafe class Win32InterfaceTable : IInterfaceTable, IDisposable
{
    private const uint IfTypeEthernet = 6;
    private const uint IfTypePpp = 23;
    private const uint IfTypeWifi = 71;
    private const uint IfTypeWwan = 243;
    private const uint IfTypeWwan2 = 244;
    private const uint ErrorBufferOverflow = 111;
    private const int PollRefreshReads = 30;

    private sealed record Meta(string Id, string Name, AdapterKind Kind, string? IPv4, bool Included);

    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Meta> _metaByLuid = new();
    private readonly Dictionary<uint, string> _idByIndex = new();
    private readonly Dictionary<uint, string> _ipv4ByIndex = new();
    private readonly List<InterfaceCounters> _result = new();
    private readonly PIPINTERFACE_CHANGE_CALLBACK _callback;   // kept alive for the lifetime of the registration

    private HANDLE _notifyHandle;
    private bool _notifyRegistered;
    private int _dirty = 1;
    private int _readsSinceRefresh;
    private bool _disposed;
    private bool _readFailureLogged;

    public Win32InterfaceTable(ILog log)
    {
        _log = log;
        _callback = OnInterfaceChanged;

        HANDLE handle = default;
        var err = PInvoke.NotifyIpInterfaceChange(ADDRESS_FAMILY.AF_UNSPEC, _callback, null, false, ref handle);
        if (err == WIN32_ERROR.NO_ERROR)
        {
            _notifyHandle = handle;
            _notifyRegistered = true;
        }
        else
        {
            _log.Warn($"NotifyIpInterfaceChange failed ({err}); refreshing adapter metadata by polling instead");
        }
    }

    /// <summary>
    /// Snapshot of the included adapters. The returned list is reused: it is valid until the next call,
    /// and there must be a single consumer (the sampler loop).
    /// </summary>
    public IReadOnlyList<InterfaceCounters> Read()
    {
        lock (_gate)
        {
            _result.Clear();
            if (_disposed) return _result;

            MIB_IF_TABLE2* table;
            var err = PInvoke.GetIfTable2(out table);
            if (err != WIN32_ERROR.NO_ERROR)
            {
                if (!_readFailureLogged)
                {
                    _readFailureLogged = true;
                    _log.Warn($"GetIfTable2 failed ({err})");
                }
                return _result;
            }
            _readFailureLogged = false;

            try
            {
                var rows = table->Table.AsSpan((int)table->NumEntries);

                if (!_notifyRegistered && ++_readsSinceRefresh >= PollRefreshReads)
                    Interlocked.Exchange(ref _dirty, 1);
                if (Interlocked.Exchange(ref _dirty, 0) == 1 || HasUnknownInterface(rows))
                    RefreshMetadata(rows);

                foreach (ref readonly var row in rows)
                {
                    if (_metaByLuid.TryGetValue(row.InterfaceLuid.Value, out var meta) && meta.Included)
                        _result.Add(new InterfaceCounters(meta.Id, meta.Name, meta.Kind, meta.IPv4,
                            row.InOctets, row.OutOctets, true));
                }
            }
            finally
            {
                PInvoke.FreeMibTable(table);
            }
            return _result;
        }
    }

    /// <summary>The interface Windows would route 1.1.1.1 through, as an adapter id; null if there is none.</summary>
    public string? BestInterfaceId()
    {
        SOCKADDR_IN destination = default;
        destination.sin_family = ADDRESS_FAMILY.AF_INET;
        destination.sin_addr.S_un.S_addr = 0x01010101; // 1.1.1.1, byte order is irrelevant for equal bytes

        uint index;
        if (PInvoke.GetBestInterfaceEx((SOCKADDR*)&destination, &index) != 0) return null;

        lock (_gate)
            return _idByIndex.TryGetValue(index, out var id) ? id : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        // Outside the lock: CancelMibChangeNotify2 waits for an in-flight callback, which never takes the lock anyway.
        if (_notifyRegistered)
        {
            var err = PInvoke.CancelMibChangeNotify2(_notifyHandle);
            if (err != WIN32_ERROR.NO_ERROR) _log.Warn($"CancelMibChangeNotify2 failed ({err})");
            _notifyRegistered = false;
        }
    }

    // Runs on a system thread pool thread; must stay trivial and lock-free.
    private void OnInterfaceChanged(void* callerContext, MIB_IPINTERFACE_ROW* row, MIB_NOTIFICATION_TYPE notificationType)
        => Interlocked.Exchange(ref _dirty, 1);

    private bool HasUnknownInterface(Span<MIB_IF_ROW2> rows)
    {
        foreach (ref readonly var row in rows)
            if (!_metaByLuid.ContainsKey(row.InterfaceLuid.Value)) return true;
        return false;
    }

    private void RefreshMetadata(Span<MIB_IF_ROW2> rows)
    {
        _readsSinceRefresh = 0;
        LoadIPv4Addresses();

        _metaByLuid.Clear();
        _idByIndex.Clear();
        foreach (ref readonly var row in rows)
        {
            var id = row.InterfaceGuid.ToString("D");
            _ipv4ByIndex.TryGetValue(row.InterfaceIndex, out var ipv4);
            _metaByLuid[row.InterfaceLuid.Value] = new Meta(
                id, row.Alias.ToString(), KindOf(row.Type), ipv4, IsIncluded(in row));
            _idByIndex[row.InterfaceIndex] = id;
        }
    }

    private static AdapterKind KindOf(uint ifType) => ifType switch
    {
        IfTypeEthernet => AdapterKind.Ethernet,
        IfTypeWifi => AdapterKind.WiFi,
        IfTypeWwan or IfTypeWwan2 => AdapterKind.Cellular,
        _ => AdapterKind.Other,
    };

    // §4: Ethernet, 802.11, WWAN or PPP only; no tunnels; hardware interfaces that are not filter drivers.
    private static bool IsIncluded(in MIB_IF_ROW2 row)
    {
        if (row.Type is not (IfTypeEthernet or IfTypeWifi or IfTypeWwan or IfTypeWwan2 or IfTypePpp)) return false;
        if ((int)row.TunnelType != 0) return false;
        return row.InterfaceAndOperStatusFlags.HardwareInterface && !row.InterfaceAndOperStatusFlags.FilterInterface;
    }

    private void LoadIPv4Addresses()
    {
        _ipv4ByIndex.Clear();
        const GET_ADAPTERS_ADDRESSES_FLAGS flags = GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_SKIP_ANYCAST
            | GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_SKIP_MULTICAST
            | GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_SKIP_DNS_SERVER
            | GET_ADAPTERS_ADDRESSES_FLAGS.GAA_FLAG_SKIP_FRIENDLY_NAME;

        uint size = 16 * 1024;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = (IP_ADAPTER_ADDRESSES_LH*)NativeMemory.Alloc(size);
            try
            {
                var rc = PInvoke.GetAdaptersAddresses((uint)ADDRESS_FAMILY.AF_INET, flags, buffer, ref size);
                if (rc == ErrorBufferOverflow) continue;   // size now holds the required length
                if (rc != 0)
                {
                    _log.Warn($"GetAdaptersAddresses failed ({rc})");
                    return;
                }

                for (var adapter = buffer; adapter != null; adapter = adapter->Next)
                {
                    for (var unicast = adapter->FirstUnicastAddress; unicast != null; unicast = unicast->Next)
                    {
                        var sockaddr = unicast->Address.lpSockaddr;
                        if (sockaddr == null || sockaddr->sa_family != ADDRESS_FAMILY.AF_INET) continue;
                        var b = ((SOCKADDR_IN*)sockaddr)->sin_addr.S_un.S_un_b;
                        _ipv4ByIndex[adapter->IfIndex] = $"{b.s_b1}.{b.s_b2}.{b.s_b3}.{b.s_b4}";
                        break;
                    }
                }
                return;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }
        _log.Warn("GetAdaptersAddresses kept overflowing its buffer; adapter IPv4 addresses unavailable");
    }
}
