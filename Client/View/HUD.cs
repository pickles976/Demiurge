using System.IO;
using Demiurge.GameClient;
using Demiurge.Editor;
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
        public static Entity CreateTerminal(
            Game game,
            ClientInputState inputState,
            ITerminalCommandDispatcher dispatcher)
        {
            var font = game.Content.Load<SpriteFont>("StrideDefaultFont");

            var outputText = new TextBlock
            {
                Text = "",
                TextColor = new Color(220, 225, 230),
                Font = font,
                TextSize = 18,
                WrapText = true,
                Height = 220,
                Margin = new Thickness(12, 10, 12, 0),
            };

            var promptText = new TextBlock
            {
                Text = "> _",
                TextColor = Color.White,
                Font = font,
                TextSize = 20,
                Margin = new Thickness(12, 4, 12, 10),
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Width = 820,
                Height = 275,
                BackgroundColor = new Color(8, 10, 12, 220),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(16, 16, 16, 16),
            };
            panel.Children.Add(outputText);
            panel.Children.Add(promptText);

            return new Entity("DeveloperTerminal")
            {
                new UIComponent
                {
                    Page = new UIPage { RootElement = panel },
                    RenderGroup = RenderGroup.Group31,
                },
                new DeveloperTerminalScript
                {
                    InputState = inputState,
                    Dispatcher = dispatcher,
                    Panel = panel,
                    OutputText = outputText,
                    PromptText = promptText,
                    Priority = -100,
                },
            };
        }

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

            var healthText = new TextBlock
            {
                Text = "HP —",
                TextColor = Color.White,
                Font = font,
                TextSize = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 12, 0),
            };

            // Health left of the bullet icon, icon + ammo text side by side.
            var ammoPanel = new StackPanel { Orientation = Orientation.Horizontal };
            ammoPanel.Children.Add(healthText);
            ammoPanel.Children.Add(bulletImage);
            ammoPanel.Children.Add(ammoText);

            var statusCanvas = new Canvas
            {
                Width = 100,
                Height = 100,
                BackgroundColor = new Color(0, 0, 0, 100),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            statusCanvas.Children.Add(ammoPanel);

            var missingThumbnail = new SpriteFromTexture
            {
                Texture = CreateMissingThumbnail(game),
            };
            var thumbnails = ItemCatalog.All.ToDictionary(
                definition => definition.Type,
                definition => LoadThumbnail(game, definition.Id, missingThumbnail));

            var hotbarBorders = new Border[HotbarConfig.SlotCount];
            var hotbarImages = new ImageElement[HotbarConfig.SlotCount];
            var hotbarLabels = new TextBlock[HotbarConfig.SlotCount];
            var hotbarPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 18),
            };
            for (int i = 0; i < HotbarConfig.SlotCount; i++)
            {
                var image = new ImageElement
                {
                    Source = missingThumbnail,
                    Width = 48,
                    Height = 38,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var label = new TextBlock
                {
                    Text = $"{i + 1}",
                    TextColor = Color.White,
                    Font = font,
                    TextSize = 15,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var content = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                content.Children.Add(image);
                content.Children.Add(label);

                var border = new Border
                {
                    Width = 86,
                    Height = 66,
                    Margin = new Thickness(2, 2, 2, 2),
                    BorderThickness = new Thickness(2, 2, 2, 2),
                    BorderColor = new Color(125, 125, 125, 230),
                    BackgroundColor = new Color(10, 10, 12, 190),
                    Content = content,
                };
                hotbarBorders[i] = border;
                hotbarImages[i] = image;
                hotbarLabels[i] = label;
                hotbarPanel.Children.Add(border);
            }

            var root = new Grid();
            root.Children.Add(statusCanvas);
            root.Children.Add(hotbarPanel);

            // Put the driving script on the same entity as the UI and hand it the text
            // block to write into.
            var uiEntity = new Entity
            {
                new UIComponent
                {
                    Page = new UIPage { RootElement = root },
                    RenderGroup = RenderGroup.Group31 // rendered by AddCleanUIStage()
                },
                new HudScript {
                    Root = root,
                    AmmoText = ammoText,
                    HealthText = healthText,
                    HotbarBorders = hotbarBorders,
                    HotbarImages = hotbarImages,
                    HotbarLabels = hotbarLabels,
                    MissingThumbnail = missingThumbnail,
                    Thumbnails = thumbnails,
                },
            };

            return uiEntity;
        }

        public static Entity CreateEditorStatus(
            Game game,
            EditorToolSettings settings,
            EditorSession session,
            EditorControllerScript controller,
            EditorInteractionState interactionState)
        {
            var text = new TextBlock
            {
                TextColor = Color.White,
                Font = game.Content.Load<SpriteFont>("StrideDefaultFont"),
                TextSize = 18,
                Margin = new Thickness(8, 8, 8, 8),
            };
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                BackgroundColor = new Color(0, 0, 0, 120),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12, 12, 12, 12),
            };
            panel.Children.Add(text);
            return new Entity("EditorStatus")
            {
                new UIComponent
                {
                    Page = new UIPage { RootElement = panel },
                    RenderGroup = RenderGroup.Group31,
                },
                new EditorStatusScript
                {
                    Text = text,
                    Settings = settings,
                    Session = session,
                    Controller = controller,
                    InteractionState = interactionState,
                },
            };
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

        private static SpriteFromTexture LoadThumbnail(
            Game game,
            string itemId,
            SpriteFromTexture missing)
        {
            string name = itemId[(itemId.IndexOf(':') + 1)..];
            string path = Path.Combine("assets", "images", "thumbnails", $"{name}.png");
            return File.Exists(path)
                ? new SpriteFromTexture { Texture = LoadTexture(game, path) }
                : missing;
        }

        private static Texture CreateMissingThumbnail(Game game)
        {
            const int size = 32;
            var pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool purple = ((x / 8) + (y / 8)) % 2 == 0;
                    int offset = (y * size + x) * 4;
                    pixels[offset] = purple ? (byte)210 : (byte)8;
                    pixels[offset + 1] = purple ? (byte)20 : (byte)8;
                    pixels[offset + 2] = purple ? (byte)230 : (byte)8;
                    pixels[offset + 3] = 255;
                }
            }
            return Texture.New2D(
                game.GraphicsDevice,
                size,
                size,
                PixelFormat.R8G8B8A8_UNorm_SRgb,
                pixels);
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
        /// Health + ammo readout for the local player. Reads the sim directly (the
        /// same netcode-writes-view-reads contract as the other view scripts) and
        /// repaints only when a value changes, so steady-state frames allocate
        /// nothing. Shown from spawn — health is always relevant; ammo reads "--"
        /// until a weapon is equipped.
        /// </summary>
        public class HudScript : SyncScript
        {
            public TextBlock AmmoText { get; set; } = null!;
            public TextBlock HealthText { get; set; } = null!;
            public UIElement Root { get; set; } = null!;
            public Border[] HotbarBorders { get; set; } = [];
            public ImageElement[] HotbarImages { get; set; } = [];
            public TextBlock[] HotbarLabels { get; set; } = [];
            public SpriteFromTexture MissingThumbnail { get; set; } = null!;
            public IReadOnlyDictionary<ItemType, SpriteFromTexture> Thumbnails { get; set; }
                = new Dictionary<ItemType, SpriteFromTexture>();

            private PlayerRegistry _registry = null!;

            private int _lastAmmo = int.MinValue;
            private bool _lastReloading;
            private int _lastHealth = int.MinValue;
            private bool _lastVisible;
            private HotbarSlot _lastHotbar;
            private uint _lastPrimaryId = uint.MaxValue;
            private uint _lastGrenadeId = uint.MaxValue;
            private int _lastGrenades = int.MinValue;

            public override void Start()
            {
                _registry = Services.GetSafeServiceAs<PlayerRegistry>();
                Root.Visibility = Visibility.Collapsed;   // until spawn
            }

            public override void Update()
            {
                var local = _registry.LocalPlayer;

                bool visible = local != null;
                if (visible != _lastVisible)
                {
                    _lastVisible = visible;
                    Root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }
                if (local == null) return;

                int health = local.Status?.Health.Current ?? 0;
                if (health != _lastHealth)
                {
                    _lastHealth = health;
                    HealthText.Text = $"HP {health}";
                }

                int ammo = local.IsArmed ? local.Ammo : -1;
                if (ammo != _lastAmmo || local.IsReloading != _lastReloading)
                {
                    _lastAmmo = ammo;
                    _lastReloading = local.IsReloading;
                    AmmoText.Text = !local.IsArmed ? "--"
                        : local.IsReloading ? "RELOADING"
                        : $"{local.Ammo}/{local.Stats.MagazineCapacity}";
                }

                RefreshHotbar(local);
            }

            private void RefreshHotbar(LocalPlayer local)
            {
                var primary = local.ItemIn(HotbarSlot.Primary);
                var grenade = local.ItemIn(HotbarSlot.Grenade);
                int grenades = local.AmmoIn(HotbarSlot.Grenade);
                uint primaryId = primary?.NetworkId ?? 0;
                uint grenadeId = grenade?.NetworkId ?? 0;
                if (local.Hotbar == _lastHotbar
                    && primaryId == _lastPrimaryId
                    && grenadeId == _lastGrenadeId
                    && grenades == _lastGrenades)
                    return;

                _lastHotbar = local.Hotbar;
                _lastPrimaryId = primaryId;
                _lastGrenadeId = grenadeId;
                _lastGrenades = grenades;

                for (int i = 0; i < HotbarBorders.Length; i++)
                {
                    bool selected = i == (int)local.Hotbar - 1;
                    HotbarBorders[i].BorderColor = selected
                        ? new Color(255, 225, 105, 255)
                        : new Color(125, 125, 125, 230);
                    HotbarBorders[i].BackgroundColor = selected
                        ? new Color(55, 50, 28, 220)
                        : new Color(10, 10, 12, 190);
                }

                SetSlot(0, primary, primary == null ? "1  EMPTY" : $"1  {DisplayName(primary.Item.Type)}");
                SetSlot(1, null, "2  SHOVEL", forceMissing: true);
                SetSlot(2, grenade, grenade == null ? "3  EMPTY" : $"3  x{grenades}");
            }

            private void SetSlot(
                int index,
                NetObject? item,
                string label,
                bool forceMissing = false)
            {
                HotbarLabels[index].Text = label;
                if (forceMissing)
                {
                    HotbarImages[index].Source = MissingThumbnail;
                    HotbarImages[index].Visibility = Visibility.Visible;
                    return;
                }

                HotbarImages[index].Visibility =
                    item == null ? Visibility.Collapsed : Visibility.Visible;
                if (item != null)
                    HotbarImages[index].Source =
                        Thumbnails.GetValueOrDefault(item.Item.Type, MissingThumbnail);
            }

            private static string DisplayName(ItemType type)
                => ItemCatalog.Id(type).Split(':')[1].Replace('_', ' ').ToUpperInvariant();
        }

        public sealed class EditorStatusScript : SyncScript
        {
            public required TextBlock Text { get; init; }
            public required EditorToolSettings Settings { get; init; }
            public required EditorSession Session { get; init; }
            public required EditorControllerScript Controller { get; init; }
            public required EditorInteractionState InteractionState { get; init; }
            private string previous = string.Empty;

            public override void Update()
            {
                if (InteractionState.Playtesting)
                {
                    const string playtest =
                        "PLAYTEST\n[F4] Return to editor";
                    if (playtest == previous) return;
                    previous = playtest;
                    Text.Text = playtest;
                    return;
                }

                string detail = Settings.Mode switch
                {
                    EditorToolMode.Terrain =>
                        $"{Settings.TerrainMode} {Settings.TerrainShape} {Settings.TerrainHalfExtent * 2f}",
                    EditorToolMode.Block =>
                        $"{BlockCatalog.Id(Settings.Block)}  " +
                        $"{Settings.BlockSize.X}x{Settings.BlockSize.Y}x{Settings.BlockSize.Z}",
                    EditorToolMode.Object => Controller.SelectedPlacementId is { } selected
                        ? $"Selected {EditorPlacementIds.Display(selected)}"
                        : $"{Settings.ObjectId ?? "No object selected"} team={Settings.ObjectTeam}",
                    _ => string.Empty,
                };
                string value =
                    "[1] Terrain   [2] Block   [3] Object\n" +
                    "[U] Undo   [Y] Redo   [R] Rotate selection   [F4] Playtest\n" +
                    $"MODE: {Settings.Mode.ToString().ToUpperInvariant()}" +
                    (Session.Dirty ? "  *" : string.Empty) +
                    $"\n{detail}";
                if (value == previous) return;
                previous = value;
                Text.Text = value;
            }
        }
    }
}
