using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;


var builder = WebApplication.CreateBuilder(args);

//CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

//JSON 
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var rooms = new ConcurrentDictionary<Guid, Room>();
var roomSockets = new ConcurrentDictionary<Guid, RoomConnections>();
var gameStates = new ConcurrentDictionary<Guid, GameState>();

//HttpClient for UserService
builder.Services.AddHttpClient("userservice", client =>
{
    client.BaseAddress = new Uri("http://localhost:5083");
});

//HttpClient for GameRulesService
builder.Services.AddHttpClient("gamerules", client =>
{
    client.BaseAddress = new Uri("http://localhost:5045"); // <-- Replace with your GameRulesService port
});

var app = builder.Build();

app.UseCors("AllowAll");
app.UseWebSockets();

// ENDPOINTS
// CREATE ROOM
app.MapPost("/rooms/create", async (CreateRoomRequest request, IHttpClientFactory httpFactory) =>
{
    // Validate user existence
    var http = httpFactory.CreateClient("userservice");

    var response = await http.GetAsync($"/users/{request.HostUserId}");
    if (!response.IsSuccessStatusCode)
    {
        return Results.BadRequest(new { error = "Host user does not exist." });
    }

    var roomId = Guid.NewGuid();
    var room = new Room(roomId, request.HostUserId, null);
    rooms[roomId] = room;

    //4x4 Dots and Boxes board 
    var boxes = new List<Box>();
    for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
            boxes.Add(new Box(r, c, null));

    var initialState = new GameState(
        Rows: 4,
        Columns: 4,
        Lines: new List<Line>(),
        Boxes: boxes
    );

    gameStates[roomId] = initialState;

    return Results.Ok(room);
});

// JOIN ROOM
app.MapPost("/rooms/{roomId:guid}/join", async (Guid roomId, JoinRoomRequest request, IHttpClientFactory httpFactory) =>
{
    if (!rooms.TryGetValue(roomId, out var room))
        return Results.NotFound(new { error = "Room not found." });

    if (room.SecondUserId != null)
        return Results.BadRequest(new { error = "Room is already full." });

    if (room.HostUserId == request.UserId)
        return Results.BadRequest(new { error = "Host cannot join their own room." });

    var http = httpFactory.CreateClient("userservice");
    var response = await http.GetAsync($"/users/{request.UserId}");
    if (!response.IsSuccessStatusCode)
    {
        return Results.BadRequest(new { error = "User does not exist." });
    }

    var updatedRoom = room with { SecondUserId = request.UserId };
    rooms[roomId] = updatedRoom;

    return Results.Ok(updatedRoom);
});

//ROOM COMMUNICATION
app.MapGet("/ws/rooms/{roomId:guid}", async (HttpContext context, Guid roomId, IHttpClientFactory httpFactory) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
        return Results.BadRequest(new { error = "Must connect via WebSocket." });

    if (!context.Request.Query.TryGetValue("userId", out var uidString) ||
        !Guid.TryParse(uidString, out var userId))
    {
        return Results.BadRequest(new { error = "Invalid or missing userId." });
    }

    if (!rooms.TryGetValue(roomId, out var room))
        return Results.NotFound(new { error = "Room not found." });

    var http = httpFactory.CreateClient("userservice");
    var userResponse = await http.GetAsync($"/users/{userId}");
    if (!userResponse.IsSuccessStatusCode)
        return Results.BadRequest(new { error = "User does not exist." });

    var ws = await context.WebSockets.AcceptWebSocketAsync();

    // Set room connection 
    roomSockets.TryGetValue(roomId, out var conn);
    conn ??= new RoomConnections(null, null);

   
    if (room.HostUserId == userId)
        conn = conn with { HostSocket = ws };
    else if (room.SecondUserId == userId)
        conn = conn with { SecondSocket = ws };
    else
        return Results.BadRequest(new { error = "User is not part of this room." });

    roomSockets[roomId] = conn;

    //Connection 
    if (conn.HostSocket != null && conn.SecondSocket != null)
    {
        await SendJson(conn.HostSocket, new { type = "gameStart", roomId });
        await SendJson(conn.SecondSocket, new { type = "gameStart", roomId });
    }

    await ListenForMessages(ws, async (msg) =>
    {
        var incoming = JsonSerializer.Deserialize<ClientMoveMessage>(msg);
        if (incoming is null)
            return;

        if (!gameStates.TryGetValue(roomId, out var currentState))
            return;

        var moveRequest = new MoveRequest(
            incoming.UserId,
            new Line(incoming.X1, incoming.Y1, incoming.X2, incoming.Y2),
            currentState
        );

        var rulesClient = httpFactory.CreateClient("gamerules");
        var rulesResponse = await rulesClient.PostAsJsonAsync("/rules/apply-move", moveRequest);

        if (!rulesResponse.IsSuccessStatusCode)
            return;

        var result = await rulesResponse.Content.ReadFromJsonAsync<MoveResult>();
        if (result is null)
            return;

        gameStates[roomId] = result.NewState;

        if (conn.HostSocket != null)
            await SendJson(conn.HostSocket, result);
        if (conn.SecondSocket != null)
            await SendJson(conn.SecondSocket, result);
    });

    return Results.Ok();
});


app.Run();

// WebSocket functions
static async Task SendJson(WebSocket socket, object obj)
{
    var json = JsonSerializer.Serialize(obj);
    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}

static async Task ListenForMessages(WebSocket socket, Func<string, Task> onMessage)
{
    var buffer = new byte[4096];

    while (socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            break;
        }

        var msg = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
        await onMessage(msg);
    }
}

record CreateRoomRequest(Guid HostUserId);
record JoinRoomRequest(Guid UserId);

record Room(Guid RoomId, Guid HostUserId, Guid? SecondUserId);
record RoomConnections(WebSocket? HostSocket, WebSocket? SecondSocket);

record ClientMoveMessage(Guid UserId, int X1, int Y1, int X2, int Y2);

record GameState(int Rows, int Columns, List<Line> Lines, List<Box> Boxes);
record Line(int X1, int Y1, int X2, int Y2);
record Box(int Row, int Col, Guid? OwnerUserId);

record MoveRequest(Guid UserId, Line Line, GameState CurrentState);

record MoveResult(
    GameState NewState,
    bool BoxCompleted,
    bool GameEnded,
    int HostScore,
    int SecondScore,
    Guid NextTurnUserId
);



