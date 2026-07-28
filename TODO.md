# Chunk Generation

# LLM Work
- [ ] rework weapon attachment onto the models' `grip` and `barrel` locators
      Every weapon gltf now carries two Blockbench locator nodes. The grip should
      sit in the player's hand; bullets should originate at the barrel.

      CURRENT MECHANISM. ItemCosmetics.Socket(Node, Seat, Rotation) hardcodes the
      offset: the `right_hand` bone plus a hand-tuned HandSeat of
      (0, -3.75/16, 0.425/16) and a two-axis HandRotation. ItemScripts links the
      weapon to the bone with a ModelNodeLinkComponent and applies that offset. So
      the offset is a magic number per slot rather than derived from the model —
      which is exactly what the locators replace.

    - [ ] CHECK THIS FIRST, everything depends on it: does the SyncStrideGltfAssets
          / GltfAssetGenerator pipeline preserve locator nodes into the runtime
          Model's skeleton, or drop them? If dropped, that is an asset-pipeline
          change before any of the rest works.
              grep -rn "locator\|Skeleton\|NodeName" tools/GltfAssetGenerator
    - [ ] grip: read the `grip` node's local transform, invert it, use that as the
          weapon entity's offset from the hand bone. The weapon then hangs so its
          grip coincides with the hand whatever the model's origin is. Deletes
          HandSeat/HandRotation, and Socket probably loses Seat/Rotation entirely.
    - [ ] barrel: replaces GunConfig.MuzzleHeight, used in Player.cs:91 (the
          authoritative fire origin, which goes on the wire for the server to
          lag-compensate against) and AimLineScript.cs:22 (already disabled).

      THE DESIGN DECISION, which is a layering question and wants deciding before
      any code. The barrel's world position is a VIEW fact — a model node's
      transform — but TryFire is SIM and its origin is authoritative input. Three
      options:
        a) View publishes the barrel origin to Sim each frame. Simple, but it is a
           View -> Sim write and cuts against RECIPES.md's one-way flow.
        b) Sim keeps a computed origin; the barrel drives visuals only (tracer
           start, muzzle flash). Preserves layering, but then bullets do not
           really come from the barrel.
        c) Bake the barrel offset into WeaponConfig at load time and let Sim derive
           the origin from data. Keeps layering AND is honest about where bullets
           come from; needs the offset extracted once and stored.
      Leaning (c) — the only one that is both correct about layering and honest
      about where bullets come from — but it is a call to make, not a default.

- [x] switch to third-person action view (keep third person top-down controller as dead code)
      - [x] third-person action view
      - [x] tilde for freecam
- [ ] reticle
- [ ] raycasting against terrain (guns)
- [ ] aim gun and head look at aim point
- [ ] add digging
      - [ ] highlight the surface you are going to dig (ghost block?)
      - [ ] left click with no weapon
      - [ ] dig instantly
      - [ ] dig just a little bit
- [ ] add placing blocks
      - [ ] place an entire block

- [ ] add grass back in
- [ ] add trees

- [ ] clean up program.cs

- [ ] track loaded chunks per player (server-side)
- [ ] load chunks as player moves around
- [ ] view-distance meshing: don't mesh sections past a radius.
- [ ] how far should we be able to see?

- [ ] generate beyond 1km but create an invisible barrier at 1km x 1km

- [ ] add grass back in
- [ ] add tree generation

- [ ] read about cave carving
- [ ] add cave carving

- [ ] add resource deposits