using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 2)
{
    Console.WriteLine("Usage: GltfAssetGenerator <project-directory> <project-name>");
    return 1;
}

var projectDirectory = args[0];
var projectName = args[1];
var assetsDir = Path.Combine(projectDirectory, "assets");

if (!Directory.Exists(assetsDir))
    return 0;

var sep = Path.DirectorySeparatorChar;
var gltfFiles = Directory.GetFiles(assetsDir, "*.gltf", SearchOption.AllDirectories);
Array.Sort(gltfFiles);
var rootAssetLines = new List<string>();
var locatorLines = new List<string>();

foreach (var gltfPath in gltfFiles)
{
    // Relative content path from assets/, no extension, forward slashes
    var rel = gltfPath.Substring(assetsDir.Length).TrimStart(sep);
    var contentPath = Path.ChangeExtension(rel, null).Replace('\\', '/');
    var dir = Path.GetDirectoryName(gltfPath)!;
    var baseName = Path.GetFileNameWithoutExtension(gltfPath);
    var fileName = Path.GetFileName(gltfPath);
    var modelGuid = ComputeGuid(contentPath);

    var gltf = LoadGltf(gltfPath);

    // 1. Extract images -> .png/.jpg and .sdtex
    var imageContentPaths = new List<string>();
    var imageGuids = new List<string>();
    var imageFileNames = new List<string>();
    for (int i = 0; i < gltf.LogicalImages.Count; i++)
    {
        var image = gltf.LogicalImages[i];
        var bytes = image.Content.Content.ToArray();
        var ext = (bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xD8) ? ".jpg" : ".png";
        var imgFileName = baseName + "_tex" + i + ext;
        var imgPath = Path.Combine(dir, imgFileName);
        File.WriteAllBytes(imgPath, bytes);

        var imgContentPath = contentPath + "_tex" + i;
        imageContentPaths.Add(imgContentPath);
        imageGuids.Add(ComputeGuid(imgContentPath));
        imageFileNames.Add(imgFileName);

        var sdtexPath = Path.Combine(dir, baseName + "_tex" + i + ".sdtex");
        var sdtex =
            $"!Texture\n" +
            $"Id: {imageGuids[i]}\n" +
            $"SerializedVersion: {{Stride: 2.0.0}}\n" +
            $"Tags: []\n" +
            $"Source: !file {imgFileName}\n" +
            $"IsCompressed: false\n" +
            $"Type: !ColorTextureType\n" +
            $"    UseSRgbSampling: true\n" +
            $"    ColorKeyColor: {{R: 255, G: 0, B: 255, A: 255}}\n" +
            $"    Alpha: Interpolated\n" +
            $"    PremultiplyAlpha: false\n" +
            $"GenerateMipmaps: false\n" +
            $"IsStreamable: false\n";
        WriteIfChanged(sdtexPath, sdtex);
    }

    // 2. Generate .sdmat per material
    var matGuids = new List<string>();
    var matNames = new List<string>();
    for (int i = 0; i < gltf.LogicalMaterials.Count; i++)
    {
        var mat = gltf.LogicalMaterials[i];
        var matContentPath = contentPath + "_mat" + i;
        var matGuid = ComputeGuid(matContentPath);
        matGuids.Add(matGuid);

        // Carry the glTF's own alpha and culling intent across. A Stride material with no
        // Transparency feature is fully OPAQUE: it samples alpha and then ignores it, so the RGB
        // sitting in transparent texels gets drawn — and exporters leave that at zero, which is why
        // masked-out regions rendered solid black.
        //
        // MASK -> MaterialTransparencyCutoffFeature: an alpha test that stays in the Opaque render
        // stage, so depth sorting is free. BLEND would be real alpha blending, which moves the mesh
        // to the back-to-front Transparent stage — correct for glass, wrong (and slower, with
        // self-sorting artifacts) for cutouts.
        string transparency = mat.Alpha switch
        {
            SharpGLTF.Schema2.AlphaMode.MASK =>
                $"    Transparency: !MaterialTransparencyCutoffFeature\n" +
                $"        Alpha: !ComputeFloat\n" +
                $"            Value: {mat.AlphaCutoff.ToString(CultureInfo.InvariantCulture)}\n",
            SharpGLTF.Schema2.AlphaMode.BLEND =>
                $"    Transparency: !MaterialTransparencyBlendFeature\n",
            _ => "",
        };

        // doubleSided means don't cull. Cutout geometry is usually thin planes that would otherwise
        // vanish from one side.
        string culling = mat.DoubleSided ? "    CullMode: None\n" : "";

        var channel = mat.FindChannel("BaseColor");
        string diffuse = channel.HasValue && channel.Value.Texture != null
            ? $"    Diffuse: !MaterialDiffuseMapFeature\n" +
              $"        DiffuseMap: !ComputeTextureColor\n" +
              $"            Key: Material.DiffuseMap\n" +
              $"            Texture: {imageGuids[channel.Value.Texture.PrimaryImage.LogicalIndex]}:" +
              $"{imageContentPaths[channel.Value.Texture.PrimaryImage.LogicalIndex]}\n" +
              $"            Filtering: Point\n"
            : $"    Diffuse: !MaterialDiffuseMapFeature\n" +
              $"        DiffuseMap: !ComputeColor\n" +
              $"            Value: R:1 G:1 B:1 A:1\n";

        string matYaml =
            $"!MaterialAsset\nId: {matGuid}\nSerializedVersion: {{Stride: 2.0.0}}\nTags: []\nAttributes:\n" +
            diffuse +
            $"    DiffuseModel: !MaterialDiffuseLambertModelFeature {{}}\n" +
            transparency +
            culling;

        var sdmatPath = Path.Combine(dir, baseName + "_mat" + i + ".sdmat");
        WriteIfChanged(sdmatPath, matYaml);

        var matName = string.IsNullOrEmpty(mat.Name) ? "Material" : mat.Name;
        matNames.Add(matName);
    }

    // A skeleton is required for animation: Stride only emits per-node animation
    // curves when the AnimationAsset references a Skeleton, and skinned meshes need
    // it to deform at runtime. Generate one whenever the source has animations or a skin.
    var needsSkeleton = gltf.LogicalAnimations.Count > 0 || gltf.LogicalSkins.Count > 0;
    var skeletonContentPath = contentPath + "_skeleton";
    var skeletonGuid = ComputeGuid(skeletonContentPath);

    // 3. Write .sdm3d with Materials list (and a Skeleton reference when needed).
    var materialsYaml = "Materials:\n";
    for (int i = 0; i < matGuids.Count; i++)
    {
        materialsYaml += $"    -   Name: {matNames[i]}\n        MaterialInstance:\n            Material: {matGuids[i]}:{contentPath}_mat{i}\n";
    }
    if (matGuids.Count == 0)
        materialsYaml = "Materials: []\n";

    var skeletonRefYaml = needsSkeleton ? $"Skeleton: {skeletonGuid}:{skeletonContentPath}\n" : "";

    var sdm3dPath = Path.ChangeExtension(gltfPath, ".sdm3d");
    var sdm3d = $"!Model\nId: {modelGuid}\nSerializedVersion: {{Stride: 2.0.0}}\nTags: []\nSource: !file {fileName}\n{skeletonRefYaml}{materialsYaml}";
    WriteIfChanged(sdm3dPath, sdm3d);

    rootAssetLines.Add("    - " + modelGuid + ":" + contentPath);

    // 4. Write the .sdskel. An empty Nodes list makes Stride import the full node
    //    hierarchy with every node preserved (and node names de-duplicated internally).
    if (needsSkeleton)
    {
        var sdskel =
            $"!Skeleton\n" +
            $"Id: {skeletonGuid}\n" +
            $"SerializedVersion: {{Stride: 2.0.0}}\n" +
            $"Tags: []\n" +
            $"Source: !file {fileName}\n" +
            $"Nodes: []\n";
        var sdskelPath = Path.Combine(dir, baseName + "_skeleton.sdskel");
        WriteIfChanged(sdskelPath, sdskel);
        rootAssetLines.Add("    - " + skeletonGuid + ":" + skeletonContentPath);
    }

    // 5. Generate .sdanim per animation. Each references the skeleton (so per-node
    //    curves are emitted) and selects its source clip via AnimationStack (the
    //    animation's index in the glTF). The content path is models/<base>_anim_<name>
    //    so runtime code can load it by the source animation's name.
    for (int i = 0; i < gltf.LogicalAnimations.Count; i++)
    {
        var animation = gltf.LogicalAnimations[i];
        var rawName = string.IsNullOrEmpty(animation.Name) ? ("anim" + i) : animation.Name;
        var safeName = SanitizeName(rawName);
        var animContentPath = contentPath + "_anim_" + safeName;
        var animGuid = ComputeGuid(animContentPath);

        var sdanim =
            $"!Animation\n" +
            $"Id: {animGuid}\n" +
            $"SerializedVersion: {{Stride: 2.0.0}}\n" +
            $"Tags: []\n" +
            $"Source: !file {fileName}\n" +
            $"AnimationStack: {i}\n" +
            $"Skeleton: {skeletonGuid}:{skeletonContentPath}\n" +
            $"RepeatMode: LoopInfinite\n" +
            $"Type: !StandardAnimationAssetType {{}}\n";

        var sdanimPath = Path.Combine(dir, baseName + "_anim_" + safeName + ".sdanim");
        WriteIfChanged(sdanimPath, sdanim);

        rootAssetLines.Add("    - " + animGuid + ":" + animContentPath);
    }

    // 6. Record locator transforms into the manifest (see the block comment on
    //    WriteLocatorManifest for why this is a build-time extraction).
    foreach (var (pose, name, translation, rotation) in ExtractLocators(gltf))
    {
        locatorLines.Add(string.Format(CultureInfo.InvariantCulture,
            "{0}{1} {2} {3:R} {4:R} {5:R} {6:R} {7:R} {8:R} {9:R}",
            contentPath, pose is null ? "" : "@" + pose, name,
            translation.X, translation.Y, translation.Z,
            rotation.X, rotation.Y, rotation.Z, rotation.W));
    }
}

// Audio is NOT compiled into Stride content: we play sound through OpenAL directly
// (see SoundManager) and load .wav files straight from assets/ at runtime. The csproj
// copies assets/**/*.wav to the output directory; nothing to generate here.

WriteLocatorManifest(Path.Combine(assetsDir, "locators.txt"), locatorLines);

// 4. Rewrite .sdpkg with current RootAssets (idempotent)
var rootAssets = rootAssetLines.Count > 0
    ? "RootAssets:\n" + string.Join("\n", rootAssetLines) + "\n"
    : "RootAssets: []\n";
var sdpkg =
    "!Package\n" +
    "SerializedVersion: {Assets: 3.1.0.0}\n" +
    "Meta:\n    Name: " + projectName + "\n    Version: 1.0.0.0\n    Authors: []\n    Owners: []\n    Dependencies: null\n" +
    "AssetFolders:\n    -   Path: !dir assets\n" +
    "ResourceFolders: []\nOutputGroupDirectories: {}\nExplicitFolders: []\nBundles: []\nTemplateFolders: []\n" +
    rootAssets;
var pkgPath = Path.Combine(projectDirectory, projectName + ".sdpkg");
WriteIfChanged(pkgPath, sdpkg);

return 0;

// Loads a .gltf, repairing null TRS components on the way in.
//
// ValidationMode.Skip is NOT enough on its own: a null inside a translation/rotation/
// scale array fails while System.Text.Json is still READING the number, long before
// any validation rule gets a say, so the whole build dies on one bad export. (A
// Blockbench 5.1.6 export of sniper_rifle.gltf did exactly that, writing
// "scale":[null,null,null] on its locator nodes.) A null component is the exporter
// failing to write a value, so the identity default for that channel is the honest
// reading of it — and it is repaired loudly, not silently.
//
// Only node TRS is repaired. A null anywhere else is not recoverable from a default
// and should still fail the build.
static SharpGLTF.Schema2.ModelRoot LoadGltf(string gltfPath)
{
    var dir = Path.GetDirectoryName(gltfPath)!;
    var fileName = Path.GetFileName(gltfPath);

    // Serve the gltf itself repaired, and any file it references (external buffers,
    // images) straight off disk, resolved next to it — the same way
    // ReadContext.CreateFromDirectory does.
    var context = SharpGLTF.Schema2.ReadContext.Create(resourceName =>
    {
        var path = Path.Combine(dir, Uri.UnescapeDataString(resourceName));
        var bytes = File.ReadAllBytes(path);
        return new ArraySegment<byte>(
            string.Equals(resourceName, fileName, StringComparison.Ordinal) ? RepairNullTransforms(bytes, fileName) : bytes);
    });

    // Skip strict schema validation: real-world exports (e.g. Sketchfab) often have
    // benign spec violations like a byteStride on animation sampler accessors.
    context.Validation = SharpGLTF.Validation.ValidationMode.Skip;
    return context.ReadSchema2(fileName);
}

static byte[] RepairNullTransforms(byte[] json, string fileName)
{
    // Cheap bail-out: no null token anywhere means nothing to repair, and the vast
    // majority of files never reach the parse below.
    if (Encoding.UTF8.GetString(json).IndexOf("null", StringComparison.Ordinal) < 0)
        return json;

    var root = JsonNode.Parse(json) as JsonObject;
    if (root?["nodes"] is not JsonArray nodes) return json;

    var repaired = 0;
    foreach (var node in nodes.OfType<JsonObject>())
    {
        repaired += RepairChannel(node, "translation", 0f);
        repaired += RepairChannel(node, "scale", 1f);
        // Quaternion identity is (0,0,0,1), so the default depends on the component.
        if (node["rotation"] is JsonArray rotation)
            for (int i = 0; i < rotation.Count; i++)
                if (rotation[i] is null) { rotation[i] = i == 3 ? 1f : 0f; repaired++; }
    }

    if (repaired == 0) return json;

    Console.WriteLine($"GltfAssetGenerator: {fileName} has {repaired} null transform component(s) " +
                      $"— repaired to identity defaults. Re-export it; the values are guesses.");
    return Encoding.UTF8.GetBytes(root.ToJsonString());

    static int RepairChannel(JsonObject node, string channel, float identity)
    {
        if (node[channel] is not JsonArray array) return 0;
        var repaired = 0;
        for (int i = 0; i < array.Count; i++)
            if (array[i] is null) { array[i] = identity; repaired++; }
        return repaired;
    }
}

// Pulls the mesh-less "locator" nodes out of a model as name -> transform in the model's
// own root space, in the rest pose and again at the start of each animation clip.
//
// This has to happen HERE, at build time, rather than being read off the runtime Model.
// A .sdskel is only emitted when the source has animations or a skin (see needsSkeleton
// above), and with no skeleton reference Stride's ImportModelCommand takes its
// SkeletonUrl == null && MergeMeshes branch: every node collapses to index 0 and each
// mesh node's transform is baked into its vertex buffer. Node names simply do not exist
// at runtime for a static model like a weapon, so ModelNodeLinkComponent would silently
// fall back to the root.
//
// That same baking is why the offsets are directly usable: vertices end up expressed in
// root space, which is the space these transforms are measured in.
//
// MESH-LESS is the load-bearing filter, not a tidiness one — sniper_rifle.gltf has a
// MESH node named `barrel` as well as a locator named `barrel`, and only the second is
// a locator. Blockbench also writes each locator as a PAIR of same-named nodes, a parent
// holding the position and a child holding an internal unit scale; both resolve to the
// same world transform, so the shallower one wins and the duplicate is dropped.
//
// ROTATION is carried as well as position because a locator on a RIG inherits the pose of
// the bones above it — a hand is somewhere different, and pointing somewhere different,
// in every clip. Clip poses are sampled at t=0 and are only meaningful for a bone the
// clip holds still; it is the consumer's job to know which of its clips those are.
static IEnumerable<(string? Pose, string Name, System.Numerics.Vector3 Translation, System.Numerics.Quaternion Rotation)>
    ExtractLocators(SharpGLTF.Schema2.ModelRoot gltf)
{
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var locators = gltf.LogicalNodes
        .Where(n => n.Mesh == null && !string.IsNullOrEmpty(n.Name))
        .Select(n => (Node: n, Depth: Depth(n)))
        .OrderBy(x => x.Depth)
        .ThenBy(x => x.Node.LogicalIndex)
        .Where(x => seen.Add(x.Node.Name))
        .Select(x => x.Node)
        .ToList();

    foreach (var node in locators)
        yield return (null, node.Name, node.WorldMatrix.Translation, RotationOf(node.WorldMatrix));

    foreach (var animation in gltf.LogicalAnimations)
        foreach (var node in locators)
        {
            var world = node.GetWorldMatrix(animation, 0f);
            yield return (SanitizeName(animation.Name), node.Name, world.Translation, RotationOf(world));
        }

    static int Depth(SharpGLTF.Schema2.Node node)
    {
        var depth = 0;
        for (var parent = node.VisualParent; parent != null; parent = parent.VisualParent) depth++;
        return depth;
    }

    // Decompose can fail on a degenerate matrix; an identity rotation beats a NaN, and
    // the translation beside it is still good.
    static System.Numerics.Quaternion RotationOf(System.Numerics.Matrix4x4 world) =>
        System.Numerics.Matrix4x4.Decompose(world, out _, out var rotation, out _)
            ? rotation
            : System.Numerics.Quaternion.Identity;
}

// One line per locator per pose:
//   "<model content path>[@<clip>] <locator name> <x> <y> <z> <qx> <qy> <qz> <qw>"
// with no @clip meaning the rest pose.
//
// A flat text file rather than generated C# on purpose. The generator runs
// BeforeTargets="StrideCompileAsset", but MSBuild expands the **/*.cs glob during
// evaluation — before any target runs — so a freshly generated source file would not
// be compiled until the NEXT build, and a changed model would silently ship stale
// numbers for one build. Data read at runtime has no such lag.
static void WriteLocatorManifest(string path, List<string> lines)
{
    lines.Sort(StringComparer.Ordinal);
    WriteIfChanged(path,
        "# Generated by GltfAssetGenerator. Mesh-less nodes from each .gltf, as\n" +
        "#   <model content path>[@<clip>] <name> <x> <y> <z> <qx> <qy> <qz> <qw>\n" +
        "# in the model's root space. No @clip is the rest pose; a clip pose is sampled\n" +
        "# at t=0 and only means anything for a bone that clip holds still.\n" +
        string.Concat(lines.Select(l => l + "\n")));
}

static string ComputeGuid(string contentPath)
{
    byte[] hashBytes;
    using (var md5 = MD5.Create())
        hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(contentPath));
    var hex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" +
           hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20);
}

static void WriteIfChanged(string path, string content)
{
    if (!File.Exists(path) || File.ReadAllText(path) != content)
        File.WriteAllText(path, content);
}

static string SanitizeName(string name)
{
    var sb = new StringBuilder(name.Length);
    foreach (var c in name)
        sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
    return sb.ToString();
}
