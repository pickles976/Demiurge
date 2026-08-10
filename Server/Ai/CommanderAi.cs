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

    // Rebuilt once per team per replan, not once per squad: every tube on a team is solving against
    // the same battlefield, and this is the whole of it.
    private readonly List<MortarTarget> fireMissionEnemies = [];
    private readonly List<System.Numerics.Vector3> fireMissionFriendlies = [];

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
            AssignResources(tick, team, boards, actors, claimedResources);
        }
    }

    private void AssignResources(
        uint tick,
        int team,
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

        BuildFireMissionInputs(team, actors);

        foreach (var pair in boards)
        {
            var board = pair.Value;
            SquadResourceObjective? selected = null;
            float bestMissionValue = MortarTargeting.MinimumMissionValue;

            // Any tube this squad already has a man on, laid on wherever the fire mission says.
            //
            // The target no longer comes from the squad's own primary threat, and that is the point.
            // A mortar is served on information the TEAM has, not on what the man behind it can
            // personally see: the tube is fifty metres behind the line by construction, so a contact
            // model built for a rifleman's line of sight is the wrong instrument entirely, and the
            // one that was there delivered a single moving individual reported a third of a second
            // late. See MortarTargeting.
            foreach (ushort actorId in board.Roster)
            {
                if (!actorById.TryGetValue(actorId, out var currentOperator)
                    || currentOperator.OperatingObjectId == 0
                    || !objects.TryGet(currentOperator.OperatingObjectId, out var currentMortar)
                    || !ItemCatalog.HasBehavior(currentMortar.Item.Type, ItemBehavior.Mortar)
                    || !TryFireMission(currentMortar, out var target, out float value)
                    || value <= bestMissionValue)
                    continue;

                bestMissionValue = value;
                selected = new SquadResourceObjective(
                    SquadResourceKind.OperateMortar,
                    currentOperator.Id,
                    currentMortar.NetworkId,
                    currentMortar.Item.Type,
                    currentMortar.Transform.Position,
                    target);
            }

            // Otherwise, a tube on the ground worth walking to. Judged by the mission it could fire
            // rather than by how close it is: a mortar nobody can bring to bear is not a resource.
            foreach (var mortar in selected is null ? pickups : [])
            {
                if (claimed.Contains(mortar.NetworkId)
                    || worked.Contains(mortar.NetworkId)
                    || !ItemCatalog.HasBehavior(mortar.Item.Type, ItemBehavior.Mortar)
                    || !TryFireMission(mortar, out var target, out float value)
                    || value <= bestMissionValue
                    || !NearestAvailableOperator(
                        board, actorById, mortar.Transform.Position, out var gunner, out _))
                    continue;

                bestMissionValue = value;
                selected = new SquadResourceObjective(
                    SquadResourceKind.OperateMortar,
                    gunner.Id,
                    mortar.NetworkId,
                    mortar.Item.Type,
                    mortar.Transform.Position,
                    target);
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

    /// <summary>
    /// Everybody a fire mission cares about, from the server's own actor list.
    ///
    /// Deliberately GROUND TRUTH rather than the squad's believed contacts. A mortar is an indirect
    /// weapon: it is laid on a grid reference somebody else supplied, and there is nobody behind the
    /// tube who can see the target by construction — the minimum range is fifty metres. Modelling a
    /// forward-observer network so the AI could arrive back at "the team knows where the enemy is"
    /// would be machinery in service of a result already available, and it would make the weapon
    /// worse at the one thing it is for. It is also what was asked for.
    /// </summary>
    private void BuildFireMissionInputs(int team, ICollection<ServerPlayer> actors)
    {
        fireMissionEnemies.Clear();
        fireMissionFriendlies.Clear();
        if (team <= 0) return;

        foreach (var actor in actors)
        {
            if (actor.Team <= 0 || actor.Status is { Health.Current: 0 }) continue;
            if (actor.Team == team)
            {
                fireMissionFriendlies.Add(actor.Position);
                continue;
            }

            // Counter-battery, as a weight rather than as a mode. A man on a crew weapon is worth
            // more than a rifleman because the tube goes with him, and he is also standing still,
            // which MortarTargeting already prices as the easier shot it is.
            bool crewServed = actor.OperatingObjectId != 0
                && objects.TryGet(actor.OperatingObjectId, out var served)
                && served.Has.HasFlag(NetComponents.Item)
                && ItemCatalog.HasBehavior(served.Item.Type, ItemBehavior.Mortar);

            fireMissionEnemies.Add(new MortarTarget(
                actor.Position,
                actor.Move.Velocity,
                crewServed ? MortarTargeting.CrewServedWeaponValue : 1f));
        }
    }

    /// <summary>The best mission this tube can fire, given where it is emplaced and which way it was
    /// laid. Its own sector and range band are what make one tube's answer differ from another's.</summary>
    private bool TryFireMission(ServerObject mortar, out System.Numerics.Vector3 target, out float value)
    {
        target = default;
        value = 0f;
        if (fireMissionEnemies.Count == 0) return false;

        // One representative flight, at the middle of the band, rather than re-solving the arc per
        // candidate. The arc varies by a couple of seconds across the whole reachable band and the
        // spread it feeds is already several metres wide; paying for exactness inside that would buy
        // no different decision.
        var muzzle = mortar.Transform.Position
            + System.Numerics.Vector3.UnitY * MortarBallistics.MuzzleHeight;
        var midpoint = muzzle
            + new System.Numerics.Vector3(
                MathF.Sin(mortar.Transform.Yaw),
                0f,
                MathF.Cos(mortar.Transform.Yaw))
                * ((MortarConfig.MinimumRange + MortarConfig.MaximumRange) * 0.5f);
        float flightSeconds =
            MortarBallistics.FlightSeconds(muzzle, midpoint, ProjectileMotion.Gravity)
            ?? MortarConfig.MaxFlightSeconds * 0.5f;

        return MortarTargeting.TrySolve(
            mortar.Transform.Position,
            mortar.Transform.Yaw,
            flightSeconds,
            fireMissionEnemies,
            fireMissionFriendlies,
            out target,
            out value);
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

    /// <summary>
    /// The last check before the lanyard, and deliberately NOT a second opinion on the target.
    ///
    /// Whether a mission is worth firing is <see cref="MortarTargeting"/>'s question, and it already
    /// prices our own casualties in the same tickets as theirs — which is what lets a round that
    /// kills three of theirs land thirty metres from one of ours, as it should. This is the floor
    /// under that: a plan is up to a commander-second old and men walk six metres in a second, so a
    /// friendly who has since moved UNDER the burst stops the round. Lethal radius plus the tube's
    /// own scatter, i.e. "we would certainly kill him", not the fifteen-metre ring the score handles.
    ///
    /// It used to be that fifteen-metre ring plus scatter, twenty metres of veto over a weapon that
    /// only reaches fifty — so a squad advancing anywhere near its own objective silenced its own
    /// support, and the tube sat loaded.
    /// </summary>
    internal static bool MortarTargetIsSafe(
        int team,
        System.Numerics.Vector3 target,
        IEnumerable<ServerPlayer> actors)
    {
        float safeRadius = MortarConfig.LethalRadius + MortarConfig.DispersionMetres;
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
