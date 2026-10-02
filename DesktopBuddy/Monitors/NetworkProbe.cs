using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DesktopBuddy.Monitors;

public sealed record NetworkSummary(
    int Samples,
    int Spikes,
    int LocalSpikes,          // the router/Wi-Fi was also slow or dropped -> problem is on your side
    double? AverageMs,
    double? WorstMs,
    int? WeakestSignalPercent,
    string? Band,             // "2.4 GHz" / "5 GHz" / "6 GHz"
    bool Wired)
{
    /// <summary>Plain-English verdict for the game report.</summary>
    public string Verdict()
    {
        if (Samples < 10) return "Not enough data (short session).";
        string connection = Wired ? "cable" : $"Wi-Fi{(Band != null ? $" ({Band})" : "")}{(WeakestSignalPercent is int s ? $", signal down to {s}%" : "")}";
        if (Spikes == 0) return $"Connection was steady the whole time (avg {AverageMs:0} ms, {connection}).";

        string where = LocalSpikes * 2 >= Spikes
            ? (Wired ? "your own network (router or cable)." :
               "your Wi-Fi: the link to your router itself was slow or dropping." +
               (Band == "2.4 GHz" ? " Switching to the 5 GHz network or using a cable would help most." :
                WeakestSignalPercent < 50 ? " Move closer to the router or use a cable." : " A cable would rule it out."))
            : "past your router (your internet provider or the route to the server). Not something on your laptop.";
        return $"{Spikes} lag spike{(Spikes == 1 ? "" : "s")} (worst {WorstMs:0} ms, avg {AverageMs:0} ms, {connection}). Mostly from {where}";
    }
}

/// <summary>
/// During games, pings the router and the internet every 2 seconds and notes the Wi-Fi signal, so the
/// game report can say whether lag came from your Wi-Fi or from further away. It measures your
/// connection, not Roblox's servers (their traffic can't be measured without a capture driver).
/// </summary>
public sealed class NetworkProbe
{
    private const int SpikeMs = 150;
    private static readonly IPAddress InternetTarget = IPAddress.Parse("1.1.1.1");

    private CancellationTokenSource? _cts;
    private Task? _task;
    private readonly List<(double? Router, double? Internet)> _samples = [];
    private readonly List<int> _signals = [];
    private string? _band;
    private bool _wired;
    private readonly object _gate = new();

    public void Start()
    {
        if (_task != null) return;
        lock (_gate)
        {
            _samples.Clear();
            _signals.Clear();
            _band = null;
        }
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => Run(_cts.Token));
    }

    public NetworkSummary Stop()
    {
        _cts?.Cancel();
        try { _task?.Wait(3000); } catch { /* cancelled */ }
        _task = null;
        _cts?.Dispose();
        _cts = null;

        lock (_gate)
        {
            var internet = _samples.Where(s => s.Internet != null).Select(s => s.Internet!.Value).ToList();
            int spikes = 0, local = 0;
            foreach (var (router, net) in _samples)
            {
                if (net is double ms && ms < SpikeMs) continue;
                spikes++;
                if (router is null || router > 50) local++; // router slow or lost too
            }
            return new NetworkSummary(_samples.Count, spikes, local,
                internet.Count > 0 ? internet.Average() : null,
                internet.Count > 0 ? internet.Max() : null,
                _signals.Count > 0 ? _signals.Min() : null,
                _band, _wired);
        }
    }

    private async Task Run(CancellationToken token)
    {
        using var ping = new Ping();
        int tick = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                IPAddress? gateway = DefaultGateway(out bool wired);
                _wired = wired;
                double? router = gateway == null ? null : await PingMs(ping, gateway);
                double? internet = await PingMs(ping, InternetTarget);
                lock (_gate) _samples.Add((router, internet));

                if (!wired && tick++ % 15 == 0) ReadWifi(); // every ~30 s
            }
            catch (Exception ex)
            {
                Log.Error("Network probe failed", ex);
            }

            try { await Task.Delay(2000, token); } catch (TaskCanceledException) { break; }
        }
    }

    private static async Task<double?> PingMs(Ping ping, IPAddress target)
    {
        try
        {
            PingReply reply = await ping.SendPingAsync(target, 1000);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }

    private static IPAddress? DefaultGateway(out bool wired)
    {
        wired = false;
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var gw = nic.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gw == null) continue;
            wired = nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211;
            return gw;
        }
        return null;
    }

    /// <summary>Signal % and band from "netsh wlan show interfaces" (English Windows labels).</summary>
    private void ReadWifi()
    {
        try
        {
            var start = new ProcessStartInfo("netsh.exe", "wlan show interfaces")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using Process p = Process.Start(start)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string label = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
                if (label.Equals("Signal", StringComparison.OrdinalIgnoreCase) && int.TryParse(value.TrimEnd('%'), out int signal))
                    lock (_gate) _signals.Add(signal);
                else if (label.Equals("Band", StringComparison.OrdinalIgnoreCase))
                    _band = value;
                else if (label.Equals("Channel", StringComparison.OrdinalIgnoreCase) && _band == null && int.TryParse(value, out int channel))
                    _band = channel <= 14 ? "2.4 GHz" : "5 GHz";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading Wi-Fi info failed", ex);
        }
    }
}
