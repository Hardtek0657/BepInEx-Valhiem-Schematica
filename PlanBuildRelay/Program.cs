using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

const string Prefix = "PLANBUILD\t2\t";
string saveDirectory = Path.Combine(AppContext.BaseDirectory, "planbuild-saves");
Directory.CreateDirectory(saveDirectory);
ConcurrentDictionary<string, ConcurrentDictionary<string, string[]>> saveStates = new();
ConcurrentDictionary<string, byte> loadedSaveKeys = new();
ConcurrentDictionary<string, SemaphoreSlim> saveLocks = new();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5001");
WebApplication app = builder.Build();

ConcurrentDictionary<Guid, WebSocket> clients = new();
ConcurrentDictionary<Guid, string> clientWorlds = new();
ConcurrentDictionary<Guid, string> clientSaves = new();

app.UseWebSockets();

app.Map("/planbuild", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket endpoint. Connect with ws://host:port/planbuild");
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    Guid id = Guid.NewGuid();
    clients[id] = socket;
    Console.WriteLine($"[{Now()}] Client connected: {id}. Connected clients: {clients.Count}");

    try
    {
        await ReceiveLoopAsync(id, socket, clients, clientWorlds, clientSaves, saveDirectory, saveStates, loadedSaveKeys, saveLocks, context.RequestAborted);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"[{Now()}] Client connection canceled: {id}");
    }
    catch (WebSocketException ex)
    {
        Console.WriteLine($"[{Now()}] Client closed WebSocket abruptly: {id}. {ex.Message}");
    }
    catch (IOException ex)
    {
        Console.WriteLine($"[{Now()}] Client connection ended: {id}. {ex.Message}");
    }
    finally
    {
        clients.TryRemove(id, out _);
        clientWorlds.TryRemove(id, out _);
        clientSaves.TryRemove(id, out _);
        Console.WriteLine($"[{Now()}] Client disconnected: {id}. Connected clients: {clients.Count}");
    }
});

app.MapGet("/", () => "PlanBuild relay is running. WebSocket endpoint: /planbuild");

app.Run();

static async Task ReceiveLoopAsync(
    Guid senderId,
    WebSocket sender,
    ConcurrentDictionary<Guid, WebSocket> clients,
    ConcurrentDictionary<Guid, string> clientWorlds,
    ConcurrentDictionary<Guid, string> clientSaves,
    string saveDirectory,
    ConcurrentDictionary<string, ConcurrentDictionary<string, string[]>> saveStates,
    ConcurrentDictionary<string, byte> loadedSaveKeys,
    ConcurrentDictionary<string, SemaphoreSlim> saveLocks,
    CancellationToken cancellationToken)
{
    byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
    List<byte> frame = new();
    try
    {
        while (!cancellationToken.IsCancellationRequested && sender.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await sender.ReceiveAsync(buffer, cancellationToken);
            }
            catch (WebSocketException ex)
            {
                Console.WriteLine($"[{Now()}] Receive stopped for {senderId}: {ex.Message}");
                break;
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[{Now()}] Receive ended for {senderId}: {ex.Message}");
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            for (int i = 0; i < result.Count; i++)
            {
                frame.Add(buffer[i]);
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            string text = Encoding.UTF8.GetString(frame.ToArray());
            frame.Clear();
            await HandleFrameAsync(sender, senderId, text, clients, clientWorlds, clientSaves, saveDirectory, saveStates, loadedSaveKeys, saveLocks, cancellationToken);
        }

        if (sender.State == WebSocketState.Open || sender.State == WebSocketState.CloseReceived)
        {
            try
            {
                await sender.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
            catch (IOException)
            {
            }
        }
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

static async Task HandleFrameAsync(
    WebSocket sender,
    Guid senderId,
    string frame,
    ConcurrentDictionary<Guid, WebSocket> clients,
    ConcurrentDictionary<Guid, string> clientWorlds,
    ConcurrentDictionary<Guid, string> clientSaves,
    string saveDirectory,
    ConcurrentDictionary<string, ConcurrentDictionary<string, string[]>> saveStates,
    ConcurrentDictionary<string, byte> loadedSaveKeys,
    ConcurrentDictionary<string, SemaphoreSlim> saveLocks,
    CancellationToken cancellationToken)
{
    if (!TryReadPlanFrame(frame, out string worldKey, out string op, out string saveName))
    {
        Console.WriteLine($"[{Now()}] Ignored non-plan frame from {senderId}");
        return;
    }

    clientWorlds[senderId] = worldKey;

    if (op == "HELLO")
    {
        Console.WriteLine($"[{Now()}] HELLO world={worldKey} from {senderId}");
        return;
    }

    if (op == "PLACE" && saveName.Length > 0 && TryReadPlaceFields(frame, out string pieceId, out string[] pieceFields))
    {
        clientSaves[senderId] = saveName;
        await WithSaveLockAsync(worldKey, saveName, saveLocks, cancellationToken, async activeCancellationToken =>
        {
            ConcurrentDictionary<string, string[]> activePieces = GetSaveState(saveStates, worldKey, saveName);
            EnsureSaveLoaded(saveDirectory, worldKey, saveName, activePieces, loadedSaveKeys);
            activePieces[pieceId] = pieceFields;
            Console.WriteLine($"[{Now()}] PLACE world={worldKey} save={saveName} {FormatPieceLog(pieceFields)}");
            await PersistSaveAsync(saveDirectory, worldKey, saveName, activePieces, activeCancellationToken);
            await BroadcastAsync(clients, clientWorlds, clientSaves, worldKey, saveName, frame, activeCancellationToken);
        });
        return;
    }

    if (op == "REMOVE" && saveName.Length > 0 && TryReadRemoveId(frame, out string removeId))
    {
        clientSaves[senderId] = saveName;
        await WithSaveLockAsync(worldKey, saveName, saveLocks, cancellationToken, async activeCancellationToken =>
        {
            ConcurrentDictionary<string, string[]> activePieces = GetSaveState(saveStates, worldKey, saveName);
            EnsureSaveLoaded(saveDirectory, worldKey, saveName, activePieces, loadedSaveKeys);
            activePieces.TryRemove(removeId, out string[]? removedFields);
            Console.WriteLine($"[{Now()}] REMOVE world={worldKey} save={saveName} id={removeId}" + (removedFields == null ? "" : " " + FormatPieceLog(removedFields)));
            await PersistSaveAsync(saveDirectory, worldKey, saveName, activePieces, activeCancellationToken);
            await BroadcastAsync(clients, clientWorlds, clientSaves, worldKey, saveName, frame, activeCancellationToken);
        });
        return;
    }

    Console.WriteLine($"[{Now()}] {op} world={worldKey} save={saveName} from {senderId}");

    if (op == "SAVE" && saveName.Length > 0)
    {
        clientSaves[senderId] = saveName;
        await WithSaveLockAsync(worldKey, saveName, saveLocks, cancellationToken, async activeCancellationToken =>
        {
            ConcurrentDictionary<string, string[]> activePieces = GetSaveState(saveStates, worldKey, saveName);
            int savedPieces = LoadStateFrame(frame, activePieces);
            loadedSaveKeys[StateKey(worldKey, saveName)] = 1;
            Console.WriteLine($"[{Now()}] SAVE_APPLIED world={worldKey} save={saveName} pieces={savedPieces}");
            await PersistSaveAsync(saveDirectory, worldKey, saveName, activePieces, activeCancellationToken);
            await BroadcastAsync(clients, clientWorlds, clientSaves, worldKey, saveName, frame, activeCancellationToken);
        });
        return;
    }

    if (op == "LOAD" && saveName.Length > 0)
    {
        clientSaves[senderId] = saveName;
        await WithSaveLockAsync(worldKey, saveName, saveLocks, cancellationToken, async activeCancellationToken =>
        {
            ConcurrentDictionary<string, string[]> activePieces = GetSaveState(saveStates, worldKey, saveName);
            EnsureSaveLoaded(saveDirectory, worldKey, saveName, activePieces, loadedSaveKeys);
            string loadDataFrame = BuildStateFrame("server", worldKey, "LOAD_DATA", saveName, activePieces);
            await SendTextAsync(sender, loadDataFrame, activeCancellationToken);
        });
        return;
    }

    if (saveName.Length > 0)
    {
        await BroadcastAsync(clients, clientWorlds, clientSaves, worldKey, saveName, frame, cancellationToken);
    }
}

static async Task BroadcastAsync(
    ConcurrentDictionary<Guid, WebSocket> clients,
    ConcurrentDictionary<Guid, string> clientWorlds,
    ConcurrentDictionary<Guid, string> clientSaves,
    string worldKey,
    string saveName,
    string frame,
    CancellationToken cancellationToken)
{
    byte[] payload = Encoding.UTF8.GetBytes(frame);
    List<Guid> failed = new();
    List<Task> tasks = new(clients.Count);
    foreach ((Guid id, WebSocket socket) in clients)
    {
        if (socket.State != WebSocketState.Open)
        {
            failed.Add(id);
            continue;
        }

        if (!clientWorlds.TryGetValue(id, out string? clientWorld) || clientWorld != worldKey)
        {
            continue;
        }

        if (!clientSaves.TryGetValue(id, out string? clientSave) || clientSave != saveName)
        {
            continue;
        }

        tasks.Add(SendBytesAsync(socket, payload, cancellationToken));
    }

    await Task.WhenAll(tasks);
    foreach (Guid id in failed)
    {
        clients.TryRemove(id, out _);
    }
}

static Task SendTextAsync(WebSocket socket, string frame, CancellationToken cancellationToken)
{
    return SendBytesAsync(socket, Encoding.UTF8.GetBytes(frame), cancellationToken);
}

static async Task SendBytesAsync(WebSocket socket, byte[] payload, CancellationToken cancellationToken)
{
    try
    {
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }
    catch
    {
    }
}

static bool TryReadPlanFrame(string frame, out string worldKey, out string op, out string saveName)
{
    worldKey = "";
    op = "";
    saveName = "";
    if (!frame.StartsWith(Prefix, StringComparison.Ordinal))
    {
        return false;
    }

    string[] parts = frame[Prefix.Length..].Split('\t');
    if (parts.Length < 3)
    {
        return false;
    }

    worldKey = SanitizeName(Decode(parts[1]));
    op = parts[2];
    if ((op == "SAVE" || op == "LOAD" || op == "PLACE" || op == "REMOVE") && parts.Length >= 4)
    {
        saveName = Decode(parts[3]);
    }

    return worldKey.Length > 0;
}

static bool TryReadPlaceFields(string frame, out string pieceId, out string[] pieceFields)
{
    pieceId = "";
    pieceFields = Array.Empty<string>();
    string[] parts = frame[Prefix.Length..].Split('\t');
    if (parts.Length < 15 || parts[2] != "PLACE")
    {
        return false;
    }

    pieceFields = parts.Skip(4).Take(11).ToArray();
    pieceId = Decode(pieceFields[0]);
    return pieceId.Length > 0;
}

static bool TryReadRemoveId(string frame, out string pieceId)
{
    pieceId = "";
    string[] parts = frame[Prefix.Length..].Split('\t');
    if (parts.Length < 5 || parts[2] != "REMOVE")
    {
        return false;
    }

    pieceId = Decode(parts[4]);
    return pieceId.Length > 0;
}

static async Task WithSaveLockAsync(
    string worldKey,
    string saveName,
    ConcurrentDictionary<string, SemaphoreSlim> saveLocks,
    CancellationToken cancellationToken,
    Func<CancellationToken, Task> action)
{
    string stateKey = StateKey(worldKey, saveName);
    SemaphoreSlim saveLock = saveLocks.GetOrAdd(stateKey, _ => new SemaphoreSlim(1, 1));
    await saveLock.WaitAsync(cancellationToken);
    try
    {
        await action(cancellationToken);
    }
    finally
    {
        saveLock.Release();
    }
}

static ConcurrentDictionary<string, string[]> GetSaveState(
    ConcurrentDictionary<string, ConcurrentDictionary<string, string[]>> saveStates,
    string worldKey,
    string saveName)
{
    return saveStates.GetOrAdd(StateKey(worldKey, saveName), _ => new ConcurrentDictionary<string, string[]>());
}

static void EnsureSaveLoaded(
    string saveDirectory,
    string worldKey,
    string saveName,
    ConcurrentDictionary<string, string[]> activePieces,
    ConcurrentDictionary<string, byte> loadedSaveKeys)
{
    string stateKey = StateKey(worldKey, saveName);
    if (!loadedSaveKeys.TryAdd(stateKey, 1))
    {
        return;
    }

    string path = SavePath(saveDirectory, worldKey, saveName);
    if (!File.Exists(path))
    {
        return;
    }

    int loaded = LoadStateFrame(File.ReadAllText(path), activePieces);
    Console.WriteLine($"[{Now()}] Loaded save world={worldKey} save={saveName}: {loaded} planned pieces.");
}

static int LoadStateFrame(string frame, ConcurrentDictionary<string, string[]> activePieces)
{
    activePieces.Clear();
    if (!frame.StartsWith(Prefix, StringComparison.Ordinal))
    {
        return 0;
    }

    string[] parts = frame[Prefix.Length..].Split('\t');
    if (parts.Length < 5 || (parts[2] != "SAVE" && parts[2] != "LOAD_DATA"))
    {
        return 0;
    }

    if (!int.TryParse(parts[4], out int count))
    {
        return 0;
    }

    int index = 5;
    for (int i = 0; i < count; i++)
    {
        if (parts.Length < index + 11)
        {
            break;
        }

        string[] pieceFields = parts.Skip(index).Take(11).ToArray();
        string id = Decode(pieceFields[0]);
        if (id.Length > 0)
        {
            activePieces[id] = pieceFields;
        }

        index += 11;
    }

    return activePieces.Count;
}

static async Task PersistSaveAsync(
    string saveDirectory,
    string worldKey,
    string saveName,
    ConcurrentDictionary<string, string[]> activePieces,
    CancellationToken cancellationToken)
{
    await File.WriteAllTextAsync(SavePath(saveDirectory, worldKey, saveName), BuildStateFrame("server", worldKey, "SAVE", saveName, activePieces), cancellationToken);
}

static string BuildStateFrame(string clientId, string worldKey, string op, string saveName, ConcurrentDictionary<string, string[]> activePieces)
{
    List<string> fields = new()
    {
        Encode(clientId),
        Encode(worldKey),
        op,
        Encode(saveName),
        activePieces.Count.ToString()
    };

    foreach (string[] pieceFields in activePieces.Values.OrderBy(piece => Decode(piece[0]), StringComparer.Ordinal))
    {
        fields.AddRange(pieceFields);
    }

    return Prefix + string.Join('\t', fields);
}

static string StateKey(string worldKey, string saveName)
{
    return SanitizeName(worldKey) + "\t" + SanitizeName(saveName);
}

static string SavePath(string directory, string worldKey, string name)
{
    return Path.Combine(directory, SanitizeName(worldKey) + "__" + SanitizeName(name) + ".frame");
}

static string FormatPieceLog(string[] pieceFields)
{
    string id = Decode(pieceFields[0]);
    string prefab = Decode(pieceFields[1]);
    string owner = Decode(pieceFields[9]);
    return $"id={id} prefab={prefab} owner={owner} pos=({pieceFields[2]}, {pieceFields[3]}, {pieceFields[4]}) rot=({pieceFields[5]}, {pieceFields[6]}, {pieceFields[7]}, {pieceFields[8]})";
}

static string SanitizeName(string value)
{
    char[] chars = value.Trim().ToCharArray();
    for (int i = 0; i < chars.Length; i++)
    {
        if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
        {
            chars[i] = '_';
        }
    }

    return new string(chars).Trim('_');
}

static string Encode(string value)
{
    return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}

static string Decode(string value)
{
    return Encoding.UTF8.GetString(Convert.FromBase64String(value));
}

static string Now()
{
    return DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
}
