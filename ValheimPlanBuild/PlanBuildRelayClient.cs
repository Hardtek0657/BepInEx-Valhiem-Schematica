using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ValheimPlanBuild;

internal static class PlanBuildRelayClient
{
    private static readonly ConcurrentQueue<string> Incoming = new();
    private static readonly ConcurrentQueue<string> StatusMessages = new();
    private static readonly SemaphoreSlim SendLock = new(1, 1);
    private static readonly string ClientId = Guid.NewGuid().ToString("N");

    private static CancellationTokenSource? _cancel;
    private static ClientWebSocket? _socket;
    private static Task? _runner;
    private static bool _connecting;
    private static bool _wasConnected;
    private static float _reconnectTimer;
    private static string _status = "offline";

    public static string StatusText => _status;
    public static bool CanSendRealtimeChanges => IsOpen;

    public static void Start()
    {
        _cancel = new CancellationTokenSource();
        _reconnectTimer = 0f;
    }

    public static void Stop()
    {
        CancellationTokenSource? cancel = _cancel;
        _cancel = null;
        cancel?.Cancel();
        cancel?.Dispose();
        ClientWebSocket? socket = _socket;
        _socket = null;
        socket?.Dispose();
    }

    public static void ForceReconnect()
    {
        ClientWebSocket? socket = _socket;
        _socket = null;
        socket?.Abort();
        socket?.Dispose();

        _connecting = false;
        _wasConnected = false;
        _reconnectTimer = 0f;
        QueueStatus("Plan build relay reconnect requested.");
    }

    public static void Update(float deltaTime)
    {
        DrainStatusMessages();
        DrainIncoming();

        if (!PlanBuildPlugin.ModEnabled.Value || !PlanBuildPlugin.IsRelayEnabled() || _cancel == null)
        {
            _status = "disabled";
            return;
        }

        if (IsOpen || _connecting)
        {
            _status = IsOpen ? "connected" : "connecting";
            return;
        }

        _reconnectTimer -= deltaTime;
        if (_reconnectTimer > 0f)
        {
            return;
        }

        _reconnectTimer = Math.Max(1f, PlanBuildPlugin.RelayReconnectSeconds.Value);
        _runner = Task.Run(() => RunAsync(_cancel.Token));
    }

    public static void SendPlace(PlanPiece piece)
    {
        string name = PlanBuildPlugin.CurrentPlanName;
        if (string.IsNullOrWhiteSpace(name))
        {
            QueueStatus("Load or create a plan before placing ghost pieces.");
            return;
        }

        SendFrame(PlanBuildFrame.Place(ClientId, name, piece));
    }

    public static void SendRemove(string id)
    {
        string name = PlanBuildPlugin.CurrentPlanName;
        if (string.IsNullOrWhiteSpace(name))
        {
            QueueStatus("Load or create a plan before removing ghost pieces.");
            return;
        }

        SendFrame(PlanBuildFrame.Remove(ClientId, name, id));
    }

    public static void SendSave(string name, IReadOnlyList<PlanPiece> pieces)
    {
        SendFrame(PlanBuildFrame.Save(ClientId, name, pieces));
    }

    public static void SendLoadRequest(string name)
    {
        SendFrame(PlanBuildFrame.LoadRequest(ClientId, name));
    }

    private static async Task RunAsync(CancellationToken token)
    {
        if (_connecting)
        {
            return;
        }

        _connecting = true;
        try
        {
            using ClientWebSocket socket = new();
            _socket = socket;
            Uri relayUri = new(PlanBuildPlugin.RelayServerUrl.Value);
            QueueStatus("Connecting plan build relay: " + relayUri);
            await socket.ConnectAsync(relayUri, token).ConfigureAwait(false);
            _wasConnected = true;
            _status = "connected";
            QueueStatus("Plan build relay connected.");
            await SendFrameAsync(PlanBuildFrame.Hello(ClientId)).ConfigureAwait(false);
            bool disconnectLogged = await ReceiveLoopAsync(socket, token).ConfigureAwait(false);
            if (!disconnectLogged)
            {
                QueueStatus("Plan build relay disconnected.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            string message = (_wasConnected ? "Plan build relay disconnected: " : "Plan build relay connection failed: ") + FormatException(ex);
            _status = message;
            QueueStatus(message);
        }
        finally
        {
            _wasConnected = false;
            _socket = null;
            _connecting = false;
        }
    }

    private static async Task<bool> ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            return await ReceiveLoopInternalAsync(socket, token, buffer).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> ReceiveLoopInternalAsync(ClientWebSocket socket, CancellationToken token, byte[] buffer)
    {
        StringBuilder builder = new();
        bool disconnectLogged = false;

        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
            }
            catch (WebSocketException ex)
            {
                QueueStatus("Plan build relay disconnected: " + ex.Message);
                disconnectLogged = true;
                break;
            }
            catch (System.IO.IOException ex)
            {
                QueueStatus("Plan build relay disconnected: " + ex.Message);
                disconnectLogged = true;
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            Incoming.Enqueue(builder.ToString());
            builder.Clear();
        }

        return disconnectLogged;
    }

    private static void SendFrame(string frame)
    {
        if (!PlanBuildPlugin.IsRelayEnabled())
        {
            return;
        }

        _ = Task.Run(() => SendFrameAsync(frame));
    }

    private static async Task SendFrameAsync(string frame)
    {
        try
        {
            ClientWebSocket? socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                QueueStatus("Plan build relay is not connected.");
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(frame);
            await SendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                SendLock.Release();
            }
        }
        catch (Exception ex)
        {
            _status = "send error: " + ex.Message;
            QueueStatus("Plan build relay send failed: " + ex.Message);
            _socket = null;
        }
    }

    private static void DrainIncoming()
    {
        while (Incoming.TryDequeue(out string frame))
        {
            if (!PlanBuildFrame.TryParse(frame, out string senderClientId, out _, out string op, out string name, out PlanPiece? piece, out List<PlanPiece> pieces, out string removeId))
            {
                continue;
            }

            if (senderClientId == ClientId)
            {
                continue;
            }

            if (op != "LOAD_DATA" && !string.IsNullOrWhiteSpace(name) && name != PlanBuildPlugin.CurrentPlanName)
            {
                continue;
            }

            if (op == "PLACE" && piece != null)
            {
                PlanBuildPlugin.World.AddOrUpdate(piece, localChange: false);
            }
            else if (op == "REMOVE")
            {
                PlanBuildPlugin.World.Remove(removeId, localChange: false);
            }
            else if (op == "LOAD_DATA" || op == "SAVE")
            {
                PlanBuildPlugin.LoadRemoteSave(name, pieces);
            }
        }
    }

    private static void DrainStatusMessages()
    {
        while (StatusMessages.TryDequeue(out string status))
        {
            if (Chat.instance)
            {
                Chat.instance.AddString("PlanBuild: " + status);
            }
            else
            {
                PlanBuildPlugin.Log.LogInfo(status);
            }
        }
    }

    private static void QueueStatus(string status)
    {
        StatusMessages.Enqueue(status);
        PlanBuildPlugin.Log.LogInfo(status);
    }

    private static string FormatException(Exception ex)
    {
        StringBuilder builder = new();
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (builder.Length > 0)
            {
                builder.Append(" -> ");
            }

            builder.Append(current.GetType().Name);
            builder.Append(": ");
            builder.Append(current.Message);
        }

        return builder.ToString();
    }

    private static bool IsOpen => _socket?.State == WebSocketState.Open;
}
