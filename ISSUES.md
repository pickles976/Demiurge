# Open issues

Notes under each item are findings from investigation, not speculation — every number in here was
measured. Where an approach was tried and rejected, it says so, to save the next session repeating it.

## AI digging too slowly

`Digging.ClicksPerVoxel = 2` and the CSG lerps toward the cut rather than assigning it, so a voxel
takes roughly 3-4 bites to become properly clear (traced: -2.54 → -0.92 → -0.11 → 0.30 → 0.50).
`Digging.TicksPerDig` is 15 at 30 Hz, i.e. two bites a second.

Now that staircasing works there is a concrete number for this: escaping a 10 m pit costs **166
bites**, which at two a second is about 80 seconds. One stair step is 13 bites (measured by
`DigEscapeTests.ClearingHeadroomAboveATreadMakesItStandable`). Whether that is too slow is a design
call — `ClicksPerVoxel` and `TicksPerDig` are the two knobs, and both are shared with player digging.

## AI not pathfinding across bridges, falling into giant trenches

The test map has deep trenches dug with a 15 m spherical brush, with bridges across. Sometimes NPCs
cross, sometimes they fall in.

**Not reproduced.** `NavigationTests.WideTrenchWithABridgeIsCrossedByRepeatedRequestsAndNeverDescendsIntoIt`
was written chasing this and **passes against the code as it was before any of this session's
changes** — it is a guard, not a reproduction, and is labelled as such in the file. Whatever actually
puts NPCs in the trenches is still unfound. Next step is watching it on the real map (`ai track`)
rather than synthesising it.

What *was* established, with numbers:

- A single search cannot cross a 15 m trench to a bridge 10 m off the straight line. It needs ~527
  expansions; `PrimaryBudget` is 25 ms and `FailureBudget` 100 ms.
- An A* expansion cost **0.677 ms** before `VoxelCursor` and **0.182 ms** after (3.7x), so the
  crossing went from 357 ms to 96 ms.
- It does not have to cross in one search — the follower re-requests continuously and repeated
  requests converge. That is why the single-shot failure is not by itself the bug.
- Three hypotheses were tested and **disproved**: that the search could not find the bridge (it can,
  given budget); that jump simulation at the lip was the cost (`AllowJump: false` changes nothing);
  that the open set exhausted (4,872 cells are reachable).

## Needs checking with the window FOCUSED

The last few singleplayer runs of this session reported 15 fps where earlier runs of near-identical
client code reported 60-140. Bisecting NPC sprinting changed the urgent-queue behaviour but not the
frame rate at all, and the conquest navigation benchmark — which does not render — is unchanged
(3179 expanded nodes either way). CLAUDE.md notes an unfocused Stride window is throttled by the
engine, and the erratic 15/5/16 pattern matches that. Unconfirmed either way: check it focused before
treating it as a regression.

## Fixed this session

- **NPCs did not react to being shot.** Only a near MISS raised suppression, so a man hit squarely
  from four hundred metres took the damage and walked on. A hit now enqueues the same
  `AcceptedSuppression` a near miss does — same MarkUnderFire, same contact, same broadcast to the
  squad — rather than growing a parallel path. Range never entered into it: the test is the
  projectile, not a radius.
- **NPCs did not react to rounds striking near them.** The fly-by test measures distance from the
  projectile's PATH, so fire aimed at the cover someone is behind — which is what suppressing fire
  IS — went unnoticed by the person being suppressed. `SuppressNearImpact` now covers the strike
  point. Radii are `GunConfig.NearMissRadius` (2.5 m, was an unnamed 2 m) and
  `ImpactSuppressionRadius` (4 m).
- **NPCs never sprinted.** They now do when the movement is the job and shooting is not: bounding,
  closing on cover not yet reached, denied permission to fire, or out of contact entirely. Never
  while firing, crouched, or digging — `SprintingMoa` and the post-sprint penalty already price it,
  so the flags that buy the speed also pay for it.
- **Squads travelled as a clump.** Destinations were a golden-angle ring around the objective, which
  spread men only once they had ARRIVED; for the whole approach they steered at one point.
  `Common/Ai/WedgeFormation.cs` gives each man a slot in a V oriented on the approach, so the shape
  holds for the journey. Slots come from roster order, which is stable, so a man keeps his place.
- **Reinforcements spawned at the back.** `FlagSystem.TrySpawnPosition` cycled controlled flags by
  network id, so a man was as likely to appear at the flag furthest from the fighting as the nearest.
  It now always picks the controlled flag closest to a flag the team does NOT hold. Note the trap
  found on the way: sorting alone fixes nothing, because the round robin still visits every flag —
  the cycling itself had to go, and the counter now only scatters men within the one spawn circle.
- **Urgent terrain lane accumulated undispatchable sections.** An NPC digging in a chunk this client
  never loaded produced a dirty section whose footprint may never arrive, and the urgent lane is
  rescanned in full every frame by design. Those are now dropped rather than requeued; ChunkCompleted
  re-dirties the whole chunk, edit included, if it ever arrives.

- **AI dug a post-hole instead of a fighting position.** `DigEmergencyCover` probed a single spot
  half a metre in FRONT of the actor and bit it repeatedly: deep enough to satisfy the depth check,
  too narrow to take cover in. The shape now lives in `Common/Voxel/FoxholePlan.cs` as an explicit
  ordered pattern — your own square first (until that is down you have no cover at all), then left,
  right and back. Forward is never cut: that lip is the parapet you shoot over, and removing it turns
  the position back into open ground facing the threat.

  Two things that had to be got right, both pinned by `FoxholePlanTests`:

  - **Depth is checked per SQUARE, not once for the hole.** A single global check stops the moment
    the first square is deep enough, which is exactly how you get a post-hole.
  - **Grade is measured behind the actor**, outside its own excavation, or the hole defines its own
    grade and the digging never terminates.

  Measured: 5 bites, floor 0.87 m below grade. Note that overlapping spherical bites compound in the
  middle, so a wider pattern digs deeper than its nominal depth — a six-square pattern reached 2.9 m
  for a 1 m target, which is a grave rather than a foxhole. The test asserts the floor from BOTH
  ends for that reason.

- **AI could not dig its way out of a trench or pit.** `Common.Tests/DigEscapeTests` reproduced it:
  400 bites into a 10 m soil pit, zero height gained. Now escapes in **166 bites**, Y=2 to Y=12.

  Three separate defects, each of which alone kept it at the bottom:

  1. **A bite is a sphere**, so aiming it at a wall carves an *alcove* — as much material comes out
     below the aim point as above it. The old branch aimed the ray upward hoping replans would stack
     into a stair; a higher alcove is still a dent with no floor. `NavTraversal.TryStaircaseTarget`
     instead takes the headroom **above** a tread one cell up, which is the material you leave.
  2. **The two dig branches fought each other.** An actor two cells back saw the higher surface,
     failed the tread test because the cell ahead was open air, and fell through to a level bite that
     drove straight through the tread its neighbour was standing on. The hole always won. The rising
     branch now cuts nothing rather than falling through — and the stair column is keyed to the
     column that HOLDS the higher surface, since at the bottom of a pit the actor is usually a cell
     short of the wall. (Getting that wrong is what regressed the three tests listed below.)
  3. **A cell is bounded by two samples on each horizontal axis.** Clearing a single sample column
     left solid material half a metre from the capsule and the step was never standable — a
     beautifully shaped pocket nobody could get into. The cut clears the cell's whole 2x2 footprint.

  Worth knowing for anything similar: a vertical wall denies the ADJACENT cell its capsule clearance,
  so the last standable floor cell is two cells out. Cutting the stair is what opens that cell up,
  and the chain only works because of it.

  Contract tests that constrain this area, all passing:
  `NavigationTests.DirtPitFrontierAimsUpwardToCutAnExit`,
  `DeepDirtPitFrontierStillAimsUpwardToCutAnExit`,
  `TrenchWithWalkableFloorStillCutsAnExitTowardTheGoal`.

- **Navigation worker crash.** `TryReuseSharedRoute` called `NavTraversal.Position` on a start cell
  captured on the main thread at enqueue and read later on a worker; a dig under that actor left it
  unstandable and the throw killed the process. The route's own `IsValid` does not cover it —
  that checks chunk revisions along the ROUTE, and `SharedRouteJoinRadius` is 24 m. Fixed with
  `NavTraversal.TryPosition`; regression test in `NavigationSystemTests`.
- **Model teleporting to spawn on NPC death.** The client never cleared a remote player's snapshot
  history, so a respawn was interpolated as a 100 ms glide from the corpse to the spawn point — and
  `IsDead` reads `RespawnTick` off the newest packet while the position is drawn 3 ticks behind, so
  the model switched back on *at the corpse* and then flew. Fixed in `PlayerRegistry`.
- **Terrain edits slow to appear.** Measured dig→visible at up to **458 ms** under load. The urgent
  lane prioritised dispatch but not COLLECTION, so a dig's finished mesh joined one unordered output
  queue behind the streaming backlog. `SectionMeshQueue` now has a separate urgent result queue.
  Now 9.8 ms average, 14.1 ms worst. The `terrain edits:` log line reports this live.
