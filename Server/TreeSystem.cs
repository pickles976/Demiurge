namespace Demiurge.GameServer
{
    internal sealed class TreeSystem
    {
        private readonly ObjectReplication objects;
        private readonly ChunkMap terrain;

        public TreeSystem(ObjectReplication objects, ChunkMap terrain)
        {
            this.objects = objects;
            this.terrain = terrain;
        }

        public void SpawnInitialTrees()
        {
            var trees = TreePlacement.Generate(terrain);
            Console.WriteLine($"[Vegetation]: spawning {trees.Count} server trees");

            foreach (var tree in trees)
                objects.Spawn(ObjectType.Tree, NetComponents.Transform, tree.Position,
                    obj => obj.Transform.Yaw = tree.Yaw);
        }
    }
}
