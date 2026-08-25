namespace ISEStudio.Llm;

/// <summary>
/// Identifies a capacity bucket. Two acquires share a bucket when they
/// have both the same <see cref="Capability"/> (e.g. <c>chat</c>,
/// <c>embedding</c>) and the same <see cref="Endpoint"/>.
/// </summary>
/// <param name="Capability">
/// What the caller is doing (e.g. <c>chat</c>, <c>embedding</c>). This
/// allows chat and embedding traffic to the same provider endpoint to
/// flow independently.
/// </param>
/// <param name="Endpoint">
/// The provider endpoint URL — chat traffic to endpoint A and chat traffic
/// to endpoint B never compete for the same permit.
/// </param>
public readonly record struct EndpointCapacityKey(string Capability, string Endpoint);
