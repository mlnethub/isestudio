using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ISEStudio.Infrastructure.Persistence;

namespace ISEStudio.Infrastructure.Startup;

/// <summary>
/// Outcome of <see cref="BootstrapAdminService.RunAsync"/>.
/// </summary>
public enum BootstrapOutcome
{
    /// <summary>
    /// <see cref="BootstrapAdminService.RunAsync"/> hasn't been called yet.
    /// </summary>
    Unknown = 0,

    /// <summary>The user table already had at least one row.</summary>
    AlreadyBootstrapped = 1,

    /// <summary>
    /// The user table is empty; the operator MUST seed the first admin
    /// manually. <see cref="BootstrapAdminService.RunAsync"/> returns this
    /// value (and <see cref="BootstrapAdminService.RequiresExit"/> is
    /// <c>true</c>) so the host process can exit with
    /// <see cref="BootstrapAdminService.BootstrapRequiredExitCode"/>.
    /// </summary>
    BootstrapRequired = 2,
}

/// <summary>
/// Boot-time check that the install has at least one user. Empty installs
/// MUST NOT land on a default admin password: the service refuses to seed
/// one and returns <see cref="BootstrapOutcome.BootstrapRequired"/> so the
/// host can exit with a documented, non-zero status code that operators
/// can use to drive their provisioning runbook.
/// </summary>
/// <remarks>
/// <para>Why a non-zero exit (rather than a "first-user provisioning" HTTP
/// endpoint)? Because the production deployment model is "no public network
/// surface on a fresh install" — operators connect over SSH / kubectl exec
/// and run the bootstrap command. Refusing to start with no users forces
/// that explicit step.</para>
/// <para>The Python backend seeds an admin from an
/// <c>ADMIN_PASSWORD</c> environment variable when set. The .NET backend
/// keeps the same constraint (no default password), but bakes in the
/// "missing config → refuse to boot" rule so a typo in the deploy manifests
/// can't accidentally land on a known-weak credential.</para>
/// <para>On PostgreSQL, the same "refuse to start" rule applies when the
/// schema itself is missing: a Postgres <c>42P01</c> (undefined_table)
/// raised against <c>users</c> is operationally equivalent to "the install
/// has no users" because the operator hasn't run the deploy-time migrations
/// yet. We catch it and route through the same
/// <see cref="BootstrapOutcome.BootstrapRequired"/> exit so a fresh compose
/// stack can't accidentally auto-bootstrap a default schema either.</para>
/// </remarks>
public sealed class BootstrapAdminService
{
    /// <summary>
    /// Process exit code emitted when the install needs manual seeding.
    /// Documented in the operator runbook; orchestrators (systemd,
    /// Kubernetes, etc.) can map it to a remediation step.
    /// </summary>
    public const int BootstrapRequiredExitCode = 17;

    /// <summary>
    /// PostgreSQL SQLSTATE for "undefined table". Surfaced when EF Core
    /// translates the user-table COUNT into a query that hits a schema
    /// the deploy-time migrations haven't created yet.
    /// </summary>
    private const string PostgresUndefinedTable = "42P01";

    private readonly ISEStudioDbContext _db;
    private readonly ILogger<BootstrapAdminService> _logger;

    /// <summary>DI constructor.</summary>
    public BootstrapAdminService(
        ISEStudioDbContext db,
        ILogger<BootstrapAdminService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>The result of the most recent <see cref="RunAsync"/> call.</summary>
    public BootstrapOutcome Outcome { get; private set; } = BootstrapOutcome.Unknown;

    /// <summary>
    /// True when <see cref="RunAsync"/> returned
    /// <see cref="BootstrapOutcome.BootstrapRequired"/>; the host should
    /// exit with <see cref="BootstrapRequiredExitCode"/>.
    /// </summary>
    public bool RequiresExit => Outcome == BootstrapOutcome.BootstrapRequired;

    /// <summary>
    /// Exit code to use when <see cref="RequiresExit"/> is true. Always
    /// equals <see cref="BootstrapRequiredExitCode"/>; surfaced as a
    /// property so host code can pass it directly to
    /// <c>Environment.ExitCode</c>.
    /// </summary>
    public int ExitCode => RequiresExit ? BootstrapRequiredExitCode : 0;

    /// <summary>
    /// Inspect the user table. If the table is missing or empty, log a clear
    /// "bootstrap required" message and return
    /// <see cref="BootstrapOutcome.BootstrapRequired"/>. Otherwise return
    /// <see cref="BootstrapOutcome.AlreadyBootstrapped"/>.
    /// </summary>
    /// <remarks>
    /// <para>Safe to invoke multiple times — the check is read-only.</para>
    /// <para>A missing <c>users</c> table on PostgreSQL (SQLSTATE 42P01) is
    /// treated as <see cref="BootstrapOutcome.BootstrapRequired"/>, NOT as
    /// an unhandled exception. The operator's remediation is the same in
    /// both cases (apply migrations + seed the first admin), so routing
    /// through the documented exit code keeps the failure mode stable
    /// across "fresh schema" and "applied schema, no users".</para>
    /// </remarks>
    public async Task<BootstrapOutcome> RunAsync(CancellationToken cancellationToken)
    {
        long userCount;
        try
        {
            userCount = await _db.Users
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMissingUsersTable(ex))
        {
            // Schema missing on PostgreSQL. Operator must apply migrations
            // AND seed the first admin — same exit code as the empty-table
            // path so the runbook only needs one remediation branch.
            Outcome = BootstrapOutcome.BootstrapRequired;
            _logger.LogCritical(ex,
                "Bootstrap required: the users table does not exist. ISEStudio refuses to " +
                "auto-create the schema or a default admin user (no default password). " +
                "Apply EF Core migrations and seed the first administrator manually. " +
                "Process will exit with code {ExitCode}.",
                BootstrapRequiredExitCode);
            return Outcome;
        }

        if (userCount == 0)
        {
            Outcome = BootstrapOutcome.BootstrapRequired;
            _logger.LogCritical(
                "Bootstrap required: the users table is empty. ISEStudio refuses to auto-create a " +
                "default admin user (no default password). Connect to the running pod (SSH, " +
                "kubectl exec, etc.) and seed the first administrator manually. " +
                "Process will exit with code {ExitCode}.",
                BootstrapRequiredExitCode);
        }
        else
        {
            Outcome = BootstrapOutcome.AlreadyBootstrapped;
            _logger.LogInformation(
                "Bootstrap check passed: {UserCount} user row(s) present.", userCount);
        }

        return Outcome;
    }

    /// <summary>
    /// Walk the exception chain looking for a PostgreSQL <c>42P01</c>
    /// (undefined_table) error from the <c>users</c> table probe. Other
    /// failures — auth errors, invalid catalog, network — are intentionally
    /// left to propagate so they surface as unhandled-exception stack
    /// traces instead of being silently mis-classified as "needs bootstrap".
    /// </summary>
    private static bool IsMissingUsersTable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg && pg.SqlState == PostgresUndefinedTable)
            {
                return true;
            }
        }
        return false;
    }
}