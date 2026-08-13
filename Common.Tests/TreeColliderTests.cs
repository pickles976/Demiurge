using System.Numerics;
using Xunit;

namespace Demiurge.Tests
{
    /// <summary>
    /// The trunk index, tested as geometry rather than as behaviour. It earns tests for the reason
    /// PlayerMovement's maths does: both ends run it, so a disagreement is not a bug you see, it is a
    /// reconciliation correction every step near a wood.
    /// </summary>
    public class TreeColliderTests
    {
        private const float BodyRadius = 0.6f;
        private const float BodyHeight = 1.8f;

        private static TreeColliders With(params Vector3[] trunks)
        {
            var colliders = new TreeColliders();
            foreach (var trunk in trunks) colliders.Add(trunk);
            return colliders;
        }

        [Fact]
        public void EmptyIndexResolvesNothing()
        {
            Assert.False(new TreeColliders()
                .TryResolve(Vector3.Zero, BodyRadius, BodyHeight, out _));
        }

        [Fact]
        public void ClearOfTheTrunkIsNotAContact()
        {
            var trees = With(Vector3.Zero);
            float clear = TreeColliders.Reach(BodyRadius) + 0.01f;

            Assert.False(trees.TryResolve(
                new Vector3(clear, 0f, 0f), BodyRadius, BodyHeight, out _));
        }

        [Fact]
        public void OverlapPushesOutAlongTheAxisToExactlyTouching()
        {
            var trees = With(Vector3.Zero);
            float reach = TreeColliders.Reach(BodyRadius);
            var feet = new Vector3(reach * 0.5f, 0f, 0f);

            Assert.True(trees.TryResolve(feet, BodyRadius, BodyHeight, out var pushOut));

            // Moving by the pushout leaves the body exactly at reach: no deeper, and not flung past.
            float resolved = MathF.Sqrt(
                MathF.Pow(feet.X + pushOut.X, 2) + MathF.Pow(feet.Z + pushOut.Y, 2));
            Assert.Equal(reach, resolved, 3);
        }

        [Fact]
        public void DeadCentreStillHasSomewhereToGo()
        {
            var trees = With(Vector3.Zero);

            Assert.True(trees.TryResolve(Vector3.Zero, BodyRadius, BodyHeight, out var pushOut));
            Assert.Equal(TreeColliders.Reach(BodyRadius), pushOut.Length(), 3);
        }

        [Fact]
        public void AboveTheCrownIsClear()
        {
            var trees = With(Vector3.Zero);
            var overhead = new Vector3(0f, TreePlacement.TrunkHeight + 0.1f, 0f);

            Assert.False(trees.TryResolve(overhead, BodyRadius, BodyHeight, out _));
        }

        [Fact]
        public void BelowTheBaseIsClear()
        {
            // A trunk on a ledge above a man in a cutting does not block the cutting.
            var trees = With(new Vector3(0f, 10f, 0f));

            Assert.False(trees.TryResolve(Vector3.Zero, BodyRadius, BodyHeight, out _));
        }

        [Fact]
        public void TheDeepestTrunkWins()
        {
            // Two trunks close enough to overlap one body. The push has to clear the one it is
            // furthest inside, or resolving the shallower leaves it still buried in the other.
            float reach = TreeColliders.Reach(BodyRadius);
            var shallow = new Vector3(reach * 0.9f, 0f, 0f);
            var deep = new Vector3(-reach * 0.2f, 0f, 0f);
            var trees = With(shallow, deep);

            Assert.True(trees.TryResolve(Vector3.Zero, BodyRadius, BodyHeight, out var pushOut));

            float fromDeep = MathF.Sqrt(
                MathF.Pow(pushOut.X - deep.X, 2) + MathF.Pow(pushOut.Y - deep.Z, 2));
            Assert.Equal(reach, fromDeep, 3);
        }

        [Fact]
        public void TrunksAreFoundAcrossGridCells()
        {
            // The index is a grid, so a trunk whose cell is not the body's must still be seen. Walk a
            // line past a trunk far from the origin and check the contact appears where the geometry
            // says it should rather than where the cell boundary is.
            var trunk = new Vector3(103.7f, 0f, -58.2f);
            var trees = With(trunk);
            float reach = TreeColliders.Reach(BodyRadius);

            for (int step = -20; step <= 20; step++)
            {
                float offset = step * 0.1f;

                // Skip the knife edge itself. Whether a body exactly `reach` from an axis counts as
                // touching is a question about float rounding, not about the index.
                if (MathF.Abs(MathF.Abs(offset) - reach) < 0.01f) continue;

                var feet = trunk with { X = trunk.X + offset };
                bool hit = trees.TryResolve(feet, BodyRadius, BodyHeight, out _);
                Assert.Equal(MathF.Abs(offset) < reach, hit);
            }
        }
    }
}
