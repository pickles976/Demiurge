using Demiurge.Net;
using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>
    /// Server-authoritative terrain edits. The server owns the field; clients ask, this decides, and
    /// the decision is broadcast as a command every client replays.
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

        public void ApplyDig(ServerPlayer player, PlayerDigData dig, uint tick)
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

            player.NextDigTick = tick + TicksPerDig;

            Apply(new TerrainEditData
            {
                Centre = target,
                HalfExtent = Digging.Bite,
                Mode = EditMode.SubtractSoil,
                Fill = BlockType.BlockType_Air,
                Shape = EditShape.Sphere,
                Strength = Digging.BiteStrength,
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
