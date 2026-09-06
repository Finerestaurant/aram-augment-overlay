using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AramOverlay.Core;

/// <summary>
/// obs-websocket 5.x over the BCL's own <see cref="ClientWebSocket"/>.
///
/// The protocol is small enough that a library would be more dependency than
/// help: a Hello with a challenge, an Identify carrying the answer, and then
/// request/response pairs matched by id. Everything this tool asks OBS for is in
/// <see cref="ObsCapture"/> above this.
///
/// Note the CLI flags --websocket_port / --websocket_password only override
/// values; they do not switch the server on. `server_enabled` in obs-websocket's
/// own config.json is the only thing that does.
/// </summary>
public sealed class ObsClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiver;
    private int _nextId;

    public bool Connected => _socket.State == WebSocketState.Open;

    public async Task ConnectAsync(string host, int port, string password,
                                   TimeSpan? timeout = null)
    {
        using var connectCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
        await _socket.ConnectAsync(new Uri($"ws://{host}:{port}"), connectCts.Token);

        var hello = await ReceiveMessageAsync(connectCts.Token)
            ?? throw new IOException(Strings.Get("Obs.NoHello"));
        var helloData = hello["d"] ?? throw new IOException(Strings.Get("Obs.BadHello"));

        var identify = new JsonObject
        {
            ["op"] = 1,
            ["d"] = new JsonObject
            {
                ["rpcVersion"] = helloData["rpcVersion"]?.GetValue<int>() ?? 1,
                // No events are used; not subscribing keeps the socket quiet.
                ["eventSubscriptions"] = 0,
            },
        };
        if (helloData["authentication"] is JsonNode auth)
        {
            string challenge = auth["challenge"]!.GetValue<string>();
            string salt = auth["salt"]!.GetValue<string>();
            identify["d"]!["authentication"] = AuthResponse(password, salt, challenge);
        }
        await SendAsync(identify, connectCts.Token);

        var identified = await ReceiveMessageAsync(connectCts.Token)
            ?? throw new IOException(Strings.Get("Obs.NoAuthReply"));
        if (identified["op"]?.GetValue<int>() != 2)
            throw new IOException(Strings.Get("Obs.AuthFailed"));

        _receiver = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
    private static string AuthResponse(string password, string salt, string challenge)
    {
        string secret = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public async Task<JsonNode?> RequestAsync(string type, JsonObject? data = null,
                                              TimeSpan? timeout = null)
    {
        string id = Interlocked.Increment(ref _nextId).ToString();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var message = new JsonObject
        {
            ["op"] = 6,
            ["d"] = new JsonObject
            {
                ["requestType"] = type,
                ["requestId"] = id,
                ["requestData"] = data ?? new JsonObject(),
            },
        };

        try
        {
            await SendAsync(message, _cts.Token);
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(20), _cts.Token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(JsonNode message, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(token);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<JsonNode?> ReceiveMessageAsync(CancellationToken token)
    {
        var buffer = new ArrayBufferWriter<byte>(8192);
        var chunk = new byte[16384];
        while (true)
        {
            var result = await _socket.ReceiveAsync(chunk, token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            buffer.Write(chunk.AsSpan(0, result.Count));
            if (result.EndOfMessage)
                break;
        }
        return JsonNode.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(_cts.Token);
                if (message is null)
                    break;
                if (message["op"]?.GetValue<int>() != 7)
                    continue;                       // events and the rest are not used

                var d = message["d"]!;
                string id = d["requestId"]!.GetValue<string>();
                if (!_pending.TryGetValue(id, out var tcs))
                    continue;

                var status = d["requestStatus"];
                if (status?["result"]?.GetValue<bool>() == true)
                {
                    tcs.TrySetResult(d["responseData"]);
                }
                else
                {
                    int code = status?["code"]?.GetValue<int>() ?? 0;
                    string comment = status?["comment"]?.GetValue<string>() ?? "";
                    tcs.TrySetException(new ObsRequestException(code, comment));
                }
            }
        }
        catch (Exception exc)
        {
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(exc);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch { /* going away regardless */ }
        _socket.Dispose();
        _cts.Dispose();
    }
}

public sealed class ObsRequestException(int code, string comment)
    : Exception(Strings.Get("Obs.RequestFailed", code, comment))
{
    public int Code { get; } = code;
}
