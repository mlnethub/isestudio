using System;
using ISEStudio.Ontology;

namespace ISEStudio.Extraction;

/// <summary>
/// Synchronization surface used by callers that want to run the
/// deterministic SKOS terminology sync against a knowledge system's TBox +
/// vocabulary graphs without taking a hard dependency on the production
/// TerminologyService class.
///
/// <para>Extracted so VocabularyService.SyncAsync can be tested against a
/// stub that throws partway through. The production TerminologyService
/// swallows inner exceptions and surfaces them as TerminologyResult.Error,
/// which makes its rollback-on-error branch unreachable from a test that
/// drives the real implementation.</para>
/// </summary>
public interface ITerminologySync
{
    /// <summary>
    /// Mirror of <see cref="TerminologyService.SyncAsync"/>. Returns the
    /// deterministic <see cref="TerminologyResult"/> counters. The contract
    /// is the same: the method must not throw on inner failures — those
    /// become <see cref="TerminologyResult.Error"/>; only
    /// <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    TerminologyResult SyncAsync(KsContext ks, CancellationToken cancellationToken);
}