using System.Numerics;

namespace Demiurge.GameServer;

/// <summary>
/// Bounded generate/test/score query for fighting positions. It runs only when MobSystem grants
/// the per-tick cover-query budget; normal combat ticks do no cover raycasts.
/// </summary>
internal sealed class CoverBehavior
{
    internal readonly record struct Choice(
        Vector3 Position,
        Vector3 PeekPosition,
        CoverKind Kind,
        float Score,
        long TerrainVersion,
        ChunkIndex[] DependencyChunks);

    private const float NearRadius = 4f;
    private const float FarRadius = 8f;
    private const int NearSamples = 4;
    private const int FarSamples = 8;
    private const int VerticalCellSearch = 3;
    private const float RaySlack = 0.1f;
    private const int MaximumThreats = 2;
    private static readonly float[] CornerSides = [-1f, 1f];

    private static readonly (int X, int Z)[] EscapeDirections =
    [
        (0, 1),
        (1, 0),
        (0, -1),
        (-1, 0),
        (1, 1),
        (1, -1),
        (-1, -1),
        (-1, 1),
    ];

    private readonly ChunkMap terrain;

    /// <summary>
    /// Kept across queries while the terrain version is stable, then reset as one unit. See
    /// <see cref="NavProbeCache"/> for the per-cell memoization contract.
    ///
    /// A query evaluates thirteen candidate positions and, for each one that qualifies, counts
    /// escape routes over its eight neighbours. The candidates overlap, the neighbours overlap, and
    /// counting one cell's routes asks whether that same cell is standable once per direction.
    /// Measured as the dominant cost of the whole query, ahead of the raycasts.
    ///
    /// Server main thread only, like the rest of this class.
    /// </summary>
    private readonly NavProbeCache probes;

    /// <summary>
    /// The terrain revision the fast local tier in <see cref="probes"/> holds answers for.
    /// Standability is a pure function of the field, so answers stay valid until the field changes
    /// — which makes the natural local-cache lifetime the EDIT VERSION rather than the query.
    ///
    /// It used to reset per query, and that threw the memo away between squadmates standing metres
    /// apart asking about overlapping cells, ~25 queries a second across a platoon. Keying on the
    /// version keeps every one of those hits and still cannot serve a stale answer. The shared
    /// chunk-revision tier retains cells that were not close to the edit, so one shovel bite no
    /// longer makes every squad's next cover query start cold.
    /// </summary>
    private long probeTerrainVersion = -1;

    public CoverBehavior(ChunkMap terrain)
    {
        this.terrain = terrain;
        // The local probe memo is fastest while terrain is unchanged. Its shared second tier is
        // what preserves unaffected cells when a shovel bite elsewhere advances EditVersion and
        // forces the local tier to reset.
        probes = new NavProbeCache(terrain, new NavStandabilityCache());
    }

    /// <summary>
    /// Rechecks the small set of facts that must remain true for an existing cover destination.
    /// This deliberately does not generate or score thirteen alternatives: an edit creating a
    /// slightly better position is not a reason to abandon one that is still safe.
    /// </summary>
    public bool TryRevalidate(
        Vector3 position,
        Vector3 peekPosition,
        CoverKind kind,
        IReadOnlyList<AiContact> believedThreats,
        out Choice choice)
    {
        choice = default;
        if (kind == CoverKind.None || believedThreats.Count == 0) return false;
        RefreshProbes();

        Span<AiContact> threats = stackalloc AiContact[MaximumThreats];
        int threatCount = SelectNearestThreats(position, believedThreats, threats);
        if (threatCount == 0
            || !TryCellAt(position.X, position.Z, position.Y, out var cell, out var refreshed)
            || HorizontalDistance(position, refreshed) > 0.75f
            || MathF.Abs(position.Y - refreshed.Y) > 0.75f)
            return false;

        // Advance is a navigation waypoint rather than protection. Its only local promise is that
        // the destination remains standable; the path's own chunk revisions protect the route.
        Vector3 refreshedPeek = refreshed;
        if (kind != CoverKind.Advance)
        {
            var crouchedEye = refreshed + Vector3.UnitY
                * (Digging.EyeHeight - PlayerMovement.CrouchEyeDrop);
            for (int i = 0; i < threatCount; i++)
                if (HasLineOfSight(
                        crouchedEye,
                        threats[i].Position + Vector3.UnitY * GunConfig.PlayerCenterHeight))
                    return false;

            // Runtime terrain edits only subtract soil (shovels and grenades). Subtraction can
            // expose a crouched actor or remove footing, both checked here, but it cannot block a
            // standing/peek line that was clear when the choice was made. Recasting those rays was
            // almost half of successful revalidation. Corner cover still needs a usable peek cell;
            // if its footing disappeared, fall back to a full search rather than guessing.
            if (kind == CoverKind.CornerFightingPosition)
            {
                if (!TryCellAt(
                        peekPosition.X,
                        peekPosition.Z,
                        peekPosition.Y,
                        out var peekCell,
                        out var candidate)
                    || peekCell == cell
                    || !WalkableEdge(cell, peekCell))
                    return false;
                refreshedPeek = candidate;
            }
        }

        Span<Vector3> dependencyThreats = stackalloc Vector3[MaximumThreats];
        for (int i = 0; i < threatCount; i++)
            dependencyThreats[i] = threats[i].Position;
        choice = new Choice(
            refreshed,
            refreshedPeek,
            kind,
            0f,
            terrain.EditVersion,
            CoverTerrainDependency.Capture(
                refreshed,
                refreshedPeek,
                dependencyThreats[..threatCount]));
        return true;
    }

    public bool TryChoose(
        ServerPlayer mob,
        IReadOnlyList<AiContact> believedThreats,
        SquadBlackboard squad,
        out Choice choice,
        bool requireAdvance = false)
    {
        choice = default;
        if (believedThreats.Count == 0) return false;

        RefreshProbes();

        var threats = new AiContact[MaximumThreats];
        int threatCount = SelectNearestThreats(
            mob.Position,
            believedThreats,
            threats);
        if (threatCount == 0) return false;

        bool found = false;
        float bestScore = float.NegativeInfinity;
        Choice bestChoice = default;
        var visited = new HashSet<long>();
        float angleOffset = DeterministicAngle(mob.Id);

        EvaluateAt(mob.Position.X, mob.Position.Z);
        EvaluateRing(NearRadius, NearSamples, angleOffset);
        EvaluateRing(FarRadius, FarSamples, angleOffset + MathF.PI / FarSamples);
        if (!found) return false;

        Span<Vector3> dependencyThreats = stackalloc Vector3[MaximumThreats];
        for (int i = 0; i < threatCount; i++)
            dependencyThreats[i] = threats[i].Position;
        choice = bestChoice with
        {
            DependencyChunks = CoverTerrainDependency.Capture(
                bestChoice.Position,
                bestChoice.PeekPosition,
                dependencyThreats[..threatCount]),
        };
        return true;

        void EvaluateRing(float radius, int samples, float offset)
        {
            for (int i = 0; i < samples; i++)
            {
                float angle = offset + MathF.Tau * i / samples;
                EvaluateAt(
                    mob.Position.X + MathF.Cos(angle) * radius,
                    mob.Position.Z + MathF.Sin(angle) * radius);
            }
        }

        void EvaluateAt(float x, float z)
        {
            if (!TryCellAt(x, z, mob.Position.Y, out var cell, out var position)
                || !visited.Add(cell.Key)
                || squad.IsClaimedByOther(mob.Id, position))
                return;
            if (requireAdvance
                && HorizontalDistance(position, threats[0].Position)
                    >= HorizontalDistance(mob.Position, threats[0].Position) - 1.5f)
                return;

            // Crouched exposure disqualifies the position outright, so ask that first and stop on
            // the first yes. This used to cast all four rays — both stances against both threats —
            // and only then check, which meant every REJECTED candidate paid full price. Most
            // candidates are rejected, and a query evaluates thirteen of them, so the wasted rays
            // were most of the query. Same predicate, asked lazily.
            var crouchedEye = position + Vector3.UnitY
                * (Digging.EyeHeight - PlayerMovement.CrouchEyeDrop);
            for (int i = 0; i < threatCount; i++)
                if (HasLineOfSight(
                        crouchedEye,
                        threats[i].Position + Vector3.UnitY * GunConfig.PlayerCenterHeight))
                    return;

            // Only a candidate that already qualifies as cover pays for the standing rays, which
            // decide whether it can shoot back rather than whether it is cover at all.
            int standingExposures = 0;
            var standingEye = position + Vector3.UnitY * Digging.EyeHeight;
            for (int i = 0; i < threatCount; i++)
                if (HasLineOfSight(
                        standingEye,
                        threats[i].Position + Vector3.UnitY * GunConfig.PlayerCenterHeight))
                    standingExposures++;

            Vector3 peekPosition = position;
            bool canShootBack = standingExposures > 0;
            if (!canShootBack)
                canShootBack = TryCornerPeek(
                    cell,
                    position,
                    threats,
                    threatCount,
                    out peekPosition);

            float horizontalDistance = HorizontalDistance(mob.Position, position);
            var facts = new CoverFacts(
                threatCount,
                // Zero by construction: any crouched exposure returned above.
                CrouchedExposures: 0,
                standingExposures,
                CanShootBack: canShootBack,
                TravelSeconds: horizontalDistance / PlayerMovement.SlowSpeed,
                EscapeRoutes: CountEscapeRoutes(cell));
            CoverRating rating = CoverScore.Evaluate(facts);
            if (!rating.Usable || rating.Score <= bestScore) return;

            bestScore = rating.Score;
            bestChoice = new Choice(
                position,
                peekPosition,
                rating.Kind,
                rating.Score,
                terrain.EditVersion,
                []);
            found = true;
        }
    }

    private bool TryCornerPeek(
        NavCell coverCell,
        Vector3 coverPosition,
        IReadOnlyList<AiContact> threats,
        int threatCount,
        out Vector3 peekPosition)
    {
        peekPosition = coverPosition;
        Vector3 toward = threats[0].Position - coverPosition;
        toward.Y = 0f;
        if (toward.LengthSquared() <= 1e-6f) return false;
        toward = Vector3.Normalize(toward);
        var lateral = new Vector3(-toward.Z, 0f, toward.X);

        int bestVisible = 0;
        foreach (float side in CornerSides)
        {
            Vector3 sample = coverPosition + lateral * (1.1f * side);
            if (!TryCellAt(
                    sample.X,
                    sample.Z,
                    coverPosition.Y,
                    out var peekCell,
                    out var candidate)
                || peekCell == coverCell
                || !WalkableEdge(coverCell, peekCell))
                continue;

            int visible = 0;
            Vector3 origin = candidate + Vector3.UnitY * Digging.EyeHeight;
            for (int i = 0; i < threatCount; i++)
            {
                Vector3 target =
                    threats[i].Position + Vector3.UnitY * GunConfig.PlayerCenterHeight;
                if (HasLineOfSight(origin, target))
                    visible++;
            }
            if (visible <= bestVisible) continue;
            bestVisible = visible;
            peekPosition = candidate;
        }
        return bestVisible > 0;
    }

    private int CountEscapeRoutes(NavCell from)
    {
        int routes = 0;
        foreach (var direction in EscapeDirections)
        {
            if (!NavTraversal.TryFindStandable(
                    probes,
                    from.X + direction.X,
                    from.Z + direction.Z,
                    from.Y,
                    NavTraversal.MaximumTraverseCellDelta,
                    NavTraversal.MaximumTraverseCellDelta,
                    out var next,
                    out _)
                || !WalkableEdge(from, next))
                continue;
            routes++;
        }
        return routes;
    }

    private bool WalkableEdge(NavCell from, NavCell to)
    {
        int dx = to.X - from.X;
        int dz = to.Z - from.Z;
        return Math.Abs(dx) <= 1
            && Math.Abs(dz) <= 1
            && (dx != 0 || dz != 0)
            && NavTraversal.TryStep(probes, from, to, out _)
            && (dx == 0 || dz == 0
                || CardinalClear(from, dx, 0) && CardinalClear(from, 0, dz));
    }

    private bool CardinalClear(NavCell from, int dx, int dz)
        => NavTraversal.TryFindStandable(
               probes,
               from.X + dx,
               from.Z + dz,
               from.Y,
               NavTraversal.MaximumTraverseCellDelta,
               NavTraversal.MaximumTraverseCellDelta,
               out var adjacent,
               out _)
           && NavTraversal.TryStep(probes, from, adjacent, out _);

    private bool TryCellAt(
        float x,
        float z,
        float aroundY,
        out NavCell cell,
        out Vector3 position)
    {
        if (NavTraversal.TryFindStandable(
                probes,
                (int)MathF.Floor(x),
                (int)MathF.Floor(z),
                (int)MathF.Floor(aroundY),
                VerticalCellSearch,
                VerticalCellSearch,
                out cell,
                out _))
        {
            // Already memoized by the find above, so this is a dictionary hit rather than a
            // second capsule resolve.
            if (!NavTraversal.TryPosition(probes, cell, out position)) return false;
            return true;
        }

        position = default;
        return false;
    }

    private bool HasLineOfSight(Vector3 origin, Vector3 target)
    {
        Vector3 delta = target - origin;
        float distance = delta.Length();
        if (distance <= 1e-5f) return true;
        var hit = TerrainRaycast.Cast(terrain, origin, delta / distance, distance);
        return hit is null || hit.Value.Distance >= distance - RaySlack;
    }

    private static int SelectNearestThreats(
        Vector3 origin,
        IReadOnlyList<AiContact> contacts,
        Span<AiContact> selected)
    {
        Span<float> distances = stackalloc float[MaximumThreats];
        distances.Fill(float.PositiveInfinity);
        int count = 0;

        foreach (var contact in contacts)
        {
            float distance = Vector3.DistanceSquared(origin, contact.Position);
            int limit = Math.Min(count, selected.Length);
            int insert = 0;
            while (insert < limit && distance >= distances[insert])
                insert++;
            if (insert >= selected.Length) continue;

            int last = Math.Min(count, selected.Length - 1);
            for (int i = last; i > insert; i--)
            {
                distances[i] = distances[i - 1];
                selected[i] = selected[i - 1];
            }
            distances[insert] = distance;
            selected[insert] = contact;
            if (count < selected.Length) count++;
        }
        return count;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private void RefreshProbes()
    {
        if (probeTerrainVersion == terrain.EditVersion) return;
        probes.Reset(terrain);
        probeTerrainVersion = terrain.EditVersion;
    }

    private static float DeterministicAngle(ushort actorId)
        => (actorId * 0.61803398875f % 1f) * MathF.Tau;
}
