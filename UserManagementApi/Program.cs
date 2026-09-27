using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var users = new ConcurrentDictionary<Guid, User>();

app.Use(async (context, next) =>
{
	var stopwatch = Stopwatch.StartNew();
	try
	{
		await next(context);
	}
	finally
	{
		stopwatch.Stop();
		app.Logger.LogInformation(
			"HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms",
			context.Request.Method,
			context.Request.Path,
			context.Response.StatusCode,
			stopwatch.ElapsedMilliseconds);
	}
});

app.Use(async (context, next) =>
{
	if (!context.Request.Path.StartsWithSegments("/api/users"))
	{
		await next(context);
		return;
	}

	const string demoToken = "demo-token";
	var authorization = context.Request.Headers["Authorization"].ToString();
	if (!string.Equals(authorization, $"Bearer {demoToken}", StringComparison.Ordinal))
	{
		context.Response.StatusCode = StatusCodes.Status401Unauthorized;
		context.Response.Headers["WWW-Authenticate"] = "Bearer";
		await context.Response.WriteAsJsonAsync(
			new { error = "Unauthorized. Use the demo bearer token." },
			context.RequestAborted);
		return;
	}

	context.User = new ClaimsPrincipal(new ClaimsIdentity(
		[new Claim(ClaimTypes.Name, "demo-user")],
		"SimulatedBearer"));
	await next(context);
});

app.MapGet("/", () => Results.Ok(new
{
	message = "User Management API",
	authentication = "For /api/users, send Authorization: Bearer demo-token."
}));

var userRoutes = app.MapGroup("/api/users");

userRoutes.MapGet("/", () => Results.Ok(users.Values.OrderBy(user => user.Name)));

userRoutes.MapGet("/{id:guid}", (Guid id) =>
	users.TryGetValue(id, out var user) ? Results.Ok(user) : Results.NotFound());

userRoutes.MapPost("/", (CreateUserRequest request) =>
{
	var errors = ValidateUser(request.Name, request.Email);
	if (errors.Count > 0)
	{
		return (IResult)Results.ValidationProblem(errors);
	}

	var user = new User(Guid.NewGuid(), request.Name!.Trim(), request.Email!.Trim());
	users[user.Id] = user;
	return Results.Created($"/api/users/{user.Id}", user);
});

userRoutes.MapPut("/{id:guid}", (Guid id, UpdateUserRequest request) =>
{
	if (!users.TryGetValue(id, out var existingUser))
	{
		return (IResult)Results.NotFound();
	}

	var errors = ValidateUser(request.Name, request.Email);
	if (errors.Count > 0)
	{
		return Results.ValidationProblem(errors);
	}

	var updatedUser = existingUser with { Name = request.Name!.Trim(), Email = request.Email!.Trim() };
	return users.TryUpdate(id, updatedUser, existingUser)
		? Results.Ok(updatedUser)
		: Results.Conflict();
});

userRoutes.MapDelete("/{id:guid}", (Guid id) =>
	users.TryRemove(id, out _) ? Results.NoContent() : Results.NotFound());

app.Run();

static Dictionary<string, string[]> ValidateUser(string? name, string? email)
{
	var errors = new Dictionary<string, string[]>();

	if (string.IsNullOrWhiteSpace(name))
	{
		errors[nameof(name)] = ["Name is required."];
	}

	if (string.IsNullOrWhiteSpace(email) || !new EmailAddressAttribute().IsValid(email))
	{
		errors[nameof(email)] = ["A valid email address is required."];
	}

	return errors;
}

record User(Guid Id, string Name, string Email);
record CreateUserRequest(string? Name, string? Email);
record UpdateUserRequest(string? Name, string? Email);
