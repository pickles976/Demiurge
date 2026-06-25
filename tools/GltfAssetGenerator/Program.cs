using System.Security.Cryptography;
using System.Text;

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

foreach (var gltfPath in gltfFiles)
{
    // Relative content path from assets/, no extension, forward slashes
    var rel = gltfPath.Substring(assetsDir.Length).TrimStart(sep);
    var contentPath = Path.ChangeExtension(rel, null).Replace('\\', '/');
    var dir = Path.GetDirectoryName(gltfPath)!;
    var baseName = Path.GetFileNameWithoutExtension(gltfPath);
    var fileName = Path.GetFileName(gltfPath);
    var modelGuid = ComputeGuid(contentPath);

    // Skip strict schema validation: real-world exports (e.g. Sketchfab) often have
    // benign spec violations like a byteStride on animation sampler accessors.
    var gltf = SharpGLTF.Schema2.ModelRoot.Load(gltfPath,
        new SharpGLTF.Schema2.ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip });

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

        var channel = mat.FindChannel("BaseColor");
        string matYaml;
        if (channel.HasValue && channel.Value.Texture != null)
        {
            var imgIdx = channel.Value.Texture.PrimaryImage.LogicalIndex;
            var texGuid = imageGuids[imgIdx];
            var texContentPath = imageContentPaths[imgIdx];
            matYaml = $"!MaterialAsset\nId: {matGuid}\nSerializedVersion: {{Stride: 2.0.0}}\nTags: []\nAttributes:\n    Diffuse: !MaterialDiffuseMapFeature\n        DiffuseMap: !ComputeTextureColor\n            Key: Material.DiffuseMap\n            Texture: {texGuid}:{texContentPath}\n            Filtering: Point\n    DiffuseModel: !MaterialDiffuseLambertModelFeature {{}}\n";
        }
        else
        {
            matYaml = $"!MaterialAsset\nId: {matGuid}\nSerializedVersion: {{Stride: 2.0.0}}\nTags: []\nAttributes:\n    Diffuse: !MaterialDiffuseMapFeature\n        DiffuseMap: !ComputeColor\n            Value: R:1 G:1 B:1 A:1\n    DiffuseModel: !MaterialDiffuseLambertModelFeature {{}}\n";
        }

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
}

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
