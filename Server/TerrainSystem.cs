using Demiurge.Net;
using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>
    /// Server-authoritative terrain edits. The server owns the field; clients ask, this decides, and
    /// the decision is broadcast as a command every client replays.
    ///
    /// Digging and placing are one path with the operator flipped — see <see cref="TerrainAction"/>
    /// — so the rate gate, the reach check and the grid snap are written once and cannot come apart.
    ///
    /// Digging is gated on TICKS rather than on how fast the client sends, for the same reason
    /// firing is: a message rate is whatever a client chooses it to be, while a tick is the same
    /// clock on both ends. The cost this protects is NOT bandwidth — an edit is 26 bytes — it is
    /// the section re-mesh every receiving client has to run afterwards.
    /// </summary>
    public class TerrainSystem
    {
        private readonly INetServer server;
        private readonly ChunkMap terrain;

        /// <summary>
        /// Minimum ticks between one player's digs — 15 at 30 Hz, so two edits a second. Slow
        /// enough to read as hand-digging, and it bounds the re-meshing a single player can force on
        /// everyone else near them.
        /// </summary>
        private const uint TicksPerDig = Digging.TicksPerDig;

        public TerrainSystem(INetServer server, ChunkMap terrain)
        {
            this.server = server;
            this.terrain = terrain;
        }

        /// <param name="plannedExcavation">Full-strength sample change: one bite clears a voxel
        /// instead of two, so a planned cut does not pay double the rate-limited shovel cycles.</param>
        /// <param name="narrowCorridor">
        /// This bite is cutting a ROUTE — a staircase or passage laid out on the 1 m navigation
        /// lattice — so it uses the 0.5 m brush, which takes its own voxel and leaves the tread below
        /// it and the squadmate's parapet beside it intact. The freehand 0.7 m brush is 2.7x the
        /// volume and deliberately spills a third of the way into its face neighbours, which is what
        /// a player wants under a cursor and is exactly why AI cuts came out as globs rather than
        /// stairs.
        ///
        /// It is NOT the right brush for every AI dig. Clearing standing headroom under a low tunnel
        /// mouth, or cutting back an unwalkable slope face, is widening rather than routing: a 1 m
        /// tube through those leaves the actor without clearance, and both integration scenarios fail
        /// on it. Those call sites keep the wide brush.
        /// </param>
        public void ApplyDig(
            ServerPlayer player,
            PlayerDigData dig,
            uint tick,
            bool plannedExcavation = false,
            bool narrowCorridor = false)
        {
            if (!IsFinite(dig.Target)) return;
            if (tick < player.NextDigTick) return;

            // Slot 2 is the placeholder shovel: it deliberately has no item object yet, but
            // selecting it authorizes digging for players and NPCs through the same path.
            if (player.Hotbar != HotbarSlot.Shovel)
                return;

            // The one thing genuinely worth enforcing: you dig what you can reach. Everything else
            // about the request is the client's own aim, which the server has no better view of.
            if (!Digging.InReach(player.Position, dig.Target)) return;

            // Snap to the grid the client derived it on. A target off the lattice would still carve
            // something, just not the voxel that was highlighted — and it is a free way to reject
            // a client trying to shave half-voxels for a smoother tunnel than the rules allow.
            var target = new Vector3(MathF.Round(dig.Target.X), MathF.Round(dig.Target.Y), MathF.Round(dig.Target.Z));

            // Below the floor there is nothing to win: ChunkConstants clamps the bottom plane solid
            // on every write, so the edit would be silently undone and we would have spent a
            // broadcast and a re-mesh on every client for no change at all.
            if (target.Y <= ChunkConstants.WorldMinY) return;

            bool placing = dig.Action == TerrainAction.Place;

            // The client will not ask while placement is off, so this is the server refusing to take
            // an old or hand-made request's word for it — the same reason reach is re-checked.
            if (placing && !Digging.PlacementEnabled) return;

            // Nothing above the world to build on or into, and the top plane is where a section
            // stops owning grid points.
            if (target.Y >= ChunkConstants.WorldMaxY - 1) return;

            // You may not build through yourself. Re-checked here rather than trusted because the
            // client's refusal is a highlight the player can see and this one is the rule.
            if (placing && Digging.WouldEncasePlayer(player.Position, target)) return;

            player.NextDigTick = tick + TicksPerDig;

            Apply(new TerrainEditData
            {
                Centre = target,
                HalfExtent = narrowCorridor ? Digging.PlannedBite : Digging.Bite,
                Mode = placing ? EditMode.Add : EditMode.SubtractSoil,
                Fill = placing ? Digging.PlacedBlock : BlockType.BlockType_Air,
                Shape = EditShape.Sphere,
                Strength = plannedExcavation
                    ? Digging.PlannedBiteStrength
                    : Digging.BiteStrength,
            });
        }

        /// <summary>Applies an edit to the authoritative field and tells everyone to do the same.</summary>
        public void Apply(TerrainEditData edit)
        {
            TerrainEdits.ApplyBox(terrain, edit.Centre, edit.HalfExtent, edit.Mode, edit.Fill, edit.Shape, edit.Strength);

            // Reliable: a dropped edit would leave that client's world permanently disagreeing with
            // the server's, with nothing to correct it — chunks are streamed once and never resent.
            var message = Message.Create(MessageSendMode.Reliable, ServerToClientId.TerrainEdit);
            message.AddSerializable(edit);
            server.SendToAll(message);
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
