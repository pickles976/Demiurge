namespace Demiurge.GameServer;

/// <summary>
/// Low-frequency team commander. It owns strategic flag assignments plus one individually reserved
/// equipment/crew-weapon opportunity per squad; squads still own route, formation, cover,
/// engagement, and capture execution.
/// </summary>
internal sealed class CommanderAi
{
    internal const uint ReplanTicks = NetworkConfig.TickRate;
    private const float ResourceSearchRadius = 100f;

    private readonly FlagSystem flags;
    private readonly ObjectReplication objects;
    private uint nextPlanTick;
    private int plannedSquadCount = -1;

    public CommanderAi(FlagSystem flags, ObjectReplication objects)
    {
        this.flags = flags;
        this.objects = objects;
    }

    public void Update(
        uint tick,
        IReadOnlyDictionary<(int Team, int Squad), SquadBlackboard> squads,
        ICollection<ServerPlayer> actors)
    {
        if (tick < nextPlanTick && squads.Count == plannedSquadCount)
            return;

        nextPlanTick = tick + ReplanTicks;
        plannedSquadCount = squads.Count;
        var claimedResources = new HashSet<uint>();

        foreach (var teamGroup in squads
                     .Where(pair => pair.Key.Team > 0)
                     .GroupBy(pair => pair.Key.Team)
                     .OrderBy(group => group.Key))
        {
            int team = teamGroup.Key;
            var boards = teamGroup
                .OrderBy(pair => pair.Key.Squad)
                .ToArray();
            var strategicSquads = boards
                .Select(pair => new StrategicSquad(
                    pair.Key.Squad,
                    pair.Value.Centre,
                    pair.Value.TryGetObjective(out var current)
                        ? current.FlagId
                        : 0))
                .ToArray();
            var strategicFlags = flags.StrategicSnapshot(team, actors);
            var assignments = StrategicObjectivePlanner.Plan(
                team,
                strategicSquads,
                strategicFlags);
            var assignedBySquad = assignments.ToDictionary(
                assignment => assignment.SquadId,
                assignment => assignment.FlagId);
            var flagsById = strategicFlags.ToDictionary(flag => flag.FlagId);

            foreach (var pair in boards)
            {
                if (assignedBySquad.TryGetValue(pair.Key.Squad, out uint flagId)
                    && flagsById.TryGetValue(flagId, out var flag))
                {
                    pair.Value.SetObjective(new SquadObjective(flag.FlagId, flag.Position));
                }
                else
                {
                    pair.Value.SetObjective(null);
                }
            }
            AssignResources(tick, boards, actors, claimedResources);
        }
    }

    private void AssignResources(
        uint tick,
        KeyValuePair<(int Team, int Squad), SquadBlackboard>[] boards,
        ICollection<ServerPlayer> actors,
        HashSet<uint> claimed)
    {
        var actorById = actors
            .Where(actor => actor.IsMob && actor.Status is not { Health.Current: 0 })
            .ToDictionary(actor => actor.Id);
        var worked = actors
            .Where(actor => actor.OperatingObjectId != 0)
            .Select(actor => actor.OperatingObjectId)
            .ToHashSet();
        var pickups = objects.All
            .Where(obj => obj.Has.HasFlag(NetComponents.Item | NetComponents.Transform))
            .ToArray();

        foreach (var pair in boards)
        {
            var board = pair.Value;
            SquadResourceObjective? selected = null;

            if (board.TryGetPrimaryThreat(tick, out var threat)
                && MortarTargetIsSafe(pair.Key.Team, threat.Position, actors))
            {
                foreach (ushort actorId in board.Roster)
                {
                    if (!actorById.TryGetValue(actorId, out var currentOperator)
                        || currentOperator.OperatingObjectId == 0
                        || !objects.TryGet(currentOperator.OperatingObjectId, out var currentMortar)
                        || !ItemCatalog.HasBehavior(currentMortar.Item.Type, ItemBehavior.Mortar)
                        || !MortarBallistics.IsTargetInFireSector(
                            currentMortar.Transform.Position,
                            currentMortar.Transform.Yaw,
                            threat.Position))
                        continue;

                    selected = new SquadResourceObjective(
                        SquadResourceKind.OperateMortar,
                        currentOperator.Id,
                        currentMortar.NetworkId,
                        currentMortar.Item.Type,
                        currentMortar.Transform.Position,
                        threat.Position);
                    break;
                }

                float bestMortarDistance = ResourceSearchRadius * ResourceSearchRadius;
                foreach (var mortar in selected is null ? pickups : [])
                {
                    if (claimed.Contains(mortar.NetworkId)
                        || worked.Contains(mortar.NetworkId)
                        || !ItemCatalog.HasBehavior(mortar.Item.Type, ItemBehavior.Mortar)
                        || !MortarBallistics.IsTargetInFireSector(
                            mortar.Transform.Position,
                            mortar.Transform.Yaw,
                            threat.Position))
                        continue;

                    if (NearestAvailableOperator(
                            board, actorById, mortar.Transform.Position,
                            out var gunner, out float distanceSquared)
                        && distanceSquared < bestMortarDistance)
                    {
                        bestMortarDistance = distanceSquared;
                        selected = new SquadResourceObjective(
                            SquadResourceKind.OperateMortar,
                            gunner.Id,
                            mortar.NetworkId,
                            mortar.Item.Type,
                            mortar.Transform.Position,
                            threat.Position);
                    }
                }
            }

            if (selected is null)
            {
                float engagementRange = board.TryGetPrimaryThreat(tick, out var currentThreat)
                    ? Horizontal(board.Centre, currentThreat.Position)
                    : 60f;
                float bestGain = EquipmentValue.MinimumGain;

                foreach (var pickup in pickups)
                {
                    if (claimed.Contains(pickup.NetworkId)
                        || !ItemCatalog.HasBehavior(pickup.Item.Type, ItemBehavior.Firearm))
                        continue;

                    foreach (ushort actorId in board.Roster)
                    {
                        if (!actorById.TryGetValue(actorId, out var actor)
                            || actor.IsCarrying
                            || actor.IsOperating
                            || !TryPrimary(actor, out var current))
                            continue;

                        float distance = Horizontal(actor.Position, pickup.Transform.Position);
                        if (distance > ResourceSearchRadius) continue;
                        float gain = EquipmentValue.NetGain(
                            current,
                            pickup.Item.Type,
                            engagementRange,
                            distance / PlayerMovement.WalkSpeed);
                        if (gain < bestGain) continue;

                        bestGain = gain;
                        selected = new SquadResourceObjective(
                            SquadResourceKind.AcquireWeapon,
                            actor.Id,
                            pickup.NetworkId,
                            pickup.Item.Type,
                            pickup.Transform.Position,
                            default);
                    }
                }
            }

            board.SetResourceObjective(selected);
            if (selected is { } assignment) claimed.Add(assignment.ObjectId);
        }
    }

    private bool TryPrimary(ServerPlayer actor, out ItemType type)
    {
        type = default;
        EquipSlot slot = actor.Equipped.ContainsKey(EquipSlot.HotbarPrimary)
            ? EquipSlot.HotbarPrimary
            : EquipSlot.Hand;
        if (!actor.Equipped.TryGetValue(slot, out uint id)
            || !objects.TryGet(id, out var item)
            || !item.Has.HasFlag(NetComponents.Weapon))
            return false;
        type = item.Item.Type;
        return true;
    }

    private static bool NearestAvailableOperator(
        SquadBlackboard board,
        IReadOnlyDictionary<ushort, ServerPlayer> actors,
        System.Numerics.Vector3 position,
        out ServerPlayer actor,
        out float distanceSquared)
    {
        actor = null!;
        distanceSquared = ResourceSearchRadius * ResourceSearchRadius;
        foreach (ushort id in board.Roster)
        {
            if (!actors.TryGetValue(id, out var candidate)
                || candidate.IsCarrying
                || candidate.IsOperating)
                continue;
            float distance = HorizontalSquared(candidate.Position, position);
            if (distance >= distanceSquared) continue;
            distanceSquared = distance;
            actor = candidate;
        }
        return actor is not null;
    }

    private static float Horizontal(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
        => MathF.Sqrt(HorizontalSquared(a, b));

    internal static bool MortarTargetIsSafe(
        int team,
        System.Numerics.Vector3 target,
        IEnumerable<ServerPlayer> actors)
    {
        float safeRadius = MortarConfig.DamageRadius + MortarConfig.DispersionMetres;
        float safeSquared = safeRadius * safeRadius;
        foreach (var actor in actors)
            if (actor.Team == team
                && actor.Status is not { Health.Current: 0 }
                && HorizontalSquared(actor.Position, target) < safeSquared)
                return false;
        return true;
    }

    private static float HorizontalSquared(System.Numerics.Vector3 a, System.Numerics.Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }
}
