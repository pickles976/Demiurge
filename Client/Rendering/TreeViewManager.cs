using Demiurge.GameClient;
using System.Collections.Concurrent;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// Draws every replicated tree, at a detail that depends on how far away it is. Nothing is ever
    /// culled: a wood seen from across the valley is still a wood.
    ///
    /// A near tree is six draw calls — five for the trunk and its branches, one for its leaf cards.
    /// A far one is two, having dropped the branches and traded a hundred and ten small cards for
    /// fifteen large ones. The silhouette survives; the detail inside it does not, and at a hundred
    /// metres there is nothing inside it to see.
    ///
    /// Tree views carry no script. A tree does not move, so its transform is set once when the view
    /// is made — where an ordinary replicated object gets a NetTransformScript that interpolates
    /// snapshots every frame, which for hundreds of static objects is hundreds of script updates a
    /// frame spent arriving at the answer they already had.
    /// </summary>
    public sealed class TreeViewManager
    {
        /// <summary>
        /// Where detail drops, and where it comes back. The gap is hysteresis: one threshold would
        /// make a tree at exactly that range swap models every time the player shifted his weight.
        /// </summary>
        private const float DetailDropRadius = 70f;
        private const float DetailRestoreRadius = 60f;

        /// <summary>How far the player moves before the detail is worth reconsidering. The two radii
        /// differ by 10 m; re-evaluating every frame would spend hundreds of distance tests to
        /// discover nothing changed.</summary>
        private const float RefreshDistance = 5f;

        private readonly Game game;
        private readonly Scene scene;
        private readonly PlayerRegistry players;
        private readonly ModelLocators locators;

        // Spawns arrive on the network thread when latency simulation is off, so the arrival side is
        // concurrent and the view side is main-thread only.
        private readonly ConcurrentDictionary<uint, NetObject> trees = new();
        private readonly ConcurrentQueue<uint> removed = new();
        private readonly Dictionary<uint, Entity> views = [];
        private readonly Dictionary<uint, TreeViewFactory.LeafDetail> details = [];
        private readonly Entity driver;

        private Vector3 lastPosition;
        private bool hasLastPosition;
        private volatile bool dirty;

        public TreeViewManager(Game game, Scene scene, PlayerRegistry players, ModelLocators locators)
        {
            this.game = game;
            this.scene = scene;
            this.players = players;
            this.locators = locators;

            driver = new Entity("TreeViewManager") { new DriverScript { Manager = this } };
            driver.Scene = scene;
        }

        /// <summary>For the tick breakdown, and for confirming the LOD does anything.</summary>
        public int TotalTrees => trees.Count;
        public int DetailedTrees => details.Count(entry => entry.Value == TreeViewFactory.LeafDetail.Near);

        public void Add(NetObject tree)
        {
            trees[tree.NetworkId] = tree;
            dirty = true;
        }

        public void Remove(uint networkId)
        {
            trees.TryRemove(networkId, out _);
            removed.Enqueue(networkId);
            dirty = true;
        }

        public void Dispose()
        {
            foreach (var view in views.Values) view.Scene = null;
            views.Clear();
            trees.Clear();
            driver.Scene = null;
        }

        public void Update()
        {
            while (removed.TryDequeue(out uint networkId)) RemoveView(networkId);

            if (players.LocalPlayer is not { } local) return;

            var position = local.Position.ToStride();
            float moveX = position.X - lastPosition.X;
            float moveZ = position.Z - lastPosition.Z;
            if (!dirty
                && hasLastPosition
                && moveX * moveX + moveZ * moveZ < RefreshDistance * RefreshDistance) return;

            dirty = false;
            hasLastPosition = true;
            lastPosition = position;

            float dropSq = DetailDropRadius * DetailDropRadius;
            float restoreSq = DetailRestoreRadius * DetailRestoreRadius;

            // Horizontal distance only: a tree is not further away for being downhill.
            foreach (var (networkId, tree) in trees)
            {
                float dx = tree.Transform.Position.X - position.X;
                float dz = tree.Transform.Position.Z - position.Z;
                float distanceSq = dx * dx + dz * dz;

                if (!views.TryGetValue(networkId, out var view))
                {
                    var detail = Detail(distanceSq, TreeViewFactory.LeafDetail.Far);
                    view = TreeViewFactory.Create(game, locators, detail);
                    view.Name = $"NetObject_{networkId}";
                    view.Transform.Position = tree.Transform.Position.ToStride();
                    view.Transform.Rotation = Quaternion.RotationY(tree.Transform.Yaw);
                    view.Scene = scene;
                    views.Add(networkId, view);
                    details.Add(networkId, detail);
                    continue;
                }

                var wanted = Detail(distanceSq, details[networkId]);
                if (wanted == details[networkId]) continue;

                TreeViewFactory.SetDetail(view, game, locators, wanted);
                details[networkId] = wanted;
            }

            // Which side of the band this distance falls on, keeping the current answer while it is
            // between the two — the hysteresis that stops a tree at the threshold from flickering.
            static TreeViewFactory.LeafDetail Detail(float distanceSq, TreeViewFactory.LeafDetail current)
            {
                if (distanceSq <= DetailRestoreRadius * DetailRestoreRadius)
                    return TreeViewFactory.LeafDetail.Near;
                if (distanceSq >= DetailDropRadius * DetailDropRadius)
                    return TreeViewFactory.LeafDetail.Far;
                return current;
            }
        }

        private void RemoveView(uint networkId)
        {
            details.Remove(networkId);
            if (!views.Remove(networkId, out var view)) return;
            view.Scene = null;
        }

        private sealed class DriverScript : SyncScript
        {
            public required TreeViewManager Manager { get; init; }
            public override void Update() => Manager.Update();
        }
    }
}
