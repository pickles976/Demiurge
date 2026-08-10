# NPC AI overhaul — design

2026-08-04

## Problem

Observed behaviour, in Sebastian's words:

- NPCs sometimes stand at the flagpost doing nothing.
- NPCs dig aimless holes while defending, even inside a castle where cover already exists.
- Excavation produces blobby craters instead of 1-wide trenches and 1-wide staircases.
- NPCs behave as if constantly repathing, especially while digging out of a hole.
- Tactics are static. Flanking, sprinting and range discipline are missing.
- Nothing decides when the team should attack versus defend.

`docs/ARCHITECTURE.md` already names the root cause and declines to fix it:

> Per-unit arbitration is the least general layer in this stack, and knowingly so. […] The
> generalized form prices every available action in one currency […] Deliberately not built yet,
> because the currency is the open question.

This document supplies the currency and rebuilds per-unit arbitration and squad tactics on it.

## Investigation findings

Five defects found while exploring, each of which explains part of the complaint list. They are
recorded here because they are load-bearing for the design, not incidental.

**1. The squad deadlock is a gate, not a bug.** `SquadTactics.Plan` issues no `Bound` order unless
some member reports `IsSet`, and `MobBrain.IsSet => AtCover`. A squad in which nobody can reach
cover — no cover nearby, an incomplete dig, or a starved cover-query budget — is forbidden from
moving and stands up. The same gate drives the aimless digging: not set → dig a fighting position →
still not set.

**2. Cover queries are starved, not expensive.** `CoverQueriesPerTick = 1` throttles the entire
server to 0–3 cover searches *per second*. Measured on the conquest benchmark, cover costs 0–400
µs/tick and is frequently exactly 0, inside a tick that runs at 3.9 ms p50 against a 33 ms budget.
The budget is not protecting performance; it is starving the behaviour.

**3. Path sharing is structurally dead.** The shared-route key is only assigned when a member's
destination is within `FlagConfig.CaptureRadius` (4 m) of the objective, but `WedgeFormation`
deliberately offsets members by `Spacing = 14 m` and `Depth = 9 m`. Only slot 0 satisfies the gate,
and one man has nobody to share with. The benchmark reports `shared routes 0` for entire runs. The
formation and the sharing gate are mutually exclusive by construction.

**4. Terrain edits invalidate whole paths across a whole chunk.** `NavPath.IsValid` fails if any
chunk in the corridor has a changed revision, and `ChunkMap.MarkEdited` stamps the global version
across the edited range. A chunk is 16×16 m, so a squad digging fighting positions is usually inside
one chunk: every man's bite invalidates every man's path. At ~2 bites/second each, that is a
self-inflicted replan storm that worsens with the number of diggers. `NavigationAgent`'s 15-second
dig-site memory is a patch over it.

**5. Gunshot memory is last-write-wins.** `MobBrain` holds one heard-shot slot
(`HeardActorId`/`HeardPosition`/`HeardTick`). A distant shot arriving a tick after a point-blank one
overwrites it — precisely the "unaware of a player shooting right behind them" failure.

Two smaller ones: the `ai stats` line reports `follow` as a *residual*
(`movement − combat − entrench`) that silently contains the collision solver, which makes path
following look 55× more expensive than it is; and `docs/BARITONE.md` references a test
(`StaircaseDigTargetsStayInsideOneMetreCorridor`) that does not exist under that name.

## Scope

Rewrite per-unit arbitration and squad tactics. Keep `SquadFormation`, `SquadBlackboard`,
`Perception`, and `NavigationSystem`. `CommanderAi` keeps its shape but loses its dependency on
`FlagSystem`. Landed by outright replacement, not behind a switch.

## The currency

Every candidate action is priced in **net health points per second**.

```
score(action) = dealt(action) − aggression⁻¹ · taken(action)

dealt = Σ over believed enemies:
          Phit(myWeapon, range, mySuppression) · myDamage · myShotsPerSecond · canSee

taken = Σ over believed threats:
          Phit(theirWeapon, range, theirSuppression) · theirDamage · theirShotsPerSecond
          · myExposure(threat)
```

`myExposure` is the fraction of the capsule a given threat can reach.

### `Phit` already exists and is already correct

`Common/Ballistics/HitEstimate.Probability` is the Rayleigh CDF — `P = 1 − exp(−r²/2σ²)` with
`σ = σ_angular · range` — which is the standard operations-research hit model for bivariate-normal
dispersion against a circular target. `Spread.SigmaRadians` converts a quoted 95% group diameter
using the Rayleigh 95% factor; `Spread.TotalMoa`/`Combine` sum dispersion sources in quadrature, as
independent errors do. `GunConfig.HitRadius` is 0.6 m.

**No new curve is invented and no `EffectiveRange` field is added.** `dealt` is
`HitEstimate.Probability(...) × Damage × shotsPerSecond`, and "shoot or close" is simply
`score(HoldAndFire)` against `score(RepositionTo(closer))` — continuous, no threshold.
`CombatBehavior.MaxEngagementRangeFor`, `PrefersToHoldFire` and `ShouldAdvance` are **deleted**, not
replaced.

### Why the gate existed: the AI aim term annihilates per-weapon dispersion

`BallisticsStats.BenchMoa` differentiates weapons (sniper 2, semi-auto 3, carbine 4, pistol 8), but
`CombatBehavior` combines it with a flat `AiAimMoa = 720`:

```
Spread.Combine(720, 4) = 720.01
```

The weapon contributes four hundredths of a MOA out of 720. Per-weapon character is arithmetically
erased for NPCs, which is *why* it had to be reintroduced by hand as `ItemType` branches. Deleting
the gate without fixing this would make every NPC weapon behave identically — strictly worse than
today.

The fix matches how the OR literature actually decomposes error: total is
`σ² = σ_weapon² + σ_aiming²`, and σ_aiming is a property of the weapon–shooter *system* — sight
radius, sight picture, trigger, weight — not a constant of the shooter. So `BallisticsStats` gains a
`SightingMoa` per profile, scaled by the per-NPC skill dial, replacing the flat `AiAimMoa`.

Starting values 60 / 150 / 200 / 400 MOA for sniper / semi-automatic / carbine / pistol give:

| | σ steady (MOA) | P(hit) @20 m | @100 m | dealt @20 m | dealt @100 m |
| --- | ---: | ---: | ---: | ---: | ---: |
| Mosin | 67 | ~1.00 | 0.679 | 28 HP/s | 19.0 HP/s |
| PPSh | 428 | 0.501 | 0.027 | 97 HP/s | 5.3 HP/s |

The requested ordering — SMG dominant close, bolt gun dominant at range — derives from dispersion,
damage and cadence with no gate and no branch.

**Calibration has a real referent.** Hitchman's ORO-T-160 (Operations Research Office, 1952) measured
infantry hit probability against man-sized targets: high to ~100 yards, sharp decline beyond, and at
310 yards 25% for experts against 6% for marksmen. This model puts a Mosin at 13% at 310 yards —
between the two, which is where a competent-soldier default belongs. The expert/marksman spread then
falls out of the skill dial scaling `SightingMoa`, giving that dial a measured referent instead of a
taste setting. The pruning bound (below) is only sound if `Phit` never overestimates, so the curve is
biased deliberately toward the pessimistic side of that data.

### Rate of fire enters three ways

**Steady-state recoil, not first-shot dispersion.** `RecoilPerShotMoa` accumulates against
`RecoilDecayMoaPerSecond` and saturates at `RecoilCapMoa`. At sustained cyclic rates the PPSh and AK
are pinned at their caps (150 and 220 MOA); the Mosin's 0.67 rounds/second mostly recovers between
shots. `dealt` must evaluate dispersion at the rate actually being fired.

**Magazine duty cycle** bounds sustained rate: 35 PPSh rounds at 20/s followed by a 1.5 s reload is
54% duty.

**Rate is a choice, not a constant.** `dealt(rate) = Phit(σ(rate)) · damage · rate`, maximised over a
few candidate rates. A Mosin at 300 m optimises to slow aimed fire; a PPSh at 10 m to cyclic. Fire
discipline therefore falls out of maximising the same number, and `SuppressionBurstShots`,
`BurstPauseTicks` and `PrecisionShotIntervalTicks` are deleted as hand-set cadences.

### Suppression, both directions

**Received** suppression is already modelled: `WeaponSpreadState.SuppressionMoa` decays from
`BallisticsConfig.SuppressedMoa = 50` over 2 s and enters `TotalMoa` in quadrature. It hurts precision
weapons far more than sprayers — `Combine(67, 50) = 83.6` drops a Mosin's P(hit) at 100 m from 0.247
to 0.166, removing a third of its outgoing damage, while `Combine(400, 50) = 403` is nothing to a
PPSh. Realistic, and emergent rather than authored. This replaces the continuous-scalar suppression
model the earlier draft proposed: the magnitude already exists in MOA.

**Applied** suppression is the structural one. Firing produces two outputs — your `dealt`, *and* a
reduction in squadmates' `taken`. The second is already in HP/s and therefore directly commensurable.
This is the precise mechanism behind the joint squad allocation: a base of fire's value is mostly the
damage it *prevents* to the mover, not the damage it does. Scored individually, suppressing looks
worthless and everyone shoots at whoever they can hit; scored jointly, covering fire is priced at what
it is worth.

Whether `SuppressedMoa = 50` is large enough for bounding to price out is empirical, and the entire
squad behaviour hangs on it. A test asserts it directly: *a bound is affordable under covering fire
and not affordable without it*. If the number is wrong that test fails loudly, rather than the AI
quietly reverting to standing still.

This is the unit that makes the stated doctrine derivable rather than authored:

| Requested behaviour | Why the number produces it |
| --- | --- |
| Outranges the enemy → dig in | `dealt` is already near its ceiling and `taken` near zero. Moving lowers accuracy and raises exposure. `Entrench` lowers `taken` further. Holding wins. |
| Matched range → take turns bounding | Moving is affordable only because suppression lowers the mover's `taken`. Without a suppression model the arithmetic says stand still — which is what the current AI does. |
| Outgunned → sprint in | At range `dealt ≈ 0`, so every second there is a pure loss and closing dominates. |
| Outnumbering → flank | More shooters means stronger suppression, which lowers the mover's cost. Numerical advantage enters through the same term. |
| Digging is defensive, movement is offensive | Not a maxim but an observation: `Entrench` can only reduce `taken`; only movement can raise `dealt`. |

It also bridges to navigation, which already prices routes in estimated execution seconds: a flank
taking 8 seconds costs 8 seconds of forgone `dealt` plus expected `taken` in transit. Seconds and
health become commensurable through HP/s, so a manoeuvre and a shot compare without a second cost
model.

**`aggression` is the single global tuning scalar**, dividing the damage-taken term. Raise it and the
whole force closes, flanks and sprints; lower it and it holds and digs. It is also the lever for the
hypothesis that a more aggressive AI is cheaper — fewer cover queries, shorter paths, fewer replans —
which is measurable rather than assumed.

### Stage-1 action set

Three actions only: `HoldAndFire`, `RepositionTo(p)` over candidate positions from a simple
candidate sampler, and `Entrench`. `RepositionTo` averages its score over transit and destination
weighted by navigation's estimated seconds; transit is where `dealt ≈ 0` and exposure is high, which
is exactly why a bound requires covering fire to price out.

If the currency is right, "outrange → dig, matched → bound, outgunned → close" emerges from three
actions and no tactical rules. If it is wrong, that is discovered before grenades and elevation are
built on top.

## Squad layer

`SquadTactics` stops deciding *what* members do and decides *who does which*. Members report their
scores per candidate action; the squad picks the assignment maximising the squad total under a small
constraint set (at most one mover per bearing; the allocation is joint, not per-member).

**The coupling is what makes it a squad.** A mover's `taken` depends on how much suppression its
squadmates apply. Scored independently, every man concludes that moving is dangerous and they all
stand still — today's behaviour. Scored jointly, covering fire pays for the bound it enables.

**Flanking on separate bearings falls out.** `taken` is per-threat with an exposure term, so two
attackers on the same bearing are defeated by the same cover and their contributions correlate. Two
on different bearings mean the enemy cannot be protected from both, so the joint score is strictly
higher. `FlankSide`'s Left/Right binary is replaced by candidate positions on several bearings.

**Round-robin is emergent and ungated.** After a man bounds he is closer, so his `dealt` while
holding rises and his marginal value as next mover falls; his partner takes the next bound. There is
no "nobody moves until someone is set" precondition — the allocation always returns an assignment,
and its worst case is "everyone holds and fires", never "everyone waits". A mover that has not
arrived inside a bounded window is re-scored as a holder where it stands, so a pinned man releases
the rotation rather than freezing it.

`MobBrain.AtCover` ceases to be a precondition for anything and becomes an input to the exposure
term, which is all it ever measured.

Squads go to a maximum of 6. Proximity re-forming stays, with membership sticky while a member
executes a committed move so a replan cannot reassign a man mid-bound. `SquadBlackboard` keeps its
delayed contact sharing — units stay non-telepathic — and its existing leases, and gains excavation
leases.

### Out-of-combat movement

Out of contact the squad requests **one** path to the objective. Each member follows that trunk with
its `WedgeFormation` slot applied as a lateral/longitudinal displacement, resolved locally by
existing steering and collision — no path request of its own. This is standard formation steering
(Millington 3.7, *Coordinated Movement*).

A member requests its own path only when its offset position is genuinely untraversable, or when it
is separated far enough that the trunk is not a useful reference. In contact everyone reverts to
individual paths, since cover positions, bounds and flanks are per-man by nature.

The shared-route key is rekeyed on `(team, squad, objective)` and stops depending on the member's
destination, which is what made it unsatisfiable (finding 3).

Movement posture: walk by default; sprint only under fire; bound only inside an active combat zone.

## Strategic layer

`CommanderAi` loses its `FlagSystem` dependency. The game mode supplies an objective set:

```
Objective(Id, Position, Owner, Contest, Value, Kind)
Kind: Static   — conquest flag, KOTH hill
      Carried  — a CTF flag in someone's hands (Position moves)
      Delivery — where a carried objective must be taken
```

Conquest implements this over `FlagSystem`; CTF and KOTH implement it over their own state.
`StrategicObjectivePlanner` keeps its rank-and-allocate shape but ranks objectives rather than flags.
A carried objective is one whose position changes each tick, which the 1 Hz replan already tolerates.

**Force ratio at the point.** The commander is omniscient. For each objective it sums friendly and
enemy strength weighted by travel time, so near bodies count more. Above threshold, attack; below,
defend. Stage 1 counts heads; unit strength can later become the same HP/s value if headcount proves
too blunt. Players are counted here and never issued orders.

**Concentration.** An objective contested beyond a time threshold without changing hands is flagged a
stalemate and permitted a second squad. That is the only case where two squads share an objective.

**Surplus and reinforcement.** A squad whose objective is uncontested is reassigned to the most
contestable one, so there is no unit-level idle behaviour to write and idleness becomes a commander
bug with a single owner. Respawning NPCs are the commander's reserve and are directed the same way.

**Forward defence** is a squad decision fed by commander information: the bearing enemy pressure is
coming from. Stage 1 uses the direction of the nearest enemy mass or enemy-held objective, not
chokepoint analysis. The squad's position query then finds cover along that bearing, which is what
puts defenders on the approach instead of on the flagpost.

**Combat zones** are a short list of `(position, radius, expiry)` published wherever shots were fired
or someone died recently, decaying with time. Units read it to choose walking versus bounding.

**Information boundary.** The commander is omniscient; units act only on what they and their squad
have perceived. A unit can therefore be in the right place for reasons it does not personally know.
This is intended, and it means unit behaviour is not always explicable from that unit's knowledge —
`ai track` must surface the commander's view separately.

## Perception, hearing, suppression

**Threat pruning is sound, not heuristic.** In `taken`, everything except `myExposure` is a scalar
table lookup requiring no ray, and `myExposure ≤ 1` by construction. The exposure-free prefix is
therefore a true upper bound on an enemy's contribution. Compute the bound for every believed enemy,
sort, and spend the ray budget top-down until exhausted. An enemy below the threshold provably could
not have changed the decision by more than the threshold. A PPSh at 100 m prunes itself because
`Phit ≈ 0` past its 75 m effective range — not because a rule was written about SMGs at range.

"Already engaged elsewhere" enters the same bound as a P(targeting me) factor, sourced from the
enemy's last observed aim direction, the bearing of recent near-miss stimuli, and whether the
blackboard believes them engaged with someone else. All scalars.

Two things fall out: **target selection is the same ranked list** read from the other end, satisfying
"most dangerous to it right now"; and the `PerceptionCursor` round-robin improves for free, spending
each NPC's one ray on whoever could most change its mind rather than on whoever is next.

**Exposure for scoring reuses perception's existing rays** — one enemy per NPC per tick at centre
mass, head ray only when centre mass is blocked. Full cover queries stay event-driven and are used
for *choosing* a position, never for scoring one. The cover budget itself is opened up, since finding
2 shows it is starving behaviour rather than protecting the tick.

**Suppression needs no new magnitude** — see the currency section. `WeaponSpreadState.SuppressionMoa`
already carries it in MOA and already feeds `TotalMoa`, so a suppressed unit's `Phit` already drops
and scoring picks that up for free. The boolean `IsUnderFire`/`UnderFireUntilTick` pair remains only
as a stimulus latch for "am I being shot at", not as an accuracy or scoring input.

**Hearing** stays enemy-only, 60 m, no occlusion, and gains positional error scaled by distance — a
close shot localises almost exactly, a far one gives an area. The single heard-shot slot becomes a
small ring keyed by salience (inverse distance) rather than recency, fixing finding 5.

**Enemy weapon identification** rides on `ContactMemory`: perception records the observed weapon on
confirming a contact, and the range matchup reads it there. Only what was actually seen is used.

**Reload behaviour**: break exposure while reloading — the mirror of the enemy-reload tell already
exploited.

**The skill dial** is one scalar with a deliberate asymmetry: it delays when a contact becomes
actionable and degrades the *actual* shot, but scoring always uses the nominal hit curve. A low-skill
NPC makes the same decisions slightly late and shoots worse; it does not correctly reason about its
own incompetence, which would be decision quality returning by the back door.

## Excavation

**When to dig needs no rule.** `Entrench`'s score is the reduction it buys in `taken`. Inside a
castle, behind a ridge, or in an existing trench, `myExposure` is already near zero, so entrenching
buys nothing and loses to every other action. "Sufficient cover nearby" is not a check anyone writes;
it is what a zero-valued action looks like.

**Shape needs no rule either.** An excavation plan is the *minimum voxel set whose removal makes the
route traversable*, each voxel priced at `NavCosts.DigOneVoxel`. A second voxel of width buys no
traversability and costs a full dig, so minimum-volume is a 1-wide corridor, and the cheapest way to
gain height is a 1-wide staircase. Neither shape is authored. Traversability is measured by the real
`TerrainCollision` standability code rather than asserted, so if 1-wide does not fit the planner
widens by itself and the true minimum is learned rather than guessed.

**The brush must remove what the planner priced.** Subtraction is `max(d, −shape)`. At radius 0.7 the
centre goes to +0.7 and face neighbours to −0.3, putting the isosurface 0.7 m out: a 1.4 m cut whose
adjacent bites merge into a tube. That is the blob. At radius 0.5 the centre is +0.5 and neighbours
−0.5, placing the wall exactly 0.5 m out for a 1.0 m corridor. Excavation requests 0.5; the player's
hand-dig keeps 0.7. Same `TerrainEdits` path, a parameter rather than a second terrain path.

**The repath storm** (finding 4) gets three parts:

1. A committed cut holds no path while executing — the whole voxel list is planned up front and run
   to completion, aborting only for external reasons (contact, the goal moving, another actor's
   edit). Most of the fix is here.
2. Path validation becomes a local standability re-check of the next few waypoints instead of
   `NavPath.IsValid`'s all-or-nothing chunk-revision comparison. A distant squadmate's hole becomes
   irrelevant. Digging is subtractive, so it can only make terrain *more* passable — the sole case
   needing invalidation is ground removed from under a route, which is exactly what a local check
   catches.
3. `SquadBlackboard` leases excavation volumes, so two men never plan overlapping cuts or dig away
   each other's parapet.

**Offensive digging** is permitted wherever the cost model justifies it.
`NavCosts.DigPenaltyMultiplier` (currently 4) is the dial if tunnelling turns out to dominate — a
number to tune rather than a rule to add.

## Staging

Replace outright, in an order that keeps conquest playable and front-loads the loudest complaints.

1. **Scoring core.** Currency, three actions, `SightingMoa` replacing flat `AiAimMoa`, rate-as-a-choice,
   exposure from perception rays, threat ranking by the exposure-free bound, hearing salience fix,
   squad allocation with the applied-suppression externality. Deletes `MaxEngagementRangeFor`,
   `PrefersToHoldFire`, `ShouldAdvance` and the hand-set burst cadences. Replaces `MobSystem`
   arbitration and `SquadTactics`. Opens the cover budget. Should fix standing at the flagpost,
   aimless digging, and digging inside cover.
2. **Squad movement.** Shared trunk path plus wedge offsets as steering displacement; sharing key
   rekeyed. Fixes squad legibility and the dominant off-thread cost.
3. **Excavation.** Commit model, lazy local validation, excavation leases, 0.5 brush radius for cuts,
   minimum-volume planning. Fixes blobs and the repath storm.
4. **Strategy.** Objective abstraction, force ratio, stalemate concentration, combat zones, game-mode
   modularity.
5. **Enrichment.** Grenades, elevation term, target-selection scoring, skill dial.

Grenade doctrine is deliberately deferred to stage 5 and expressed as *considerations feeding one
score* — target in cover, squad pinned, enemies clustered, about to assault — rather than as
alternative rules. They are not mutually exclusive, and a pinned squad facing two clustered enemies
in cover should throw sooner than any single condition justifies.

## Testing

**Property tests** — properties of the model, never traces through an implementation, per the
design-method section in `CLAUDE.md`:

- a unit with a range advantage never scores closing above holding;
- a unit without one never scores holding above closing;
- `Entrench` scores zero at zero exposure;
- the squad allocation never returns an all-static assignment when a mover would raise the total;
- no pruned threat's true contribution exceeds the pruning threshold;
- an excavation plan contains no voxel not required for traversability;
- a committed cut survives a squadmate's terrain edit;
- a 1-wide corridor admits the `PlayerMovement.Body` capsule (0.4 m radius plus `SkinWidth`).

**Match statistics** from headless conquest runs: fraction of time stationary, voxels dug per NPC per
minute, flanks attempted, mean engagement range, shared-route reuses, path requests per squad per
minute. These answer "they stand around" and "they dig too much" numerically.

**Visual review.** `ai track` extended to show each NPC's role, range matchup, chosen action and its
score, plus the commander's view, so a wrong behaviour can be reported as a wrong number.

**The dig-thrash scenario: a whole squad in a pit.** The conquest benchmark reports
`0 spatially invalidated` and `shared routes 0` throughout, so it exercises neither excavation nor
sharing and cannot show the storm. The scenario that does is the existing pit from
`Common.Tests/DigEscapeTests` (13 m across, floor at y = 2.5, diggable soil walls) — already used by
`MobDigEscapeIntegrationTests.NpcExcavatesOutOfAJumpProofPitAndReturnsToSurface` for a *single* NPC —
with a full squad dropped in instead of one man. It is the maximally adversarial case for finding 4:
every member is inside one chunk, every member wants to dig, and every bite currently invalidates
everyone.

Assertions, written as properties of the model rather than traces:

- **Every member reaches the surface within a time budget**, and that budget does not degrade
  sharply with squad size. A squad of six should not take six times as long as one man.
- **Total voxels removed scales sub-linearly with squad size, approaching the cost of one
  staircase.** This is the sharpest statement of "they don't invalidate each other's staircase", and
  it is implementation-independent: once one man has cut steps, the others' routes out are
  dig-free, so minimum-volume pricing must prefer walking up the existing stairs to cutting a
  second flight. Six men digging six staircases is the current failure; six men digging roughly one
  is the target.
- **No committed cut is abandoned except for an external reason.** Counted directly.
- **Path invalidations attributable to a squadmate's terrain edit are zero.**

The first assertion catches the symptom, the second catches the cause, and the second doubles as
verification that minimum-volume excavation works at all.

**Performance.** Hold the new system to roughly the current share: AI at 0.3–1.9 ms/tick within a
tick running 3.9 ms p50 / 11 ms p99 against 33 ms. Per CLAUDE.md, percentiles are the requirement,
not means — the existing `[ServerTick]` percentile line is the gate.

## Deliberately excluded

Influence maps for real front lines and open flanks; player-commanded squads; withdrawal and retreat
(fight to the last man was chosen); the full GAIP-style tactical position query language, since stage
1 uses a simple candidate sampler; the general voxel-AABB path invalidation that would also fix
player-caused thrash; and the coarse-march cover-query optimisation.

## Risks

- **The `Phit` curve is the main one.** Wrong numbers make the AI confidently wrong in ways that read
  as bugs rather than as tuning. It is one small inspectable table, and `ai track` will print the
  resulting scores. The pruning bound is only sound if `Phit` never underestimates, so the curve
  wants a deliberate pessimistic margin, pinned by the pruning-soundness test above.
- **1-wide clearance is 0.1 m** against the 0.4 m capsule. Must be verified by test, not assumed.
- **Replace-outright means a window where the AI is worse** before it is better.
- **Proximity re-forming can still reassign a man mid-manoeuvre**; sticky membership during committed
  moves mitigates but does not eliminate this.
- **The omniscient commander makes unit behaviour not always explicable** from that unit's own
  knowledge.

## Reading

- Millington, *AI for Games* 3e — 3.7 coordinated movement; 5.7 goal-oriented behaviour; 6.2 tactical
  analyses; 6.3 tactical pathfinding; 6.4 coordinated action.
- Mark, *Behavioral Mathematics for Game AI* — utility considerations and response curves.
- *Game AI Pro 1* — ch. 9 utility theory; ch. 26 tactical position selection.
- *Game AI Pro 2* — ch. 3 dual-utility reasoning; ch. 30 modular tactical influence maps.
- *Game AI Pro 3* — ch. 13 choosing effective utility-based considerations.

Weapon effectiveness:

- Hitchman, N. A., **ORO-T-160, *Operational Requirements for an Infantry Hand Weapon***, Operations
  Research Office, Johns Hopkins University, June 1952. The canonical P(hit)-versus-range study for
  infantry rifles: high to ~100 yards, sharp decline beyond, 25% (expert) versus 6% (marksman) on a
  man-sized target at 310 yards. Used here as the calibration referent for `SightingMoa` and the
  skill dial. [Full text (DTIC AD0000346)](https://archive.org/stream/DTIC_AD0000346/DTIC_AD0000346_djvu.txt),
  [summary](https://www.everydaymarksman.co/resources/norman-hitchmans-status-quo-smashing-1952-report/),
  [context](https://www.thefirearmblog.com/blog/2014/07/08/weekly-dtic-hitchman-gustafson-reports/).
- [*Effect of the firing position on aiming error and probability of hit*](https://www.sciencedirect.com/science/article/pii/S2214914719301965)
  — modern treatment of the same dispersion-to-P(hit) decomposition, and the reason stance belongs in
  the quadrature sum rather than as a multiplier.
