using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// NPC roads: trade between two towns leaves traffic, busy pairs get a route (one search a check, a few per check world-wide),
    /// the richer town builds it a link a day for silver from what its faction has above <see cref="LivingWorldTuningDef.roadTreasuryReserve"/>,
    /// and a road nobody keeps up decays tier by tier. All on the sim clock
    /// (<see cref="SimClock.Now"/>), so history builds and decays roads too.
    /// </summary>
    public static partial class Roads
    {
        private static readonly List<long> keyBuf = new List<long>();
        private static readonly List<RoadRoute> routeBuf = new List<RoadRoute>(), dueBuf = new List<RoadRoute>();
        private static readonly List<long> dueKeys = new List<long>();
        private static readonly List<KeyValuePair<long, float>> candidates = new List<KeyValuePair<long, float>>();
        private static readonly List<int> planTiles = new List<int>();

        // ---- Traffic ----

        /// <summary>
        /// Goods were delivered from the seller's town to the buyer: traffic on their pair, O(1). A null seller (goods from nowhere),
        /// the same town or a town outside the simulation counts for nothing. Counted whenever the economy is simulated, whether or not roads are on:
        /// the trade routes overlay draws it, and roads read it only when they are on.
        /// </summary>
        public static void NoteDelivery(Settlement seller, Settlement buyer, float amount)
        {
            if (LivingWorldComponent.Instance == null || !LivingWorldMod.Settings.simulateEconomy || seller == null || buyer == null || seller == buyer || amount <= 0f) return;
            RoadState st = State;
            if (st == null) return;
            CompSettlementLife from = seller.GetComponent<CompSettlementLife>(), to = buyer.GetComponent<CompSettlementLife>();
            if (from == null || to == null || !from.Active || !to.Active) return;
            long key = PairKey(seller.ID, buyer.ID);
            st.traffic.TryGetValue(key, out float v);
            st.traffic[key] = v + amount;
        }

        /// <summary>The town as an active simulated settlement, null if it is gone or out of the simulation.</summary>
        private static CompSettlementLife LiveTown(int objectId)
        {
            if (!(LivingWorldComponent.WorldObjectById(objectId) is Settlement s)) return null;
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            return life != null && life.Active ? life : null;
        }

        /// <summary>At war or otherwise hostile (a permanent enemy is hostile to everyone); the same faction never is.</summary>
        internal static bool Hostile(LivingWorldComponent comp, Faction a, Faction b) =>
            a != null && b != null && a != b && (a.HostileTo(b) || a.def.permanentEnemy || b.def.permanentEnemy || Diplomacy.ActiveWar(comp, a, b) != null);

        // ---- The daily step ----

        public static void DailyStep(LivingWorldComponent comp, float pace)
        {
            RoadState st = comp.roadState;
            if (st == null) return;
            LivingWorldTuningDef t = Tuning.T;
            // The traffic is counted and decays with the economy, roads or not (exactly once a day, here): the trade routes overlay reads it.
            if (LivingWorldMod.Settings.simulateEconomy) DecayTraffic(st, t);
            if (!Active) return;
            int now = SimClock.Now;
            if (LivingWorldMod.Settings.simulateEconomy)
            {
                StepRoutes(comp, st, now);
                if (now - st.lastCheckTick >= t.roadCheckDays * GenDate.TicksPerDay)
                {
                    st.lastCheckTick = now;
                    PlanNew(comp, st, t, now);
                }
            }
            if (now - st.lastDegradeTick >= t.roadDegradeDays * GenDate.TicksPerDay)
            {
                st.lastDegradeTick = now;
                Degrade(comp, st, t);
            }
            if (!SimClock.InHistory) FameDaily(comp, st, now);
        }

        /// <summary>
        /// One day's decay of every pair's traffic; a pair is forgotten when it falls under one or when one of its towns is gone. The
        /// lookup is the component's object index, which is rebuilt whenever a world object comes or goes (see
        /// <see cref="LivingWorldComponent.WorldObjectById"/>), so a miss means the town is really gone, also in history where the tick stands still.
        /// </summary>
        internal static void DecayTraffic(RoadState st, LivingWorldTuningDef t)
        {
            keyBuf.Clear();
            keyBuf.AddRange(st.traffic.Keys);
            float max = 0f;
            foreach (long key in keyBuf)
            {
                float v = st.traffic[key] * t.roadTrafficDecayPerDay;
                SplitKey(key, out int a, out int b);
                if (v < 1f || LivingWorldComponent.WorldObjectById(a) == null || LivingWorldComponent.WorldObjectById(b) == null)
                {
                    st.traffic.Remove(key);
                    continue;
                }
                st.traffic[key] = v;
                if (v > max) max = v;
            }
            StatMaxTraffic = max;
        }

        // ---- Planning ----

        private static void PlanNew(LivingWorldComponent comp, RoadState st, LivingWorldTuningDef t, int now)
        {
            candidates.Clear();
            foreach (KeyValuePair<long, float> kv in st.traffic)
                if (kv.Value >= t.roadTrafficThreshold) candidates.Add(kv);
            StatOverThreshold = candidates.Count;
            // Busiest first; the pair key breaks ties, so the order never depends on dictionary order.
            candidates.Sort((x, y) => x.Value != y.Value ? y.Value.CompareTo(x.Value) : x.Key.CompareTo(y.Key));
            keyBuf.Clear();
            foreach (KeyValuePair<long, int> kv in st.backoff)
                if (kv.Value <= now) keyBuf.Add(kv.Key);
            foreach (long key in keyBuf) st.backoff.Remove(key);
            int attempts = 0, open = st.OpenCount();
            foreach (KeyValuePair<long, float> c in candidates)
            {
                if (attempts >= t.roadPlanAttempts || open >= t.roadOpenWorldwide) break;
                if (st.RouteFor(c.Key) != null || st.backoff.ContainsKey(c.Key)) continue;
                SplitKey(c.Key, out int ida, out int idb);
                CompSettlementLife a = LiveTown(ida), b = LiveTown(idb);
                if (a == null || b == null || Hostile(comp, a.Settlement.Faction, b.Settlement.Faction)) continue;
                // The richer faction's town builds (the lower id on a tie); if that faction has its hands full, the other one may.
                float ta = comp.ExistingStateFor(a.Settlement.Faction)?.treasury ?? 0f, tb = comp.ExistingStateFor(b.Settlement.Faction)?.treasury ?? 0f;
                CompSettlementLife builder = ta >= tb ? a : b, other = builder == a ? b : a;
                if (st.OpenCount(builder.Settlement.Faction.loadID) >= t.roadOpenPerFaction)
                {
                    if (other.Settlement.Faction == builder.Settlement.Faction || st.OpenCount(other.Settlement.Faction.loadID) >= t.roadOpenPerFaction) continue;
                    CompSettlementLife swap = builder;
                    builder = other;
                    other = swap;
                }
                // A faction with nothing to spare cannot pay for a link: no use opening a route (and a search) that would only block.
                if ((comp.ExistingStateFor(builder.Settlement.Faction)?.treasury ?? 0f) < t.roadTreasuryReserve) continue;
                attempts++;
                StatPlanAttempts++;
                if (!PlanRoute(builder.Settlement.Tile, other.Settlement.Tile, t.roadNpcMaxEdges, planTiles, out RouteReject why))
                {
                    StatPlanFailures++;
                    StatRejects[(int)why]++;
                    st.backoff[c.Key] = now + t.roadPlanBackoffDays * GenDate.TicksPerDay;
                    continue;
                }
                CreateRoute(comp, st, builder, other, planTiles);
                open++;
            }
            if (attempts > StatMaxAttempts) StatMaxAttempts = attempts;
        }

        /// <summary>Opens a route for the planned tiles: the builder town's faction and tech decide the road.</summary>
        internal static RoadRoute CreateRoute(LivingWorldComponent comp, RoadState st, CompSettlementLife builder, CompSettlementLife other, List<int> tiles)
        {
            Faction bf = builder.Settlement.Faction;
            var route = new RoadRoute
            {
                id = comp.NextId(),
                factionId = bf.loadID,
                builderId = builder.Settlement.ID,
                otherId = other.Settlement.ID,
                tiles = new List<int>(tiles),
                def = (KnowHow.Effective(bf) <= TechLevel.Neolithic ? LWRoadDefOf.DirtRoad : LWRoadDefOf.StoneRoad).defName,
            };
            st.Add(route);
            StatRoutesOpened++;
            return route;
        }

        // ---- Building ----

        /// <summary>One day for one route and nothing else (tests: the other routes of a big world spend the same treasury and chronicle their own ends).</summary>
        internal static void StepOneRoute(LivingWorldComponent comp, RoadRoute r) => StepRoute(comp, comp.roadState, r, SimClock.Now);

        private static void StepRoutes(LivingWorldComponent comp, RoadState st, int now)
        {
            routeBuf.Clear();
            foreach (RoadRoute r in st.routes)
                if (r.Building) routeBuf.Add(r);
            foreach (RoadRoute r in routeBuf) StepRoute(comp, st, r, now);
        }

        /// <summary>
        /// One day of one open route: the builder town must still be an active town of the builder faction and not hostile to the
        /// other end, and the treasury must pay without dropping under the reserve. Links that are already as good are skipped free; one changed link a day.
        /// </summary>
        private static void StepRoute(LivingWorldComponent comp, RoadState st, RoadRoute r, int now)
        {
            CompSettlementLife builder = LiveTown(r.builderId), other = LiveTown(r.otherId);
            FactionState fs = builder == null ? null : comp.ExistingStateFor(builder.Settlement.Faction);
            if (builder == null || other == null || fs == null || builder.Settlement.Faction.loadID != r.factionId
                || Hostile(comp, builder.Settlement.Faction, other.Settlement.Faction))
            {
                Block(st, r, now);
                return;
            }
            RoadDef def = r.Def;
            bool laid = false;
            while (!laid && r.next < r.tiles.Count - 1)
            {
                int a = r.tiles[r.next], b = r.tiles[r.next + 1];
                EdgeNeed need = Need(a, b, def);
                if (need == EdgeNeed.Invalid)
                {
                    Block(st, r, now);
                    return;
                }
                if (need == EdgeNeed.Satisfied)
                {
                    r.next++;
                    continue;
                }
                float cost = NpcCost(def, a, b);
                if (fs.treasury - cost < Tuning.T.roadTreasuryReserve)
                {
                    Block(st, r, now);
                    return;
                }
                switch (LaySegment(a, b, def, r.id))
                {
                    case LayResult.Changed:
                        fs.treasury -= cost;
                        r.paid += cost;
                        r.changed++;
                        r.next++;
                        laid = true;
                        break;
                    case LayResult.AlreadySatisfied:
                        r.next++;
                        break;
                    default:
                        Block(st, r, now);
                        return;
                }
            }
            r.state = RouteState.Open;
            r.blockedSince = FactionState.Never;
            if (r.next >= r.tiles.Count - 1) Finish(comp, st, r, builder, other, now);
        }

        private static void Block(RoadState st, RoadRoute r, int now)
        {
            if (r.state != RouteState.Blocked || r.blockedSince == FactionState.Never)
            {
                r.state = RouteState.Blocked;
                r.blockedSince = now;
            }
            if (now - r.blockedSince < Tuning.T.roadBlockedDaysToAbandon * GenDate.TicksPerDay) return;
            r.state = RouteState.Abandoned;
            r.decaying = true;
            StatRoutesAbandoned++;
            st.backoff[r.PairKey] = now + Tuning.T.roadAbandonBackoffDays * GenDate.TicksPerDay;
            if (r.claimed.Count == 0) st.Drop(r);
        }

        private static void Finish(LivingWorldComponent comp, RoadState st, RoadRoute r, CompSettlementLife builder, CompSettlementLife other, int now)
        {
            r.state = RouteState.Finished;
            r.condition = 1f;
            StatRoutesFinished++;
            if (r.changed == 0)
            {
                // Everything was there already: nothing of ours to keep, and no use planning the same pair again soon.
                st.backoff[r.PairKey] = now + Tuning.T.roadAbandonBackoffDays * GenDate.TicksPerDay;
                st.Drop(r);
                return;
            }
            ChronicleEventDef def = LWChronicleDefOf.LW_RoadJoined;
            comp.chronicle.Record(def, def.text.Formatted(builder.Settlement.Label.Named("A"), other.Settlement.Label.Named("B"), r.Def.label.Named("ROAD")).Resolve(),
                builder.Settlement.Faction, other.Settlement.Faction, builder.Settlement);
        }

        // ---- Decay ----

        /// <summary>
        /// Every few weeks a road nobody keeps up wears: finished or abandoned routes whose end is gone or not friendly to the builder,
        /// and every abandoned one, lose condition; at nothing their links drop a tier (see <see cref="Downgrade"/>) and the condition
        /// starts over. A link shared by several routes that wear out in the same check drops once, whatever the number of claimants
        /// (<see cref="DecayDue"/>). A route that holds no link any more is forgotten.
        /// </summary>
        internal static void Degrade(LivingWorldComponent comp, RoadState st, LivingWorldTuningDef t)
        {
            routeBuf.Clear();
            foreach (RoadRoute r in st.routes)
                if (r.state == RouteState.Finished || r.state == RouteState.Abandoned) routeBuf.Add(r);
            // Everyone's verdict first: whether a link is still held up by another route depends on all of them.
            foreach (RoadRoute r in routeBuf) r.decaying = r.state == RouteState.Abandoned || Neglected(comp, r);
            dueBuf.Clear();
            foreach (RoadRoute r in routeBuf)
            {
                if (!r.decaying) continue;
                r.condition -= t.roadDegradeStep;
                if (r.condition > 0.001f) continue;
                r.condition = 1f;
                dueBuf.Add(r);
            }
            DecayDue(st, dueBuf);
            foreach (RoadRoute r in routeBuf)
                if (r.claimed.Count == 0) st.Drop(r);
        }

        /// <summary>
        /// The routes that are due to lower their links in this check: the union of their claimed links is worked through once, in key
        /// order (so the result depends on neither the order of the routes nor of their claims). A link some other route, the player or
        /// a route still being built keeps up is only let go of; the others drop one tier, once.
        /// </summary>
        internal static void DecayDue(RoadState st, List<RoadRoute> due)
        {
            dueKeys.Clear();
            foreach (RoadRoute r in due)
                foreach (long key in r.claimed)
                    if (!dueKeys.Contains(key)) dueKeys.Add(key);
            dueKeys.Sort();
            foreach (long key in dueKeys)
            {
                RoadEdge e = st.Edge(key);
                if (e == null)
                {
                    foreach (RoadRoute r in due) r.claimed.Remove(key);
                    continue;
                }
                if (HeldUp(st, e))
                {
                    foreach (RoadRoute r in due)
                        if (r.claimed.Remove(key)) e.claims.Remove(r.id); // somebody else maintains this link: these routes let go of it
                    continue;
                }
                Downgrade(e);
            }
        }

        private static bool Neglected(LivingWorldComponent comp, RoadRoute r)
        {
            CompSettlementLife a = LiveTown(r.builderId), b = LiveTown(r.otherId);
            Faction builder = comp.FactionById(r.factionId);
            return a == null || b == null || builder == null || Hostile(comp, builder, a.Settlement.Faction) || Hostile(comp, builder, b.Settlement.Faction);
        }

        /// <summary>A player laid it, or a route that is still being built or is kept up claims it (a route that is wearing out does not).</summary>
        private static bool HeldUp(RoadState st, RoadEdge e)
        {
            if (e.player) return true;
            foreach (int id in e.claims)
            {
                RoadRoute o = st.Route(id);
                if (o != null && (o.Building || (o.state == RouteState.Finished && !o.decaying))) return true;
            }
            return false;
        }
    }
}
