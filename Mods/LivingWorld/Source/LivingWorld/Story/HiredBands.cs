using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>Where a hire stands. Saved as a byte: append only. Out of range on load reads as <see cref="Void"/>.</summary>
    public enum HirePhase : byte { EnvoyOut, Marching, Done, CalledOff, Refused, Moot, Void }

    /// <summary>
    /// The colony's deal with a faction to send a town's war band against a town of a common enemy: who, which towns, what was quoted and what the
    /// faction took, and how it ended. Refers to everything by id (names snapshotted). It holds one of the contract slots while it is
    /// <see cref="Active"/>: from the moment the envoy is sent until the band has fought or been called off.
    /// </summary>
    public class HireContract : IExposable
    {
        public const int Schema = 1;

        public int schemaVersion = Schema;
        public int id;
        public int hirerId = -1, enemyId = -1, sourceId = -1, targetId = -1;
        public string hirerName, enemyName, sourceName, targetName;
        /// <summary>The silver the envoy carried, and the strength it was quoted for.</summary>
        public int quotedPrice;
        public float quotedStrength;
        /// <summary>What the hirer took (the price, less what a smaller band was worth), and the strength sent.</summary>
        public int netFee;
        public float sentStrength;
        public HirePhase phase;
        public int envoyId = -1, bandId = -1;
        /// <summary>Each runs at most once: the refund, and the band's result.</summary>
        public bool refunded, completed;
        public int createdTick, endedTick = -1;
        /// <summary>How it ended, as a stable token: the result ("Razed"), or the <c>LW_Hire_Why_*</c> / <c>LW_Hire_Void_*</c> key of why it stopped. Translated when shown, never stored translated.</summary>
        public string outcome;

        public bool Active => phase == HirePhase.EnvoyOut || phase == HirePhase.Marching;

        public void ExposeData()
        {
            byte ph = (byte)phase;
            Scribe_Values.Look(ref schemaVersion, "hc_schema", Schema);
            Scribe_Values.Look(ref id, "hc_id");
            Scribe_Values.Look(ref hirerId, "hc_hirer", -1);
            Scribe_Values.Look(ref enemyId, "hc_enemy", -1);
            Scribe_Values.Look(ref sourceId, "hc_source", -1);
            Scribe_Values.Look(ref targetId, "hc_target", -1);
            Scribe_Values.Look(ref hirerName, "hc_hirerName");
            Scribe_Values.Look(ref enemyName, "hc_enemyName");
            Scribe_Values.Look(ref sourceName, "hc_sourceName");
            Scribe_Values.Look(ref targetName, "hc_targetName");
            Scribe_Values.Look(ref quotedPrice, "hc_price");
            Scribe_Values.Look(ref quotedStrength, "hc_strength");
            Scribe_Values.Look(ref netFee, "hc_netFee");
            Scribe_Values.Look(ref sentStrength, "hc_sent");
            Scribe_Values.Look(ref ph, "hc_phase");
            Scribe_Values.Look(ref envoyId, "hc_envoy", -1);
            Scribe_Values.Look(ref bandId, "hc_band", -1);
            Scribe_Values.Look(ref refunded, "hc_refunded");
            Scribe_Values.Look(ref completed, "hc_completed");
            Scribe_Values.Look(ref createdTick, "hc_created");
            Scribe_Values.Look(ref endedTick, "hc_ended", -1);
            Scribe_Values.Look(ref outcome, "hc_outcome");
            if (Scribe.mode == LoadSaveMode.LoadingVars) phase = HiredBands.ReadPhase(ph);
        }
    }

    /// <summary>What the colony could hire a faction to do: its band from one town against one town of a common enemy, at a price.</summary>
    public struct HireOffer
    {
        public Faction hirer, enemy;
        public Settlement source, target;
        /// <summary>The strength quoted (half the source's garrison), the odds it beats the target's defence (0..1), and the chance the hirer agrees.</summary>
        public float strength, odds, acceptChance;
        public int price, etaTicks;
        /// <summary>Why this cannot be sent now (null: it can).</summary>
        public string cannot;
    }

    /// <summary>
    /// Hiring a war band. The colony pays a faction that is not its enemy, and is already at war with the target's faction, to send one of its towns'
    /// bands against a town of that enemy. An envoy carries the silver to the source town; the answer (and the price kept) is decided there, and the
    /// band marches as an ordinary war band of the hirer, with the contract on it. It is paid for the attempt: the fee is refunded by half, once, only
    /// when the band is called off before it fights. A contract ends in exactly one place (<see cref="Complete"/>, <see cref="CallOff"/>, or a void).
    /// </summary>
    public static class HiredBands
    {
        /// <summary>Contracts to keep for the history, at most, and for how long.</summary>
        private const int KeepEnded = 20;
        private static bool phaseWarned;

        public static int StatHires, StatResults, StatRefunds;
        /// <summary>Tests: how far from the target a source town may be (tiles; null: the war bands' own range).</summary>
        internal static float? TestRange;
        /// <summary>Tests: no colony on the surface to refund to.</summary>
        internal static bool TestNoColony;

        public static float Range => TestRange ?? Military.MaxRaidRange;

        public static bool Enabled => WhyOff() == null;

        /// <summary>Which switch keeps the colony from hiring, in words (null: none). The option needs diplomacy, parties and warfare as well.</summary>
        public static string WhyOff()
        {
            LivingWorldSettings s = LivingWorldMod.Settings;
            if (!s.hireWarBands) return "LW_Hire_Off_Setting".Translate();
            if (!s.simulateDiplomacy) return "LW_Hire_Off_Diplomacy".Translate();
            if (!s.worldParties) return "LW_Hire_Off_Parties".Translate();
            if (!s.simulateWarfare) return "LW_Hire_Off_Warfare".Translate();
            return null;
        }

        internal static HirePhase ReadPhase(byte raw)
        {
            if (raw <= (byte)HirePhase.Void) return (HirePhase)raw;
            if (!phaseWarned) LWLog.Warning($"hire contract with unknown phase {raw}: read as void");
            phaseWarned = true;
            return HirePhase.Void;
        }

        // ---- Contracts -------------------------------------------------------------------------------------------

        public static HireContract ById(LivingWorldComponent comp, int id)
        {
            if (id < 0 || comp == null) return null;
            foreach (HireContract c in comp.hires)
                if (c.id == id) return c;
            return null;
        }

        /// <summary>The marching contract this war band carries (null for any other band).</summary>
        internal static HireContract ContractOf(WorldParty band)
        {
            if (band == null || band.recordId < 0) return null;
            HireContract c = ById(LivingWorldComponent.Instance, band.recordId);
            return c != null && c.phase == HirePhase.Marching && c.bandId == band.ID ? c : null;
        }

        public static int ActiveCount(LivingWorldComponent comp, int hirerId = -1)
        {
            int n = 0;
            foreach (HireContract c in comp.hires)
                if (c.Active && (hirerId < 0 || c.hirerId == hirerId)) n++;
            return n;
        }

        private static void End(HireContract c, HirePhase to, string outcome)
        {
            c.phase = to;
            c.endedTick = SimClock.Now;
            c.outcome = outcome;
        }

        /// <summary>Contracts are kept a year after they end, and no more than twenty ended ones (the oldest go first).</summary>
        internal static void Prune(LivingWorldComponent comp)
        {
            if (comp.hires.Count == 0) return;
            int now = SimClock.Now;
            comp.hires.RemoveAll(c => !c.Active && c.endedTick != -1 && now - c.endedTick > GenDate.TicksPerYear);
            while (true)
            {
                int ended = 0;
                HireContract oldest = null;
                foreach (HireContract c in comp.hires)
                {
                    if (c.Active) continue;
                    ended++;
                    if (oldest == null || c.endedTick < oldest.endedTick) oldest = c;
                }
                if (ended <= KeepEnded) return;
                comp.hires.Remove(oldest);
            }
        }

        // ---- Offers ----------------------------------------------------------------------------------------------

        /// <summary>
        /// What the colony could hire this faction to do, one offer per enemy of the colony that the faction also fights, computed when a dialog opens
        /// (never per frame). With a <paramref name="fixedTarget"/>: exactly one offer, against that town, with the reason when it cannot be made.
        /// </summary>
        public static List<HireOffer> Offers(Faction hirer, Settlement fixedTarget = null)
        {
            var list = new List<HireOffer>();
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || hirer == null) return list;
            if (fixedTarget != null)
            {
                if (fixedTarget.Faction != null && !fixedTarget.Faction.IsPlayer) list.Add(Build(comp, hirer, fixedTarget.Faction, fixedTarget, null));
                return list;
            }
            // The dialog lists nothing for a feature that is switched off (the other switches are listed, with their reasons).
            if (!LivingWorldMod.Settings.hireWarBands || HirerProblem(comp, hirer) != null) return list;
            foreach (FactionState es in comp.factionStates)
            {
                Faction e = es.faction;
                if (e == null || e == hirer || e.IsPlayer || e.defeated || es.settlements.Count == 0 || !e.HostileTo(Faction.OfPlayer) || !e.HostileTo(hirer)) continue;
                list.Add(Build(comp, hirer, e, null, null));
            }
            return list;
        }

        /// <summary>The offers made against one town by every faction that is not the colony's enemy and fights that town's faction (with their reasons).</summary>
        internal static List<HireOffer> OffersAgainst(Settlement target)
        {
            var list = new List<HireOffer>();
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Faction e = target?.Faction;
            if (comp == null || e == null || e.IsPlayer) return list;
            foreach (FactionState hs in comp.factionStates)
            {
                Faction h = hs.faction;
                if (h == null || h == e || !Diplomacy.Diplomatic(h) || h.HostileTo(Faction.OfPlayer) || !h.HostileTo(e)) continue;
                list.Add(Build(comp, h, e, target, null));
            }
            return list;
        }

        private static string HirerProblem(LivingWorldComponent comp, Faction hirer)
        {
            if (!Diplomacy.Diplomatic(hirer)) return "LW_Dipl_NoTalk".Translate();
            if (hirer.HostileTo(Faction.OfPlayer)) return "LW_Hire_Hostile".Translate(hirer.Name);
            if (hirer.PlayerGoodwill < Tuning.T.hireMinGoodwill) return "LW_Hire_Goodwill".Translate(hirer.Name, Tuning.T.hireMinGoodwill);
            FactionState hs = comp.StateFor(hirer);
            if (hs == null) return "LW_Dipl_NoTowns".Translate();
            foreach (CompSettlementLife s in hs.settlements)
                if (s.Active && s.Settlement.Faction == hirer) return null;
            return "LW_Dipl_NoTowns".Translate();
        }

        /// <summary>One offer, with every number the player is shown and the first reason it cannot be made.</summary>
        private static HireOffer Build(LivingWorldComponent comp, Faction hirer, Faction enemy, Settlement fixedTarget, Settlement fixedSource)
        {
            var o = new HireOffer { hirer = hirer, enemy = enemy };
            string why = WhyOff() ?? HirerProblem(comp, hirer);
            Map home = Neighbourhood.Home;
            if (why == null && (home == null || home.Parent is not Settlement)) why = "LW_Dipl_NoColony".Translate();
            if (why == null && (enemy == null || enemy.defeated || !enemy.HostileTo(Faction.OfPlayer))) why = "LW_Hire_EnemyNotHostile".Translate(enemy?.Name ?? "?");
            if (why == null && !enemy.HostileTo(hirer)) why = "LW_Hire_NotAtWar".Translate(hirer.Name, enemy.Name);
            CompSettlementLife target = null, source = null;
            if (why == null)
            {
                target = fixedTarget != null ? Life(fixedTarget) : NearestTarget(comp, enemy, home);
                if (target == null || !target.Active || target.Settlement.Faction != enemy) why = "LW_Hire_NoTarget".Translate(enemy.Name);
                else if (SettlementOps.Protected(target.Settlement)) why = "LW_Hire_TargetProtected".Translate(target.Settlement.Label);
                o.target = target?.Settlement;
            }
            if (why == null)
            {
                source = BestSource(comp, hirer, target, home, fixedSource);
                if (source == null) why = "LW_Hire_NoSource".Translate(hirer.Name, target.Settlement.Label, Mathf.RoundToInt(Range));
                o.source = source?.Settlement;
            }
            if (why == null)
            {
                FactionState hs = comp.StateFor(hirer);
                o.strength = Tuning.T.hireShare * source.garrison;
                o.price = Price(hs, hirer, o.strength);
                o.odds = Odds(hirer, enemy, target, o.strength);
                o.acceptChance = AcceptChance(hs, hirer);
                o.etaTicks = EstimateTicks(LWPartyDefOf.LW_Envoy, home.Tile, source.Settlement.Tile) + EstimateTicks(LWPartyDefOf.LW_WarBand, source.Settlement.Tile, target.Settlement.Tile);
                if (ActiveCount(comp, hirer.loadID) >= Tuning.T.hireMaxPerHirer) why = "LW_Hire_CapHirer".Translate(hirer.Name);
                else if (ActiveCount(comp) >= Tuning.T.hireMaxActive) why = "LW_Hire_CapTotal".Translate(Tuning.T.hireMaxActive);
                else why = PlayerDiplomacy.CannotSend(hirer);
            }
            o.cannot = why;
            return o;
        }

        private static CompSettlementLife Life(Settlement s) => s != null && s.Spawned ? s.GetComponent<CompSettlementLife>() : null;

        /// <summary>The enemy's town nearest the colony that the world may touch (no map, no live quest).</summary>
        private static CompSettlementLife NearestTarget(LivingWorldComponent comp, Faction enemy, Map home)
        {
            FactionState es = comp.StateFor(enemy);
            CompSettlementLife best = null;
            float bestD = float.MaxValue;
            if (es == null) return null;
            foreach (CompSettlementLife t in es.settlements)
            {
                if (!t.Active || t.Settlement.Faction != enemy || t.Settlement.Tile.Layer != home.Tile.Layer || SettlementOps.Protected(t.Settlement)) continue;
                float d = Find.WorldGrid.ApproxDistanceInTiles(home.Tile, t.Settlement.Tile);
                if (d < bestD)
                {
                    bestD = d;
                    best = t;
                }
            }
            return best;
        }

        /// <summary>The hirer's town with the most men that has 120 to send, within range of the target, and that the envoy and the band can reach.</summary>
        private static CompSettlementLife BestSource(LivingWorldComponent comp, Faction hirer, CompSettlementLife target, Map home, Settlement only)
        {
            FactionState hs = comp.StateFor(hirer);
            CompSettlementLife best = null;
            if (hs == null) return null;
            PlanetTile at = target.Settlement.Tile;
            foreach (CompSettlementLife s in hs.settlements)
            {
                if (only != null && s.Settlement != only) continue;
                if (!s.Active || s.Settlement.Faction != hirer || s.garrison < 120f || (best != null && s.garrison <= best.garrison)) continue;
                PlanetTile tile = s.Settlement.Tile;
                if (tile.Layer != at.Layer || tile.Layer != home.Tile.Layer || Find.WorldGrid.ApproxDistanceInTiles(tile, at) > Range) continue;
                if (!Find.WorldReachability.CanReach(tile, at) || !Find.WorldReachability.CanReach(home.Tile, tile)) continue;
                best = s;
            }
            return best;
        }

        /// <summary>The temper of the hirer's treasury and mood: tired, friendless factions charge more; allies and merchants less.</summary>
        private static int Price(FactionState hs, Faction hirer, float strength)
        {
            float trade = hs?.TraitFactor(t => t.tradeVolume) ?? 1f;
            float temper = Mathf.Clamp(1f + 0.6f * (hs?.warWeariness ?? 0f) - (hirer.PlayerRelationKind == FactionRelationKind.Ally ? 0.15f : 0f) - 0.2f * (trade - 1f), 0.7f, 1.6f);
            return Mathf.RoundToInt(Mathf.Max(Tuning.T.hireMinPrice, strength * Tuning.T.hirePricePerPoint * temper) / 50f) * 50;
        }

        /// <summary>The chance the hirer says yes when the envoy arrives, as things stand.</summary>
        public static float AcceptChance(FactionState hs, Faction hirer)
        {
            if (hs == null) return 0f;
            float trade = hs.TraitFactor(t => t.tradeVolume);
            float grudge = Diplomacy.GrudgeAgainst(hs, Faction.OfPlayer);
            return Mathf.Clamp(0.75f + 0.2f * (trade - 1f) - 0.3f * hs.warWeariness + (hirer.PlayerRelationKind == FactionRelationKind.Ally ? 0.1f : 0f) - grudge / 150f, 0.2f, 0.95f);
        }

        /// <summary>The chance a band of this strength beats the town's defence: the terms of <see cref="Battle.Resolve"/> before its dice.</summary>
        public static float Odds(Faction hirer, Faction enemy, CompSettlementLife target, float strength)
        {
            FactionState hs = LivingWorldComponent.Instance.StateFor(hirer);
            float att = strength * (hs?.TraitFactor(t => t.militarization) ?? 1f) * KnowHow.BattleFactor(hirer);
            float hills = target.Settlement.Tile.Tile?.hilliness >= Hilliness.LargeHills ? 1.2f : 1f;
            float def = (target.garrison + 10f) * target.Tier.fortification * hills * KnowHow.BattleFactor(enemy);
            return Battle.WinChance(att / Mathf.Max(0.01f, def));
        }

        public static string OddsLabel(float odds) =>
            (odds >= 0.65f ? "LW_Hire_OddsLikely" : odds >= 0.4f ? "LW_Hire_OddsEven" : "LW_Hire_OddsUnlikely").Translate(odds.ToStringPercent()).Resolve();

        private static int EstimateTicks(PartyKindDef kind, PlanetTile from, PlanetTile to) =>
            Mathf.RoundToInt(Find.WorldGrid.ApproxDistanceInTiles(from, to) * kind.ticksPerTile * 1.3f);

        // ---- Sending -----------------------------------------------------------------------------------------------

        /// <summary>
        /// The colony pays: the offer is checked again, the contract takes its slot, and an envoy sets out with the silver. Null: nothing was
        /// sent (the offer no longer holds, the silver is short, the envoy could not leave) and no slot is held.
        /// </summary>
        public static HireContract Send(HireOffer o)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || o.hirer == null || o.enemy == null || o.source == null || o.target == null) return null;
            HireOffer now = Build(comp, o.hirer, o.enemy, o.target, o.source);
            if (now.cannot != null)
            {
                Messages.Message(now.cannot, MessageTypeDefOf.RejectInput, historical: false);
                return null;
            }
            // What is paid and recorded is what stands now. If the quote moved since the player saw it, they are told and asked again.
            if (now.price != o.price)
            {
                Messages.Message("LW_Hire_QuoteChanged".Translate(o.hirer.Name, o.price, now.price), MessageTypeDefOf.RejectInput, historical: false);
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(ConfirmText(now), () => Send(now)));
                return null;
            }
            Map home = Neighbourhood.Home;
            if (Demands.ColonySilver(home) < now.price)
            {
                Messages.Message("LW_Dipl_NotEnoughSilver".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return null;
            }
            var c = new HireContract
            {
                id = comp.NextId(),
                hirerId = o.hirer.loadID, enemyId = o.enemy.loadID, sourceId = o.source.ID, targetId = o.target.ID,
                hirerName = o.hirer.Name, enemyName = o.enemy.Name, sourceName = o.source.Label, targetName = o.target.Label,
                quotedPrice = now.price, quotedStrength = now.strength,
                phase = HirePhase.EnvoyOut,
                createdTick = SimClock.Now,
            };
            // The slot is taken before the envoy leaves, and given back if it cannot.
            comp.hires.Add(c);
            WorldParty envoy = PlayerDiplomacy.Send(o.hirer, EnvoyMission.PlayerHireBand, now.price, o.enemy, o.source, c.id);
            if (envoy == null)
            {
                comp.hires.Remove(c);
                return null;
            }
            c.envoyId = envoy.ID;
            StatHires++;
            return c;
        }

        // ---- The answer ----------------------------------------------------------------------------------------------

        /// <summary>Why the contract no longer holds, as a <c>LW_Hire_Why_*</c> key (null: it does), checked when the envoy arrives.</summary>
        private static string Invalid(HireContract c, Faction hirer, Faction enemy, CompSettlementLife source, CompSettlementLife target)
        {
            if (!Enabled) return "LW_Hire_Why_Disabled";
            if (hirer == null || hirer.defeated || hirer.HostileTo(Faction.OfPlayer)) return "LW_Hire_Why_HirerHostile";
            if (enemy == null || enemy.defeated || !enemy.HostileTo(Faction.OfPlayer)) return "LW_Hire_Why_EnemyPeace";
            if (!hirer.HostileTo(enemy)) return "LW_Hire_Why_Peace";
            if (target == null) return "LW_Hire_Why_TargetGone";
            if (target.Settlement.Faction != enemy) return "LW_Hire_Why_TargetHands";
            if (!target.Active || SettlementOps.Protected(target.Settlement)) return "LW_Hire_Why_TargetProtected";
            if (source == null || !source.Active || source.Settlement.Faction != hirer || source.garrison < 0.6f * c.quotedStrength) return "LW_Hire_Why_SourceShort";
            return null;
        }

        /// <summary>A reason, in words, with the contract's names ({0} hirer, {1} enemy, {2} target, {3} source).</summary>
        internal static string Why(HireContract c, string key) => key.Translate(c.hirerName, c.enemyName, c.targetName, c.sourceName).Resolve();

        /// <summary>
        /// The envoy reached the source town. The contract is checked again (moot: the silver walks home), the hirer says yes or no (no: the silver
        /// walks home), or takes the fee and sends the band. The caller turns the envoy back; what stays on it is what the hirer did not take.
        /// </summary>
        internal static void Answer(LivingWorldComponent comp, WorldParty envoy, Faction hirer, FactionState fs)
        {
            HireContract c = ById(comp, envoy.recordId);
            if (c == null || c.phase != HirePhase.EnvoyOut || c.envoyId != envoy.ID)
            {
                LWLog.Warning($"hire envoy {envoy.ID} reached {envoy.Destination?.Label} with no contract waiting for it: it walks home");
                return;
            }
            var source = SettlementById(c.sourceId);
            var target = SettlementById(c.targetId);
            Faction enemy = comp.FactionById(c.enemyId);
            string moot = Invalid(c, hirer, enemy, source?.GetComponent<CompSettlementLife>(), target?.GetComponent<CompSettlementLife>());
            if (moot != null)
            {
                End(c, HirePhase.Moot, moot);
                Letter("LW_Hire_MootLabel".Translate(c.hirerName), "LW_Hire_MootText".Translate(Names(c, hirer, enemy, envoy.silver, ("WHY", Why(c, moot)))), LetterDefOf.NeutralEvent, new LookTargets(envoy), hirer);
                return;
            }
            if (!PlayerDiplomacy.Roll(AcceptChance(fs, hirer)))
            {
                End(c, HirePhase.Refused, "refused");
                Letter("LW_Hire_NoLabel".Translate(c.hirerName), "LW_Hire_NoText".Translate(Names(c, hirer, enemy, envoy.silver)), LetterDefOf.NegativeEvent, new LookTargets(envoy), hirer);
                return;
            }
            CompSettlementLife from = source.GetComponent<CompSettlementLife>(), to = target.GetComponent<CompSettlementLife>();
            // The hirer may have less to spare than it quoted: it sends what it can, takes a share of the fee to match, and the rest goes home.
            float sent = Mathf.Min(c.quotedStrength, 0.6f * from.garrison);
            c.sentStrength = sent;
            c.netFee = Mathf.Clamp(Mathf.RoundToInt(c.quotedPrice * sent / c.quotedStrength), 0, c.quotedPrice);
            envoy.silver = c.quotedPrice - c.netFee;
            fs.treasury += c.netFee;
            c.phase = HirePhase.Marching;
            BandLaunch launch = Military.LaunchBand(comp, fs, from, to, sent);
            if (launch.party != null)
            {
                launch.party.recordId = c.id;
                c.bandId = launch.party.ID;
                TaggedString text = "LW_Hire_YesText".Translate(Names(c, hirer, enemy, c.netFee, ("SENT", Mathf.RoundToInt(sent)), ("ETA", launch.party.TicksToArrive().ToStringTicksToPeriod())));
                if (c.netFee < c.quotedPrice) text += "\n\n" + "LW_Hire_YesPartial".Translate((c.quotedPrice - c.netFee).Named("BACK"));
                Letter("LW_Hire_YesLabel".Translate(c.hirerName), text, LetterDefOf.PositiveEvent, new LookTargets(launch.party), hirer);
                return;
            }
            // No room for a band on the map: the fight is over already.
            Complete(c, target, launch.result, c.quotedPrice - c.netFee);
        }

        private static Settlement SettlementById(int id) => LivingWorldComponent.WorldObjectById(id) as Settlement;

        // ---- Arrival, call-off, result ---------------------------------------------------------------------------------

        /// <summary>
        /// The band reached its target. True: it fights (as the hired band; or, if the hirer turned on the colony meanwhile, the contract is void and the
        /// band goes on as the hirer's own). False: called off, with the reason (a key), and whether half the fee comes back.
        /// </summary>
        internal static bool OnArrival(WorldParty band, HireContract c, out string callOffReasonKey, out bool refund)
        {
            callOffReasonKey = null;
            refund = false;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Faction hirer = comp.FactionById(c.hirerId), enemy = comp.FactionById(c.enemyId);
            if (hirer == null || hirer.HostileTo(Faction.OfPlayer))
            {
                Void(c, band, "LW_Hire_Void_Hostile");
                return true;
            }
            var ts = band.Destination as Settlement;
            CompSettlementLife life = Life(ts);
            string why = ts == null || life == null ? "LW_Hire_Why_TargetGone"
                : ts.Faction != enemy ? "LW_Hire_Why_TargetHands"
                : SettlementOps.Protected(ts) || !life.Active ? "LW_Hire_Why_TargetProtected"
                : !hirer.HostileTo(enemy) ? "LW_Hire_Why_Peace"
                : !enemy.HostileTo(Faction.OfPlayer) ? "LW_Hire_Why_EnemyPeace" : null;
            if (why == null) return true;
            callOffReasonKey = why;
            refund = true;
            return false;
        }

        /// <summary>
        /// The band is called off outside its arrival (the target is gone, the way is lost): a hirer that has turned on the colony pays no refund (the
        /// contract is void and the band is its own again), any other is called off with half the fee back.
        /// </summary>
        internal static void CallOffOrVoid(WorldParty band, HireContract c, string whyKey)
        {
            if (c == null || c.phase != HirePhase.Marching) return;
            Faction hirer = LivingWorldComponent.Instance.FactionById(c.hirerId);
            if (hirer == null || hirer.HostileTo(Faction.OfPlayer)) Void(c, band, "LW_Hire_Void_Hostile");
            else CallOff(c, whyKey, refund: true);
        }

        /// <summary>The band turns back without fighting: the contract ends (once), half the net fee comes back (once, if asked), the colony is told.</summary>
        internal static void CallOff(HireContract c, string reasonKey, bool refund)
        {
            if (c == null || c.phase != HirePhase.Marching) return;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            End(c, HirePhase.CalledOff, reasonKey);
            ClearBand(c);
            Faction hirer = comp.FactionById(c.hirerId);
            int back = refund ? Refund(comp, c, hirer) : 0;
            TaggedString text = "LW_Hire_CalledOffText".Translate(Names(c, hirer, comp.FactionById(c.enemyId), back, ("WHY", Why(c, reasonKey))));
            if (back > 0) text += " " + "LW_Hire_CalledOffRefund".Translate(back.Named("SILVER"));
            else if (refund && c.refunded) text += " " + "LW_Hire_CalledOffNoRefund".Translate();
            Letter("LW_Hire_CalledOffLabel".Translate(c.targetName), text, LetterDefOf.NeutralEvent, Look(LivingWorldComponent.WorldObjectById(c.targetId)), hirer);
        }

        /// <summary>
        /// Half the net fee, once: out of the hirer's treasury (down to nothing: the colony always gets the whole refund) and dropped by pod at the
        /// colony nearest the source (the target, if the source is gone). With both towns gone, or no colony on the surface, it is forfeited (the hirer
        /// keeps it), with a letter. Returns the silver sent.
        /// </summary>
        private static int Refund(LivingWorldComponent comp, HireContract c, Faction hirer)
        {
            if (c.refunded) return 0;
            c.refunded = true;
            int amount = Mathf.RoundToInt(Tuning.T.hireRefundShare * c.netFee);
            if (amount <= 0) return 0;
            var from = LivingWorldComponent.WorldObjectById(c.sourceId) as Settlement ?? LivingWorldComponent.WorldObjectById(c.targetId) as Settlement;
            Map map = TestNoColony || from == null ? null : Tributaries.ColonyFor(from).map;
            if (map == null)
            {
                // Nobody to send it to: the hirer keeps it.
                Letter("LW_Hire_RefundLostLabel".Translate(c.hirerName), "LW_Hire_RefundLostText".Translate(Names(c, hirer, comp.FactionById(c.enemyId), amount)), LetterDefOf.NegativeEvent, LookTargets.Invalid, hirer);
                return 0;
            }
            FactionState hs = comp.StateFor(hirer);
            if (hs != null) hs.treasury = Mathf.Max(0f, hs.treasury - amount);
            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = amount;
            DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(map), map, new List<Thing> { silver }, forbid: false, faction: hirer);
            StatRefunds++;
            return amount;
        }

        /// <summary>The hirer turned on the colony: the contract is void (no refund) and the band is the hirer's own again.</summary>
        private static void Void(HireContract c, WorldParty band, string reasonKey)
        {
            End(c, HirePhase.Void, reasonKey);
            if (band != null) band.recordId = -1;
            Letter("LW_Hire_VoidLabel".Translate(c.hirerName), reasonKey.Translate(c.hirerName, c.enemyName, c.targetName), LetterDefOf.NegativeEvent,
                Look(LivingWorldComponent.WorldObjectById(c.targetId)), LivingWorldComponent.Instance.FactionById(c.hirerId));
        }

        /// <summary>The band has fought (a party on arrival, or at once with no room for one): what it did is told and remembered, exactly once. <paramref name="back"/>: silver the envoy brings home because the hirer sent a smaller band (told with the result when there was no letter of the yes).</summary>
        internal static void Complete(HireContract c, Settlement target, BattleResult r, int back = 0)
        {
            if (c == null || c.completed || c.phase != HirePhase.Marching) return;
            c.completed = true;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Faction hirer = comp.FactionById(c.hirerId), enemy = comp.FactionById(c.enemyId);
            string result = r.razed ? "Razed" : r.captured ? "Captured" : r.attackerWon ? "Plundered" : "Repelled";
            End(c, HirePhase.Done, result);
            ClearBand(c);
            StatResults++;
            bool won = r.attackerWon;
            TaggedString text = ("LW_Hire_ResultText_" + result).Translate(Names(c, hirer, enemy, c.netFee, ("SENT", Mathf.RoundToInt(c.sentStrength))));
            if (back > 0) text += "\n\n" + "LW_Hire_YesPartial".Translate(back.Named("BACK"));
            Letter(("LW_Hire_ResultLabel_" + result).Translate(c.targetName), text, won ? LetterDefOf.PositiveEvent : LetterDefOf.NeutralEvent, Look(target), hirer);
            ChronicleEventDef def = LWChronicleDefOf.LW_PlayerHiredBand;
            string outcome = ("LW_Hire_Outcome_" + result).Translate().Resolve();
            comp.chronicle.Record(def, def.text.Formatted(Faction.OfPlayer.Name.Named("COLONY"), c.hirerName.Named("HIRER"), c.sourceName.Named("SOURCE"), c.targetName.Named("TARGET"),
                c.enemyName.Named("ENEMY"), outcome.Named("OUTCOME")).Resolve(), Faction.OfPlayer, hirer, wo: target != null && !target.Destroyed ? target : null, involvesPlayer: true, silent: true);
            if (enemy != null) Diplomacy.AddGrudge(comp.ExistingStateFor(enemy), Faction.OfPlayer, 15f, "LW_Grudge_HiredBand".Translate().Resolve());
            hirer?.TryAffectGoodwillWith(Faction.OfPlayer, 3, canSendMessage: false, canSendHostilityLetter: false, reason: LWHistoryDefOf.LW_HiredOurBand);
        }

        // ---- The parties' end ----------------------------------------------------------------------------------------

        /// <summary>
        /// A party was removed from the world. Its contract does not stay open: the envoy that never answered (destroyed) voids it, the envoy that
        /// came home without an answer (turned back early) makes it moot; the band that was destroyed marching voids it. An envoy coming home after
        /// a yes leaves the marching contract alone (matched by phase).
        /// </summary>
        internal static void Notify_PartyRemoved(WorldParty p)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp?.hires == null || comp.hires.Count == 0) return;
            foreach (HireContract c in comp.hires)
            {
                if (c.phase == HirePhase.EnvoyOut && c.envoyId == p.ID)
                {
                    bool home = p.phase == PartyPhase.Returning;
                    End(c, home ? HirePhase.Moot : HirePhase.Void, home ? "envoy home" : "envoy lost");
                    if (home) MootEarly(comp, c, "LW_Hire_Why_SourceLost");
                }
                else if (c.phase == HirePhase.Marching && c.bandId == p.ID)
                {
                    End(c, HirePhase.Void, "band lost");
                    if (!comp.preparedForRemoval && Current.ProgramState == ProgramState.Playing)
                        Messages.Message("LW_Hire_Void_BandLost".Translate(c.hirerName, c.enemyName, c.targetName), MessageTypeDefOf.NegativeEvent, historical: false);
                }
            }
        }

        /// <summary>The colony is told that a hire ended before its envoy could be answered (the envoy turned back, or never reached the town).</summary>
        private static void MootEarly(LivingWorldComponent comp, HireContract c, string whyKey)
        {
            if (comp.preparedForRemoval || Current.ProgramState != ProgramState.Playing) return;
            Faction hirer = comp.FactionById(c.hirerId);
            Letter("LW_Hire_MootLabel".Translate(c.hirerName), "LW_Hire_MootEarlyText".Translate(Names(c, hirer, comp.FactionById(c.enemyId), c.quotedPrice, ("WHY", Why(c, whyKey)))),
                LetterDefOf.NeutralEvent, Look(SettlementById(c.sourceId)), hirer);
        }

        /// <summary>The envoy turned back before anyone answered (the faction is gone): the hire is moot, and its slot is free while the envoy walks home.</summary>
        internal static void Notify_EnvoyTurnedBack(WorldParty envoy)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            HireContract c = ById(comp, envoy.recordId);
            if (c == null || c.phase != HirePhase.EnvoyOut || c.envoyId != envoy.ID) return;
            End(c, HirePhase.Moot, "faction gone");
            MootEarly(comp, c, "LW_Hire_Why_HirerGone");
        }

        /// <summary>The save is prepared for removing the mod: its parties are destroyed, so contracts under way end here.</summary>
        internal static void Notify_PreparedForRemoval(LivingWorldComponent comp)
        {
            foreach (HireContract c in comp.hires)
                if (c.Active) End(c, HirePhase.Void, "prepared for removal");
        }

        private static void ClearBand(HireContract c)
        {
            foreach (WorldParty p in WorldParty.All)
                if (p.ID == c.bandId && p.recordId == c.id) p.recordId = -1;
        }

        // ---- Letters and lines ---------------------------------------------------------------------------------------

        private static NamedArgument[] Names(HireContract c, Faction hirer, Faction enemy, float silver, params (string, object)[] more)
        {
            var args = new List<NamedArgument>
            {
                (hirer?.NameColored ?? (TaggedString)c.hirerName).Named("HIRER"), (enemy?.NameColored ?? (TaggedString)c.enemyName).Named("ENEMY"),
                c.sourceName.Named("SOURCE"), c.targetName.Named("TARGET"), Mathf.RoundToInt(silver).Named("SILVER"), c.quotedPrice.Named("PRICE"),
            };
            foreach ((string name, object value) in more) args.Add(value.Named(name));
            return args.ToArray();
        }

        private static LookTargets Look(WorldObject o) => o != null && !o.Destroyed && o.Spawned ? new LookTargets(o) : LookTargets.Invalid;

        private static void Letter(TaggedString label, TaggedString text, LetterDef def, LookTargets look, Faction hirer) =>
            Find.LetterStack.ReceiveLetter(label, text, def, look, hirer);

        /// <summary>The line a hired band adds to its inspect pane.</summary>
        internal static string InspectLine(WorldParty band)
        {
            HireContract c = ContractOf(band);
            return c == null ? null : "LW_Hire_Inspect".Translate(Tributaries.ColonyLabel(band.Origin), c.targetName).Resolve();
        }

        // ---- The gizmo ---------------------------------------------------------------------------------------------

        /// <summary>
        /// "Hire a war band" on a town hostile to the colony: a menu of what each faction at war with the town's people would do, then a confirmation.
        /// Cheap to build every frame (no offers are computed until it is clicked).
        /// </summary>
        internal static IEnumerable<Gizmo> Gizmos(CompSettlementLife life)
        {
            Settlement s = life.Settlement;
            if (!LivingWorldMod.Settings.hireWarBands || !life.Active || s.Faction == null || s.Faction.IsPlayer || !s.Faction.HostileTo(Faction.OfPlayer)) yield break;
            var cmd = new Command_Action
            {
                defaultLabel = "LW_Hire_Gizmo".Translate(),
                defaultDesc = "LW_Hire_GizmoDesc".Translate(s.Label),
                icon = TexCommand.Attack,
                action = () => OpenMenu(s),
            };
            string off = WhyOff();
            if (off != null) cmd.Disable(off);
            else if (SettlementOps.Protected(s)) cmd.Disable("LW_Hire_TargetProtected".Translate(s.Label));
            yield return cmd;
        }

        private static void OpenMenu(Settlement s)
        {
            List<HireOffer> offers = OffersAgainst(s);
            Map home = Neighbourhood.Home;
            int silver = home == null ? 0 : Demands.ColonySilver(home);
            var options = new List<FloatMenuOption>();
            foreach (HireOffer offer in offers)
            {
                HireOffer o = offer;
                string cannot = o.cannot ?? (silver < o.price ? "LW_Dipl_NotEnoughSilver".Translate().Resolve() : null);
                if (cannot != null)
                {
                    options.Add(new FloatMenuOption("LW_Hire_MenuCannot".Translate(o.hirer.Name, cannot), null));
                    continue;
                }
                options.Add(new FloatMenuOption("LW_Hire_MenuOption".Translate(o.hirer.Name, Mathf.RoundToInt(o.strength), o.source.Label, o.price),
                    () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(ConfirmText(o), () => Send(o)))));
            }
            if (options.Count == 0) options.Add(new FloatMenuOption("LW_Hire_MenuNone".Translate(s.Faction.Name), null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>What a hire says about itself, for the confirmation and the diplomacy dialog.</summary>
        internal static string Describe(HireOffer o) =>
            "LW_Hire_Details".Translate(o.hirer.Name, Mathf.RoundToInt(o.strength), o.source.Label, o.target.Label, o.enemy.Name, o.price, OddsLabel(o.odds),
                o.etaTicks.ToStringTicksToPeriod(), o.acceptChance.ToStringPercent()).Resolve();

        private static string ConfirmText(HireOffer o) => Describe(o) + "\n\n" + "LW_Hire_ConfirmRules".Translate(Mathf.RoundToInt(Tuning.T.hireRefundShare * 100f)).Resolve();
    }
}
