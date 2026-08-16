using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnToPilot.Api;
using OnToPilot.Authentication;
using OnToPilot.Authorization;
using OnToPilot.Configuration;
using OnToPilot.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.Configure<OnToPilotOptions>(
    builder.Configuration.GetSection(OnToPilotOptions.SectionName));

// Wire the EF Core DbContext. Production uses PostgreSQL; tests flip the
// provider via configuration (see AuthTestWebApplicationFactory). The actual
// schema is owned by the InitialCompatibility migration; the application
// does not call EnsureCreated() at runtime. The provider choice is resolved
// lazily from the IServiceProvider so test factories that add configuration
// sources late in `ConfigureWebHost` are honored.
builder.Services.AddDbContext<OnToPilotDbContext>((sp, options) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var provider = config["OnToPilot:Persistence:Provider"] ?? "npgsql";
    if (string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase))
    {
        var sqlite = config["OnToPilot:Persistence:SqliteConnection"]
            ?? "Data Source=:memory:";
        options.UseSqlite(sqlite);
    }
    else
    {
        var npgsql = config["OnToPilot:Persistence:ConnectionString"]
            ?? "Host=localhost;Port=5432;Database=ontopilot;Username=postgres;Password=postgres";
        options.UseNpgsql(npgsql);
    }
});

// ---- Auth services ----
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordService, PasswordService>();
builder.Services.AddSingleton<KnowledgeSystemAccessService>();

builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();

// Translate model-state validation failures into the FastAPI envelope
// ({ "detail": "..." }) instead of the default application/problem+json
// body that [ApiController] otherwise emits.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var detail = context.ModelState
            .Where(kv => kv.Value is { Errors.Count: > 0 })
            .Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value!.Errors.Select(e => e.ErrorMessage))}")
            .FirstOrDefault()
            ?? "Invalid request";
        return new BadRequestObjectResult(new { detail });
    };
});

var app = builder.Build();

// Global error envelope runs FIRST so every response shape (auth challenges,
// model validation, unhandled exceptions, 404 routes) ultimately carries
// {"detail": "..."} — the same shape the Python backend emits.
app.UseMiddleware<FastApiErrorMiddleware>();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

/// <summary>
/// Exposed as a partial class so test projects can derive
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// against <c>Program</c>.
/// </summary>
public partial class Program;
