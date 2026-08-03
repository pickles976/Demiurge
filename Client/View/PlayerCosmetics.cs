using Demiurge;

namespace Demiurge.GameClient;

/// <summary>
/// Which body a player wears, keyed by team — the player-model counterpart to
/// <see cref="ItemCosmetics"/>, and client-only for the same reason: the server and the wire never
/// need to know what anybody looks like.
///
/// The two models are ONE RIG in two coats. Same 18 joints under the same names, same clip names,
/// same 918 vertices between the same bounds — verified against the .gltf files, not assumed. That
/// is what lets everything else here stay team-blind: the aim bone, the hand socket, the head
/// collider and every locator measurement hold for both, so a new team costs a texture and a row in
/// <see cref="Model"/> rather than a second set of numbers to keep in step.
///
/// Break that and the failure is quiet — a weapon seated on a bone that moved, or a head sphere over
/// empty air — so a model whose rig differs needs its own locator-derived numbers rather than a row
/// here.
/// </summary>
public static class PlayerCosmetics
{
    /// <summary>
    /// The rig the measurements are taken from. Both models share it, so
    /// <see cref="WeaponMount.PlayerModel"/> stays a single reference rather than becoming
    /// per-team: asking cat_orange for the hand bone gives cat_gray's hand bone too.
    /// </summary>
    public const string ReferenceModel = WeaponMount.PlayerModel;

    /// <summary>
    /// An unknown team gets team 1's body rather than no body. Teams come off the wire, and a
    /// player nobody can see is worse than a player wearing the wrong coat.
    /// </summary>
    public static string Model(int team) => team switch
    {
        2 => "assets/models/cat_gray.gltf",
        _ => "assets/models/cat_orange.gltf",
    };

    /// <summary>
    /// The five clips every body has, as Stride content paths for the given team's model. Each
    /// model carries its own baked copies — identical animation, but a clip is compiled against the
    /// model it shipped in, so they are not shared across the two.
    /// </summary>
    public static string AnimationPath(int team, string clip)
    {
        string model = Model(team);
        int start = model.LastIndexOf('/') + 1;
        return $"models/{model[start..^".gltf".Length]}_anim_{clip}";
    }

    /// <summary>The clips <see cref="PlayerViewScript"/> selects between.</summary>
    public static readonly string[] Clips = ["Walk", "Idle", "Aiming", "Crouch", "CrouchWalk"];
}
