namespace ISEStudio.Tests.Observability;

/// <summary>
/// Test collection that serialises the activity-wrapping tests so the
/// shared <see cref="System.Diagnostics.ActivitySource"/> listeners cannot
/// observe activities from sibling test classes running in parallel.
/// </summary>
[CollectionDefinition("ActivityWrapping", DisableParallelization = true)]
public sealed class ActivityWrappingCollection
{
}