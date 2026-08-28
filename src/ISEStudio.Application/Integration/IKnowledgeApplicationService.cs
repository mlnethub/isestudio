using ISEStudio.Application.Foundation;

namespace ISEStudio.Application.Integration;

/// <summary>
/// Application-service seam for the twelve <c>knowledge.*</c>
/// dispatcher arms (12/13 slice): list / create / delete / get /
/// update / list_members / add_member / grantable_users /
/// remove_member / member_detail / review_counts / refresh_stats.
/// The implementation resolves the scoped <c>KnowledgeService</c>
/// through the constructor and owns envelope unpacking (body DTOs,
/// KnowledgeSystemGuid, ResourceId user Guid) + throw semantics
/// (missing body → <see cref="InvalidOperationException"/>, like the
/// pre-split helpers).
///
/// <para>Returns are <c>object?</c> because the wire DTOs
/// (<c>KnowledgeSystemOut</c> / <c>MemberOut</c> /
/// <c>MemberDetailOut</c> / <c>ReviewCountsOut</c>) live in the
/// Infrastructure slice. A <c>null</c> return degrades to the
/// dispatcher's schema-compatible fallback per arm.</para>
/// </summary>
public interface IKnowledgeApplicationService
{
    /// <summary><c>knowledge.list</c> — KS rows visible to the actor.</summary>
    Task<object?> ListAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.create</c> — body <c>{name, ...}</c>.</summary>
    Task<object?> CreateAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.delete</c> — KS Guid in <c>KnowledgeSystemGuid</c>.</summary>
    Task<object?> DeleteAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.get</c> — KS detail for the actor.</summary>
    Task<object?> GetAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.update</c> — patch body for the KS Guid.</summary>
    Task<object?> UpdateAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.list_members</c> — members of the KS.</summary>
    Task<object?> ListMembersAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.add_member</c> — body <c>{username, role}</c>.</summary>
    Task<object?> AddMemberAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.grantable_users</c> — users grantable to the KS, <c>?q=</c> filter.</summary>
    Task<object?> GrantableUsersAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.remove_member</c> — user Guid in <c>ResourceId</c>.</summary>
    Task<object?> RemoveMemberAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.member_detail</c> — member detail for the user Guid.</summary>
    Task<object?> MemberDetailAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.review_counts</c> — conflict review counts for the KS.</summary>
    Task<object?> ReviewCountsAsync(InternalRequest request, CancellationToken cancellationToken);

    /// <summary><c>knowledge.refresh_stats</c> — recompute KS class/property/axiom counts.</summary>
    Task<object?> RefreshStatsAsync(InternalRequest request, CancellationToken cancellationToken);
}
