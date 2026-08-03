using Xunit;

namespace Demiurge.ServerTests;

/// <summary>
/// Tests that bind real sockets, forced to run one at a time.
/// </summary>
/// <remarks>
/// <c>ServerHost</c> binds <c>NetworkConfig.Port</c> for Riptide and <c>ChunkTransport.Port</c> for the
/// terrain stream, both fixed. xUnit parallelises across test CLASSES by default, so two classes that
/// each start a host raced for the same ports and failed in milliseconds with a bind error — which
/// reads like a broken server rather than a broken test. Each class passed alone, which is exactly how
/// this kind of fault hides.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealPortCollection
{
    public const string Name = "binds real ports";
}
