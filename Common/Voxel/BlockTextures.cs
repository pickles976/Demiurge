namespace Demiurge
{
    /// <summary>
    /// Which texture files each block type draws with, and how they tile.
    ///
    /// The convention is assets/textures/blocks/&lt;name&gt;/&lt;name&gt;_N.png numbered from 1, so
    /// adding a variant is a file drop plus bumping the count. Every variant of one type must share
    /// dimensions, because they load into one Texture2DArray — but different types are separate
    /// arrays, so they're free to differ from each other.
    ///
    /// In Common rather than the client because it's a manifest, not rendering: the server will want
    /// the same block-to-name mapping for tool and material rules.
    /// </summary>
    public static class BlockTextures
    {
        /// <summary>
        /// Variants are picked per world cell by a hash in the shader, so a count of 1 is normal and
        /// costs nothing — the shader specialises the hash away.
        /// </summary>
        /// <param name="TileSize">World units per texture repeat.</param>
        public readonly record struct Entry(string[] Paths, float TileSize)
        {
            public int Variants => Paths.Length;

            /// <summary>The numbered convention: blocks/name/name_1.png … name_N.png.</summary>
            public static Entry Numbered(string name, int variants, float tileSize)
                => new([.. Enumerable.Range(1, variants).Select(n => $"assets/textures/blocks/{name}/{name}_{n}.png")],
                       tileSize);

            /// <summary>A single texture at an arbitrary path.</summary>
            public static Entry Single(string path, float tileSize) => new([path], tileSize);
        }

        /// <summary>
        /// TileSize 1 for the 16x16 pixel art keeps one texel per 1/16 world unit, which is what makes
        /// it read as pixel art rather than as blur. The 1024x1024 prototype textures want a much
        /// larger repeat.
        /// </summary>
        static readonly Dictionary<BlockType, Entry> entries = new()
        {
            [BlockType.BlockType_Grass] = Entry.Numbered("grass", variants: 4, tileSize: 1f),
            [BlockType.BlockType_Dirt] = Entry.Numbered("dirt", variants: 4, tileSize: 1f),
            [BlockType.BlockType_Stone] = Entry.Numbered("stone", variants: 4, tileSize: 1f),
            [BlockType.BlockType_Sandbags] = Entry.Numbered("sandbags", variants: 4, tileSize: 1f),
            [BlockType.BlockType_StoneBricks] = Entry.Numbered("stone_bricks", variants: 4, tileSize: 1f),
        };

        /// <summary>
        /// What a type with no art yet draws: the purple prototype texture, i.e. the usual
        /// missing-texture convention.
        ///
        /// It must NOT be a plausible-looking material. Falling back to grass made a stone wall draw
        /// as grass, which reads as a material bug rather than as absent art — the same trap as
        /// substituting air for a missing chunk. Prefer failures that are obvious over failures that
        /// are pretty.
        /// </summary>
        public static readonly Entry Missing =
            Entry.Single("assets/prototype/textures/Purple/texture_01.png", tileSize: 4f);

        public static Entry For(BlockType type)
            => entries.TryGetValue(type, out var entry) ? entry : Missing;

        public static bool Has(BlockType type) => entries.ContainsKey(type);
    }
}
