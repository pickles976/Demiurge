using System.IO;
using Demiurge.GameClient;
using Demiurge.Editor;
using StbImageSharp;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Events;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;
using Stride.Rendering.Sprites;

using Stride.UI;
using Stride.UI.Controls;
using Stride.UI.Panels;

namespace Demiurge
{
    public class HUD
    {
        /// <summary>Conquest ticket bar colours and size, shared by the layout and the script.</summary>
        public static readonly Color Team1Color = new(235, 145, 45, 255);
        public static readonly Color Team2Color = new(198, 203, 209, 255);
        public const float TicketBarWidth = 190f;
        public const float TicketBarHeight = 7f;
        private const float PickupPromptTopMargin = 200f;

        /// <summary>Breathing room between the minimap and the status readout beside it, and under
        /// both. Page units, not pixels.</summary>
        private const float StatusGap = 12f;
        private const float OperatingPromptTopMargin = 400f;
        /// <summary>Text that belongs to nobody — connecting words, coordinates, reasons.</summary>
        public static readonly Color NeutralColor = new(235, 238, 242, 245);

        /// <summary>
        /// The colour a team is drawn in anywhere on the HUD: the ticket bar, the activity feed.
        /// Team 1 is orange and team 2 grey, matching the cat models the two sides wear.
        /// </summary>
        /// <summary>Loadout cards: the plate colour of the class you are not taking, and of the
        /// one you are.</summary>
        public static readonly Color UnselectedClassColor = new(14, 14, 17, 205);
        public static readonly Color SelectedClassColor = new(74, 92, 58, 235);

        /// <summary>Card width in the page's 1280x720 units. Set on the card CONTENT rather than on
        /// the button — see the note where it is used.</summary>
        private const float ClassCardWidth = 152f;

        public static Color TeamColor(int team) => team switch
        {
            1 => Team1Color,
            2 => Team2Color,
            _ => NeutralColor,
        };

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

        /// <summary>
        /// The bottom-left minimap. Its own entity and its own UIComponent, because it is the one
        /// piece of HUD that has to share a coordinate system with LineRenderer — see MinimapScript,
        /// which pins this component's resolution to the back buffer so its icons land where its
        /// lines do. Folding it into CreateUI's page would impose that on everything else.
        /// </summary>
        public static Entity CreateMinimap(
            Game game,
            PlayerRegistry registry,
            ObjectRegistry objects,
            TeamIntel intel,
            TerrainState terrain,
            ClientInputState inputState,
            Entity cameraEntity)
        {
            var canvas = new Canvas();
            var ui = new UIComponent
            {
                Page = new UIPage { RootElement = canvas },
                RenderGroup = RenderGroup.Group31,   // rendered by AddCleanUIStage()
            };

            // Neutral, friendly, enemy — the order MinimapScript.Colour resolves a team to.
            ISpriteProvider[] flagIcons =
            [
                Icon(game, "assets/images/flag_white.png"),
                Icon(game, "assets/images/flag_blue.png"),
                Icon(game, "assets/images/flag_red.png"),
            ];

            return new Entity("Minimap")
            {
                ui,
                new MinimapScript
                {
                    Registry = registry,
                    Objects = objects,
                    Intel = intel,
                    Terrain = terrain,
                    InputState = inputState,
                    CameraEntity = cameraEntity,
                    Ui = ui,
                    IconCanvas = canvas,
                    FlagIcons = flagIcons,
                    Priority = 31,
                },
            };
        }

        private static SpriteFromTexture Icon(Game game, string path)
            => new() { Texture = LoadTexture(game, path) };

        public static Entity CreateUI(Game game, ObjectRegistry objects, ClientInputState inputState)
        {
            var font = game.Content.Load<SpriteFont>("StrideDefaultFont");

            // Bullet icon. Texture.Load uses System.Drawing (Windows-only), so decode the
            // PNG with StbImageSharp and upload it manually — same pattern as Program.cs.
            var bulletTexture = LoadTexture(game, "assets/images/bullet.png");

            var bulletImage = new ImageElement
            {
                Source = new SpriteFromTexture { Texture = bulletTexture },
                Width = 15,
                Height = 15,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // The text the HudScript updates. Starts as a placeholder until the first event.
            var ammoText = new TextBlock
            {
                Text = "-/-",
                TextColor = Color.White,
                Font = font,
                TextSize = 17,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
            };

            // The pouches, as their own number. Deliberately not folded into the "loaded/capacity"
            // pair beside it: that pair answers "how many can I fire before I reload", this answers
            // "how many times can I reload", and a reader who has to work out which of two slashed
            // numbers is which is being asked to do arithmetic mid-fight. Dimmer and smaller because
            // it is the one you check between contacts rather than during one.
            var reserveText = new TextBlock
            {
                Text = "-",
                TextColor = new Color(210, 210, 215, 185),
                Font = font,
                TextSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(7, 0, 0, 0),
            };

            var healthText = new TextBlock
            {
                Text = "HP —",
                TextColor = Color.White,
                Font = font,
                TextSize = 17,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };

            // Health left of the bullet icon, icon + ammo text side by side.
            //
            // No background panel and no fixed box around it. The readout is four short runs of
            // text; a plate behind them was drawing a rectangle to say where the text was, which the
            // text already says. The StackPanel sizes to its content, so "beside the minimap" is one
            // margin rather than a box whose dimensions have to be kept in step with the font.
            //
            // The margin is in this page's 1280x720 units while the minimap's footprint is in
            // back-buffer pixels, so it is converted rather than copied — the two spaces are only
            // equal by accident of aspect, and would stop being so on a non-16:9 window.
            float toPageUnits =
                UIComponent.DefaultHeight / (float)game.GraphicsDevice.Presenter.BackBuffer.Height;
            var statusPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(
                    MinimapScript.CornerFootprint * toPageUnits + StatusGap,
                    0,
                    0,
                    StatusGap),
            };
            statusPanel.Children.Add(healthText);
            statusPanel.Children.Add(bulletImage);
            statusPanel.Children.Add(ammoText);
            statusPanel.Children.Add(reserveText);

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
            root.Children.Add(statusPanel);
            root.Children.Add(hotbarPanel);

            // Conquest tickets, top centre: the two sides face each other across the middle, the
            // way Battlefield reads — team 1 orange on the left, team 2 grey on the right.
            var ticketPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 14, 0, 0),
            };
            var ticketCounters = new TextBlock[2];
            var ticketFills = new Border[2];
            for (int i = 0; i < 2; i++)
            {
                var color = i == 0 ? Team1Color : Team2Color;
                // Starts at the full count rather than blank: the server's first word on the subject
                // may be up to one bleed interval away, and a starting score is what is true until
                // then. The server re-sends every interval, so a wrong guess corrects itself.
                var counter = new TextBlock
                {
                    Text = $"{ConquestConfig.StartingTickets}",
                    TextColor = color,
                    Font = font,
                    TextSize = 26,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };

                // Canvas rather than a Grid: the fill is sized in pixels every update, and a Canvas
                // is the panel that leaves a child's Width alone.
                var fill = new Border
                {
                    Width = TicketBarWidth,
                    Height = TicketBarHeight,
                    BackgroundColor = color,
                };
                var track = new Canvas
                {
                    Width = TicketBarWidth,
                    Height = TicketBarHeight,
                    BackgroundColor = new Color(12, 14, 16, 190),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                track.Children.Add(fill);

                var column = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Width = TicketBarWidth,
                    Margin = new Thickness(i == 0 ? 0 : 18, 0, i == 0 ? 18 : 0, 0),
                };
                column.Children.Add(counter);
                column.Children.Add(track);

                ticketCounters[i] = counter;
                ticketFills[i] = fill;
                ticketPanel.Children.Add(column);
            }
            root.Children.Add(ticketPanel);

            // One row per event, each row a horizontal run of text blocks, because a line mixes
            // colours: the actors are drawn in their team colour and the words between them are not.
            var activityLines = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 8, 12, 8),
            };
            var activityPanel = new Border
            {
                BackgroundColor = new Color(5, 5, 7, 125),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 18, 18, 0),
                Content = activityLines,
                Visibility = Visibility.Collapsed,
            };
            root.Children.Add(activityPanel);

            // Below the reticle, where the thing being offered is: the prompt is about what you are
            // standing on, so it reads with the world rather than with the status corner.
            var pickupText = new TextBlock
            {
                Text = "",
                TextColor = new Color(240, 243, 247, 250),
                Font = font,
                TextSize = 20,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                WrapText = false,
                Margin = new Thickness(16, 8, 16, 8),
            };
            var pickupPanel = new Border
            {
                BackgroundColor = new Color(5, 5, 7, 150),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, PickupPromptTopMargin, 0, 0),
                Content = pickupText,
                Visibility = Visibility.Collapsed,
            };
            root.Children.Add(pickupPanel);

            // Held-Tab board. One vertical run of rows, rebuilt when the server sends a new one
            // rather than every frame — it changes on a kill, not on a tick.
            var scoreboardRows = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(18, 12, 18, 12),
            };
            var scoreboardPanel = new Border
            {
                BackgroundColor = new Color(5, 5, 7, 205),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = scoreboardRows,
                Visibility = Visibility.Collapsed,
            };
            root.Children.Add(scoreboardPanel);

            var respawnText = new TextBlock
            {
                Text = "KILLCAM",
                TextColor = Color.White,
                Font = font,
                TextSize = 30,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 12, 20, 12),
            };
            var respawnPanel = new Border
            {
                BackgroundColor = new Color(5, 5, 7, 175),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                // Below the ticket bar, which now owns the top centre.
                Margin = new Thickness(0, 92, 0, 0),
                Content = respawnText,
                Visibility = Visibility.Collapsed,
            };
            root.Children.Add(respawnPanel);

            // The loadout picker, shown with the killcam. Below the respawn clock rather than over
            // the middle of the screen: the killcam is the other thing you are watching while you
            // wait, and a menu across it would be trading one for the other.
            var classButtons = new Button[PlayerClasses.All.Length];
            var classTitles = new TextBlock[PlayerClasses.All.Length];
            var classCards = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            for (int i = 0; i < PlayerClasses.All.Length; i++)
            {
                var playerClass = PlayerClasses.All[i];
                var title = new TextBlock
                {
                    Text = PlayerClasses.Name(playerClass),
                    TextColor = Color.White,
                    Font = font,
                    TextSize = 19,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                var key = new TextBlock
                {
                    Text = $"[{i + 1}]",
                    TextColor = new Color(180, 180, 188, 170),
                    Font = font,
                    TextSize = 13,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0),
                };

                // The card's own width, not the button's. Button.SizeToContent must stay true:
                // with it false, MeasureOverride measures the button's IMAGE and never measures
                // its content at all, so every line of text inside lands at the same origin.
                var card = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Width = ClassCardWidth,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                card.Children.Add(title);
                card.Children.Add(key);

                // A Button rather than a Border, for the one thing a Border cannot do: ButtonBase
                // sets CanBeHitByUser and raises Click on release, which is what makes the card
                // clickable at all. Its images are left null so it draws as the flat plate the rest
                // of this HUD is made of.
                var button = new Button
                {
                    Content = card,
                    Padding = new Thickness(8, 12, 8, 12),
                    Margin = new Thickness(5, 0, 5, 0),
                    BackgroundColor = UnselectedClassColor,
                };
                classButtons[i] = button;
                classTitles[i] = title;
                classCards.Children.Add(button);
            }

            var classPanel = new Border
            {
                BackgroundColor = new Color(5, 5, 7, 175),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                // Under the respawn clock, which sits at 92 and is two lines of 30pt text tall.
                Margin = new Thickness(0, 186, 0, 0),
                Padding = new Thickness(10, 10, 10, 10),
                Content = classCards,
                Visibility = Visibility.Collapsed,
            };
            root.Children.Add(classPanel);

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
                    ReserveText = reserveText,
                    HealthText = healthText,
                    HotbarBorders = hotbarBorders,
                    HotbarImages = hotbarImages,
                    HotbarLabels = hotbarLabels,
                    MissingThumbnail = missingThumbnail,
                    Thumbnails = thumbnails,
                    RespawnPanel = respawnPanel,
                    RespawnText = respawnText,
                    ClassPanel = classPanel,
                    ClassButtons = classButtons,
                    ClassTitles = classTitles,
                    Readiness = game.Services.GetService<SpawnReadiness>(),
                    WeaponPanels = [statusPanel, hotbarPanel],
                    PickupPanel = pickupPanel,
                    PickupText = pickupText,
                    Objects = objects,
                    ActivityPanel = activityPanel,
                    ActivityLines = activityLines,
                    ActivityFont = font,
                    ScoreboardPanel = scoreboardPanel,
                    ScoreboardRows = scoreboardRows,
                    InputState = inputState,
                    TicketCounters = ticketCounters,
                    TicketFills = ticketFills,
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
            public TextBlock ReserveText { get; set; } = null!;
            public TextBlock HealthText { get; set; } = null!;
            public UIElement Root { get; set; } = null!;
            public Border[] HotbarBorders { get; set; } = [];
            public ImageElement[] HotbarImages { get; set; } = [];
            public TextBlock[] HotbarLabels { get; set; } = [];
            public SpriteFromTexture MissingThumbnail { get; set; } = null!;
            public IReadOnlyDictionary<ItemType, SpriteFromTexture> Thumbnails { get; set; }
                = new Dictionary<ItemType, SpriteFromTexture>();
            public UIElement RespawnPanel { get; set; } = null!;
            public TextBlock RespawnText { get; set; } = null!;
            /// <summary>The loadout picker, shown while waiting for a wave. Nullable so the script
            /// stays usable without one rather than assuming its own layout.</summary>
            public UIElement? ClassPanel { get; set; }
            public Button[] ClassButtons { get; set; } = [];
            public TextBlock[] ClassTitles { get; set; } = [];
            /// <summary>Ammo, health and the hotbar — everything about the weapon in hand. Hidden
            /// together whenever the hands are not available for one.</summary>
            public UIElement[] WeaponPanels { get; set; } = [];
            public UIElement PickupPanel { get; set; } = null!;
            public TextBlock PickupText { get; set; } = null!;
            /// <summary>Every replicated object, so the prompt can find the pickup underfoot.
            /// Netcode writes, view reads — the usual direction.</summary>
            public ObjectRegistry Objects { get; set; } = null!;
            public UIElement ActivityPanel { get; set; } = null!;
            public StackPanel ActivityLines { get; set; } = null!;
            public SpriteFont ActivityFont { get; set; } = null!;
            /// <summary>Read before polling Tab: the developer terminal completes command tokens
            /// with it, and a board that popped up behind an open terminal would be answering a
            /// keystroke that was never meant for the game.</summary>
            public ClientInputState InputState { get; set; } = null!;
            public UIElement ScoreboardPanel { get; set; } = null!;
            public StackPanel ScoreboardRows { get; set; } = null!;
            /// <summary>Index 0 is team 1, index 1 is team 2 — the two sides the bar draws.</summary>
            public TextBlock[] TicketCounters { get; set; } = [];
            public Border[] TicketFills { get; set; } = [];

            private PlayerRegistry _registry = null!;
            private NetworkManager _network = null!;
            private readonly Queue<(ActivityFeedSegment[] Segments, long Expires)> _activity = [];
            private readonly Queue<ActivityFeedSegment[]> _receivedActivity = [];
            private readonly object _activityGate = new();
            private const int MaximumActivityLines = 6;
            private static readonly long ActivityLifetimeTicks =
                8L * System.Diagnostics.Stopwatch.Frequency;

            private int _lastAmmo = int.MinValue;
            private int _lastReserve = int.MinValue;
            private bool _lastReloading;
            private int _lastHealth = int.MinValue;
            private bool _lastVisible;
            private HotbarSlot _lastHotbar;
            private bool _lastDeploying;

            /// <summary>Null in configurations without a runtime session (the editor status HUD).</summary>
            public SpawnReadiness? Readiness { get; set; }
            private uint _lastPrimaryId = uint.MaxValue;
            private uint _lastShovelId = uint.MaxValue;
            private uint _lastGrenadeId = uint.MaxValue;
            private int _lastGrenades = int.MinValue;
            private bool _lastDead;
            private int _lastRespawnSeconds = int.MinValue;
            private PlayerClass? _shownClass;
            private bool _classPanelShown;
            /// <summary>Network id the prompt currently names; 0 for none. Keyed by id rather than
            /// by item type so walking between two identical rifles still refreshes.</summary>
            private uint _promptedPickup;
            private bool _weaponPanelsShown = true;

            // Written on the network thread, read on the main thread — the whole message at once,
            // since it is the complete score rather than a delta. Applied in Update.
            private MatchTicketsData? _receivedTickets;
            private readonly object _ticketGate = new();
            private readonly int[] _shownTickets = [-1, -1];

            // Same hand-off as the tickets above, and for the same reason: the board arrives whole,
            // on the network thread, and is applied on the main one.
            private ScoreboardData? _receivedScoreboard;
            private readonly object _scoreboardGate = new();
            private ScoreboardEntry[] _scoreboard = [];
            private bool _scoreboardDirty;
            private bool _scoreboardShown;

            public override void Start()
            {
                _registry = Services.GetSafeServiceAs<PlayerRegistry>();
                _network = Services.GetSafeServiceAs<NetworkManager>();
                _network.ActivityFeedReceived += OnActivityFeed;
                _network.MatchTicketsReceived += OnMatchTickets;
                _network.ScoreboardReceived += OnScoreboard;

                // Subscribed here rather than where the cards are built: the click means "issue me
                // this kit", which needs the local player, and layout has no business knowing about
                // him. One handler per card, so the class is captured rather than searched for.
                for (int i = 0; i < ClassButtons.Length && i < PlayerClasses.All.Length; i++)
                {
                    var playerClass = PlayerClasses.All[i];
                    ClassButtons[i].Click += (_, _) => SelectClass(playerClass);
                }

                Root.Visibility = Visibility.Collapsed;   // until spawn
            }

            public override void Cancel()
            {
                if (_network is not null)
                {
                    _network.ActivityFeedReceived -= OnActivityFeed;
                    _network.MatchTicketsReceived -= OnMatchTickets;
                    _network.ScoreboardReceived -= OnScoreboard;
                }
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
                RefreshTickets();
                // Before the local-player gate: the board is about the match, so a dead or
                // not-yet-spawned player is exactly who wants to look at it.
                RefreshScoreboard();
                if (local == null) return;

                int health = local.Status?.Health.Current ?? 0;
                if (health != _lastHealth)
                {
                    _lastHealth = health;
                    HealthText.Text = $"HP {health}";
                }

                RefreshRespawn(local);

                int ammo = local.IsArmed ? local.Ammo : -1;
                int reserve = local.IsArmed ? local.Reserve : -1;
                // Reserve joins the change gate: picking a weapon up can change the pouches without
                // changing what is loaded, and that must not go unredrawn until the next shot.
                if (ammo != _lastAmmo || reserve != _lastReserve || local.IsReloading != _lastReloading)
                {
                    _lastAmmo = ammo;
                    _lastReserve = reserve;
                    _lastReloading = local.IsReloading;
                    AmmoText.Text = !local.IsArmed ? "--"
                        : local.IsReloading ? "RELOADING"
                        : $"{local.Ammo}/{local.Stats.MagazineCapacity}";
                    ReserveText.Text = local.IsArmed ? $"+{local.Reserve}" : string.Empty;
                }

                RefreshDeploying();
                RefreshWeaponPanels(local);
                RefreshHotbar(local);
                RefreshPickupPrompt(local);
                RefreshActivityFeed();
            }

            private void OnActivityFeed(ActivityFeedData activity)
            {
                if (activity.Segments is not { Length: > 0 } segments) return;
                lock (_activityGate)
                    _receivedActivity.Enqueue(segments);
            }

            /// <summary>Network thread. Keeps only the newest score; an older one that overtakes it
            /// carries no information the newer one lacks.</summary>
            private void OnMatchTickets(MatchTicketsData tickets)
            {
                lock (_ticketGate)
                    _receivedTickets = tickets;
            }

            private void RefreshTickets()
            {
                MatchTicketsData? received;
                lock (_ticketGate)
                {
                    received = _receivedTickets;
                    _receivedTickets = null;
                }
                if (received is not { Teams: { } teams }) return;

                foreach (var entry in teams)
                {
                    int index = entry.Team - 1;   // team 1 draws left, team 2 right
                    if (index < 0 || index >= TicketCounters.Length) continue;
                    if (_shownTickets[index] == entry.Tickets) continue;

                    _shownTickets[index] = entry.Tickets;
                    TicketCounters[index].Text = entry.Tickets.ToString();
                    TicketFills[index].Width = TicketBarWidth
                        * Math.Clamp(entry.Tickets / (float)ConquestConfig.StartingTickets, 0f, 1f);
                }
            }

            /// <summary>
            /// The weapon read-out belongs to a man who can use a weapon. Hauling something or
            /// working an emplacement takes his hands, so the ammo, the health and the hotbar go
            /// with them rather than reporting on a rifle he cannot reach.
            /// </summary>
            private void RefreshWeaponPanels(LocalPlayer local)
            {
                bool available = !local.IsCarrying && !local.IsOperating;
                if (available == _weaponPanelsShown) return;
                _weaponPanelsShown = available;

                foreach (var panel in WeaponPanels)
                    panel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            }

            /// <summary>
            /// Offers what E would actually take. The choice is PickupTargeting's, in Common, and
            /// the server runs the identical call in ItemSystem.ApplyInteract — so the prompt cannot
            /// name one weapon while the key equips another.
            /// </summary>
            private void RefreshPickupPrompt(LocalPlayer local)
            {
                // Hands full: the only thing E can do is put it down, so that is what to offer. It
                // takes priority over anything underfoot for the same reason the server does — you
                // cannot pick a second thing up while holding the first.
                if (!local.IsDead && local.CarriedItem is { } hauled)
                {
                    ShowPrompt(
                        hauled.NetworkId,
                        $"Press E to put down {ItemCatalog.Name(hauled.Item.Type)}");
                    return;
                }

                // A dozen or so world items, scanned once a frame. Cheap enough not to schedule,
                // and it has to be live: the offer changes as you walk.
                // Working one: the only thing left to offer is how to stop.
                if (local.IsOperating)
                {
                    ShowPrompt(uint.MaxValue, "Press F to step away", lower: true);
                    return;
                }

                var pickup = local.IsDead
                    ? null
                    : PickupTargeting.Nearest(local.Position, Objects.Objects, Describe);

                if (pickup is null)
                {
                    ShowPrompt(0u, null);
                    return;
                }

                // Something emplaced offers two things and both are worth saying, because one key
                // takes it away and the other works it — a gunner who only knew about E would carry
                // off the weapon he meant to fire.
                string name = ItemCatalog.Name(pickup.Item.Type);
                ShowPrompt(
                    pickup.NetworkId,
                    ItemConfig.IsCarryable(pickup.Item.Type)
                        ? $"Press F to use {name}    Press E to pick up {name}"
                        : $"Press E to pick up {name}");
            }

            /// <summary>Shows one prompt, keyed by the object it names so identical neighbours still
            /// refresh. Null text hides the panel.</summary>
            private void ShowPrompt(uint networkId, string? text, bool lower = false)
            {
                if (networkId == _promptedPickup) return;
                _promptedPickup = networkId;

                PickupPanel.Margin = new Thickness(
                    0,
                    lower ? OperatingPromptTopMargin : PickupPromptTopMargin,
                    0,
                    0);
                PickupPanel.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
                if (text is not null) PickupText.Text = text;
            }

            private static PickupTargeting.Candidate Describe(NetObject obj)
                => new(obj.Has, obj.Item.Type, obj.Transform.Position);

            private void RefreshActivityFeed()
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                bool changed = false;
                lock (_activityGate)
                {
                    while (_receivedActivity.TryDequeue(out var segments))
                    {
                        _activity.Enqueue((segments, now + ActivityLifetimeTicks));
                        changed = true;
                        while (_activity.Count > MaximumActivityLines)
                            _activity.Dequeue();
                    }
                }

                while (_activity.TryPeek(out var line) && line.Expires <= now)
                {
                    _activity.Dequeue();
                    changed = true;
                }
                if (!changed) return;

                ActivityPanel.Visibility =
                    _activity.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

                // Rebuilt whole rather than diffed: this runs only when a line arrived or expired,
                // and the feed is six lines of a handful of words.
                ActivityLines.Children.Clear();
                foreach (var entry in _activity)
                    ActivityLines.Children.Add(BuildActivityLine(entry.Segments));
            }

            /// <summary>Network thread. Keeps only the newest board; an older one that overtakes it
            /// would be a stale roster, and every board is complete so nothing is lost by dropping
            /// it.</summary>
            private void OnScoreboard(ScoreboardData data)
            {
                lock (_scoreboardGate) _receivedScoreboard = data;
            }

            /// <summary>
            /// Draws the board while Tab is held.
            ///
            /// IsKeyDown rather than IsKeyPressed: this is a hold, and Stride's pressed edge re-fires
            /// on the OS key auto-repeat, which would make a held key look like a burst of taps.
            ///
            /// Rows are rebuilt when the SERVER's board changes, not per frame and not on the key —
            /// showing it is a visibility flip over rows that are already correct.
            /// </summary>
            private void RefreshScoreboard()
            {
                ScoreboardData? received;
                lock (_scoreboardGate)
                {
                    received = _receivedScoreboard;
                    _receivedScoreboard = null;
                }
                if (received is { } board)
                {
                    _scoreboard = board.Entries ?? [];
                    _scoreboardDirty = true;
                }

                bool show = InputState?.TerminalOpen != true
                    && Input.IsKeyDown(Stride.Input.Keys.Tab);
                if (show && _scoreboardDirty)
                {
                    _scoreboardDirty = false;
                    ScoreboardRows.Children.Clear();
                    ScoreboardRows.Children.Add(BuildScoreboardRow(
                        "PLAYER", "K", "D", new Color(210, 214, 220, 235), header: true));
                    foreach (var entry in _scoreboard)
                        ScoreboardRows.Children.Add(BuildScoreboardRow(
                            entry.IsMob ? $"NPC {entry.ActorId}" : $"Player {entry.ActorId}",
                            entry.Kills.ToString(),
                            entry.Deaths.ToString(),
                            TeamColor(entry.Team),
                            header: false));
                }

                if (show == _scoreboardShown) return;
                _scoreboardShown = show;
                ScoreboardPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }

            /// <summary>One line of the board. Fixed column widths rather than a Grid: three columns
            /// whose sizes never change do not need a layout pass to agree about them.</summary>
            private StackPanel BuildScoreboardRow(
                string name,
                string kills,
                string deaths,
                Color color,
                bool header)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                row.Children.Add(ScoreboardCell(name, color, 190f, TextAlignment.Left, header));
                row.Children.Add(ScoreboardCell(kills, color, 55f, TextAlignment.Right, header));
                row.Children.Add(ScoreboardCell(deaths, color, 55f, TextAlignment.Right, header));
                return row;
            }

            private TextBlock ScoreboardCell(
                string text,
                Color color,
                float width,
                TextAlignment alignment,
                bool header)
            {
                var cell = new TextBlock
                {
                    Text = text,
                    TextColor = header ? new Color(160, 165, 175, 220) : color,
                    Font = ActivityFont,
                    TextSize = header ? 15 : 18,
                    TextAlignment = alignment,
                    WrapText = false,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                cell.Width = width;
                return cell;
            }

            private StackPanel BuildActivityLine(ActivityFeedSegment[] segments)
            {
                var line = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                foreach (var segment in segments)
                {
                    line.Children.Add(new TextBlock
                    {
                        Text = segment.Text ?? string.Empty,
                        TextColor = TeamColor(segment.Team),
                        Font = ActivityFont,
                        TextSize = 18,
                        WrapText = false,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                return line;
            }

            private void RefreshRespawn(LocalPlayer local)
            {
                bool dead = local.IsDead;
                // Before the change gate below: keys are pressed between redraws, and the clock
                // only ticks once a second.
                if (dead) ReadClassKeys(local);
                RefreshClassSelection(local, dead);

                int seconds = local.RespawnTick == 0
                    ? RespawnConfig.WaveSeconds
                    : Math.Max(
                        0,
                        (int)Math.Ceiling(
                            (local.RespawnTick - _registry.EstimatedServerTick)
                            / NetworkConfig.TickRate));
                if (dead == _lastDead && (!dead || seconds == _lastRespawnSeconds)) return;

                _lastDead = dead;
                _lastRespawnSeconds = seconds;
                RespawnPanel.Visibility = dead ? Visibility.Visible : Visibility.Collapsed;
                if (dead)
                    RespawnText.Text = $"KILLCAM\nRESPAWN WAVE IN {seconds}";
            }

            /// <summary>
            /// 1, 2 and 3 pick a class while you are waiting. The same keys select hotbar slots
            /// alive, which is not a clash: <c>LocalPlayerController</c> stops reading input at all
            /// once the local player is dead, and a dead man has no hotbar to switch.
            ///
            /// Not while the terminal is open — those digits are being typed at it.
            /// </summary>
            private void ReadClassKeys(LocalPlayer local)
            {
                if (InputState.TerminalOpen) return;

                if (Input.IsKeyPressed(Keys.D1) || Input.IsKeyPressed(Keys.NumPad1))
                    SelectClass(PlayerClasses.All[0]);
                else if (Input.IsKeyPressed(Keys.D2) || Input.IsKeyPressed(Keys.NumPad2))
                    SelectClass(PlayerClasses.All[1]);
                else if (Input.IsKeyPressed(Keys.D3) || Input.IsKeyPressed(Keys.NumPad3))
                    SelectClass(PlayerClasses.All[2]);
            }

            private void SelectClass(PlayerClass playerClass)
                => _registry.LocalPlayer?.SelectClass(playerClass);

            /// <summary>Shows the picker while a wave is pending, and marks the card you took.</summary>
            private void RefreshClassSelection(LocalPlayer local, bool dead)
            {
                if (ClassPanel is null) return;

                // Both gated: Visibility and BackgroundColor invalidate layout, and this runs every
                // frame the player is on the screen.
                if (dead != _classPanelShown)
                {
                    _classPanelShown = dead;
                    ClassPanel.Visibility = dead ? Visibility.Visible : Visibility.Collapsed;
                }
                if (!dead || local.SelectedClass == _shownClass) return;

                _shownClass = local.SelectedClass;
                for (int i = 0; i < ClassButtons.Length && i < PlayerClasses.All.Length; i++)
                {
                    bool selected = PlayerClasses.All[i] == local.SelectedClass;
                    ClassButtons[i].BackgroundColor = selected ? SelectedClassColor : UnselectedClassColor;
                    if (i < ClassTitles.Length)
                        ClassTitles[i].TextColor = selected ? Color.White : new Color(215, 215, 222, 225);
                }
            }

            /// <summary>
            /// Says why the player cannot move yet. Without it, being frozen on spawn reads as the
            /// game being broken rather than as the game waiting for the ground to arrive.
            /// </summary>
            private void RefreshDeploying()
            {
                bool deploying = Readiness is { Ready: false };
                if (deploying == _lastDeploying) return;

                _lastDeploying = deploying;
                if (!deploying)
                {
                    if (!_lastDead) RespawnPanel.Visibility = Visibility.Collapsed;
                    return;
                }

                RespawnPanel.Visibility = Visibility.Visible;
                RespawnText.Text = "DEPLOYING\nPREPARING TERRAIN";
            }

            private void RefreshHotbar(LocalPlayer local)
            {
                var primary = local.ItemIn(HotbarSlot.Primary);
                var shovel = local.ItemIn(HotbarSlot.Shovel);
                var grenade = local.ItemIn(HotbarSlot.Grenade);
                int grenades = local.AmmoIn(HotbarSlot.Grenade);
                uint primaryId = primary?.NetworkId ?? 0;
                uint shovelId = shovel?.NetworkId ?? 0;
                uint grenadeId = grenade?.NetworkId ?? 0;
                if (local.Hotbar == _lastHotbar
                    && primaryId == _lastPrimaryId
                    && shovelId == _lastShovelId
                    && grenadeId == _lastGrenadeId
                    && grenades == _lastGrenades)
                    return;

                _lastHotbar = local.Hotbar;
                _lastPrimaryId = primaryId;
                _lastShovelId = shovelId;
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
                // The shovel is a real replicated object like the other two, so it looks its
                // thumbnail up the same way. This used to pass forceMissing, which pinned the slot to
                // the purple placeholder and ignored the thumbnail table entirely — written before
                // shovel.png existed, and left behind once it did.
                SetSlot(1, shovel, "2  SHOVEL");
                SetSlot(2, grenade, grenade == null ? "3  EMPTY" : $"3  x{grenades}");
            }

            private void SetSlot(
                int index,
                NetObject? item,
                string label)
            {
                HotbarLabels[index].Text = label;
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
