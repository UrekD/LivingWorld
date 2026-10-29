using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>A land-holding world object (settlement or site): the faction it flies, how far it claims, how it ranks.</summary>
    public struct TerritorySource
    {
        /// <summary>The <see cref="factionId"/> of a source without a faction (a quest site): it keeps its tile, tints nothing, touches no one.</summary>
        public const int NoFaction = -1;

        public WorldObject obj;
        public int objectId;
        public int tile;
        /// <summary>Null for a factionless source.</summary>
        public Faction faction;
        public int factionId;
        public Color color;
        public int reach;
        /// <summary>Tie-break between equally near claims: population (the colony outranks everyone).</summary>
        public float priority;
        public bool player;
    }

    /// <summary>Two buffers alternate: one holds the published snapshot while the next is built in the other.</summary>
    internal sealed class TerritoryBuffer
    {
        public readonly int[] owner;
        public readonly byte[] dist;
        public readonly List<int> claimed = new List<int>();
        /// <summary>Bumped whenever the contents are wiped for reuse: a snapshot built from an earlier generation is expired.</summary>
        public int generation;

        public TerritoryBuffer(int tiles)
        {
            owner = new int[tiles];
            dist = new byte[tiles];
        }

        /// <summary>Resets only what was claimed (the arrays are planet-sized).</summary>
        public void Clear()
        {
            generation++;
            foreach (int t in claimed) owner[t] = 0;
            claimed.Clear();
        }
    }

    /// <summary>
    /// One coherent picture of who holds which land, published by <see cref="Territory.Rebuild"/>. Read-only. The source
    /// table, per-source counts and edge counts are the snapshot's own, but the per-tile owner data lives in one of two
    /// planet-sized buffers that the second rebuild after this one wipes and reuses (a fresh array per rebuild would cost
    /// about a megabyte each, and history rebuilds daily). So a snapshot is a borrowed view: the buffer's generation is
    /// checked on every access, and once <see cref="Expired"/> the tile data reads as empty (no claimed tiles, no owner)
    /// instead of mixing the later rebuild's owners into this snapshot's source table. Ask <see cref="Territory.Current"/>
    /// again rather than keeping one.
    /// </summary>
    public sealed class TerritorySnapshot
    {
        public readonly World world;
        public readonly PlanetLayer surface;
        /// <summary>Changes when any tile's source or any source's faction or colour changes.</summary>
        public readonly int revision;
        public readonly TerritorySource[] sources;
        public readonly int[] tileCounts;
        public readonly int claimedCount;
        private readonly TerritoryBuffer buf;
        private readonly int bufGeneration;
        internal readonly Dictionary<long, int> sharedEdges;
        internal readonly Dictionary<int, int> indexByObject;
        internal readonly List<Faction> factions;
        internal readonly int[] playerObjectIds;
        internal readonly int[] playerTiles;

        internal TerritorySnapshot(World world, PlanetLayer surface, int revision, TerritorySource[] sources, int[] tileCounts, TerritoryBuffer buf,
            Dictionary<long, int> sharedEdges)
        {
            this.world = world;
            this.surface = surface;
            this.revision = revision;
            this.sources = sources;
            this.tileCounts = tileCounts;
            this.buf = buf;
            bufGeneration = buf.generation;
            this.sharedEdges = sharedEdges;
            claimedCount = buf.claimed.Count;
            indexByObject = new Dictionary<int, int>();
            factions = new List<Faction>();
            var players = new List<int>();
            var tiles = new List<int>();
            for (int i = 0; i < sources.Length; i++)
            {
                indexByObject[sources[i].objectId] = i;
                if (sources[i].faction != null && !factions.Contains(sources[i].faction)) factions.Add(sources[i].faction);
                if (!sources[i].player) continue;
                players.Add(sources[i].objectId);
                tiles.Add(sources[i].tile);
            }
            playerObjectIds = players.ToArray();
            playerTiles = tiles.ToArray();
        }

        /// <summary>The per-tile data was recycled by a later rebuild: <see cref="Claimed"/> and the owner lookups come back empty.</summary>
        public bool Expired => buf.generation != bufGeneration;

        /// <summary>The buffer's owner storage (index + 1, 0 for none) for hot loops inside the mod, null once <see cref="Expired"/>.</summary>
        internal int[] LiveOwners => Expired ? null : buf.owner;

        /// <summary>The buffer's claimed list for hot loops inside the mod; callers check <see cref="Expired"/> themselves.</summary>
        internal List<int> ClaimedList => buf.claimed;

        /// <summary>The tiles that have an owner, each once. A view, not the buffer: kept past expiry it reads as empty too.</summary>
        public IReadOnlyList<int> Claimed => claimedView ??= new ClaimedTiles(this);
        private ClaimedTiles claimedView;

        private sealed class ClaimedTiles : IReadOnlyList<int>
        {
            private readonly TerritorySnapshot snap;

            public ClaimedTiles(TerritorySnapshot snap) => this.snap = snap;

            public int Count => snap.Expired ? 0 : snap.buf.claimed.Count;

            public int this[int index] => snap.Expired ? throw new System.ArgumentOutOfRangeException(nameof(index)) : snap.buf.claimed[index];

            public IEnumerator<int> GetEnumerator()
            {
                List<int> list = snap.buf.claimed;
                for (int i = 0; i < list.Count && !snap.Expired; i++) yield return list[i];
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>Index into <see cref="sources"/> of the tile's owner, -1 for none, out of bounds (surface tile ids only) or expired.</summary>
        public int SourceIndexAt(int tileId) => !Expired && (uint)tileId < (uint)buf.owner.Length ? buf.owner[tileId] - 1 : -1;

        /// <summary>The owning faction's loadID, <see cref="TerritorySource.NoFaction"/> (-1) for no owner or a factionless one.</summary>
        public int FactionIdAt(int tileId)
        {
            int i = SourceIndexAt(tileId);
            return i < 0 ? -1 : sources[i].factionId;
        }
    }

    /// <summary>
    /// Territory: every settlement claims the passable land around it, the nearest claim wins. Derived data, never saved:
    /// rebuilt from the world at the start of each daily step (and once after load / world generation), published as one
    /// snapshot. Queries read the snapshot only and have no side effects. Main thread only.
    /// </summary>
    public static class Territory
    {
        /// <summary>Reach in tiles: NPC settlements by tier (hamlet 2 … city 5), the faction capital one more.</summary>
        public const int BaseReach = 2, MaxTierReach = 5, CapitalBonus = 1;

        private static TerritorySnapshot snapshot;
        private static readonly TerritoryBuffer[] buffers = new TerritoryBuffer[2];
        private static int front;
        private static int revisionCounter;
        // Breadth-first search state: visit stamps instead of clearing a planet-sized array per source.
        private static int[] visit;
        private static int stamp;
        private static int[] floodTiles = new int[256];
        private static int[] floodDist = new int[256];
        private static int floodCount;
        // The surface's passability and neighbour graph as plain arrays, built once per world (terrain does not change at runtime):
        // the search would otherwise go through a tile object, its biome and a native array on every visit.
        private static PlanetLayer gridSurface;
        private static bool[] passable;
        private static int[] nbStart; // tiles + 1 entries: tile t's neighbours are nbTile[nbStart[t]..nbStart[t + 1])
        private static int[] nbTile;
        // Rebuild scratch.
        private static int[] sourceFaction = new int[0];
        private static int[] sameAs = new int[0];
        // Source collection scratch.
        private static readonly List<TerritorySource> sourceBuf = new List<TerritorySource>();
        private static readonly List<Settlement> tmpSettlements = new List<Settlement>();
        private static readonly List<CompSettlementLife> tmpLives = new List<CompSettlementLife>();
        private static readonly Dictionary<int, int> capitals = new Dictionary<int, int>();
        // The colonies' live reach (not the daily snapshot), cached against the inputs it was worked out from.
        private static readonly HashSet<int> liveReach = new HashSet<int>();
        private static readonly List<int> liveKey = new List<int>(), liveKeyNow = new List<int>();
        private static World liveWorld;
        // PlanetTile.Layer throws for a layer id the grid does not have; the id itself is private.
        private static readonly AccessTools.StructFieldRef<PlanetTile, int> tileLayerId = AccessTools.StructFieldRefAccess<PlanetTile, int>("layerId");

        public static double LastRebuildMs, WorstRebuildMs;
        public static int Rebuilds;
        /// <summary>Founding checks and how many of them said no (otherwise valid sites inside someone else's land or a colony's reach).</summary>
        public static int StatChecks, StatRejected;

        /// <summary>A new world: nothing from the previous one may leak through statics.</summary>
        public static void Reset()
        {
            snapshot = null;
            buffers[0] = buffers[1] = null;
            front = 0;
            visit = null;
            stamp = 0;
            floodCount = 0;
            InvalidateGrid();
            sourceBuf.Clear();
            tmpSettlements.Clear();
            tmpLives.Clear();
            capitals.Clear();
            InvalidateLive();
            LastRebuildMs = WorstRebuildMs = 0d;
            Rebuilds = StatChecks = StatRejected = 0;
        }

        /// <summary>Borders are switched on for this world (the setting is the all-off one on a save prepared for removal).</summary>
        public static bool Active => LivingWorldComponent.Instance != null && LivingWorldMod.Settings.borders;

        /// <summary>A snapshot of the current world is published and borders are on. Unready: queries say "no territory", hooks keep the old behaviour.</summary>
        public static bool Ready => snapshot != null && snapshot.world == Find.World && LivingWorldMod.Settings.borders;

        /// <summary>The published snapshot when <see cref="Ready"/>, else null.</summary>
        public static TerritorySnapshot Current => Ready ? snapshot : null;

        public static int Revision => Ready ? snapshot.revision : -1;

        /// <summary>Forgets the picture (borders switched off, save prepared for removal), and the cached terrain with it, so
        /// switching borders off and on also picks up tiles changed in place (dev mode "set biome").</summary>
        public static void Clear()
        {
            InvalidateLive();
            InvalidateGrid();
            if (snapshot == null) return;
            snapshot = null;
            MarkDirty();
        }

        /// <summary>The colonies' live reach is worked out afresh at the next founding check.</summary>
        private static void InvalidateLive()
        {
            liveReach.Clear();
            liveKey.Clear();
            liveWorld = null;
        }

        /// <summary>
        /// The borders checkbox changed (settings window). Off: the picture and the live reservation are dropped at once. On: the
        /// current world is rebuilt now, not at the next daily step (no time has to pass; a world still loading builds when it is done).
        /// The overlay mode (<see cref="LivingWorldSettings.overlayMode"/>) is presentation only and plays no part.
        /// </summary>
        public static void Notify_BordersSettingChanged()
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || Verse.Current.ProgramState != ProgramState.Playing || comp.world != Find.World) return; // no world: the next one builds its own
            if (!LivingWorldMod.Settings.borders) Clear();
            else if (LongEventHandler.AnyEventNowOrWaiting) ScheduleInitial(comp, comp.world);
            else Rebuild(comp);
        }

        /// <summary>The border layer redraws everything when it is next visible (the toggle flipped, the picture was dropped).</summary>
        public static void MarkDirty() => Find.World?.grid?.Surface?.GetLayer<WorldDrawLayer_LWBorders>()?.Invalidate();

        /// <summary>The overlay is told about a newly published picture and redraws (only the regions that changed) if anything it shows did.</summary>
        private static void NotifyLayer(PlanetLayer surface, TerritorySnapshot next)
        {
            WorldDrawLayer_LWBorders layer = surface.GetLayer<WorldDrawLayer_LWBorders>();
            if (layer != null && layer.Notify_Published(next)) layer.SetDirty();
        }

        /// <summary>Rebuilds once the world is complete (after load or generation: references resolved, no long event running).</summary>
        public static void ScheduleInitial(LivingWorldComponent comp, World world)
        {
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                if (LivingWorldComponent.Instance != comp || Find.World != world) return;
                InvalidateGrid(); // the world is complete: whatever was read from its tiles before is not to be trusted
                Rebuild(comp);
                WorldOverlay.SeedShadow();
            });
        }

        /// <summary>The tile's layer, null for the invalid tile or a layer id the grid does not have.</summary>
        internal static PlanetLayer LayerOf(PlanetTile tile)
        {
            WorldGrid grid = Find.WorldGrid;
            if (!tile.Valid || grid == null) return null;
            int id = tileLayerId(ref tile);
            if (id < 0) return grid.Surface; // the game's own reading of a negative layer id
            return grid.PlanetLayers.TryGetValue(id, out PlanetLayer layer) ? layer : null;
        }

        private static bool OnSurface(PlanetTile tile, PlanetLayer surface) => surface != null && LayerOf(tile) == surface;

        internal static bool Passable(Tile t) => !t.PrimaryBiome.impassable && t.hilliness != Hilliness.Impassable;

        /// <summary>A colony: a player settlement on the surface whose map is a home.</summary>
        private static bool IsPlayerHome(Settlement s, PlanetLayer surface) =>
            !s.Destroyed && s.Faction != null && s.Faction.IsPlayer && OnSurface(s.Tile, surface) && s.HasMap && s.Map.IsPlayerHome;

        private static int PlayerReach(LivingWorldComponent comp) => comp == null ? 1 : 1 + Legends.Tier(Legends.Renown(comp));

        /// <summary>The cached passability and neighbour graph are rebuilt at the next use.</summary>
        private static void InvalidateGrid()
        {
            gridSurface = null;
            passable = null;
            nbStart = null;
            nbTile = null;
        }

        private static void EnsureGrid(PlanetLayer surface)
        {
            int n = surface.TilesCount;
            if (gridSurface == surface && passable != null && passable.Length == n) return;
            List<Tile> tiles = surface.Tiles;
            var offsets = surface.UnsafeTileIDToNeighbors_offsets;
            var values = surface.UnsafeTileIDToNeighbors_values;
            passable = new bool[n];
            for (int t = 0; t < n; t++) passable[t] = Passable(tiles[t]);
            nbStart = new int[n + 1];
            for (int t = 0; t < n; t++) nbStart[t] = offsets[t];
            nbStart[n] = values.Length;
            nbTile = new int[values.Length];
            for (int k = 0; k < nbTile.Length; k++) nbTile[k] = values[k].tileId;
            gridSurface = surface;
        }

        /// <summary>Fills <see cref="floodTiles"/>/<see cref="floodDist"/> (<see cref="floodCount"/> entries): passable land within reach steps of start (start itself always, whatever it stands on).</summary>
        private static void Flood(int start, int reach)
        {
            if (++stamp == int.MaxValue)
            {
                System.Array.Clear(visit, 0, visit.Length);
                stamp = 1;
            }
            int[] visited = visit, starts = nbStart, neighbours = nbTile, tiles = floodTiles, dists = floodDist;
            bool[] open = passable;
            int count = 1, mark = stamp;
            visited[start] = mark;
            tiles[0] = start;
            dists[0] = 0;
            for (int head = 0; head < count; head++)
            {
                int d = dists[head];
                if (d >= reach) break;
                int t = tiles[head];
                for (int k = starts[t], to = starts[t + 1]; k < to; k++)
                {
                    int n = neighbours[k];
                    if (visited[n] == mark) continue;
                    visited[n] = mark;
                    if (!open[n]) continue;
                    if (count == tiles.Length)
                    {
                        System.Array.Resize(ref floodTiles, count * 2);
                        System.Array.Resize(ref floodDist, count * 2);
                        tiles = floodTiles;
                        dists = floodDist;
                    }
                    tiles[count] = n;
                    dists[count] = d + 1;
                    count++;
                }
            }
            floodCount = count;
        }

        private static void EnsureBuffers(PlanetLayer surface)
        {
            int n = surface.TilesCount;
            if (buffers[0] == null || buffers[0].owner.Length != n)
            {
                buffers[0] = new TerritoryBuffer(n);
                buffers[1] = new TerritoryBuffer(n);
                front = 0;
                snapshot = null; // it pointed into the old buffers
            }
            EnsureSearch(surface);
        }

        /// <summary>What <see cref="Flood"/> needs: the visit stamps and the cached graph (not the territory buffers).</summary>
        private static void EnsureSearch(PlanetLayer surface)
        {
            int n = surface.TilesCount;
            if (visit == null || visit.Length != n)
            {
                visit = new int[n];
                stamp = 0;
            }
            EnsureGrid(surface);
        }

        /// <summary>
        /// Root-surface tiles within <paramref name="steps"/> neighbour steps of the tile over passable land (the tile itself first), into
        /// <paramref name="into"/> (cleared first) in breadth-first order. Deterministic: founding uses it instead of vanilla's FastTileFinder,
        /// whose parallel job keeps an arbitrary 50 of the matches in thread order, so the same world and seed found towns elsewhere each run.
        /// Empty for a tile off the root surface. No RNG.
        /// </summary>
        internal static void PassableWithin(PlanetTile center, int steps, List<PlanetTile> into)
        {
            into.Clear();
            PlanetLayer surface = Find.WorldGrid?.Surface;
            if (surface == null || !surface.IsRootSurface || surface.TilesCount == 0 || !OnSurface(center, surface)) return;
            EnsureSearch(surface);
            Flood(center.tileId, steps);
            for (int k = 0; k < floodCount; k++) into.Add(new PlanetTile(floodTiles[k], surface));
        }

        private static void CollectSources(LivingWorldComponent comp, PlanetLayer surface)
        {
            sourceBuf.Clear();
            tmpSettlements.Clear();
            tmpLives.Clear();
            capitals.Clear();
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                if (s.Destroyed || !OnSurface(s.Tile, surface)) continue;
                // Sim state decides the reach; a settlement without it (hidden or no faction, other mods) just keeps its own tile.
                CompSettlementLife life = s.Faction == null || s.Faction.IsPlayer ? null : s.GetComponent<CompSettlementLife>();
                if (life != null && !life.Active) life = null;
                int idx = tmpSettlements.Count;
                tmpSettlements.Add(s);
                tmpLives.Add(life);
                if (life == null) continue;
                int f = s.Faction.loadID;
                if (!capitals.TryGetValue(f, out int best)) capitals[f] = idx;
                else if (life.population > tmpLives[best].population || (life.population == tmpLives[best].population && s.ID < tmpSettlements[best].ID)) capitals[f] = idx;
            }
            int playerReach = -1; // the chronicle scan, once
            for (int i = 0; i < tmpSettlements.Count; i++)
            {
                Settlement s = tmpSettlements[i];
                CompSettlementLife life = tmpLives[i];
                int reach = 0;
                float priority = 0f;
                bool player = false;
                if (life != null)
                {
                    reach = Mathf.Clamp(BaseReach + life.Tier.order, BaseReach, MaxTierReach) + (capitals[s.Faction.loadID] == i ? CapitalBonus : 0);
                    priority = life.population;
                }
                else if (IsPlayerHome(s, surface))
                {
                    if (playerReach < 0) playerReach = PlayerReach(comp);
                    reach = playerReach;
                    priority = float.MaxValue;
                    player = true;
                }
                sourceBuf.Add(Source(s, reach, priority, player));
            }
            // Sites (camps, quest sites, some of them factionless) keep their tile too.
            foreach (Site site in Find.WorldObjects.Sites)
                if (!site.Destroyed && OnSurface(site.Tile, surface)) sourceBuf.Add(Source(site, 0, 0f, false));
        }

        private static TerritorySource Source(WorldObject o, int reach, float priority, bool player) => new TerritorySource
        {
            obj = o,
            objectId = o.ID,
            tile = o.Tile.tileId,
            faction = o.Faction,
            factionId = o.Faction?.loadID ?? TerritorySource.NoFaction,
            color = o.Faction?.Color ?? Color.clear,
            reach = reach,
            priority = priority,
            player = player,
        };

        /// <summary>
        /// Builds and publishes the snapshot from the world as it is now. No RNG, no seeding, no dependence on the component's
        /// derived indexes. With borders off (or the save prepared for removal) it clears instead.
        /// <paramref name="reverseSources"/> only exists for the permutation test: the result must not depend on source order.
        /// </summary>
        public static void Rebuild(LivingWorldComponent comp, bool reverseSources = false)
        {
            var sw = Stopwatch.StartNew();
            InvalidateLive();
            World world = Find.World;
            PlanetLayer surface = world?.grid?.Surface;
            if (comp == null || comp.preparedForRemoval || !LivingWorldMod.Settings.borders || surface == null || !surface.IsRootSurface || surface.TilesCount == 0)
            {
                Clear();
                return;
            }
            EnsureBuffers(surface);
            CollectSources(comp, surface);
            if (reverseSources) sourceBuf.Reverse();
            TerritorySnapshot prev = snapshot != null && snapshot.world == world && snapshot.surface == surface ? snapshot : null;
            TerritorySnapshot next = Compute(world, surface, sourceBuf.ToArray(), prev);
            snapshot = next;
            front = 1 - front;
            if (prev == null || next.revision != prev.revision) NotifyLayer(surface, next);
            LastRebuildMs = sw.Elapsed.TotalMilliseconds;
            if (LastRebuildMs > WorstRebuildMs) WorstRebuildMs = LastRebuildMs;
            Rebuilds++;
        }

        /// <summary>
        /// Works out ownership, counts and shared edges for the sources into the back buffer and returns them as a snapshot,
        /// without publishing it (the revision stays the previous one's when nothing visible changed). The next call recycles
        /// the back buffer, so an unpublished result expires with it. Only <see cref="Rebuild"/> publishes; tests feed it
        /// made-up sources (no world object needed: <see cref="TerritorySource.obj"/> is not read here).
        /// </summary>
        internal static TerritorySnapshot Compute(World world, PlanetLayer surface, TerritorySource[] sources, TerritorySnapshot prev)
        {
            EnsureBuffers(surface);
            TerritoryBuffer back = buffers[1 - front];
            back.Clear();
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i].reach <= 0)
                {
                    Claim(back, sources, sources[i].tile, 0, i);
                    continue;
                }
                Flood(sources[i].tile, sources[i].reach);
                int[] tiles = floodTiles, dists = floodDist;
                for (int k = 0, n = floodCount; k < n; k++) Claim(back, sources, tiles[k], dists[k], i);
            }
            if (sourceFaction.Length < sources.Length) sourceFaction = new int[sources.Length];
            for (int i = 0; i < sources.Length; i++) sourceFaction[i] = sources[i].factionId;
            var counts = new int[sources.Length];
            var edges = new Dictionary<long, int>();
            int[] owner = back.owner, factions = sourceFaction, starts = nbStart, neighbours = nbTile;
            List<int> claimed = back.claimed;
            long runKey = 0L;
            int run = 0; // consecutive edges of the same faction pair are added to the dictionary at once
            for (int c = 0; c < claimed.Count; c++)
            {
                int t = claimed[c];
                int si = owner[t] - 1;
                counts[si]++;
                int fa = factions[si];
                if (fa == TerritorySource.NoFaction) continue; // a factionless source has no border with anyone
                for (int k = starts[t], to = starts[t + 1]; k < to; k++)
                {
                    int n = neighbours[k];
                    if (n <= t || owner[n] == 0) continue; // each undirected edge once, from its lower tile
                    int fb = factions[owner[n] - 1];
                    if (fb == fa || fb == TerritorySource.NoFaction) continue;
                    long key = Diplomacy.Key(fa, fb);
                    if (run > 0 && key == runKey)
                    {
                        run++;
                        continue;
                    }
                    if (run > 0) AddEdges(edges, runKey, run);
                    runKey = key;
                    run = 1;
                }
            }
            if (run > 0) AddEdges(edges, runKey, run);
            int revision = prev != null && SameLand(prev, back, sources) ? prev.revision : ++revisionCounter;
            return new TerritorySnapshot(world, surface, revision, sources, counts, back, edges);
        }

        private static void AddEdges(Dictionary<long, int> edges, long key, int count)
        {
            edges.TryGetValue(key, out int e);
            edges[key] = e + count;
        }

        /// <summary>Gives the tile to the source if it is the best claim so far: nearest, then higher priority, then lower object id.</summary>
        private static void Claim(TerritoryBuffer buf, TerritorySource[] sources, int tile, int dist, int source)
        {
            int cur = buf.owner[tile];
            if (cur == 0)
            {
                buf.claimed.Add(tile);
            }
            else
            {
                int bd = buf.dist[tile];
                bool better;
                if (dist != bd) better = dist < bd;
                else if (sources[source].priority != sources[cur - 1].priority) better = sources[source].priority > sources[cur - 1].priority;
                else better = sources[source].objectId < sources[cur - 1].objectId;
                if (!better) return;
            }
            buf.owner[tile] = source + 1;
            buf.dist[tile] = (byte)dist;
        }

        /// <summary>Same tiles, same owners, same factions and colours: nothing the map shows or the hooks read has changed.</summary>
        private static bool SameLand(TerritorySnapshot prev, TerritoryBuffer next, TerritorySource[] sources)
        {
            if (prev.claimedCount != next.claimed.Count) return false;
            if (next.claimed.Count == 0) return true;
            int[] prevOwner = prev.LiveOwners;
            if (prevOwner == null) return false;
            // Per source of the new table: which source of the old one is the same object with the same faction and colour (index + 1), else -1.
            if (sameAs.Length < sources.Length) sameAs = new int[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                sameAs[i] = -1;
                if (!prev.indexByObject.TryGetValue(sources[i].objectId, out int pi)) continue;
                if (prev.sources[pi].factionId == sources[i].factionId && prev.sources[pi].color == sources[i].color) sameAs[i] = pi + 1;
            }
            int[] owner = next.owner, same = sameAs;
            List<int> claimed = next.claimed;
            for (int c = 0; c < claimed.Count; c++)
            {
                int t = claimed[c];
                if (prevOwner[t] != same[owner[t] - 1]) return false;
            }
            return true;
        }

        // ---- Queries (surface tiles only; anything else is "no territory") ----

        /// <summary>The snapshot's source index for the tile, -1 when there is none (not ready, other layer, out of bounds, unclaimed, owner gone).</summary>
        private static int OwnerIndex(PlanetTile tile)
        {
            TerritorySnapshot s = snapshot;
            if (s == null || s.world != Find.World || !LivingWorldMod.Settings.borders || LayerOf(tile) != s.surface) return -1;
            int i = s.SourceIndexAt(tile.tileId);
            if (i < 0) return -1;
            WorldObject o = s.sources[i].obj;
            return o.Destroyed || !o.Spawned ? -1 : i;
        }

        /// <summary>The settlement (or site) whose land this is.</summary>
        public static WorldObject OwnerOf(PlanetTile tile)
        {
            int i = OwnerIndex(tile);
            return i < 0 ? null : snapshot.sources[i].obj;
        }

        /// <summary>The faction whose land this is, as of the snapshot (the same picture <see cref="SharedBorder"/> counts).</summary>
        public static Faction FactionOf(PlanetTile tile)
        {
            int i = OwnerIndex(tile);
            return i < 0 ? null : snapshot.sources[i].faction;
        }

        /// <summary>How many tiles the source holds.</summary>
        public static int TileCount(WorldObject source)
        {
            TerritorySnapshot s = Current;
            return s != null && source != null && s.indexByObject.TryGetValue(source.ID, out int i) ? s.tileCounts[i] : 0;
        }

        /// <summary>Tile edges where the two factions' lands touch (symmetric; 0 for the same faction or no snapshot).</summary>
        public static int SharedBorder(Faction a, Faction b)
        {
            TerritorySnapshot s = Current;
            if (s == null || a == null || b == null || a == b) return 0;
            return s.sharedEdges.TryGetValue(Diplomacy.Key(a.loadID, b.loadID), out int n) ? n : 0;
        }

        /// <summary>Factions whose land touches the faction's.</summary>
        public static void Bordering(Faction f, List<Faction> into)
        {
            into.Clear();
            TerritorySnapshot s = Current;
            if (s == null) return;
            foreach (Faction o in s.factions)
                if (o != f && SharedBorder(f, o) > 0) into.Add(o);
        }

        // ---- Founding ----

        /// <summary>
        /// May this faction found a settlement here: unclaimed land or its own, and never inside a colony's reach. The colonies'
        /// reach is worked out live (the snapshot is a day old at most); the rest comes from the snapshot. Borders off: always.
        /// </summary>
        public static bool CanFound(Faction faction, PlanetTile tile)
        {
            if (!LivingWorldMod.Settings.borders) return true;
            StatChecks++;
            bool ok = !InColonyReach(tile);
            if (ok)
            {
                int i = OwnerIndex(tile);
                ok = i < 0 || (faction != null && snapshot.sources[i].factionId == faction.loadID);
            }
            if (!ok) StatRejected++;
            return ok;
        }

        private static bool InColonyReach(PlanetTile tile)
        {
            List<Map> maps = Find.Maps;
            World world = Find.World;
            PlanetLayer surface = world?.grid?.Surface;
            if (maps == null || maps.Count == 0 || surface == null || !surface.IsRootSurface || !OnSurface(tile, surface)) return false;
            // The key is everything the reach depends on: each eligible home's id and tile, then the effective reach (renown tier).
            // Checked on every call, so a home that moves or a renown threshold crossed mid-tick is seen by the next one.
            liveKeyNow.Clear();
            foreach (Map m in maps)
            {
                if (!(m.Parent is Settlement s) || !IsPlayerHome(s, surface)) continue;
                liveKeyNow.Add(s.ID);
                liveKeyNow.Add(s.Tile.tileId);
            }
            int homes = liveKeyNow.Count / 2;
            if (homes > 0) liveKeyNow.Add(PlayerReach(LivingWorldComponent.Instance));
            if (liveWorld != world || !SameKey(liveKey, liveKeyNow))
            {
                liveWorld = world;
                liveKey.Clear();
                liveKey.AddRange(liveKeyNow);
                liveReach.Clear();
                if (homes > 0) EnsureBuffers(surface);
                for (int i = 0; i < homes; i++)
                {
                    Flood(liveKeyNow[2 * i + 1], liveKeyNow[2 * homes]);
                    for (int k = 0; k < floodCount; k++) liveReach.Add(floodTiles[k]);
                }
            }
            return liveReach.Contains(tile.tileId);
        }

        private static bool SameKey(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>Colonies appeared, left or moved since the snapshot (checked hourly: the live view of the player's land).</summary>
        public static bool PlayerHomesChanged()
        {
            TerritorySnapshot s = snapshot;
            List<Map> maps = Find.Maps;
            if (s == null || maps == null || s.world != Find.World || !LivingWorldMod.Settings.borders) return false;
            int n = 0;
            foreach (Map m in maps)
            {
                if (!(m.Parent is Settlement ps) || !IsPlayerHome(ps, s.surface)) continue;
                n++;
                int i = System.Array.IndexOf(s.playerObjectIds, ps.ID);
                if (i < 0 || s.playerTiles[i] != ps.Tile.tileId) return true;
            }
            return n != s.playerObjectIds.Length;
        }

        /// <summary>One line for logs and metrics.</summary>
        public static string Summary()
        {
            TerritorySnapshot s = Current;
            return s == null ? "territory off" : $"territory ms={LastRebuildMs:F2} worstMs={WorstRebuildMs:F2} claimed={s.claimedCount} sources={s.sources.Length} revision={s.revision} " +
                                                  $"borderPairs={s.sharedEdges.Count} foundChecks={StatChecks} foundRejected={StatRejected}";
        }
    }
}
