using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

// Endpoints
app.MapPost("/rules/apply-move", (MoveRequest request) =>
{
    var result = ApplyMove(request);
    return Results.Ok(result);
});

app.Run();

// Game logic
static MoveResult ApplyMove(MoveRequest request)
{
    var state = request.CurrentState;

    // Check if move exists
    if (state.Lines.Any(l =>
        l.X1 == request.Line.X1 &&
        l.Y1 == request.Line.Y1 &&
        l.X2 == request.Line.X2 &&
        l.Y2 == request.Line.Y2))
    {
        throw new Exception("This line has already been drawn.");
    }

    // Make new line
    var newLines = new List<Line>(state.Lines) { request.Line };
    var newBoxes = new List<Box>(state.Boxes);

    bool boxCompleted = false;

    // Check completed boxes
    for (int r = 0; r < state.Rows - 1; r++)
    {
        for (int c = 0; c < state.Columns - 1; c++)
        {
            var box = newBoxes.First(b => b.Row == r && b.Col == c);

            if (box.OwnerUserId == null)
            {
                bool top = newLines.Any(l => l.X1 == c && l.Y1 == r && l.X2 == c + 1 && l.Y2 == r);
                bool bottom = newLines.Any(l => l.X1 == c && l.Y1 == r + 1 && l.X2 == c + 1 && l.Y2 == r + 1);
                bool left = newLines.Any(l => l.X1 == c && l.Y1 == r && l.X2 == c && l.Y2 == r + 1);
                bool right = newLines.Any(l => l.X1 == c + 1 && l.Y1 == r && l.X2 == c + 1 && l.Y2 == r + 1);

                if (top && bottom && left && right)
                {
                    boxCompleted = true;
                    newBoxes.Remove(box);
                    newBoxes.Add(new Box(r, c, request.UserId));
                }
            }
        }
    }

    // Score
    int hostScore = newBoxes.Count(b => b.OwnerUserId == state.Boxes.First().OwnerUserId);
    int secondScore = newBoxes.Count(b =>
        b.OwnerUserId != null &&
        b.OwnerUserId != state.Boxes.First().OwnerUserId);

    // Next turn
    Guid nextTurn = boxCompleted ? request.UserId : Guid.Empty; // RoomService will compute actual next turn

    // Check game over
    bool gameEnded = newBoxes.All(b => b.OwnerUserId != null);

    var newState = new GameState(state.Rows, state.Columns, newLines, newBoxes);

    return new MoveResult(newState, boxCompleted, gameEnded, hostScore, secondScore, nextTurn);
}


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

