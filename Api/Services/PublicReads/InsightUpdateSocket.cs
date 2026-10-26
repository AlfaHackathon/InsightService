using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using InsightService.Contracts.PublicReads;

namespace InsightService.Services.PublicReads;

public sealed record InsightSocketLimits
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int QueueCapacity { get; init; } = 64;
}

// One sender and one receiver per connection; every task is canceled and awaited.
public sealed class InsightUpdateSocket(InsightUpdateSnapshot snapshots, InsightSocketLimits limits)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task Run(WebSocket socket, Guid user, PublicInsightMembership scope, DateTimeOffset expires,
        IReadOnlyList<InsightUpdateHead> baseline, CancellationToken requestAborted)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var remaining = expires - DateTimeOffset.UtcNow;
        lifetime.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var queue = Channel.CreateBounded<object>(new BoundedChannelOptions(limits.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var status = WebSocketCloseStatus.NormalClosure;
        var reason = "reconnect_and_refetch";
        var lastPong = Environment.TickCount64;
        void Stop(WebSocketCloseStatus closeStatus, string closeReason)
        {
            status = closeStatus;
            reason = closeReason;
            lifetime.Cancel();
        }
        queue.Writer.TryWrite(new { eventType = "ready", time = DateTimeOffset.UtcNow, expiresAt = expires });

        async Task Produce()
        {
            var known = baseline.ToDictionary(head => (head.Kind, head.Id), head => head.Version);
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(limits.PollInterval, lifetime.Token);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                budget.CancelAfter(limits.ReadTimeout);
                var heads = await snapshots.Read(user, scope, budget.Token);
                var next = new Dictionary<(string Kind, string Id), string>();
                foreach (var head in heads)
                {
                    var key = (head.Kind, head.Id);
                    if (known.TryGetValue(key, out var version) && version == head.Version)
                    {
                        next[key] = version;
                        continue;
                    }
                    var update = await snapshots.Authorize(user, scope, head, budget.Token);
                    if (update is null)
                    { continue; }
                    if (!queue.Writer.TryWrite(update))
                    {
                        Stop((WebSocketCloseStatus)1013, "slow_client_refetch");
                        return;
                    }
                    next[key] = head.Version;
                }
                known = next;
            }
        }

        async Task Send(object value)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            budget.CancelAfter(limits.SendTimeout);
            if (value is InsightUpdateEvent && await snapshots.Scope(user, budget.Token) != scope)
            {
                Stop(WebSocketCloseStatus.PolicyViolation, "membership_changed");
                return;
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, budget.Token);
        }

        async Task Consume()
        {
            var pending = queue.Reader.WaitToReadAsync(lifetime.Token).AsTask();
            var heartbeat = Task.Delay(limits.HeartbeatInterval, lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                if (heartbeat.IsCompleted || await Task.WhenAny(pending, heartbeat) == heartbeat)
                {
                    if (Environment.TickCount64 - Interlocked.Read(ref lastPong) > limits.PongTimeout.TotalMilliseconds)
                    {
                        Stop(WebSocketCloseStatus.PolicyViolation, "pong_timeout");
                        return;
                    }
                    await Send(new { eventType = "heartbeat", time = DateTimeOffset.UtcNow });
                    heartbeat = Task.Delay(limits.HeartbeatInterval, lifetime.Token);
                }
                else
                {
                    if (!await pending)
                    { return; }
                    // One event per turn so heartbeat cannot starve under a continuous backlog.
                    if (queue.Reader.TryRead(out var value))
                    { await Send(value); }
                    pending = queue.Reader.WaitToReadAsync(lifetime.Token).AsTask();
                }
            }
        }

        async Task Receive()
        {
            var buffer = new byte[256];
            while (!lifetime.IsCancellationRequested)
            {
                var frame = await socket.ReceiveAsync(buffer.AsMemory(), receiving.Token);
                if (frame.MessageType == WebSocketMessageType.Close)
                { lifetime.Cancel(); return; }
                if (frame.MessageType != WebSocketMessageType.Text || !frame.EndOfMessage)
                {
                    Stop(WebSocketCloseStatus.InvalidMessageType, "pong_required");
                    return;
                }
                using var json = JsonDocument.Parse(buffer.AsMemory(0, frame.Count));
                if (json.RootElement.ValueKind != JsonValueKind.Object ||
                    !json.RootElement.TryGetProperty("eventType", out var type) || type.GetString() != "pong" ||
                    json.RootElement.EnumerateObject().Count() != 1)
                {
                    Stop(WebSocketCloseStatus.PolicyViolation, "pong_required");
                    return;
                }
                Interlocked.Exchange(ref lastPong, Environment.TickCount64);
            }
        }

        async Task Guard(Func<Task> operation)
        {
            try
            { await operation(); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (PublicInsightReadException error) when (error.StatusCode is 401 or 403)
            { Stop(WebSocketCloseStatus.PolicyViolation, "membership_required"); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Stop((WebSocketCloseStatus)1013, "unavailable_refetch"); }
        }

        var tasks = new[] { Guard(Produce), Guard(Consume), Guard(Receive) };
        try
        {
            await Task.WhenAny(tasks);
            lifetime.Cancel();
            await Task.WhenAll(tasks[0], tasks[1]);
        }
        finally
        {
            lifetime.Cancel();
            queue.Writer.TryComplete();
            using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(status, reason, close.Token);
                }
                await tasks[2].WaitAsync(close.Token);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException) { socket.Abort(); }
            finally
            {
                receiving.Cancel();
                await tasks[2];
            }
        }
    }
}
