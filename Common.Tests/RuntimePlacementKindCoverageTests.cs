using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Every <see cref="RuntimePlacementKind"/> must be known to the runtime validator.
///
/// This exists because appending a kind and forgetting its validation case is silent in the worst
/// possible way: the enum compiles, the editor bakes, and the failure surfaces as every placement of
/// that kind rejecting the whole map at load — which reads as "the map is broken", not as "one
/// switch is missing a case". It cost a launch-time investigation once already.
/// </summary>
public class RuntimePlacementKindCoverageTests
{
    [Fact]
    public void EveryKindIsHandledByValidation()
    {
        foreach (var kind in Enum.GetValues<RuntimePlacementKind>())
        {
            var map = new RuntimeMap
            {
                MapId = Guid.NewGuid(),
                Name = "coverage",
                Terrain = new ChunkMap(),
                Placements = [new RuntimePlacement(kind, new Vector3(0.5f, 4f, 0.5f))],
            };

            var result = RuntimeMapValidation.Validate(map);

            // Only the "unknown kind" complaint is the concern. A bare placement of some kinds is
            // legitimately invalid for other reasons — a spawn with no team, an unsupported anchor —
            // and those are not what this guards.
            Assert.DoesNotContain(
                result.Errors,
                error => error.Contains("Unknown placement kind", StringComparison.Ordinal));
        }
    }
}
