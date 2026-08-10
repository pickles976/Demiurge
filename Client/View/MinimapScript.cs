using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;
using Stride.Rendering.Sprites;

namespace Demiurge;

/// <summary>
/// The bottom-left corner map: where you are, where your side is, and where the enemy is BELIEVED
/// to be.
///
/// Two renderers, on purpose. The dial, the markers and the heading chevron are shapes whose
/// geometry changes every frame, so they go through <see cref="LineRenderer"/>'s immediate 2D API
/// like the reticle and the mortar's sector do. The flag icons are TEXTURES, which lines cannot
/// draw, so they are pooled <see cref="ImageElement"/>s this script moves.
///
/// The two coordinate systems are made to coincide rather than converted between: a UIComponent
/// defaults to a 1280x720 virtual resolution while LineRenderer works in real back-buffer pixels, so
/// this one's resolution is pinned to the back buffer every frame. One space, one set of numbers,
/// and no scale factor to get wrong when the window changes.
///
/// The actor symbols are lifted from the mortar's fire-mission view — a circle is a man of yours, a
/// diamond is one of theirs. A player who learned them looking down a tube should not have to learn
/// them again looking at a map.
///
/// It is HEADING UP, not north up. The map answers "what is in front of me", and a north-up map
/// makes the player do the rotation in his head while somebody is shooting at him.
/// </summary>
public sealed class MinimapScript : SyncScript
{
    public required PlayerRegistry Registry { get; init; }
    public required ObjectRegistry Objects { get; init; }
    public required TeamIntel Intel { get; init; }
    public required TerrainState Terrain { get; init; }
    public required ClientInputState InputState { get; init; }

    /// <summary>The camera, for the fly-camera check. Held rather than reached for through
    /// Entity.Get because this script lives on the HUD now, not on the camera.</summary>
    public required Entity CameraEntity { get; init; }

    public required UIComponent Ui { get; init; }
    public required Canvas IconCanvas { get; init; }

    /// <summary>0 neutral, 1 friendly, 2 enemy — the three icons, in the order
    /// <see cref="Colour"/> resolves a team to.</summary>
    public required ISpriteProvider[] FlagIcons { get; init; }

    /// <summary>
    /// Radius of the dial in pixels, and the metres of world it covers.
    ///
    /// 200 m covers the fighting without covering the map: far enough to show the flag you are
    /// heading for and the squad on your flank, short enough that a marker still means somewhere you
    /// could be in under a minute. It makes this a STRATEGIC map rather than a proximity one, which
    /// is why the markers are not scaled with it — a contact is a symbol you have to be able to see,
    /// not a thing with a size, and at 2.2 m per pixel a to-scale man would be invisible.
    /// </summary>
    private const float ScreenRadius = 92f;
    private const float WorldRadius = 200f;

    /// <summary>Inset from the bottom-left corner of the screen.</summary>
    private const float ScreenMargin = 20f;

    /// <summary>Total pixels the dial claims out of the corner, dial plus inset. HUD.CreateUI lifts
    /// the ammo and health readout clear of it by this much.</summary>
    public const float CornerFootprint = ScreenRadius * 2f + ScreenMargin;

    private const float MarkerRadius = 5f;
    private const float FlagIconSize = 16f;
    private const int CircleSegments = 12;

    /// <summary>Icons in the pool. A conquest map has five flags; ten leaves room for a bigger one
    /// without the pool ever growing mid-frame.</summary>
    private const int MaxFlagIcons = 10;

    private static readonly Color BorderColor = new(225, 230, 238, 90);
    private static readonly Color SelfColor = new(255, 255, 255, 235);
    private static readonly Color FriendlyColor = new(120, 190, 255, 220);
    private static readonly Color EnemyColor = new(255, 105, 95, 235);

    private readonly List<ImageElement> icons = [];
    private readonly HashSet<ushort> visibleEnemyIds = [];
    private MinimapTerrain? ground;
    private ImageElement groundImage = null!;

    public override void Start()
    {
        // The ground goes in FIRST, so every marker draws over it. A Canvas paints its children in
        // the order they were added.
        ground = new MinimapTerrain(Game, Entity.Scene, WorldRadius);
        groundImage = new ImageElement
        {
            Source = new SpriteFromTexture { Texture = ground.Texture },
            Width = ScreenRadius * 2f,
            Height = ScreenRadius * 2f,
            Opacity = 0.70f,
            Visibility = Visibility.Hidden,
        };
        groundImage.DependencyProperties.Set(Canvas.PinOriginPropertyKey, new Vector3(0.5f, 0.5f, 0f));
        groundImage.DependencyProperties.Set(Canvas.UseAbsolutePositionPropertyKey, true);
        IconCanvas.Children.Add(groundImage);

        for (int i = 0; i < MaxFlagIcons; i++)
        {
            var icon = new ImageElement
            {
                Width = FlagIconSize,
                Height = FlagIconSize,
                Visibility = Visibility.Hidden,
            };
            // Pinned by its CENTRE, so the position handed to it is the point on the map rather
            // than the icon's top-left corner.
            icon.DependencyProperties.Set(Canvas.PinOriginPropertyKey, new Vector3(0.5f, 0.5f, 0f));
            icon.DependencyProperties.Set(Canvas.UseAbsolutePositionPropertyKey, true);
            icons.Add(icon);
            IconCanvas.Children.Add(icon);
        }
    }

    public override void Update()
    {
        // The UI's own space, made equal to the back buffer so the icons land where the lines do.
        var bounds = Game.Window.ClientBounds;
        Ui.Resolution = new Vector3(bounds.Width, bounds.Height, 1000f);

        int drawn = 0;
        bool visible = IsFirstPerson(out var local);
        if (visible)
        {
            ground?.Update(local.Position, local.Yaw, visible: true);
            drawn = Draw(local, bounds);
        }
        else
        {
            ground?.Update(default, 0f, visible: false);
        }

        groundImage.Visibility = visible ? Visibility.Visible : Visibility.Hidden;

        // Everything the pool did not use this frame. Hidden rather than removed: a flag that goes
        // out of range and comes back should not cost an allocation.
        for (int i = drawn; i < icons.Count; i++) icons[i].Visibility = Visibility.Hidden;
    }

    /// <summary>
    /// The same list the reticle keeps: the fly camera owns the view when it is on, a dead man is
    /// watching a killcam somewhere else, and a gunner on a mortar is already looking at a map of
    /// his own.
    /// </summary>
    private bool IsFirstPerson(out LocalPlayer local)
    {
        local = null!;
        if (InputState.TerminalOpen || CameraEntity.Get<DebugFlyCameraScript>()?.Active == true)
            return false;
        if (Registry.LocalPlayer is not { IsDead: false, IsOperating: false } player) return false;

        local = player;
        return true;
    }

    private int Draw(LocalPlayer local, Rectangle bounds)
    {
        var centre = new Vector2(
            -bounds.Width * 0.5f + ScreenRadius + ScreenMargin,
            -bounds.Height * 0.5f + ScreenRadius + ScreenMargin);

        // Ground first: it is the backdrop the rest is read against.
        groundImage.DependencyProperties.Set(
            Canvas.AbsolutePositionPropertyKey,
            ToCanvas(centre, bounds));

        LineRenderer.Circle2D(centre, ScreenRadius, BorderColor, segments: 48);

        // Facing up. Yaw here is atan2(x, z) — the convention the whole codebase uses — so the
        // player's forward is (sin yaw, cos yaw) in world X/Z.
        //
        // His RIGHT is forward x up, and getting that cross product backwards is what mirrored the
        // map: with +Y up in a right-handed basis, facing +Z puts your right hand at -X, not +X. So
        // right is (-cos yaw, sin yaw) and the screen-X term below carries the negation. A mirror
        // survives every check a rotation error fails — bearings stay the right distance apart and
        // straight ahead stays straight ahead — which is why it reads as looking at the dial from
        // behind the screen rather than as anything being crooked.
        float sin = MathF.Sin(local.Yaw);
        float cos = MathF.Cos(local.Yaw);

        int used = 0;
        foreach (var obj in Objects.Objects)
        {
            if (obj.Type != ObjectType.Flag || used >= icons.Count) continue;
            if (!TryPlot(obj.Transform.Position, local.Position, sin, cos, centre, out var point))
                continue;

            var icon = icons[used++];
            icon.Source = FlagIcons[
                Colour(FlagConfig.FlyingTeam(obj.Team.Value, obj.Team.CapturingTeam), local.Team)];
            icon.Visibility = Visibility.Visible;
            icon.DependencyProperties.Set(
                Canvas.AbsolutePositionPropertyKey,
                ToCanvas(point, bounds));
        }

        foreach (var actor in Registry.Players)
        {
            if (actor.IsDead || actor.Id == local.Id || actor.Team != local.Team) continue;
            if (TryPlot(actor.Position, local.Position, sin, cos, centre, out var point))
                LineRenderer.Circle2D(point, MarkerRadius, FriendlyColor, CircleSegments);
        }

        // Live sight is local knowledge and needs no server round trip. Requiring both the body to
        // be in the actual camera frustum and a clear terrain ray prevents the replicated actor list
        // from becoming a wallhack. Any exposed aim point is enough, matching gameplay perception.
        visibleEnemyIds.Clear();
        var viewCamera = CameraEntity.Get<CameraComponent>();
        if (viewCamera is not null)
        {
            foreach (var actor in Registry.Players)
            {
                if (actor.IsDead || actor.Team == local.Team || actor.Team <= 0) continue;
                if (!TryPlot(actor.Position, local.Position, sin, cos, centre, out var point))
                    continue;
                if (!CanSee(viewCamera, actor)) continue;

                visibleEnemyIds.Add(actor.Id);
                DrawDiamond(point, EnemyColor);
            }
        }

        // Believed, not seen. These come from what the team's NPCs can see and what anybody heard —
        // see TeamIntelSystem — which is why they are not the actors above filtered by team.
        foreach (var contact in Intel.Contacts)
        {
            if (visibleEnemyIds.Contains(contact.ActorId)) continue;
            var colour = EnemyColor;
            colour.A = (byte)(EnemyColor.A * contact.Confidence / 255);
            if (TryPlot(contact.Position, local.Position, sin, cos, centre, out var point))
                DrawDiamond(point, colour);
        }

        DrawSelf(centre);
        return used;
    }

    public override void Cancel()
    {
        ground?.Dispose();
        ground = null;
    }

    private bool CanSee(CameraComponent camera, Player target)
    {
        var origin = CameraEntity.Transform.Position.ToNumerics();
        foreach (float height in GunConfig.AimHeightsFor(target.State))
        {
            var point = target.Position + System.Numerics.Vector3.UnitY * height;
            if (!InsideCamera(camera, point)) continue;

            var segment = point - origin;
            float distance = segment.Length();
            if (distance <= 0.05f) return true;

            var direction = segment / distance;
            const float originClearance = 0.05f;
            var obstruction = TerrainRaycast.Cast(
                Terrain.Map,
                origin + direction * originClearance,
                direction,
                distance - originClearance);
            if (obstruction is null || obstruction.Value.Distance >= distance - 0.1f)
                return true;
        }

        return false;
    }

    private static bool InsideCamera(CameraComponent camera, System.Numerics.Vector3 point)
    {
        var clip = Vector4.Transform(new Vector4(point.ToStride(), 1f), camera.ViewProjectionMatrix);
        if (clip.W <= 1e-4f) return false;

        return clip.X >= -clip.W && clip.X <= clip.W
            && clip.Y >= -clip.W && clip.Y <= clip.W
            && clip.Z >= 0f && clip.Z <= clip.W;
    }

    /// <summary>Neutral, friendly, enemy — viewer-relative, exactly as the flag on its pole is
    /// coloured. See FlagViewScript for why a flag is relative where a player's coat is not.</summary>
    private static int Colour(int flyingTeam, int localTeam)
        => flyingTeam == FlagConfig.NeutralTeam ? 0 : flyingTeam == localTeam ? 1 : 2;

    /// <summary>LineRenderer's 2D space (pixels from the screen centre, +Y UP) to the canvas's
    /// (pixels from the top-left, +Y down). The one place the two disagree.</summary>
    private static Vector3 ToCanvas(Vector2 point, Rectangle bounds)
        => new(point.X + bounds.Width * 0.5f, bounds.Height * 0.5f - point.Y, 0f);

    /// <summary>
    /// World position to a point on the dial, or false when it is off the map.
    ///
    /// Clipped rather than clamped to the rim. A marker pinned to the edge claims a bearing it does
    /// not have — everything beyond the radius would pile onto the circle and read as a crowd in
    /// that direction — and "I cannot see that far" is the honest thing for a map to say.
    /// </summary>
    private static bool TryPlot(
        System.Numerics.Vector3 target,
        System.Numerics.Vector3 origin,
        float sin,
        float cos,
        Vector2 centre,
        out Vector2 point)
    {
        float dx = target.X - origin.X;
        float dz = target.Z - origin.Z;
        if (dx * dx + dz * dz > WorldRadius * WorldRadius)
        {
            point = default;
            return false;
        }

        float scale = ScreenRadius / WorldRadius;
        // Screen X is the offset along the player's right, screen Y along his forward. See the
        // handedness note above for where that leading minus comes from.
        point = centre + new Vector2(
            -(dx * cos - dz * sin) * scale,
            (dx * sin + dz * cos) * scale);
        return true;
    }

    /// <summary>A believed enemy: the mortar view's diamond.</summary>
    private static void DrawDiamond(Vector2 point, Color colour)
    {
        var up = new Vector2(0f, MarkerRadius);
        var right = new Vector2(MarkerRadius, 0f);
        LineRenderer.DrawPolyline2D(
            [point + up, point + right, point - up, point - right],
            colour,
            closed: true);
    }

    /// <summary>You, at the middle, pointing up — a chevron rather than a dot, so the map reads as
    /// oriented even when nothing else is on it.</summary>
    private static void DrawSelf(Vector2 centre)
    {
        const float size = 6f;
        LineRenderer.DrawPolyline2D(
            [
                centre + new Vector2(0f, size),
                centre + new Vector2(size * 0.7f, -size * 0.7f),
                centre,
                centre + new Vector2(-size * 0.7f, -size * 0.7f),
            ],
            SelfColor,
            closed: true);
    }
}
