using Demiurge;
using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;
using NVector3 = System.Numerics.Vector3;

/// <summary>
/// The gunner's view of a mortar: a camera looking down on the ground he can reach, the sector he is
/// allowed to reach it in, and the circle his bomb will land somewhere inside.
///
/// It runs on the CAMERA entity and takes the view over while the local player is operating, the
/// same way the killcam does — one script owning the camera for the duration of a state rather than
/// a mode flag threaded through the first-person camera.
///
/// Everything drawn here is drawn from the shared rule in MortarBallistics, not from a second copy
/// of the numbers: the sector edges, the range band and the aim marker are all the client asking
/// the same functions the server will answer the fire request with. What the gunner sees offered is
/// therefore exactly what he will be allowed to do.
/// </summary>
public sealed class MortarControlScript : SyncScript
{
    public required PlayerRegistry Registry { get; init; }
    public required ObjectRegistry Objects { get; init; }
    public required ClientInputState InputState { get; init; }

    /// <summary>
    /// The field of view the gunner's camera takes. Set here rather than inherited, because it and
    /// the height below are what turn a mouse position into a place on the ground — see
    /// GroundUnderCursor, which does that arithmetic directly instead of unprojecting.
    /// </summary>
    private const float FieldOfViewDegrees = 60f;

    /// <summary>Ground kept in view behind the tube and beyond the far arc, in metres. The gunner
    /// and his mortar have to be on screen: he is standing in the open working it, and a view that
    /// cropped him out would hide the one thing that can kill him.</summary>
    private const float ViewMargin = 15f;

    /// <summary>
    /// Half the depth the view must cover: from a little behind the emplacement to a little past
    /// the far arc. Derived from the range band rather than picked, so retuning the mortar's reach
    /// reframes the camera instead of quietly cropping it.
    /// </summary>
    private const float HalfDepth = (MortarConfig.MaximumRange + 2f * ViewMargin) * 0.5f;

    /// <summary>How far up the camera has to sit for <see cref="HalfDepth"/> to fit the vertical
    /// field of view.</summary>
    private static readonly float CameraHeight =
        HalfDepth / MathF.Tan(FieldOfViewDegrees * MathF.PI / 360f);

    /// <summary>Shake at the tube. Bigger than a rifle's and smaller than a grenade's: it is a
    /// bomb leaving a tube a metre away, not one landing on you.</summary>
    private const float FiringTrauma = 0.45f;

    private static readonly Color SectorColor = new(255, 30, 25, 250);
    private static readonly Color ScatterColor = new(255, 255, 255, 255);
    private static readonly Color AimColor = new(255, 255, 255, 190);
    private static readonly Color InvalidScatterColor = new(255, 30, 25, 255);
    private static readonly Color InvalidAimColor = new(255, 30, 25, 220);
    private static readonly Color ReloadColor = new(255, 190, 15, 250);

    /// <summary>
    /// Contact markers. Coloured by RELATIONSHIP rather than by team number: from the tube the only
    /// question is what to drop a bomb on, and a gunner reading his own team's colour off a chart
    /// mid-mission is a gunner who shells his own men. Shape carries the same information again, so
    /// it survives being colour-blind and survives a red-on-brown background.
    /// </summary>
    private static readonly Color EnemyMarkerColor = new(255, 30, 25, 255);
    private static readonly Color FriendlyMarkerColor = new(25, 145, 255, 250);

    /// <summary>
    /// How big a marker is on the ground, in metres.
    ///
    /// Derived from the framing rather than picked, because the camera height follows
    /// <see cref="MortarConfig.MaximumRange"/> — at 200 m of reach the gunner is 190 m up, where a
    /// man is about two pixels tall and simply cannot be seen. Two percent of the visible half-depth
    /// is a few metres of ground and reads as a clear symbol; retuning the mortar's range rescales it
    /// instead of quietly shrinking it back to nothing.
    /// </summary>
    private static readonly float MarkerRadius = HalfDepth * 0.02f;

    private const int FriendlyMarkerSegments = 12;

    private bool wasFiring;

    /// <summary>
    /// Seconds until the tube is loaded again, run client-side. The server owns the real deadline
    /// and refuses an early request; this exists to draw the ring and to stop asking, so the two
    /// only disagree if a request is lost — and then this one is the pessimistic of the two.
    /// </summary>
    private float reloadRemaining;

    public override void Update()
    {
        if (InputState.TerminalOpen || Entity.Get<DebugFlyCameraScript>()?.Active == true) return;
        if (Registry.LocalPlayer is not { } local || !local.IsOperating || local.IsDead)
        {
            wasFiring = false;
            return;
        }
        if (FindEmplacement(local) is not { } mortar) return;

        float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
        reloadRemaining = MathF.Max(0f, reloadRemaining - dt);

        var position = mortar.Transform.Position;
        float facing = mortar.Transform.Yaw;

        PoseCamera(position, facing, dt);
        DrawSector(position, facing);
        DrawContacts(local);

        if (GroundUnderCursor(position, facing) is not { } aim) return;
        bool isInFireSector = MortarBallistics.IsTargetInFireSector(position, facing, aim);
        DrawAim(isInFireSector);

        // Edge-triggered: one bomb per click. Loading is automatic and takes the five seconds the
        // ring counts down; a held button must not queue a request against every frame of it.
        bool firing = Input.IsMouseButtonDown(MouseButton.Left);
        if (firing && !wasFiring && isInFireSector && reloadRemaining <= 0f)
        {
            local.TryFireMortar(aim);
            reloadRemaining = MortarConfig.ReloadSeconds;
            CameraTrauma.Add(FiringTrauma);
        }
        wasFiring = firing;
    }

    /// <summary>
    /// The emplacement being worked. Found by the same reach rule the server used to seat him on it,
    /// and he cannot walk while operating, so the nearest one is necessarily the one — see
    /// PickupTargeting, which both ends share.
    /// </summary>
    private NetObject? FindEmplacement(LocalPlayer local)
        => PickupTargeting.Nearest(
            local.Position,
            Objects.Objects,
            obj => new PickupTargeting.Candidate(obj.Has, obj.Item.Type, obj.Transform.Position));

    /// <summary>
    /// Everyone on the ground, as symbols big enough to aim at.
    ///
    /// Drawn in WORLD space rather than projected to the screen. The camera is posed a few lines
    /// above, so its view-projection matrix this frame is still last frame's — anything projected
    /// through it would lag the view by a frame and slide about while the gunner traverses. A line in
    /// world space is transformed at render time, after the pose has landed, and is simply correct.
    ///
    /// Depth-tested so a man behind a ridge is occluded by it. The gunner is looking at ground he
    /// cannot see from where he stands, and a marker that shone through terrain would be telling him
    /// something his eyes could not.
    /// </summary>
    private void DrawContacts(LocalPlayer local)
    {
        foreach (var actor in Registry.Players)
        {
            if (actor.IsDead) continue;

            bool friendly = actor.Team == local.Team;
            var centre = actor.Position.ToStride() + Vector3.UnitY * MarkerHeight;
            if (friendly) DrawFriendlyMarker(centre);
            else DrawEnemyMarker(centre);
        }
    }

    /// <summary>Lifted clear of the ground so the symbol is not swallowed by the surface it stands
    /// on, and by less than a man is tall so it still reads as being AT him.</summary>
    private const float MarkerHeight = 1f;

    /// <summary>A diamond, laid flat on the ground: hostile.</summary>
    private static void DrawEnemyMarker(Vector3 centre)
    {
        var north = centre + Vector3.UnitZ * MarkerRadius;
        var east = centre + Vector3.UnitX * MarkerRadius;
        var south = centre - Vector3.UnitZ * MarkerRadius;
        var west = centre - Vector3.UnitX * MarkerRadius;

        LineRenderer.DrawDepthTestedLine(north, east, EnemyMarkerColor);
        LineRenderer.DrawDepthTestedLine(east, south, EnemyMarkerColor);
        LineRenderer.DrawDepthTestedLine(south, west, EnemyMarkerColor);
        LineRenderer.DrawDepthTestedLine(west, north, EnemyMarkerColor);
    }

    /// <summary>A circle, laid flat on the ground: friendly. Do not drop a bomb on it.</summary>
    private static void DrawFriendlyMarker(Vector3 centre)
    {
        Vector3 previous = default;
        for (int i = 0; i <= FriendlyMarkerSegments; i++)
        {
            float angle = MathF.Tau * i / FriendlyMarkerSegments;
            var point = centre + new Vector3(
                MathF.Cos(angle) * MarkerRadius,
                0f,
                MathF.Sin(angle) * MarkerRadius);
            if (i > 0) LineRenderer.DrawDepthTestedLine(previous, point, FriendlyMarkerColor);
            previous = point;
        }
    }

    /// <summary>
    /// Straight down over the middle of the band, turned so the emplaced heading points UP the
    /// screen. Turning it that way is what makes the sector read as "ahead" rather than as an
    /// arbitrary wedge, and it is what lets the cursor mapping below be a plain affine one.
    /// </summary>
    private void PoseCamera(NVector3 mortar, float facing, float dt)
    {
        var centre = GroundCentre(mortar, facing);

        if (Entity.Get<CameraComponent>() is { } camera)
            camera.VerticalFieldOfView = FieldOfViewDegrees;

        Entity.Transform.Position = (centre + NVector3.UnitY * CameraHeight).ToStride();

        // Pitch the camera's -Z onto -Y, then spin about the vertical so its +Y (up-screen) lands on
        // the heading. Stride multiplies "apply a, then b", so the pitch comes first.
        var pose = Quaternion.RotationX(-MathF.PI / 2f) * Quaternion.RotationY(facing + MathF.PI);
        Entity.Transform.Rotation = CameraTrauma.Update(dt) * pose;
    }

    /// <summary>
    /// The point on the ground the view is centred on: halfway between a little behind the tube and
    /// a little past the far arc, so the emplacement, the gunner standing at it and the whole of the
    /// reachable ground are all on screen at once.
    /// </summary>
    private static NVector3 GroundCentre(NVector3 mortar, float facing)
        => mortar + Heading(facing) * (HalfDepth - ViewMargin);

    private static NVector3 Heading(float facing)
        => new(MathF.Sin(facing), 0f, MathF.Cos(facing));

    /// <summary>
    /// Screen-right, derived from the pose rather than guessed. With the camera's up-screen laid on
    /// the heading and its forward on -Y, right must satisfy right x up = -forward = +Y, which gives
    /// (-cos, 0, sin) — the NEGATIVE of the naive "rotate the heading 90 degrees".
    /// </summary>
    private static NVector3 Across(float facing)
    {
        var heading = Heading(facing);
        return new NVector3(-heading.Z, 0f, heading.X);
    }

    /// <summary>
    /// The place on the ground under the mouse. Deliberately left unclamped so the reticle can show
    /// when the gunner is pointing outside what the tube can actually reach.
    ///
    /// Worked out directly rather than by unprojecting a ray, and that is the fix for a cursor that
    /// used to run the wrong way: the camera is posed in THIS method's frame while the projection
    /// matrix it would have to invert is still last frame's, so the two disagreed about where the
    /// camera was. Looking straight down at a flat plane needs no matrix anyway — the ground is
    /// parallel to the image plane, so metres map to screen linearly and the whole thing is one
    /// multiply per axis.
    /// </summary>
    private NVector3? GroundUnderCursor(NVector3 mortar, float facing)
    {
        var bounds = Game.Window.ClientBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        float halfWidth = HalfDepth * bounds.Width / bounds.Height;

        var mouse = Input.MousePosition;                    // 0..1, origin top-left
        float acrossFraction = mouse.X * 2f - 1f;
        float alongFraction = 1f - mouse.Y * 2f;            // screen Y grows downward; the ground does not

        var ground = GroundCentre(mortar, facing)
            + Across(facing) * (acrossFraction * halfWidth)
            + Heading(facing) * (alongFraction * HalfDepth);

        return ground with { Y = mortar.Y };
    }

    /// <summary>Metres on the ground to pixels across the screen.</summary>
    private float ToPixels(float metres)
    {
        var bounds = Game.Window.ClientBounds;
        float halfWidth = HalfDepth * bounds.Width / bounds.Height;
        return metres / halfWidth * bounds.Width * 0.5f;
    }

    /// <summary>
    /// The bounds of what the gunner may ask for: the two sector edges and the near and far arcs
    /// between them. Drawn as the closed outline of the reachable ground rather than as two rays,
    /// because the minimum range is as much a limit as the traverse is and a mortar that cannot
    /// defend itself should look like one.
    /// </summary>
    private static void DrawSector(NVector3 mortar, float facing)
    {
        float limit = MortarConfig.SectorHalfAngleDegrees * MathF.PI / 180f;
        const int arcSegments = 24;

        var outline = new List<Vector3>(2 * (arcSegments + 1));
        for (int i = 0; i <= arcSegments; i++)
            outline.Add(OnGround(mortar, facing - limit + 2f * limit * i / arcSegments,
                MortarConfig.MaximumRange));
        for (int i = arcSegments; i >= 0; i--)
            outline.Add(OnGround(mortar, facing - limit + 2f * limit * i / arcSegments,
                MortarConfig.MinimumRange));

        LineRenderer.DrawPolyline(outline, SectorColor, closed: true);
    }

    private static Vector3 OnGround(NVector3 mortar, float yaw, float range)
        => (mortar + new NVector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw)) * range).ToStride();

    /// <summary>
    /// The beaten zone, drawn on the cursor. The reticle IS the mouse — it is not a world point that
    /// happens to be near it — so it is placed straight from the pointer in pixels and never goes
    /// through the camera at all. Only the radius is a world measurement, since the circle stands
    /// for five metres of ground.
    /// </summary>
    private void DrawAim(bool isInFireSector)
    {
        var bounds = Game.Window.ClientBounds;
        var mouse = Input.MousePosition;                    // 0..1, origin top-left, +Y DOWN
        var centre = new Vector2(
            (mouse.X - 0.5f) * bounds.Width,
            (0.5f - mouse.Y) * bounds.Height);              // LineRenderer 2D is centred pixels, +Y UP

        float radius = ToPixels(MortarConfig.DispersionMetres);
        LineRenderer.Circle2D(centre, radius,
            isInFireSector ? ScatterColor : InvalidScatterColor);
        LineRenderer.DrawPoint2D(centre,
            isInFireSector ? AimColor : InvalidAimColor, size: 6f);
        DrawReloadRing(centre, radius + 6f);
    }

    /// <summary>
    /// How much of the next bomb is ready, as an arc filling clockwise from the top around the aim
    /// point. Around the cursor rather than in a corner because that is where the gunner is looking:
    /// the question "can I fire yet" is asked while staring at the ground he wants to hit.
    /// </summary>
    private void DrawReloadRing(Vector2 centre, float radius)
    {
        if (reloadRemaining <= 0f) return;

        float loaded = 1f - reloadRemaining / MortarConfig.ReloadSeconds;
        const int segments = 48;
        int drawn = Math.Max(1, (int)(segments * loaded));

        // Starts at the top and fills clockwise. +Y is UP in this space, so the top is +pi/2 and
        // clockwise means the angle DECREASING — the opposite of the usual screen-space sweep.
        var arc = new List<Vector2>(drawn + 1);
        for (int i = 0; i <= drawn; i++)
        {
            float angle = MathF.PI / 2f - 2f * MathF.PI * i / segments;
            arc.Add(centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius);
        }
        LineRenderer.DrawPolyline2D(arc, ReloadColor);
    }

}
