using System.Globalization;
using System.Numerics;

namespace Demiurge.GameClient
{
    /// <summary>
    /// Named points measured off a model in its own root space — where a weapon's grip
    /// sits, where its barrel ends, where a rig's hand is in a given pose — extracted
    /// from the source .gltf at build time by GltfAssetGenerator and read back here from
    /// assets/locators.txt.
    ///
    /// It is a build-time extraction rather than a runtime read of the Model because a
    /// static model HAS no nodes at runtime: a .sdskel is only emitted for sources with
    /// animations or a skin, and without one Stride's importer collapses every node to
    /// index 0 and bakes the transforms into the vertex buffers. Weapons are static, so
    /// their node names are gone by the time the game loads them.
    ///
    /// Transforms are in the same space the baked vertices end up in, so they compose
    /// with a model's own geometry without any further correction.
    /// </summary>
    public sealed class ModelLocators
    {
        public const string DefaultPath = "assets/locators.txt";

        /// <summary>Where a locator sits and which way it faces. Rotation is identity for
        /// a plain Blockbench locator — they carry a position only — and meaningful for a
        /// node on a rig, which inherits the pose of the bones above it.</summary>
        public readonly record struct Pose(Vector3 Translation, Quaternion Rotation);

        // (model content path optionally suffixed @clip, locator name) -> pose. Content
        // paths are keyed the way GLTFLoader keys them ("models/ak47"), so an asset path
        // converts the same way.
        private readonly Dictionary<(string Model, string Name), Pose> poses = new();

        public static ModelLocators Load(string path = DefaultPath)
        {
            var locators = new ModelLocators();

            // Missing manifest is fatal and immediate: every gun's placement and every
            // shot's origin depend on it, and the alternative is weapons silently seated
            // at their model origin with no clue why.
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.AsSpan().Trim();
                if (line.IsEmpty || line[0] == '#') continue;

                var f = line.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length != 9)
                    throw new InvalidDataException(
                        $"{path}: expected '<model>[@<clip>] <name> <x> <y> <z> <qx> <qy> <qz> <qw>', got '{raw}'");

                locators.poses[(f[0], f[1])] = new Pose(
                    new Vector3(Number(f[2]), Number(f[3]), Number(f[4])),
                    new Quaternion(Number(f[5]), Number(f[6]), Number(f[7]), Number(f[8])));
            }

            return locators;

            static float Number(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        }

        /// <summary>The named locator on the model at <paramref name="gltfPath"/>, in its
        /// rest pose or in the pose <paramref name="clip"/> holds it. Null is ordinary,
        /// not an error — body armor has no grip.</summary>
        public Pose? Get(string gltfPath, string name, string? clip = null)
        {
            var key = clip is null ? ContentPathOf(gltfPath) : ContentPathOf(gltfPath) + "@" + clip;
            return poses.TryGetValue((key, name), out var pose) ? pose : null;
        }

        /// <summary>As <see cref="Get"/>, but for the callers that cannot carry on
        /// without it — a missing grip is survivable, a missing rig hand is not.</summary>
        public Pose Require(string gltfPath, string name, string? clip = null) =>
            Get(gltfPath, name, clip)
            ?? throw new InvalidDataException(
                $"{DefaultPath} has no '{name}' locator for {gltfPath}{(clip is null ? "" : " in clip " + clip)}");

        // Same asset-path -> content-path conversion GLTFLoader does, so both agree on
        // what names a model.
        private static string ContentPathOf(string gltfPath) =>
            Path.ChangeExtension(Path.GetRelativePath("assets", gltfPath), null).Replace('\\', '/');
    }
}
