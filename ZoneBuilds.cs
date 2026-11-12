using System.Diagnostics;

namespace MhoPackageModifier;

/// <summary>
/// --build-zone: rebuilds a zone's main level from stock with its whole placeholder recipe, inside the tool (the
/// recipes were bash scripts before: publish/scans/tools/ht_build.sh). Every step runs as a dry run on a scratch copy
/// of the stock file (the .bak, or the live file if it has never been modified), each writer verifying its own
/// output as usual; the finished package is then written once through MeshImport.WriteLive (.bak / verified temp /
/// swap), so a whole rebuild is one undo step. Inputs that aren't in the game (placeholder FBX, facade textures)
/// ship in ZoneData/&lt;Zone&gt; next to the exe.
/// </summary>
static class ZoneBuilds
{
    public enum Walls { Facade, Grey }

    /// <param name="LodOnly">The zone's recipe can leave its sky and fog out (--lod-only: LOD buildings, ground and water only,
    /// built on another mod's copy of the level; Kurt 2026-11-08, on top of MichaelMerc's walls / skies mod).</param>
    public sealed record Zone(string Name, string Package, string Description, Func<Walls, bool, List<string[]>> Steps, bool HasFacade = true, bool LodOnly = false);

    const string Target = "@target", Data = "@data/";
    const string LodSize = "@lodsize";                                  // --max-size for LOD textures: --lod-size / the Zones tab (default 0 = original; 2048 read crisper than 512 in game)
    const string Library = "@lib:";                                     // a game package, read from its .bak if one exists

    public static readonly Zone[] Zones =
    [
        new("Hightown", "Madripoor_HighTown_B.upk",
            "Upper Madripoor / Hightown (night): a sky dome, distant building boxes (hidden within 3500), the ground, two water layers around the " +
            "GameCenter stairwell hole, and a night sky. Zone facade: night-window walls and a baked ground plane. Default grey: grey boxes and grey ground slabs.",
            HightownSteps, LodOnly: true),
        new("IndustryCity", "Brooklyn_Docks_A.upk",
            "Industry City (ICP, day): baked building LODs (4 parts, one piece per cell, drawn from 2500 away; the random middle block left out), " +
            "a baked ground plane, the zone's water as two layers around the AIM Sub pit (the tiles' own water removed separately), harbour mist, " +
            "and the stock sky with a city skyline on a translucent inner dome. The Walls choice doesn't apply here.",
            IndustryCitySteps, HasFacade: false, LodOnly: true),
        new("OdinsPalace", "Asgard_Hub_B.upk",
            "Odin's Palace (Kurse operation): full starfield sky (backdrop copies for the sides and top), building boxes " +
            "(64-unit raster, MinDrawDistance 3500), always-drawn slabs under the floors and platforms, the real Bifrost deck at a distance. Grey walls only so far.",
            OdinsPalaceSteps, HasFacade: false, LodOnly: true),
        new("Midtown", "MidTown_Static.upk",
            "Midtown free roam (day): Kurt's baked LOD of the 36 UES_Static tiles (one piece per cell, drawn from 2500 away). LOD only: build it with --lod-only on the Walls mod's MidTown_Static.",
            MidtownSteps, HasFacade: false, LodOnly: true),
        new("MidtownFull", "MidTown_Static.upk",
            "Midtown (2026-11-09): the LOD baked with no reduction at all, one tile column per part (X5 in halves + ground), 4096 atlases, pieces on a 768 grid. LOD only.",
            MidtownFullSteps, HasFacade: false, LodOnly: true),
        new("MidtownGround3", "MidTown_Static.upk",
            "Midtown test (2026-11-11, Kurt's option A): MidtownFull's buildings with the ground in three atlases (tile columns X0-1, X2-3, X4-5), each laid out and painted by us (no texels shared between different pieces). LOD only.",
            (w, l) => MidtownFullSteps(w, l, 9, groundParts: 3, groundShrink: "6"), HasFacade: false, LodOnly: true),
        // Tighter atlas layouts (2026-11-09, 2048 / 4096, 6 px island margins) looked patchy in game (DXT blocks bleed between
        // islands): dropped. The parts_full bake (X5 in two halves: as one piece it baked 94 % black) is the Zone LODs' Midtown.
    ];

    /// <summary>
    /// Industry City, ported from icp_build.sh (2026-09-24/25). Cells are laid out at run time and stored at the origin;
    /// the placeholder FBX are already in final positions (IndustryCity_layout.txt). Sky: the Brood skydome material
    /// copied as icp_sky_mat with Kurt's photo (black "stars" layer, grey-84 mask = the photo's brightness). Clouds are a
    /// second dome just inside it (--add-cloud-dome) with its own UV0, so they can tile far less than the photo's UVs
    /// allow: a second copy, icp_cloud_mat, with a black photo, a flat cloud grey as its scrolling "stars" layer and the
    /// make_clouds.py mask, whose alpha is the cloud shape.
    /// </summary>
    // ZoneData/IndustryCity/lod_bundle<n>.fbx + lod_bundle<n>_diff.dds (make_mips.py --scale: 1 warehouses 0.45, read too
    // bright next to the real ones at 0.6; 2 sub area, 3 containers, 4 cargo ship 0.6).
    const int LodBundles = 4;
    // LOD atlases and the baked ground plane: no mips, at the zone build's LOD texture size (--lod-size / Zones tab, default
    // 512: Kurt 2026-09-27 "as these are LODs, the 512 is decent"). The 2048 .dds sources carry full mip chains, so the
    // import takes the stored level (the ground's is premultiplied and cut at half coverage). ICP main level 24.3 -> 10.9 MB.

    // A cloud layer of our own (2026-09-26, parked 2026-09-27 in favour of the stock sky's clouds): a translucent inner dome.
    const bool IcpCloudLayer = false;
    const string Template = "maptemplates.sky.t_udk_sky_cloudmask01";

    static string[][] IcpCloudSteps(string skyComp) =>
    [
        // Gambit's hotspot VFX instance mic_cloudy_glow_03 (parent mat_vfxbasev2_spriteemissive in MarvelGame.upk): translucent
        // emissive, two layers; l1 panner (x, y, all) = scroll speed, l1 scale = tiling. Layer 0 (a sprite mask, multiplied in)
        // white. Came out inverted (white = clear) and still fast: to do. The Asgard dome material before it spun at a fixed rate.
        // icp_clouds_big: icp_clouds_big_src.png (make_clouds.py --cells 2x2 --coverage 0.5 --softness 0.35, RGB = A), DXT5 + mips.
        ["--import-texture", Target, Template, "icp_black", Data + "icp_black.dds"],
        ["--import-texture", Target, Template, "icp_clouds_big", Data + "icp_clouds_big.dds"],
        ["--import-texture", Target, Template, "icp_white", Data + "icp_white.dds"],
        ["--copy-export", Library + "UC__MarvelEntity_Hotspot_GambitStreetSweepHotspot_SF.upk", "vfx_gambit_d.materials.mic_cloudy_glow_03", Target,
            "--rename", "icp_cloud_layer_mat",
            "--replace-ref", "vfx_shared_textures.textures.tex_cloud_basic=maptemplates.sky.icp_clouds_big",
            "--replace-ref", "vfx_shared_textures.textures.tex_glow_soft=maptemplates.sky.icp_white",
            "--replace-ref", "vfx_shared_textures.textures.tex_butterflies_2=maptemplates.sky.icp_black"],
        ["--set-property", Target, "vfx_gambit_d.materials.icp_cloud_layer_mat",
            "param:l1: uvs: panner: (x, y, all)=0.7,-0.3,0.2,0", "param:l1: uvs: scale: (x, y, all)=1,1,1,0"],
        // 70% (inside the 80% city dome). Planar UV0: a flat cloud ceiling seen through the dome (clouds shrink and flatten
        // toward the horizon), scale 0.2, soft floor at 6 degrees; vertex alpha fades them out from 20 down to 2 degrees.
        ["--add-cloud-dome", Target, skyComp, "vfx_gambit_d.materials.icp_cloud_layer_mat", "--uv", "1,1", "--planar", "0.2,0", "--horizon", "6", "--horizon-fade", "2,20", "--shrink", "0.7"],
    ];

    static List<string[]> IndustryCitySteps(Walls walls, bool lodOnly)
    {
        const string lib = Library + "SCS__CH0201ShippingYardRegion_SF.upk";
        const string water = "brooklyn_docks_lighting.brooklyn_docks_water_mat";
        const string template = "maptemplates.sky.t_udk_sky_cloudmask01";
        const string sf = "madripoor_hitown_buildings.madripoor_hitown_storefront_a";
        const string skyComp = "theworld.persistentlevel.staticmeshcollectionactor_0.sma_sm_skysphere_smc_16";
        string[] around = ["-100224,-100224,100224,-1152", "-100224,1152,100224,100224", "-100224,-1152,-4608,1152", "0,-1152,100224,1152"];
        // The hole itself gets water at pit level (-210 / -215, under the stock pit water at -205, so no flicker when the
        // tile loads): from a distance the pit is otherwise empty. 10x the hole (Kurt, 2026-09-26), reaching under the main water, so
        // no view through the hole's edge finds the sky.
        string icpWater = string.Join(';', around.Concat(around.Select(b => b + ",-126")).Concat(["-25344,-11520,20736,11520,-210", "-25344,-11520,20736,11520,-215"]));
        return
        [
            // Ground: Kurt's baked plane (from --export-placed, vertex-blended terrain baked top-down, alpha where there's no
            // ground) on a masked copy of the library's pier-edge material, at the old stock-water height -116 (under the
            // docks, over our water). It shows where the real ground isn't drawn: the distance and the pit ring (red test,
            // 2026-09-26). DXT1 1-bit alpha at the 1/3 mask clip, with a full mip chain (make_mips.py --scale 0.7, to match the real ground): with one mip it
            // shimmered from a distance.
            ["--import-texture", Target, template, "icp_groundplane_diff", Data + "icp_groundplane_diff.dds", "--no-mips", "--max-size", LodSize],
            ["--copy-export", lib, "brooklyn_pieredge_a.brooklyn_pieredge_mat", Target, "--cut", "physmaterial", "--rename", "icp_groundplane_mat",
                "--replace-ref", "brooklyn_pieredge_a.brooklyn_pieredge_diff_a=maptemplates.sky.icp_groundplane_diff"],
            // Building LODs: Kurt's low-poly textured bakes of every --export-placed piece 100+ tall and 100+ long (fences,
            // trees, poles, railings, cables, ladders skipped), in 4 parts, each with its own 2048 atlas (2026-09-26); they
            // replace the grey boxes (all excluded). Drawn like the boxes (MinDrawDistance 3500). Unlit: the bake already has
            // its lighting, and our components have no lightmap, so a lit material left the side away from the sun nearly
            // black. Hightown's emissive storefront material (emissive = diffuse x spec G x EmissiveMult 1) with G full, no
            // specular (R 0) or reflection (B 0), flat normal: the bake shows as it is. Atlases converted with make_mips.py
            // --scale 0.6 (0.7 read a little bright next to the real buildings). --lod-shrink pulls every face 6 units in
            // along its normal and --lod-drop 4 lowers it, so each LOD surface sits just inside the real one (ICP's cells
            // are still drawn past 3500: coincident surfaces z-fought).
            // The water first: its cubemap (brooklyn_docks_cubea, ~68 KB) also stands in for the LOD materials' reflection slot.
            ["--copy-export", lib, water, Target, "--cut", "physmaterial"],
            ["--import-texture", Target, template, "icp_lod_emissive_spec", Data + "lod_emissive_spec.dds"],
            ["--import-texture", Target, template, "icp_lod_flat_nrml", Data + "lod_flat_nrml.dds"],
            .. Enumerable.Range(1, LodBundles).SelectMany(n => new[]
            {
                new[] { "--import-texture", Target, template, $"icp_lod_bundle{n}_diff", Data + $"lod_bundle{n}_diff.dds", "--no-mips", "--max-size", LodSize },
                ["--copy-export", Library + "SCS__DailyRHighTownInvasionRegionL30_SF.upk", sf + "_mat", Target, "--cut", "physmaterial", "--rename", $"icp_lod_bundle{n}_mat",
                    "--replace-ref", sf + $"_diff=maptemplates.sky.icp_lod_bundle{n}_diff", "--replace-ref", sf + "_spec=maptemplates.sky.icp_lod_emissive_spec",
                    "--replace-ref", sf + "_nrml=maptemplates.sky.icp_lod_flat_nrml",
                    // Reflection is off (spec B = 0), but the storefront's chrome cubemap (~790 KB) came along with it.
                    "--replace-ref", "madripoor_hitown_trffcbarrier.madripoor_hitown_chromecuberef_mat=brooklyn_terrain.brooklyn_docks_cubea"],
            }),
            // MinDrawDistance 2500 (3500 left a small gap where the real cells had already streamed out).
            ["--add-cell-placeholders", Target, Data + "raster.fbx", "--from-live", "--min-draw", "2500",
                "--textured-fbx", Data + "groundplane.fbx", "--textured-material", "brooklyn_pieredge_a.icp_groundplane_mat", "--textured-z", "-116",
                .. Enumerable.Range(1, LodBundles).SelectMany(n => new[] { "--lod", Data + $"lod_bundle{n}.fbx=madripoor_hitown_buildings.icp_lod_bundle{n}_mat" }),
                "--exclude-box", "-200000,-200000,200000,200000", "--lod-shrink", "6", "--lod-drop", "4",
                // The random middle block (two rows of 3 cells that swap between seeds): no building LOD there, or a
                // swapped run would show warehouses in the wrong row. The ground plane still covers it.
                "--lod-exclude-box", "2304,-1152,9216,3456",
                // LOD pieces on a 768 grid (2026-11-09, as Midtown): on the 2304 grid a building near a cell's corner hid early
                "--lod-cell", "768"],
            // Water: the stock harbour water is terrain_flat_filler at -116 in the cells (removed from those tiles with
            // --remove-components, 2026-09-25), so two layers just under it (-121, -126: translucent, reads as depth), with
            // a hole over the two AIM Sub cells, which keep their own pit water at -205.
            // No --sky-drop: lowering the dome only sinks the horizon into the height fog (2026-09-26).
            ["--add-sky-placeholders", Target, "none", "--ground-z", "-121", "--ground-box", icpWater, "--ground-material", water, "--ground-grid", "4608"],
            // (--lod-only stops here: the sky and the fog are left as the base package has them)
            .. (lodOnly ? Array.Empty<string[]>() : IndustryCitySky(skyComp, template)),
        ];
    }

    /// <summary>Industry City's sky (the skyline inner dome) and harbour mist: left out by --lod-only.</summary>
    static string[][] IndustryCitySky(string skyComp, string template) =>
        [
            // Sky (2026-09-27, Kurt's option A): the stock procedural sky and its moving clouds stay on the stock dome. Our city
            // skyline is an inner dome (80%: 2% z-fought at ~2M units) with a masked, two-sided, emissive copy of
            // madripoor_marketfood_mat (emissive = diffuse x spec G, as the building LODs): icp_city_src.png, converted by the
            // import (DXT5, its soft alpha kept, no mips, colour x0.45: the Brood mask showed the photo at about a third) = Kurt's
            // cut-out skyline icp_city_kurt.png as it is (once around; he sets its size and position in the image). Before this the photo was on the stock
            // dome itself (Brood skydome copy, UV0 v x0.499 for its 2 x v sampling); see git history.
            ["--import-texture", Target, template, "icp_city", Data + "icp_city_kurt.png", "--scale", "0.45", "--format", "dxt5", "--no-mips"],
            // Translucent (Kurt 2026-09-27: the PNG's own soft alpha, no mask, no mips): a copy of madripoor_shops_misc_mat
            // (envbaseshaderv3_translucent, emissive = diffuse x spec G; one-sided). The masked madripoor_marketfood_mat before
            // it cut the alpha at 1/3.
            ["--copy-export", Library + "Brooklyn_Speakeasy_A.upk", "madripoor_shops.madripoor_shops_misc_mat", Target, "--cut", "physmaterial",
                "--rename", "icp_city_mat",
                "--replace-ref", "madripoor_shops.madripoor_shops_misc_diff=maptemplates.sky.icp_city",
                "--replace-ref", "madripoor_shops.madripoor_shops_misc_spec=maptemplates.sky.icp_lod_emissive_spec",
                "--replace-ref", "madripoor_shops.madripoor_shops_misc_nrml=maptemplates.sky.icp_lod_flat_nrml"],
            // Sort priority -3: drawn before the water (sky mesh, 0) every frame; the mirrored lower half shows through the water
            // like a reflection and flickered while the two swapped order.
            ["--add-cloud-dome", Target, skyComp, "madripoor_shops.icp_city_mat", "--uv", "1,1", "--shrink", "0.8", "--sort-priority", "-3"],
            .. (IcpCloudLayer ? IcpCloudSteps(skyComp) : []),
            // Night harbour mist (2026-09-26; stock: opacity 0.5, start 100, density 0.1, height 764, opposite light blue
            // 138,182,244, inscattering warm 222,218,146): more distance fog (hides the LOD swap and the zone edge), clear
            // around the player (MHO's camera sits high), hugging the water, cooler colours for the night sky.
            ["--set-property", Target, "theworld.persistentlevel.exponentialheightfog_0.exponentialheightfogcomponent_0",
                "FogMaxOpacity=0.8", "StartDistance=1200", "FogDensity=0.2", "FogHeight=400",
                "OppositeLightColor=110,125,145", "LightInscatteringColor=150,145,130"],
        ];

    /// <summary>
    /// Odin's Palace (tuned in-game 2026-09-25). Sky: the starfield backdrop only covered ~130 degrees below the horizon
    /// (--sky-coverage); rotated copies (yaw 90/180/270, then roll 180 at four yaws) cover every direction. Boxes: the
    /// region isn't centred (the server log's area bounds = tile bounds), so no offset. The level has no procedural sky
    /// dome, so the sky sphere mesh and its material are copied from stock Brooklyn_Docks_A only as templates (layout,
    /// grey copy) and the components copy the level's own waterfall component.
    /// </summary>
    static List<string[]> OdinsPalaceSteps(Walls walls, bool lodOnly)
    {
        const string actor = "theworld.persistentlevel.staticmeshcollectionactor_0";
        const string brooklyn = Library + "Brooklyn_Docks_A.upk";
        const string lib = Library + "SCS__DailyGAsgardINSTRegionL40_SF.upk";
        const string bridgeA = Library + "Asgardia_Bridge_EXT_INS_A.upk", bridgeB = Library + "Asgardia_Bridge_EXT_INS_B.upk";
        const string deck = "rainbowbridge,rainbowbridgeicecover_polysurface4,rainbowbridgeicecover_polysurface5";
        // Kurt 2026-11-09: the distant deck in the live rainbow (the mesh's own mat_rainbowbridge_master, copied with it)
        // instead of the tiles' dead one.
        string[] live = lodOnly ? ["--material", "asgard.mat_rainbowbridge_dead=asgard.mat_rainbowbridge_master"] : [];
        return
        [
            // The sky: the starfield backdrop copies (--lod-only leaves them out: the base package's own sky stays).
            .. (lodOnly ? Array.Empty<string[]>() : [["--add-component-copies", Target, actor + ".sma_starfield_a_smc_15", "--yaw", "90,180,270,0:0:180,90:0:180,180:0:180,270:0:180"]]),
            // (templates only: the boxes' mesh layout and grey material; not placed, so not drawn)
            ["--copy-export", brooklyn, "maptemplates.sky.m_procedural_sky_daytime", Target],
            ["--copy-export", brooklyn, "maptemplates.sky.sm_skysphere", Target, "--cut", "bodysetup"],
            // The Bifrost deck as real geometry (the tiles' ice cover + rainbow bridge segments; ice_water_mat and its
            // textures are already in the stock level): meshes and the rainbow material copied in, the tiles' 11
            // placements recreated 2 under the real ones, drawn beyond 3500.
            ["--copy-export", bridgeB, "asgard.instance.rainbowbridgeicecover_polysurface4", Target, "--cut", "bodysetup"],
            ["--copy-export", bridgeA, "asgard.instance.rainbowbridgeicecover_polysurface5", Target, "--cut", "bodysetup"],
            ["--copy-export", lib, "asgard.rainbowbridge", Target, "--cut", "bodysetup"],
            ["--copy-export", lib, "asgard.mat_rainbowbridge_dead", Target],
            // Floors, platforms and the bridge: slabs from the cells' height maps, each 8 under its own surface and 192 thick,
            // drawn at every distance (groundboxes.py band -200..300, multi-level tolerance 64). Kurt's Blender edit, which
            // also removes the bridge-corridor slabs (the walkable strip is ~3x the deck's width; the real deck is copied in).
            .. (lodOnly ? OdinsPalaceBake(actor) : [["--add-cell-placeholders", Target, Data + "raster.fbx", "--always-fbx", Data + "ground.fbx", "--from-live",
                "--component-template", actor + ".staticmeshactor_smc_0", "--min-draw", "3500"]]),
            // With the baked LOD (--lod-only) the deck shows from 2500 like the LOD (2026-11-09: at 3500 the LOD's own bridge,
            // baked without a colour texture, showed between 2500 and 3500 and the bridge seemed to cut out).
            ["--add-mesh-instances", Target, bridgeA, deck, "--template", actor + ".staticmeshactor_smc_0", "--min-draw", lodOnly ? "2500" : "3500", "--z-offset", lodOnly ? "-4" : "-2", .. live],
            ["--add-mesh-instances", Target, bridgeB, deck, "--template", actor + ".staticmeshactor_smc_0", "--min-draw", lodOnly ? "2500" : "3500", "--z-offset", lodOnly ? "-4" : "-2", .. live],
        ];
    }

    /// <summary>
    /// Odin's Palace with Kurt's baked LOD (2026-11-09, --lod-only): his Blender bake of --export-placed (every piece of the
    /// 14 tiles, floors included; fences / trees / poles ... skipped) in one object with one 2048 atlas
    /// (ZoneData/OdinsPalace/lod_bundle1.fbx + lod_bundle1_diff.png). Built like Industry City's bundle: unlit copy of Hightown's
    /// emissive storefront material (full-G spec, flat normal), split per 2304 cell, MinDrawDistance 2500, pulled 6 in along
    /// the normals and dropped 4. It replaces the grey boxes and the floor slabs (the bake has the floors, and the slabs' tops
    /// would cover the shrunk LOD floor). The atlas keeps its full size and no mips: its islands are tiny, so a smaller copy
    /// or mips would bleed the black between them in.
    /// </summary>
    static string[][] OdinsPalaceBake(string actor) => Bake("odin", ["--component-template", actor + ".staticmeshactor_smc_0",
        // The Bifrost corridor (x 1664..2176, y -5632..128): the copied deck is the bridge there. The bake's own
        // bridge came out near black (the rainbow material has no colour texture to bake) and flickered under it.
        "--lod-exclude-box", "1600,-5700,2240,200",
        // LOD pieces on a 768 grid (2026-11-09, as Midtown): on the 2304 grid a building near a cell's corner hid early
        "--lod-cell", "768"]);

    /// <summary>
    /// Kurt's baked LOD (one object, one atlas: ZoneData/&lt;zone&gt;/lod_bundle1.fbx + lod_bundle1_diff.png) as Industry City's
    /// bundle is built: unlit copy of Hightown's emissive storefront material (full-G spec, flat normal), split per 2304 cell,
    /// MinDrawDistance 2500, pulled 6 in along the normals and dropped 4; every grey box left out (raster.fbx only feeds
    /// the step). The atlas keeps its size and has no mips (its islands are tiny: a smaller copy or mips bleed black in).
    /// <paramref name="prefix"/> names the new objects (prefix_lod_…); <paramref name="extra"/> goes on the cells step.
    /// </summary>
    static string[][] Bake(string prefix, string[] extra, string shrink = "6", string drop = "4", int parts = 1, string minDraw = "2500", string brightness = "0.6",
        string? groundDrop = null, string[]? beforeCells = null, int groundParts = 1, string? groundShrink = null)
    {
        const string template = "maptemplates.sky.t_udk_sky_cloudmask01";
        const string sf = "madripoor_hitown_buildings.madripoor_hitown_storefront_a";
        var steps = new List<string[]>
        {
            new[] { "--import-texture", Target, template, $"{prefix}_lod_emissive_spec", Data + "lod_emissive_spec.dds" },
            new[] { "--import-texture", Target, template, $"{prefix}_lod_flat_nrml", Data + "lod_flat_nrml.dds" },
        };
        // one atlas and material per baked part (lod_bundle<n>.fbx + lod_bundle<n>_diff.png)
        for (int n = 1; n <= parts; n++)
        {
            steps.Add(["--import-texture", Target, template, $"{prefix}_lod_bundle{n}_diff", Data + $"lod_bundle{n}_diff.png", "--format", "dxt1", "--scale", brightness, "--no-mips"]);
            steps.Add(["--copy-export", Library + "SCS__DailyRHighTownInvasionRegionL30_SF.upk", sf + "_mat", Target, "--cut", "physmaterial", "--rename", $"{prefix}_lod_bundle{n}_mat",
                "--replace-ref", sf + $"_diff=maptemplates.sky.{prefix}_lod_bundle{n}_diff", "--replace-ref", sf + $"_spec=maptemplates.sky.{prefix}_lod_emissive_spec",
                "--replace-ref", sf + $"_nrml=maptemplates.sky.{prefix}_lod_flat_nrml",
                // Reflection is off (spec B = 0): the storefront's chrome cubemap isn't needed.
                "--replace-ref", "madripoor_hitown_trffcbarrier.madripoor_hitown_chromecuberef_mat=none"]);
        }
        // groundDrop: part 1 (the ground) drawn at every distance, groundDrop under the real ground, instead of as a LOD
        if (beforeCells != null) steps.Add(beforeCells);
        steps.Add(["--add-cell-placeholders", Target, Data + "raster.fbx", "--from-live", "--min-draw", minDraw,
            .. (groundDrop != null ? new[] { "--textured-fbx", Data + "lod_bundle1.fbx", "--textured-material", $"madripoor_hitown_buildings.{prefix}_lod_bundle1_mat", "--textured-drop", groundDrop } : []),
            .. (groundDrop != null && groundShrink != null ? new[] { "--textured-shrink", groundShrink } : []),
            // groundParts > 1 (Midtown 2026-11-11): the ground in several atlases, bundles 1..groundParts all always drawn
            .. (groundDrop != null ? Enumerable.Range(2, groundParts - 1).SelectMany(n => new[] { "--textured-more", Data + $"lod_bundle{n}.fbx=madripoor_hitown_buildings.{prefix}_lod_bundle{n}_mat" }) : []),
            .. Enumerable.Range(groundDrop != null ? groundParts + 1 : 1, groundDrop != null ? parts - groundParts : parts).SelectMany(n => new[] { "--lod", Data + $"lod_bundle{n}.fbx=madripoor_hitown_buildings.{prefix}_lod_bundle{n}_mat" }),
            "--exclude-box", "-200000,-200000,200000,200000", "--lod-shrink", shrink, "--lod-drop", drop, .. extra]);
        return [.. steps];
    }

    /// <summary>
    /// Midtown free roam (MidTown_Static; 2026-11-09, LOD only): Kurt's bake of every placed piece of the 36 UES_Static tiles
    /// (world positions, no offset; ZoneData/Midtown/layout.txt), built on the Walls mod's MidTown_Static (its skyline and
    /// ceilings kept). The components copy the sky sphere's (the level's collection actor), as the grey boxes did.
    /// lod_bundle1.fbx is Kurt's edit of the built LOD (Stark Tower's opaque faces removed), already shrunk and dropped, so
    /// no second shrink or drop; split by triangle (big ground pieces). The atlas's unbaked (pure black) texels are filled
    /// mid grey (Kurt: grey, not a black void; the building parts' unbaked texels a muted sky blue, which reads as windows). Building parts baked
    /// at 4096 (their many small window pieces), textureless materials coloured by name before baking (metal steel), every glass material flat sky blue;
    /// Michael's skymod pieces and Stark Tower's own materials (avengerstower*: opaque in front of the real glass tower) left out of the parts. Two-sided: walls the bake left facing inward were missing in game.
    /// </summary>
    static List<string[]> MidtownSteps(Walls walls, bool lodOnly) =>
    [
        .. Bake("midtown", ["--lod-by-triangle"], parts: 4,
            // 3500 (Kurt 2026-11-09): at 2500 the LOD showed through the real buildings' glass while their tiles were still loaded
            minDraw: "3500"),
        // Michael's skyline buildings (the Walls mod's skymod textures, ZoneData/Midtown/skymod/*.png as exported from his
        // package) 25 % darker, so they fade into the background (Kurt 2026-11-09). His material has no brightness setting.
        .. MidtownSkyline.Select(t => new[] { "--replace-texture", Target, "skymod_midtown_static." + t, Data + "skymod/" + t + ".png", "--format", "dxt1", "--scale", "0.75" }),
    ];
    /// <summary>Midtown test B (Kurt's compromise, 2026-11-09): parts_full/bake_part.py with no reduction (no dissolve either),
    /// ground + one part per tile column X0..X5 (4096 atlases), window glass pixels made sky blue before baking.
    /// 2026-11-11 (Kurt): the building atlases' solid dark glass panes repainted with a sky-blue (top) to dark (bottom) gradient,
    /// "up" taken from the LOD meshes (publish/exports/MidTown_Static/window_fix: window_gradient.py), and one building
    /// (x 9431, y 2957) back to its original dark (restore_building.py). The unlit LOD can't show the real glass's
    /// angle-dependent reflection; the gradient stands in for it.</summary>
    static List<string[]> MidtownFullSteps(Walls walls, bool lodOnly) => MidtownFullSteps(walls, lodOnly, 7);
    static List<string[]> MidtownFullSteps(Walls walls, bool lodOnly, int parts, int groundParts = 1, string? groundShrink = null) =>
    [
        ["--import-texture", Target, "maptemplates.sky.t_udk_sky_cloudmask01", "midtown_lod_asphalt", Data + "asphalt.png", "--format", "dxt1", "--scale", "0.9"],
        // Kurt (Full Bake test): darker / warmer than the zone and a gap before the real tiles came in: 0.75 (and the atlases
        // made a little cooler), 2500
        // then: a drop-out gap at 2500 (a cell chunk's distance is to its centre) -> 1200; brighter 0.9; park trees (cypress,
        // leaves, bark) left out of the export
        // then (Kurt): the ground never disappears (always drawn, 8 under the real ground; it now takes every piece under 100 that
        // isn't a building), an asphalt-grey plane under the whole zone at -40 as a backstop, fountain water left out
        // then (Kurt): a building mid-right hid too early: LOD pieces on a 768 grid (--lod-cell), each centred near its building
        .. Bake("midtown", ["--lod-by-triangle", "--lod-cell", "768",
                "--water-boxes", "-8000,-9000,22000,23000", "--water-material", "madripoor_hitown_buildings.midtown_lod_asphalt_mat",
                "--water-z", "-40", "--water-grid", "4608", "--water-uv", "512"],
            parts: parts, minDraw: "1200", brightness: "0.9", groundDrop: "8", groundParts: groundParts, groundShrink: groundShrink,
            beforeCells: ["--copy-export", Library + "SCS__DailyRHighTownInvasionRegionL30_SF.upk", "madripoor_hitown_buildings.madripoor_hitown_storefront_a_mat", Target,
                "--cut", "physmaterial", "--rename", "midtown_lod_asphalt_mat",
                "--replace-ref", "madripoor_hitown_buildings.madripoor_hitown_storefront_a_diff=maptemplates.sky.midtown_lod_asphalt",
                "--replace-ref", "madripoor_hitown_buildings.madripoor_hitown_storefront_a_spec=maptemplates.sky.midtown_lod_emissive_spec",
                "--replace-ref", "madripoor_hitown_buildings.madripoor_hitown_storefront_a_nrml=maptemplates.sky.midtown_lod_flat_nrml",
                "--replace-ref", "madripoor_hitown_trffcbarrier.madripoor_hitown_chromecuberef_mat=none"]),
        .. MidtownSkyline.Select(t => new[] { "--replace-texture", Target, "skymod_midtown_static." + t, Data + "../Midtown/skymod/" + t + ".png", "--format", "dxt1", "--scale", "0.75" }),
    ];
    static readonly string[] MidtownSkyline = ["skymod_tex_bands_dark", "skymod_tex_brick_red", "skymod_tex_brick_tan", "skymod_tex_curtain_dark",
        "skymod_tex_deco_stone", "skymod_tex_glass_bands", "skymod_tex_glass_tower", "skymod_tex_grid_brown", "skymod_tex_prewar_ac", "skymod_tex_stone_arch"];

    /// <summary>The Hightown recipe (tuned in-game 2026-09-25; the reasons are in the ht_build.sh comments).</summary>
    static List<string[]> HightownSteps(Walls walls, bool lodOnly)
    {
        const string actor = "theworld.persistentlevel.staticmeshcollectionactor_0";
        const string lib = Library + "SCS__DailyRHighTownInvasionRegionL30_SF.upk";
        const string water = "madripoor_hitown_floortiles.madripoor_hitown_water";
        const string sf = "madripoor_hitown_buildings.madripoor_hitown_storefront_a";
        const string template = "maptemplates.sky.t_udk_sky_cloudmask01";
        // Tile coordinates -> in-game: the server centres the region (district + Jumbotron Overlook sub-area + sewers).
        const string shift = "5208,-15512";
        // City water (tile -15000,-15000..40000,41000 + shift) in 4 boxes around the GameCenter stairwell hole, at -80
        // (5 under the stock water at -75), plus the same boxes 5 lower at -85 (two translucent layers read as depth).
        string[] city = ["-9792,-30512,45208,-4440", "-9792,-3384,45208,25488", "-9792,-4440,7064,-3384", "8120,-4440,45208,-3384"];
        string boxes = string.Join(';', city.Concat(city.Select(b => b + ",-85")));

        var steps = new List<string[]>
        {
            new[] { "--copy-export", Library + "Brooklyn_Docks_A.upk", actor, Target, "--cut", "bodysetup" },
            new[] { "--add-level-actor", Target, actor },
        };
        if (walls == Walls.Facade)
        {
            steps.Add(["--import-texture", Target, template, "ht_facade_diff", Data + "ht_facade_diff.dds"]);
            steps.Add(["--import-texture", Target, template, "ht_facade_spec", Data + "ht_facade_spec.dds"]);
            steps.Add(["--import-texture", Target, template, "ht_flat_nrml", Data + "ht_flat_nrml.dds"]);
            steps.Add(["--copy-export", lib, sf + "_mat", Target, "--cut", "physmaterial", "--rename", "ht_facade_mat",
                "--replace-ref", sf + "_diff=maptemplates.sky.ht_facade_diff", "--replace-ref", sf + "_spec=maptemplates.sky.ht_facade_spec",
                "--replace-ref", sf + "_nrml=maptemplates.sky.ht_flat_nrml"]);
            // Ground: Kurt's baked plane (the real streets, alpha where there's no ground) with a copy of the library's
            // masked pier-edge material (envbaseshaderv3_masked, diffuse only) pointing at the baked texture (DXT1 with 1-bit alpha at the 1/3 mask clip: half DXT5's size, same cut-out; RGB x0.8
            // from Kurt's TopDown_D.png: "darken 20%", 2026-09-25).
            steps.Add(["--import-texture", Target, template, "ht_groundplane_diff", Data + "ht_groundplane_diff.dds"]);
            steps.Add(["--copy-export", lib, "brooklyn_pieredge_a.brooklyn_pieredge_mat", Target, "--cut", "physmaterial", "--rename", "ht_groundplane_mat",
                "--replace-ref", "brooklyn_pieredge_a.brooklyn_pieredge_diff_a=maptemplates.sky.ht_groundplane_diff"]);
        }
        var cells = new List<string> { "--add-cell-placeholders", Target, Data + "raster.fbx", "--from-live", "--offset", shift, "--min-draw", "3500" };
        // Textured: the baked ground plane at the old stock-water height (-75: under the real ground, over our water layers)
        // replaces the grey slabs, which would sit on top of it. Grey: the slabs.
        if (walls == Walls.Facade) cells.AddRange(["--wall-material", "madripoor_hitown_buildings.ht_facade_mat", "--wall-uv", "512",
            "--textured-fbx", Data + "groundplane.fbx", "--textured-material", "brooklyn_pieredge_a.ht_groundplane_mat", "--textured-z", "-75"]);
        else cells.AddRange(["--always-fbx", Data + "ground.fbx"]);
        if (lodOnly)
        {
            // --lod-only (2026-11-08, no sky changes): the copied actor only holds our pieces. Its sky dome is taken off its
            // list, and the water is a mesh and component of its own (in the sky sphere it would go with the dome).
            steps.Add(["--copy-export", lib, water, Target, "--cut", "physmaterial"]);
            steps.Add([.. cells, "--no-sky", "--water-boxes", boxes, "--water-material", water, "--water-z", "-80", "--water-grid", "4608"]);
            return steps;
        }
        steps.Add([.. cells]);
        steps.Add(["--copy-export", lib, water, Target, "--cut", "physmaterial"]);
        steps.Add(["--add-sky-placeholders", Target, "none", "--ground-z", "-80", "--ground-box", boxes, "--ground-material", water, "--ground-grid", "4608"]);
        steps.Add(["--set-property", Target, "maptemplates.sky.m_procedural_sky_daytime", "param:horizoncolor=0.0312,0.0857,0.1355",
            "param:zenithcolor=0,0.0312,0.0829", "param:rimcolor=0.5,0.3688,0.1631", "param:sun=5,3,1", "param:cloudbrightness=0.5"]);
        return steps;
    }

    public static Zone? Find(string name) => Zones.FirstOrDefault(z => z.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static bool GameRunning() => Process.GetProcessesByName("MarvelHeroesOmega").Length > 0;

    /// <param name="basePath">Start from this package instead of the stock one (another mod's copy of the level).</param>
    /// <param name="lodOnly">Only the LOD buildings, ground and water (no sky, no fog); the zone must support it.</param>
    /// <param name="outPath">Write the result to this file (for a mod), never into the game folder.</param>
    public static int Build(string zoneName, string gameFolder, Walls walls, bool dryRun, int lodSize = 512, string? basePath = null, bool lodOnly = false, string? outPath = null)
    {
        var zone = Find(zoneName);
        if (zone == null) { Console.WriteLine($"No zone recipe '{zoneName}'. Known: {string.Join(", ", Zones.Select(z => z.Name))}"); return 2; }
        string live = Path.Combine(gameFolder, zone.Package);
        if (!File.Exists(live)) { Console.WriteLine($"{live} not found."); return 2; }
        string dataDir = Path.Combine(AppContext.BaseDirectory, "ZoneData", zone.Name);
        if (!Directory.Exists(dataDir)) { Console.WriteLine($"Zone data folder missing: {dataDir}"); return 2; }
        if (lodOnly && !zone.LodOnly) { Console.WriteLine($"{zone.Name}'s recipe can't leave its sky out yet (--lod-only): Industry City, Odin's Palace and Hightown so far."); return 2; }
        if (basePath != null && !File.Exists(basePath)) { Console.WriteLine($"{basePath} not found."); return 2; }
        if (outPath != null && Path.GetFullPath(outPath).StartsWith(Path.GetFullPath(gameFolder), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("--out must be outside the game folder (it's for a mod)."); return 2; }
        if (!dryRun && outPath == null && GameRunning()) { Console.WriteLine("The game is running; close it first. Nothing written."); return 1; }

        string stock = basePath ?? (File.Exists(live + ".bak") ? live + ".bak" : live);
        string work = Path.Combine(Path.GetTempPath(), "MhoPackageModifier_zone", zone.Name);
        Directory.CreateDirectory(work);
        string workFile = Path.Combine(work, zone.Package);
        File.Copy(stock, workFile, overwrite: true);
        string outFile = Path.Combine(AppContext.BaseDirectory, "import_out", zone.Package);
        if (walls == Walls.Facade && !zone.HasFacade) { Console.WriteLine($"  {zone.Name} has no facade yet: grey walls."); walls = Walls.Grey; }
        var steps = zone.Steps(walls, lodOnly);
        Console.WriteLine($"Build zone {zone.Name} ({zone.Package}), walls {walls.ToString().ToLowerInvariant()}, LOD textures {(lodSize > 0 ? $"max {lodSize}" : "original size")}{(lodOnly ? ", LOD buildings / ground / water only" : "")}, from {(basePath != null ? basePath : stock == live ? "the live file (no .bak yet: stock)" : "its .bak (stock)")}{(outPath != null ? $", to {outPath}" : dryRun ? "  [dry run]" : "")}");

        string Resolve(string a) =>
            a == Target ? workFile
            : a == LodSize ? lodSize.ToString()
            : a.StartsWith(Data) ? Path.Combine(dataDir, a[Data.Length..])
            : a.StartsWith(Library) ? (File.Exists(Path.Combine(gameFolder, a[Library.Length..]) + ".bak") ? Path.Combine(gameFolder, a[Library.Length..]) + ".bak" : Path.Combine(gameFolder, a[Library.Length..]))
            : a;

        for (int n = 0; n < steps.Count; n++)
        {
            string[] args = [.. steps[n].Select(Resolve), "--dry-run"];
            Console.WriteLine();
            Console.WriteLine($"== step {n + 1}/{steps.Count}: {steps[n][0]}");
            if (File.Exists(outFile)) File.Delete(outFile);
            int rc = Program.Run(args, "");
            if (rc != 0 || !File.Exists(outFile)) { Console.WriteLine($"Step {n + 1} failed (exit {rc}); nothing written to the game."); return 1; }
            File.Copy(outFile, workFile, overwrite: true);
        }

        byte[] built = File.ReadAllBytes(workFile);
        Console.WriteLine();
        Console.WriteLine($"All {steps.Count} steps passed: {built.Length:N0} bytes.");
        if (outPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            string tmp = outPath + ".tmp";
            File.WriteAllBytes(tmp, built);
            if (!File.ReadAllBytes(tmp).AsSpan().SequenceEqual(built)) { File.Delete(tmp); Console.WriteLine("The written file didn't read back identical; nothing kept."); return 1; }
            File.Move(tmp, outPath, overwrite: true);
            Console.WriteLine($"  wrote {outPath} (game folder untouched)");
            return 0;
        }
        if (dryRun)
        {
            File.Copy(workFile, outFile, overwrite: true);
            Console.WriteLine($"  dry run: wrote {outFile} (game folder untouched)");
            return 0;
        }
        History.Label = $"build-zone {zone.Name} walls={walls.ToString().ToLowerInvariant()}";
        return MeshImport.WriteLive(live, built, bytes => bytes.AsSpan().SequenceEqual(built) ? [] : ["the file on disk isn't the built package"]) ? 0 : 1;
    }
}
