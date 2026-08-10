using Demiurge.GameClient;
using StbImageSharp;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Demiurge;

/// <summary>
/// A flag on a pole, flown at the height of its own capture.
///
/// The height IS the progress bar. FlagSystem already runs the two-phase Conquest clock — an owned
/// flag's progress drains to zero before the attacker's starts climbing from it — so mapping that one
/// number onto the pole gives the whole story without a second representation of it: your flag sinks
/// while they take it, hits the bottom as the position goes neutral, and theirs rises in its place.
/// Nothing here knows what "being captured" is; it reads a number between zero and one and a colour.
///
/// The two ends of the travel come from the model's own <c>flag_bottom</c> and <c>flag_top</c>
/// locators rather than from constants here, so re-cutting the pole in Blockbench moves the flag with
/// it and nobody has to remember this file exists.
/// </summary>
public sealed class FlagViewScript : SyncScript
{
    public required NetObject Object { get; init; }
    public required ModelLocators Locators { get; init; }
    public required PlayerRegistry Players { get; init; }

    /// <summary>The mesh that rides up and down the pole. A node rather than a child entity: the
    /// model is one asset and its parts are its own, which is what the skeleton is for.</summary>
    private const string FlagNode = "flag";
    private const string ModelPath = "assets/models/flag.gltf";

    private int flagNode = -1;
    private float bottomY;
    private float travel;
    private int shownTeam = int.MinValue;

    public override void Start()
    {
        var bottom = Locators.Require(ModelPath, "flag_bottom");
        var top = Locators.Require(ModelPath, "flag_top");
        bottomY = bottom.Translation.Y;
        travel = top.Translation.Y - bottomY;
    }

    public override void Update()
    {
        // Whose flag is on the pole. The owner if there is one, and the team taking it otherwise —
        // which is exactly the two phases: a flag being drained still flies its owner's colours all
        // the way down, and only once it is nobody's does the attacker's go up.
        int flying = Object.Team.Value != FlagConfig.NeutralTeam
            ? Object.Team.Value
            : Object.Team.CapturingTeam;

        RaiseTo(MathUtil.Clamp(Object.Team.Progress, 0f, 1f));
        Recolor(flying);
        DrawCaptureRing(flying);
    }

    private void RaiseTo(float progress)
    {
        var model = Entity.Get<ModelComponent>();
        var skeleton = model?.Skeleton;
        if (skeleton is null) return;

        if (flagNode < 0)
        {
            flagNode = Array.FindIndex(skeleton.Nodes, node => node.Name == FlagNode);
            // A model without the part draws as a bare pole rather than crashing — the same
            // tolerance ItemParts takes, and for the same reason: art and code ship separately.
            if (flagNode < 0) return;
        }

        ref var transform = ref skeleton.NodeTransformations[flagNode].Transform;
        transform.Position.Y = bottomY + travel * progress;
    }

    /// <summary>
    /// Blue for the side you are on, red for the side you are not, white for nobody's.
    ///
    /// VIEWER-RELATIVE, unlike the player coats, which are a fixed orange and grey per team. A coat
    /// answers "who is that" and must mean the same thing to everyone watching; a flag answers "is
    /// this mine", which is a different question with a different answer for each of the two people
    /// looking at it.
    /// </summary>
    private void Recolor(int flying)
    {
        int local = Players.LocalPlayer?.Team ?? 0;
        int shown = flying == FlagConfig.NeutralTeam ? 0 : flying == local ? 1 : 2;
        if (shown == shownTeam) return;

        var model = Entity.Get<ModelComponent>();
        if (model is null) return;
        shownTeam = shown;
        model.Materials[0] = Cloth(GraphicsDevice, shown);
    }

    private void DrawCaptureRing(int flying)
    {
        var origin = Object.Transform.Position.ToStride();
        var color = TeamColor(flying, 180);
        const int segments = 24;
        int filled = (int)MathF.Round(MathUtil.Clamp(Object.Team.Progress, 0f, 1f) * segments);
        var empty = new Color(210, 210, 215, 38);
        var previous = origin + new Vector3(FlagConfig.CaptureRadius, 0.05f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float angle = i * MathUtil.TwoPi / segments;
            var point = origin + new Vector3(
                MathF.Cos(angle) * FlagConfig.CaptureRadius,
                0.05f,
                MathF.Sin(angle) * FlagConfig.CaptureRadius);
            LineRenderer.DrawLine(previous, point, i <= filled ? color : empty);
            previous = point;
        }
    }

    /// <summary>0 neutral, 1 friendly, 2 enemy — the three atlases, in the same order
    /// <see cref="Recolor"/> resolves them to.</summary>
    private static string ClothTexturePath(int shown) => shown switch
    {
        1 => "assets/blockbench/blue_flag_texture.png",
        2 => "assets/blockbench/red_flag_texture.png",
        _ => "assets/blockbench/white_flag_texture.png",
    };

    // Three materials for the whole process, built on demand and never evicted — the same reasoning
    // as PlayerCosmetics.Coat, which this deliberately mirrors: a material is immutable, so every
    // flag on the map that is showing the same thing can share one.
    private static readonly Dictionary<int, Material> cloths = [];

    private static Material Cloth(GraphicsDevice device, int shown)
    {
        if (cloths.TryGetValue(shown, out var cached)) return cached;

        var cloth = Material.New(device, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(
                    new ComputeTextureColor(ClothTexture(device, ClothTexturePath(shown)))
                    {
                        Filtering = TextureFilter.Point,
                    }),
                DiffuseModel = new MaterialDiffuseLambertModelFeature(),
                Transparency = new MaterialTransparencyCutoffFeature
                {
                    Alpha = new ComputeFloat(0.05f),
                },
                CullMode = CullMode.None,
            },
        });
        cloths[shown] = cloth;
        return cloth;
    }

    /// <summary>Texture.Load pulls in Windows-only System.Drawing.Common, so the PNG is decoded with
    /// StbImageSharp and uploaded by hand — the path every other runtime texture here takes.</summary>
    private static Texture ClothTexture(GraphicsDevice device, string path)
    {
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return Texture.New2D(
            device,
            image.Width,
            image.Height,
            PixelFormat.R8G8B8A8_UNorm_SRgb,
            image.Data);
    }

    private static Color TeamColor(int team, byte alpha)
    {
        if (team <= 0) return new Color(220, 220, 220, alpha);
        uint hash = unchecked((uint)team * 2654435761u);
        return new Color(
            (byte)(80 + (hash & 0x7f)),
            (byte)(80 + ((hash >> 8) & 0x7f)),
            (byte)(80 + ((hash >> 16) & 0x7f)),
            alpha);
    }
}
