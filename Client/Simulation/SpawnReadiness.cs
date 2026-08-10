namespace Demiurge.GameClient
{
    /// <summary>
    /// Whether the ground under the local player has been meshed yet.
    ///
    /// Shared state rather than an event, and read by two very different consumers — the controller,
    /// which refuses to move a player standing on terrain that is not there, and the HUD, which has
    /// to explain why. The same A+B pattern the status HUD uses: one writer, several readers, no
    /// polling of the terrain view from places that have no business knowing about it.
    /// </summary>
    public sealed class SpawnReadiness
    {
        /// <summary>False until the sections around the spawn have finished meshing.</summary>
        public bool Ready { get; set; }
    }
}
