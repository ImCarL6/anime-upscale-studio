using System.Globalization;
using System.Net;
using System.Net.Sockets;
using NetMQ;
using NetMQ.Sockets;

namespace Anime4KEncoder;

// Each encoder has a separate, loopback-only command endpoint. Video bytes never use it.
internal sealed class LivePacing : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private double _requested;
    private double _appliedRate;
    internal double AppliedRate => Volatile.Read(ref _appliedRate);
    internal string? Error { get; private set; }
    internal bool IsDisposed { get; private set; }
    internal string Endpoint { get; }
    internal string Filter { get; }

    internal LivePacing(double rate)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Endpoint = $"tcp://127.0.0.1:{port}";
        // Escaped at both FFmpeg filtergraph and option-parser levels.
        var bind = Endpoint.Replace(":", @"\:");
        Filter = $"zmq=bind_address='{bind}',realtime@throttle=limit={(rate >= 1000000 ? "1000000000" : "0.1")}:speed={rate.ToString(CultureInfo.InvariantCulture)}";
        _requested = rate;
        _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal void SetRate(double rate) => Volatile.Write(ref _requested, rate);

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            var rate = Volatile.Read(ref _requested);
            if (AppliedRate == rate) { _stop.Token.WaitHandle.WaitOne(100); continue; }
            try
            {
                using var socket = new RequestSocket();
                socket.Options.Linger = TimeSpan.Zero;
                socket.Connect(Endpoint);
                bool Command(string name, string value) =>
                    socket.TrySendFrame(TimeSpan.FromMilliseconds(300), "realtime@throttle " + name + " " + value) &&
                    socket.TryReceiveFrameString(TimeSpan.FromMilliseconds(700), out var reply) && reply.StartsWith("0 ", StringComparison.Ordinal);
                // Avoid per-frame discontinuity warnings at effectively unlimited speed;
                // restore the small limit before slowing down so no long sleep can accumulate.
                if (Command("limit", rate >= 1000000 ? "1000000000" : "0.1") &&
                    Command("speed", rate.ToString(CultureInfo.InvariantCulture)))
                {
                    Volatile.Write(ref _appliedRate, rate);
                    Error = null;
                }
                else Error = "Aguardando confirmação do controle de ritmo";
            }
            catch (Exception ex) { Error = ex.Message; }
            _stop.Token.WaitHandle.WaitOne(100);
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _stop.Cancel();
        _worker.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
