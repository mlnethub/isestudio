using Microsoft.EntityFrameworkCore;
using OnToPilot.Configuration;
using OnToPilot.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.Configure<OnToPilotOptions>(
    builder.Configuration.GetSection(OnToPilotOptions.SectionName));

// Wire the EF Core DbContext against PostgreSQL. The connection string is
// read from "OnToPilot:Persistence:ConnectionString" so deployment can
// override it via environment variable. The actual schema is owned by the
// InitialCompatibility migration; the application does not call
// EnsureCreated() at runtime.
var connectionString = builder.Configuration["OnToPilot:Persistence:ConnectionString"]
    ?? "Host=localhost;Port=5432;Database=ontopilot;Username=postgres;Password=postgres";
builder.Services.AddDbContext<OnToPilotDbContext>(options =>
    options.UseNpgsql(connectionString));

var app = builder.Build();

app.MapControllers();

app.Run();

/// <summary>
/// Exposed as a partial class so test projects can derive
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// against <c>Program</c>.
/// </summary>
public partial class Program;