using System.IO;
using Demiurge.GameClient;
using StbImageSharp;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Events;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Sprites;

using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace Demiurge
{
    public class HUD
    {
        public static Entity CreateUI(Game game)
        {
            var font = game.Content.Load<SpriteFont>("StrideDefaultFont");

            // Bullet icon. Texture.Load uses System.Drawing (Windows-only), so decode the
            // PNG with StbImageSharp and upload it manually — same pattern as Program.cs.
            var bulletTexture = LoadTexture(game, "assets/images/bullet.png");

            var bulletImage = new ImageElement
            {
                Source = new SpriteFromTexture { Texture = bulletTexture },
                Width = 24,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // The text the HudScript updates. Starts as a placeholder until the first event.
            var ammoText = new TextBlock
            {
                Text = "-/-",
                TextColor = Color.White,
                Font = font,
                TextSize = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };

            // Icon + text laid out side by side.
            var ammoPanel = new StackPanel { Orientation = Orientation.Horizontal };
            ammoPanel.Children.Add(bulletImage);
            ammoPanel.Children.Add(ammoText);

            var canvas = new Canvas
            {
                Width = 100,
                Height = 100,
                BackgroundColor = new Color(0, 0, 0, 100),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            canvas.Children.Add(ammoPanel);

            // Put the driving script on the same entity as the UI and hand it the text
            // block to write into.
            var uiEntity = new Entity
            {
                new UIComponent
                {
                    Page = new UIPage { RootElement = canvas },
                    RenderGroup = RenderGroup.Group31 // rendered by AddCleanUIStage()
                },
                new HudScript { 
                    canvas = canvas,
                    AmmoText = ammoText },
            };

            return uiEntity;
        }

        /// <summary>
        /// Entity/FPS readout, top-left. Replaces game.AddProfiler() and
        /// DebugTextSystem.Print, whose FastTextRenderer crashes on Vulkan (see
        /// Program.cs); this renders through the UI system instead, which is fine.
        /// </summary>
        public static Entity CreateDebugStats(Game game)
        {
            var font = game.Content.Load<SpriteFont>("StrideDefaultFont");

            var statsText = new TextBlock
            {
                Text = "",
                TextColor = Color.White,
                Font = font,
                TextSize = 18,
                Margin = new Thickness(8, 4, 8, 0),
            };

            // Last message from the server (see NetworkManager.HandleWelcome).
            var serverText = new TextBlock
            {
                Text = "Server: —",
                TextColor = Color.White,
                Font = font,
                TextSize = 18,
                Margin = new Thickness(8, 2, 8, 4),
            };

            var statsPanel = new StackPanel { Orientation = Orientation.Vertical };
            statsPanel.Children.Add(statsText);
            statsPanel.Children.Add(serverText);

            var canvas = new Canvas
            {
                BackgroundColor = new Color(0, 0, 0, 100),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            canvas.Children.Add(statsPanel);

            return new Entity("DebugStats")
            {
                new UIComponent
                {
                    Page = new UIPage { RootElement = canvas },
                    RenderGroup = RenderGroup.Group31 // rendered by AddCleanUIStage()
                },
                new DebugStatsScript { StatsText = statsText, ServerText = serverText },
            };
        }

        private static Texture LoadTexture(Game game, string path)
        {
            ImageResult img;
            using (var stream = File.OpenRead(path))
                img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            return Texture.New2D(game.GraphicsDevice, img.Width, img.Height,
                PixelFormat.R8G8B8A8_UNorm_SRgb, img.Data);
        }

        /// <summary>
        /// Rebuilds the stats string only when a value changes, so steady-state
        /// frames allocate nothing.
        /// </summary>
        public class DebugStatsScript : SyncScript
        {
            public TextBlock StatsText { get; set; } = null!;
            public TextBlock ServerText { get; set; } = null!;

            private NetworkManager _network = null!;

            private int _lastEntityCount = -1;
            private int _lastFps = -1;
            private ushort _lastClientId;

            public override void Start()
            {
                _network = Services.GetSafeServiceAs<NetworkManager>();
                // Initial paint, in case the welcome arrived before this script started.
                RefreshServerText();
            }

            public override void Update()
            {
                if (_network.ClientId != _lastClientId)
                    RefreshServerText();

                int entityCount = Entity.Scene.Entities.Count;
                int fps = (int)Game.DrawTime.FramePerSecond;

                if (entityCount == _lastEntityCount && fps == _lastFps)
                    return;

                _lastEntityCount = entityCount;
                _lastFps = fps;
                StatsText.Text = $"Entities: {entityCount}   FPS: {fps}";
            }

            private void RefreshServerText()
            {
                _lastClientId = _network.ClientId;
                ServerText.Text = $"Client ID: {_lastClientId}";
            }
        }

        /// <summary>
        /// Ammo readout for the local player. Reads the sim directly (the same
        /// netcode-writes-view-reads contract as the other view scripts) and repaints
        /// only when a value changes, so steady-state frames allocate nothing.
        /// Hidden until the local player spawns — every player carries a gun now,
        /// so "weapon equipped" is simply "spawned".
        /// </summary>
        public class HudScript : SyncScript
        {
            public TextBlock AmmoText { get; set; } = null!;
            public Canvas canvas {get; set; } = null!;

            private PlayerRegistry _registry = null!;

            private int _lastAmmo = -1;
            private bool _lastReloading;
            private bool _lastVisible;

            public override void Start()
            {
                _registry = Services.GetSafeServiceAs<PlayerRegistry>();
                canvas.Visibility = Visibility.Collapsed;   // until spawn
            }

            public override void Update()
            {
                var local = _registry.LocalPlayer;

                bool visible = local is { IsArmed: true };
                if (visible != _lastVisible)
                {
                    _lastVisible = visible;
                    canvas.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }
                if (local is not { IsArmed: true }) return;

                if (local.Ammo == _lastAmmo && local.IsReloading == _lastReloading) return;
                _lastAmmo = local.Ammo;
                _lastReloading = local.IsReloading;

                AmmoText.Text = local.IsReloading
                    ? "RELOADING"
                    : $"{local.Ammo}/{local.Stats.MagazineCapacity}";
            }
        }
    }
}
