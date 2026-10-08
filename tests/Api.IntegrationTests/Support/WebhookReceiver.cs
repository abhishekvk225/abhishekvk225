using System.Net;
using System.Text;

namespace NexaVerify.Api.IntegrationTests.Support;

/// <summary>A tiny HTTP endpoint on loopback that records what it receives, standing in for a client's webhook receiver.</summary>
public sealed class WebhookReceiver : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<Received> _received = [];

    public sealed record Received(string Body, IReadOnlyDictionary<string, string> Headers);

    public WebhookReceiver()
    {
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}/hook";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = Task.Run(Loop);
    }

    public string Url { get; }

    public int StatusCode { get; set; } = 200;

    public string? RedirectTo { get; set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public IReadOnlyList<Received> Requests
    {
        get
        {
            lock (_received)
            {
                return _received.ToList();
            }
        }
    }

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var headers = context.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => context.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
                lock (_received)
                {
                    _received.Add(new Received(body, headers));
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay);
                }

                try
                {
                    if (RedirectTo is not null)
                    {
                        context.Response.StatusCode = 302;
                        context.Response.RedirectLocation = RedirectTo;
                    }
                    else
                    {
                        context.Response.StatusCode = StatusCode;
                    }

                    context.Response.Close();
                }
                catch
                {
                    // client gave up
                }
            });
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Close();
        try
        {
            await _loop;
        }
        catch
        {
            // listener closed
        }
    }
}
