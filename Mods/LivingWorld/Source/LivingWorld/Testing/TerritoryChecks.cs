using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Territory cases that build their own situation instead of hoping a random world has it: the bounded-corridor ranking,
    /// a retained snapshot, a factionless site, the settings checkbox, a renown threshold crossing and a moved colony between
    /// live founding checks, and settlers turned away from a ruin. Each one restores what it touched, and the founding
    /// counters (metrics) are put back, so a failure never leaves the test world changed.
    /// </summary>
    public static class TerritoryChecks
    {
        private const string Tag = "[LivingWorld:test] territory: ";
        private static readonly List<PlanetTile> neighbourBuf = new List<PlanetTile>();

        public static IEnumerable<string> Controlled()
        {
            var bad = new List<string>();
            LivingWorldComponent c = LivingWorldComponent.Instance;
            if (!LivingWorldMod.Settings.borders || Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_NOSIM") == "1") return bad;
            int checks = Territory.StatChecks, rejected = Territory.StatRejected, settled = Expansion.StatSettled, settleRejected = Expansion.StatSettleRejected;
            try
            {
                Guard(bad, "corridor", () => Corridor(bad));
                Guard(bad, "retained snapshot", () => RetainedSnapshot(bad, c));
                Guard(bad, "factionless site", () => FactionlessSite(bad, c));
                Guard(bad, "settings checkbox", () => Checkbox(bad, c));
                Guard(bad, "renown crossing", () => RenownCrossing(bad, c));
                Guard(bad, "moved colony", () => MovedColony(bad, c));
                Guard(bad, "rejected settlers", () => RejectedSettlers(bad, c));
            }
            finally
            {
                Territory.Rebuild(c);
                Territory.StatChecks = checks;
                Territory.StatRejected = rejected;
                Expansion.StatSettled = settled;
                Expansion.StatSettleRejected = settleRejected;
            }
            return bad;
        }

        /// <summary>One case failing (even by exception) must not stop the others.</summary>
        private static void Guard(List<string> bad, string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                bad.Add($"territory ({name}): {e.GetType().Name}: {e.Message}");
            }
        }

        // ---- Ranking ----

        /// <summary>
        /// The bounded-corridor counterexample on real tiles: hamlet A (reach 2) and capital B (reach 6) with B three steps from A, and
        /// a tile X three steps from A on the far side, six from B. X is outside A's reach and exactly inside B's, and B's way there runs
        /// through A's land: it must be B's, whatever order the sources are listed in. A has object id 0 (owner storage is index + 1).
        /// </summary>
        private static void Corridor(List<string> bad)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            if (!FindCorridor(surface, out int a, out int b, out int x))
            {
                Log.Message(Tag + "no open land for the corridor case");
                return;
            }
            TerritorySource hamlet = Made(0, a, 2, 5f), capital = Made(1, b, 6, 50f);
            foreach (bool reversed in new[] { false, true })
            {
                TerritorySource[] sources = reversed ? new[] { capital, hamlet } : new[] { hamlet, capital };
                TerritorySnapshot snap = Territory.Compute(Find.World, surface, sources, null);
                string order = reversed ? "capital first" : "hamlet first";
                if (OwnerId(snap, a) != 0) bad.Add($"territory (corridor, {order}): the hamlet (object id 0) lost its own tile {a} to {OwnerId(snap, a)}");
                if (OwnerId(snap, b) != 1) bad.Add($"territory (corridor, {order}): the capital lost its own tile {b} to {OwnerId(snap, b)}");
                if (OwnerId(snap, x) != 1) bad.Add($"territory (corridor, {order}): tile {x}, outside the hamlet's reach and inside the capital's, went to {OwnerId(snap, x)} instead of the capital");
            }
            Log.Message($"{Tag}corridor tiles hamlet {a}, capital {b}, far side {x}");
        }

        private static TerritorySource Made(int id, int tile, int reach, float priority) => new TerritorySource
        {
            objectId = id,
            tile = tile,
            factionId = 1000 + id,
            reach = reach,
            priority = priority,
        };

        /// <summary>The object id owning the tile in the snapshot, -1 for no one.</summary>
        private static int OwnerId(TerritorySnapshot snap, int tile)
        {
            int i = snap.SourceIndexAt(tile);
            return i < 0 ? -1 : snap.sources[i].objectId;
        }

        /// <summary>Tiles a, b, x on passable land with a–b 3, a–x 3 and b–x 6 steps (a straight line through a).</summary>
        private static bool FindCorridor(PlanetLayer surface, out int a, out int b, out int x)
        {
            a = b = x = -1;
            var fromA = new Dictionary<int, int>();
            var fromB = new Dictionary<int, int>();
            int tried = 0;
            for (int t = 0; t < surface.TilesCount && tried < 500; t++)
            {
                if (!Territory.Passable(surface.Tiles[t])) continue;
                tried++;
                Distances(surface, t, 3, fromA);
                List<int> ring = fromA.Where(kv => kv.Value == 3).Select(kv => kv.Key).ToList();
                foreach (int candidate in ring)
                {
                    Distances(surface, candidate, 6, fromB);
                    foreach (int other in ring)
                    {
                        if (!fromB.TryGetValue(other, out int d) || d != 6) continue;
                        a = t;
                        b = candidate;
                        x = other;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Passable steps from the start, up to max: the graph the territory floods.</summary>
        private static void Distances(PlanetLayer surface, int start, int max, Dictionary<int, int> into)
        {
            into.Clear();
            var queue = new List<int> { start };
            into[start] = 0;
            for (int head = 0; head < queue.Count; head++)
            {
                int t = queue[head], d = into[t];
                if (d >= max) continue;
                surface.GetTileNeighbors(t, neighbourBuf);
                foreach (PlanetTile n in neighbourBuf)
                {
                    if (into.ContainsKey(n.tileId) || !Territory.Passable(surface.Tiles[n.tileId])) continue;
                    into[n.tileId] = d + 1;
                    queue.Add(n.tileId);
                }
            }
        }

        // ---- Snapshots ----

        /// <summary>
        /// A snapshot kept across rebuilds (order permutation, a source added, a source removed): still coherent after the
        /// next one, expired (reads as empty, never throws or mixes in another rebuild's owners) once its buffer is recycled.
        /// </summary>
        private static void RetainedSnapshot(List<string> bad, LivingWorldComponent c)
        {
            Territory.Rebuild(c);
            TerritorySnapshot a = Territory.Current;
            if (a == null || a.claimedCount == 0)
            {
                Log.Message(Tag + "nothing claimed for the retained snapshot case");
                return;
            }
            var owners = new Dictionary<int, int>();
            foreach (int t in a.Claimed) owners[t] = OwnerId(a, t);
            int someTile = a.Claimed[0];
            IReadOnlyList<int> kept = a.Claimed; // a consumer that keeps the collection, not the snapshot
            Territory.Rebuild(c, reverseSources: true);
            TerritorySnapshot b = Territory.Current;
            if (a.Expired || Differences(a, owners) > 0) bad.Add("territory (retained snapshot): changed after one further rebuild");
            Site site = null;
            try
            {
                if (FindSiteTile(out _, out PlanetTile tile)) site = AddSite(tile);
                Territory.Rebuild(c); // third: recycles a's buffer (with an extra source if there was one)
                if (!a.Expired) bad.Add("territory (retained snapshot): not expired after two further rebuilds");
                int keptSeen = 0;
                foreach (int t in kept) keptSeen++;
                if (kept.Count != 0 || keptSeen != 0) bad.Add("territory (retained snapshot): a kept Claimed collection shows a later rebuild's tiles");
                if (a.Claimed.Count != 0 || a.SourceIndexAt(someTile) != -1 || a.FactionIdAt(someTile) != TerritorySource.NoFaction)
                    bad.Add("territory (retained snapshot): an expired snapshot still answers from recycled storage");
                if (b.Expired || Differences(b, owners) > 0) bad.Add("territory (retained snapshot): the reversed-order snapshot changed after one further rebuild");
            }
            finally
            {
                RemoveSite(site);
                Territory.Rebuild(c); // fourth, the source gone again: recycles b's buffer
            }
            if (!b.Expired || b.Claimed.Count != 0 || b.SourceIndexAt(someTile) != -1) bad.Add("territory (retained snapshot): not expired after the source was removed");
        }

        /// <summary>Tiles whose owner in the snapshot differs from the recorded object ids (or that are missing), plus a count mismatch.</summary>
        private static int Differences(TerritorySnapshot snap, Dictionary<int, int> owners)
        {
            int n = snap.Claimed.Count != owners.Count ? 1 : 0;
            foreach (int t in snap.Claimed)
            {
                int i = snap.SourceIndexAt(t);
                if (i < 0 || i >= snap.sources.Length || !owners.TryGetValue(t, out int id) || snap.sources[i].objectId != id) n++;
            }
            return n;
        }

        // ---- Sources ----

        /// <summary>A factionless site inside a town's land keeps its own tile: it owns it, flies no faction, borders no one, is not drawn.</summary>
        private static void FactionlessSite(List<string> bad, LivingWorldComponent c)
        {
            Territory.Rebuild(c);
            if (!FindSiteTile(out Settlement town, out PlanetTile tile))
            {
                Log.Message(Tag + "no free tile in a town's land for the factionless site case");
                return;
            }
            int townTiles = Territory.TileCount(town);
            Site site = AddSite(tile);
            try
            {
                Territory.Rebuild(c);
                TerritorySnapshot snap = Territory.Current;
                if (Territory.OwnerOf(tile) != site) bad.Add("territory (factionless site): the site does not keep its tile");
                if (Territory.FactionOf(tile) != null || snap.FactionIdAt(tile.tileId) != TerritorySource.NoFaction) bad.Add("territory (factionless site): its tile reads as a faction's");
                if (Territory.TileCount(site) != 1) bad.Add($"territory (factionless site): the site holds {Territory.TileCount(site)} tiles, not just its own");
                if (Territory.TileCount(town) != townTiles - 1) bad.Add($"territory (factionless site): the town holds {Territory.TileCount(town)} tiles, expected {townTiles - 1}");
                if (Territory.CanFound(town.Faction, tile)) bad.Add("territory (factionless site): a settlement may be founded on the site's tile");
                WorldChecks.BorderProblems(bad, snap, "factionless site");
                Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>()?.RegenerateNow();
            }
            finally
            {
                RemoveSite(site);
                Territory.Rebuild(c);
            }
            if (Territory.TileCount(town) != townTiles) bad.Add("territory (factionless site): the town's land did not come back after the site was removed");
        }

        /// <summary>A free tile inside an NPC settlement's land (not its own), and the settlement.</summary>
        private static bool FindSiteTile(out Settlement town, out PlanetTile tile)
        {
            town = null;
            tile = PlanetTile.Invalid;
            TerritorySnapshot snap = Territory.Current;
            if (snap == null) return false;
            PlanetLayer surface = Find.WorldGrid.Surface;
            foreach (int t in snap.Claimed)
            {
                if (!(snap.sources[snap.SourceIndexAt(t)].obj is Settlement s) || s.Faction == null || s.Faction.IsPlayer || t == s.Tile.tileId) continue;
                var candidate = new PlanetTile(t, surface);
                if (Find.WorldObjects.AnyWorldObjectAt(candidate)) continue;
                town = s;
                tile = candidate;
                return true;
            }
            return false;
        }

        /// <summary>A site without a faction (as quests make them), spawned on the tile. Nothing draws it before it is removed again.</summary>
        private static Site AddSite(PlanetTile tile)
        {
            var site = (Site)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Site);
            site.Tile = tile;
            site.SetFaction(null);
            Find.WorldObjects.Add(site);
            return site;
        }

        private static void RemoveSite(Site site)
        {
            if (site != null && !site.Destroyed) site.Destroy();
        }

        // ---- Setting ----

        /// <summary>
        /// The borders checkbox with no time passing: off drops the picture at once, on rebuilds from the world as it is now
        /// (a capture made while it was off shows), not from what was there before.
        /// </summary>
        private static void Checkbox(List<string> bad, LivingWorldComponent c)
        {
            Territory.Rebuild(c);
            TerritorySnapshot before = Territory.Current;
            if (before == null) return;
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool was = stored.borders;
            bool capture = WorldChecks.FindCapture(out Settlement victim, out Faction taker);
            Faction owner = victim?.Faction;
            int tile = before.Claimed[0];
            try
            {
                stored.borders = false;
                Territory.Notify_BordersSettingChanged();
                if (Territory.Current != null || Territory.Ready) bad.Add("territory (checkbox): switching borders off left the picture in place");
                if (Territory.OwnerOf(new PlanetTile(tile, Find.WorldGrid.Surface)) != null) bad.Add("territory (checkbox): an owner is still reported with borders off");
                if (capture) victim.SetFaction(taker); // the world moves on while they are off
                stored.borders = true;
                Territory.Notify_BordersSettingChanged();
                if (!Territory.Ready) bad.Add("territory (checkbox): switching borders on did not build the picture");
                else if (Territory.Current == before) bad.Add("territory (checkbox): switching borders on brought back the old snapshot");
                else if (capture && Territory.FactionOf(victim.Tile) != taker) bad.Add("territory (checkbox): the picture after switching on does not show a capture made while off");
            }
            finally
            {
                stored.borders = was;
                if (capture) victim.SetFaction(owner);
                Territory.Notify_BordersSettingChanged();
            }
        }

        // ---- Live reservation ----

        /// <summary>
        /// Two founding checks in one tick with the colony's renown crossing a tier between them: the ring the higher tier newly
        /// protects is open to the first and closed to the second, and open again once the renown is gone.
        /// </summary>
        private static void RenownCrossing(List<string> bad, LivingWorldComponent c)
        {
            Territory.Rebuild(c);
            if (!FindHome(out Settlement home)) return;
            PlanetLayer surface = Find.WorldGrid.Surface;
            int renown = Legends.Renown(c), tier = Legends.Tier(renown), reach = 1 + tier;
            int target = renown + 1;
            while (Legends.Tier(target) <= tier && target < renown + 1000) target++;
            ChronicleEventDef major = DefDatabase<ChronicleEventDef>.AllDefsListForReading.FirstOrDefault(d => d.importance == ChronicleImportance.Major);
            if (Legends.Tier(target) <= tier || major == null)
            {
                Log.Message(Tag + "no higher renown tier to cross");
                return;
            }
            var ring = new Dictionary<int, int>();
            Distances(surface, home.Tile.tileId, reach + 1, ring);
            PlanetTile probe = PlanetTile.Invalid;
            foreach (KeyValuePair<int, int> kv in ring)
            {
                var candidate = new PlanetTile(kv.Key, surface);
                if (kv.Value != reach + 1 || Territory.OwnerOf(candidate) != null || !Territory.CanFound(null, candidate)) continue;
                probe = candidate;
                break;
            }
            if (!probe.Valid)
            {
                Log.Message(Tag + "no unclaimed tile just past the colony's reach for the renown case");
                return;
            }
            int tick = Find.TickManager.TicksGame;
            var added = new List<ChronicleEntry>();
            try
            {
                for (int gain = target - renown; gain > 0; gain -= 3)
                {
                    var e = new ChronicleEntry { def = major, text = "", involvesPlayer = true };
                    c.chronicle.entries.Add(e);
                    added.Add(e);
                }
                if (Legends.Tier(Legends.Renown(c)) <= tier) bad.Add("territory (renown): the test did not raise the renown tier");
                if (Territory.CanFound(null, probe)) bad.Add($"territory (renown): tile {probe.tileId} is still open after the renown tier rose (cached reach {reach})");
            }
            finally
            {
                foreach (ChronicleEntry e in added) c.chronicle.entries.Remove(e);
            }
            if (!Territory.CanFound(null, probe)) bad.Add($"territory (renown): tile {probe.tileId} stays closed after the renown tier fell back");
            if (Find.TickManager.TicksGame != tick) bad.Add("territory (renown): the tick moved during the test");
        }

        /// <summary>A colony moved to another tile keeps its id and its map, and still counts as changed (the hourly check).</summary>
        private static void MovedColony(List<string> bad, LivingWorldComponent c)
        {
            Territory.Rebuild(c);
            if (!FindHome(out Settlement home)) return;
            PlanetLayer surface = Find.WorldGrid.Surface;
            PlanetTile origin = home.Tile;
            surface.GetTileNeighbors(origin, neighbourBuf);
            if (neighbourBuf.Count == 0) return;
            PlanetTile elsewhere = neighbourBuf[0];
            if (Territory.PlayerHomesChanged()) bad.Add("territory (moved colony): a change is reported right after a rebuild");
            try
            {
                home.Tile = elsewhere;
                if (!Territory.PlayerHomesChanged()) bad.Add("territory (moved colony): moving a colony (same id, same map count) is not noticed");
                Territory.Rebuild(c);
                if (Territory.PlayerHomesChanged()) bad.Add("territory (moved colony): still a change after a rebuild at the new tile");
            }
            finally
            {
                home.Tile = origin;
                Territory.Rebuild(c);
            }
        }

        /// <summary>The player colony the snapshot knows: a settlement on the surface whose map is a home.</summary>
        private static bool FindHome(out Settlement home)
        {
            home = null;
            TerritorySnapshot snap = Territory.Current;
            if (snap == null) return false;
            foreach (TerritorySource s in snap.sources)
            {
                if (!s.player || !(s.obj is Settlement settlement)) continue;
                home = settlement;
                return true;
            }
            Log.Message(Tag + "no player colony on the surface for the live reservation cases");
            return false;
        }

        // ---- Founding ----

        /// <summary>Settlers arriving at a ruin inside another faction's land are turned away, and the ruin is left as it was.</summary>
        private static void RejectedSettlers(List<string> bad, LivingWorldComponent c)
        {
            Territory.Rebuild(c);
            if (!FindSiteTile(out Settlement town, out PlanetTile tile))
            {
                Log.Message(Tag + "no free tile in a town's land for the rejected settlers case");
                return;
            }
            Faction other = null;
            foreach (Faction f in Territory.Current.factions)
                if (f != town.Faction && !f.IsPlayer && FactionState.IsSimulated(f)) other = f;
            if (other == null)
            {
                Log.Message(Tag + "no second faction for the rejected settlers case");
                return;
            }
            var ruin = (DestroyedSettlement)WorldObjectMaker.MakeWorldObject(tile.LayerDef.DestroyedSettlementWorldObjectDef);
            ruin.Tile = tile;
            ruin.SetFaction(town.Faction);
            Find.WorldObjects.Add(ruin);
            Settlement made = null;
            try
            {
                // Only call Settle when the rule already says no: a regressed rule must not found a town (ruler progress,
                // chronicle, letters) in the test world.
                if (Territory.CanFound(other, tile))
                {
                    bad.Add($"territory (rejected settlers): {other.Name} may found inside {town.Faction.Name}'s land");
                    return;
                }
                int rejected = Expansion.StatSettleRejected, settlements = Find.WorldObjects.Settlements.Count;
                made = Expansion.Settle(c, other, null, tile, 5f);
                if (made != null) bad.Add($"territory (rejected settlers): {other.Name} settled inside {town.Faction.Name}'s land");
                if (ruin.Destroyed || !ruin.Spawned || Find.WorldObjects.DestroyedSettlementAt(tile) != ruin) bad.Add("territory (rejected settlers): the ruin was destroyed by settlers who were turned away");
                if (Expansion.StatSettleRejected != rejected + 1) bad.Add("territory (rejected settlers): the rejection was not counted");
                if (Find.WorldObjects.Settlements.Count != settlements + (made != null ? 1 : 0)) bad.Add("territory (rejected settlers): the number of settlements changed");
            }
            finally
            {
                if (made != null && !made.Destroyed) made.Destroy();
                if (!ruin.Destroyed) ruin.Destroy();
            }
        }
    }
}
