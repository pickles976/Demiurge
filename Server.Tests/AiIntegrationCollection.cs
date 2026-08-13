namespace Demiurge.ServerTests;

/// <summary>
/// AI acceptance scenarios drive an off-thread navigation pool while advancing simulated time.
/// Running them beside other CPU-heavy test classes starves that pool and turns a deterministic
/// behavior assertion into a scheduler benchmark. Performance has dedicated benchmark tests;
/// behavioral integrations run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AiIntegrationCollection
{
    public const string Name = "AI integration";
}
