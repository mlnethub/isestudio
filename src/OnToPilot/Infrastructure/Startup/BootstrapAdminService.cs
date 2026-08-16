using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OnToPilot.Infrastructure.Persistence;

namespace OnToPilot.Infrastructure.Startup;

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
/// </remarks>
public sealed class BootstrapAdminService
{
    /// <summary>
    /// Process exit code emitted when the install needs manual seeding.
    /// Documented in the operator runbook; orchestrators (systemd,
    /// Kubernetes, etc.) can map it to a remediation step.
    /// </summary>
    public const int BootstrapRequiredExitCode = 17;

    private readonly OnToPilotDbContext _db;
    private readonly ILogger<BootstrapAdminService> _logger;

    /// <summary>DI constructor.</summary>
    public BootstrapAdminService(
        OnToPilotDbContext db,
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
    /// Inspect the user table. If it has zero rows, log a clear
    /// "bootstrap required" message and return
    /// <see cref="BootstrapOutcome.BootstrapRequired"/>. Otherwise return
    /// <see cref="BootstrapOutcome.AlreadyBootstrapped"/>.
    /// </summary>
    /// <remarks>
    /// Safe to invoke multiple times — the check is read-only.
    /// </remarks>
    public async Task<BootstrapOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var userCount = await _db.Users
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        if (userCount == 0)
        {
            Outcome = BootstrapOutcome.BootstrapRequired;
            _logger.LogCritical(
                "Bootstrap required: the users table is empty. OntoPilot refuses to auto-create a " +
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
}