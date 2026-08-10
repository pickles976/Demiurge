using Demiurge;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;
using StbImageSharp;

namespace Demiurge.GameClient;

/// <summary>
/// What a player looks like — the player-model counterpart to <see cref="ItemCosmetics"/>, and
/// client-only for the same reason: the server and the wire never need to know what anybody looks
/// like.
///
/// Every team wears ONE RIG in a different coat, and that is now structural rather than maintained:
/// there is a single <see cref="Model"/>, and team only picks the texture hung on it through
/// <see cref="Coat"/>. It used to be two .gltf exports that had to stay identical joint for joint,
/// which meant every rig edit had to be made twice and a slip failed quietly — a weapon seated on a
/// bone that moved, or a head sphere over empty air. One model cannot drift from itself.
///
/// So a new team costs a PNG and a row in <see cref="CoatTexture"/>. A body that genuinely needed a
/// different rig would need its own locator-derived numbers too, and is the case this deliberately
/// does not stretch to cover.
/// </summary>
public static class PlayerCosmetics
{
    /// <summary>The rig every measurement is taken from — see <see cref="WeaponMount.PlayerModel"/>,
    /// which is the same asset because there is only one.</summary>
    public const string ReferenceModel = WeaponMount.PlayerModel;

    /// <summary>The body everyone wears, whatever their team.</summary>
    public const string Model = WeaponMount.PlayerModel;

    /// <summary>
    /// The clips every body has, as Stride content paths. One model means one baked set, where
    /// each team's export used to carry its own identical-but-not-shared copies.
    /// </summary>
    public static string AnimationPath(string clip)
    {
        int start = Model.LastIndexOf('/') + 1;
        return $"models/{Model[start..^".gltf".Length]}_anim_{clip}";
    }

    /// <summary>
    /// The coat a team wears. Authored in Blockbench beside the .bbmodel the rig is exported from,
    /// and read from there rather than copied next to the model, so there is one file to repaint.
    ///
    /// An unknown team gets team 1's coat rather than no body. Teams come off the wire, and a player
    /// nobody can see is worse than a player wearing the wrong colour.
    /// </summary>
    public static string CoatTexture(int team) => team switch
    {
        2 => "assets/blockbench/gray_cat_texture.png",
        _ => "assets/blockbench/orange_cat_texture.png",
    };

    /// <summary>
    /// The material for a team's body, as a per-slot override for the one material slot the model
    /// has. Built once per team and shared by every player and corpse on that side — a material is
    /// immutable here, so there is no reason for each entity to own one.
    ///
    /// The attributes mirror what the asset pipeline bakes for the model's own material: point
    /// filtering (the texture is a small hand-painted atlas and must not blur), Lambert diffuse,
    /// alpha cutoff, and no backface culling for the flat pieces.
    /// </summary>
    public static Material Coat(Game game, int team)
    {
        if (coats.TryGetValue(team, out var cached)) return cached;

        var coat = Material.New(game.GraphicsDevice, new MaterialDescriptor
        {
            Attributes =
            {
                Diffuse = new MaterialDiffuseMapFeature(
                    new ComputeTextureColor(CoatTexture(game, team))
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
        coats[team] = coat;
        return coat;
    }

    // Keyed by team and never evicted: two textures and two materials, on a device that outlives
    // every session, against re-decoding the PNGs on each map load. Same shape as
    // TreeViewFactory's leaf material.
    private static readonly Dictionary<int, Material> coats = [];

    /// <summary>
    /// Texture.Load pulls in Windows-only System.Drawing.Common, so the PNG is decoded with
    /// StbImageSharp and uploaded by hand — the same path the HUD and the terrain materials take.
    /// </summary>
    private static Texture CoatTexture(Game game, int team)
    {
        string path = CoatTexture(team);
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return Texture.New2D(
            game.GraphicsDevice,
            image.Width,
            image.Height,
            PixelFormat.R8G8B8A8_UNorm_SRgb,
            image.Data);
    }

    /// <summary>The clips <see cref="PlayerViewScript"/> selects between.</summary>
    public static readonly string[] Clips =
        ["Walk", "Idle", "Aiming", "Crouch", "CrouchWalk", "Prone", "ProneCrawl"];

    // ---- Headgear -------------------------------------------------------------------------------
    //
    // Every actor wears one. It is COSMETIC, not an item: no ItemType, no wire traffic, no pickup —
    // the same reasoning that keeps bodies and animation clips out of the protocol. If a helmet ever
    // has to be shot off, taken, or counted as armor, it stops being this and becomes an equippable
    // through the ItemConfig/EquipSlot.Head path, which already has a socket row waiting for it.
    //
    // It rides the `head` bone through a ModelNodeLinkComponent, so it inherits the aim lean and the
    // walk cycle for free — the bone is a child of the chain PlayerViewScript already pitches.

    public const string HelmetModel = "assets/models/helmet.gltf";

    /// <summary>The bone it hangs off. Present on both bodies; the rig note above is why one name
    /// serves every team.</summary>
    public const string HelmetBone = "head";

    /// <summary>
    /// Model units to the rig's. ONE, and it should stay one: the helmet is modelled to fit this
    /// head, so any scaling here is the code disagreeing with the art. It is written down anyway
    /// because every cosmetic answers this question — <see cref="ItemCosmetics.WorldScale"/> is the
    /// same entry for held items — and a missing row reads as an oversight rather than as a
    /// deliberate 1:1.
    /// </summary>
    public const float HelmetScale = 1f;

    /// <summary>
    /// Where it sits in BONE space, which for `head` is the rig's own frame: +Y up, +Z the way the
    /// character faces.
    ///
    /// Zero, for the same reason the scale is one: the model is authored to sit on this head, so the
    /// starting point is what the artist drew rather than a correction to it. This and
    /// <see cref="HelmetRotation"/> are the tuning surface if the rig's bone origin turns out to sit
    /// somewhere the model does not expect.
    /// </summary>
    public static readonly System.Numerics.Vector3 HelmetSeat = System.Numerics.Vector3.Zero;

    /// <summary>Turn applied in the helmet's own frame, for a model whose forward is not the rig's.
    /// Identity until the first look says otherwise.</summary>
    public static readonly System.Numerics.Quaternion HelmetRotation = System.Numerics.Quaternion.Identity;
}
