using System.Collections.Concurrent;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// CORS configuration
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

// JSON
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var users = new ConcurrentDictionary<Guid, string>();

var app = builder.Build();

app.UseCors("AllowAll");


// ENDPOINTS 
// POST /users/login
app.MapPost("/users/login", (LoginRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Username))
    {
        return Results.BadRequest(new { error = "Username cannot be empty." });
    }

    foreach (var u in users)
    {
        if (u.Value == request.Username)
        {
            return Results.Ok(new UserResponse(u.Key, u.Value));
        }
    }

 // Create a new user
    var newId = Guid.NewGuid();
    users[newId] = request.Username;

    return Results.Ok(new UserResponse(newId, request.Username));
});

// GET /users/{userId}
app.MapGet("/users/{userId:guid}", (Guid userId) =>
{
    if (users.TryGetValue(userId, out var username))
    {
        return Results.Ok(new UserResponse(userId, username));
    }

    return Results.NotFound(new { error = "User not found." });
});


app.Run();

record LoginRequest(string Username);
record UserResponse(Guid UserId, string Username);
