using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Mongo.Fakes.Server;

namespace Mongo.Fakes.Server;

public sealed class MongoFakeServer(IMongoBackend backend, int port = 0) : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private readonly ConcurrentDictionary<Guid, Task> _clientTasks = new();
    private readonly CommandRouter _router = new(backend);

    public IMongoBackend Backend { get; } = backend;
    public int Port { get; private set; } = port;
    public string ConnectionString => $"mongodb://127.0.0.1:{Port}/?directConnection=true";

    public async Task StartAsync(CancellationToken ct = default)
    {
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptLoopTask = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_listener == null)
                    break;
                TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;

                Guid clientId = Guid.NewGuid();
                var connection = new ClientConnection(client, _router);
                var clientTask = connection.ProcessAsync(ct);
                _clientTasks.TryAdd(clientId, clientTask);

                _ = clientTask.ContinueWith(t => { _clientTasks.TryRemove(clientId, out _); }, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener?.Stop();
        _cts?.Cancel();

        if (_acceptLoopTask != null)
        {
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        var tasks = _clientTasks.Values.ToList();
        _clientTasks.Clear();

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }

        _cts?.Dispose();
    }
}
