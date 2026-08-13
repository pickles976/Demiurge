using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// The trunks a body cannot walk through, in a form the shared movement step can ask cheaply.
    ///
    /// It lives in Common and is pure for the same reason PlayerMovement is: BOTH ENDS RUN IT. The
    /// client predicts a move and the server re-steps the same one, and if the two disagree about
    /// where a trunk is, every step near a tree produces a reconciliation correction. So this holds
    /// no engine types, does no lookups of its own, and answers only from what it was given.
    ///
    /// A uniform grid rather than anything cleverer, because trunks are STATIC in X and Z — a tree
    /// that dies falls straight down and never moves sideways — so the index is built once and only
    /// grows. The cell is sized to the collision reach, which means a query touches at most four
    /// cells and usually one.
    ///
    /// Note what is NOT here: the canopy. A trunk is half a metre across and the leaves are eight,
    /// and making a man walk around the drip line of every tree would turn a wood into a maze that
    /// looks passable. Only the trunk stops anybody.
    /// </summary>
    public sealed class TreeColliders
    {
        /// <summary>
        /// How far a body's centre can be from a trunk's axis before they are clear of each other.
        /// The trunk's own radius plus the body's, so it is the same number the resolve pushes to.
        /// </summary>
        public static float Reach(float bodyRadius) => TreePlacement.TrunkRadius + bodyRadius;

        /// <summary>Grid pitch. One trunk reach plus a body, so a query never has to look further
        /// than the neighbouring cell.</summary>
        private const float CellSize = 4f;

        private readonly Dictionary<(int X, int Z), List<Vector3>> cells = [];

        public int Count { get; private set; }

        public void Clear()
        {
            cells.Clear();
            Count = 0;
        }

        /// <summary>
        /// Adds a trunk at its placement position — the base, which is sunk below the surface, so the
        /// trunk's vertical span starts there rather than at the ground.
        /// </summary>
        public void Add(Vector3 position)
        {
            var key = KeyOf(position.X, position.Z);
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add(position);
            Count++;
        }

        /// <summary>
        /// The nearest trunk overlapping a body standing at <paramref name="feet"/>, if any, and how
        /// far it has to move to be clear.
        ///
        /// Horizontal only. A trunk is a wall, not a floor: pushing vertically would let a man climb
        /// a tree by walking into it, and would fight the ground contact that is holding him up.
        /// </summary>
        public bool TryResolve(
            Vector3 feet, float bodyRadius, float bodyHeight, out Vector2 pushOut)
        {
            pushOut = default;
            if (Count == 0) return false;

            float reach = Reach(bodyRadius);
            float deepest = 0f;

            int minX = (int)MathF.Floor((feet.X - reach) / CellSize);
            int maxX = (int)MathF.Floor((feet.X + reach) / CellSize);
            int minZ = (int)MathF.Floor((feet.Z - reach) / CellSize);
            int maxZ = (int)MathF.Floor((feet.Z + reach) / CellSize);

            for (int cx = minX; cx <= maxX; cx++)
            {
                for (int cz = minZ; cz <= maxZ; cz++)
                {
                    if (!cells.TryGetValue((cx, cz), out var trunks)) continue;

                    foreach (var trunk in trunks)
                    {
                        // Above the crown or below the base: a felled trunk lying in a pit does not
                        // block the ground above it, and nothing blocks a man who has cleared it.
                        if (feet.Y >= trunk.Y + TreePlacement.TrunkHeight) continue;
                        if (feet.Y + bodyHeight <= trunk.Y) continue;

                        float dx = feet.X - trunk.X;
                        float dz = feet.Z - trunk.Z;
                        float distanceSq = dx * dx + dz * dz;
                        if (distanceSq >= reach * reach) continue;

                        float distance = MathF.Sqrt(distanceSq);
                        float depth = reach - distance;
                        if (depth <= deepest) continue;

                        deepest = depth;

                        // Dead centre on the axis has no direction to leave by. Any direction is as
                        // good as another there, and picking one beats dividing by zero.
                        pushOut = distance > 1e-4f
                            ? new Vector2(dx / distance, dz / distance) * depth
                            : new Vector2(depth, 0f);
                    }
                }
            }

            return deepest > 0f;
        }

        private static (int X, int Z) KeyOf(float x, float z)
            => ((int)MathF.Floor(x / CellSize), (int)MathF.Floor(z / CellSize));
    }
}
