using System.Diagnostics;
using System.ComponentModel;

namespace ISEStudio.Migration.Rehearsal;

public sealed record BackupValidationResult(bool IsListable, string Format, string Detail);

public interface IPostgresBackupValidator
{
    Task<BackupValidationResult> ValidateAsync(string path, CancellationToken cancellationToken);
}

public sealed class PgRestoreBackupValidator : IPostgresBackupValidator
{
    public async Task<BackupValidationResult> ValidateAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pg_restore",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--list");
        startInfo.ArgumentList.Add(path);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start pg_restore.");
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var detail = await errorTask.ConfigureAwait(false);
            _ = await outputTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new BackupValidationResult(false, "unknown", $"pg_restore --list exited {process.ExitCode}: {detail.Trim()}");
            return new BackupValidationResult(true, "pg_dump", "pg_restore --list succeeded.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("pg_restore executable is required to validate restored backups.", ex);
        }
    }
}