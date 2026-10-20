using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// The colony acts on the world: an envoy walks from the colony to a faction's nearest town carrying an offer of
    /// peace (with a gift), a call to arms against a common enemy, or a demand for tribute. The answer is decided on
    /// arrival from the faction's state (weariness, grudges, strength, temperament) and the envoy walks back.
    /// </summary>
    public static class PlayerDiplomacy
    {
        public const int CooldownTicks = GenDate.TicksPerDay * 15;
        /// <summary>After a tribute was paid, the next demand to the same faction waits this long.</summary>
        public const int TributeCooldownTicks = GenDate.TicksPerDay * 30;
        public static int StatAnswers;
        /// <summary>Tests: force every answer to yes or no.</summary>
        public static bool? TestOutcome;

        private static bool Roll(float chance) => TestOutcome ?? Rand.Chance(chance);

        public static bool IsPlayerMission(EnvoyMission m) => m >= EnvoyMission.PlayerPeace;

        public static bool Enabled => LivingWorldMod.Settings.simulateDiplomacy && LivingWorldMod.Settings.worldParties;

        /// <summary>A goodwill situation (ideology, …) holds the faction below the line where hostility ends.</summary>
        public static bool PeaceCapped(Faction f) => Find.GoodwillSituationManager.GetMaxGoodwill(f) < 0;

        /// <summary>Why the colony cannot send an envoy to this faction now (null: it can).</summary>
        public static string CannotSend(Faction f)
        {
            // Off in this save (prepared for removing the mod, or diplomacy/parties switched off): no new world parties.
            if (!Enabled) return "LW_Dipl_Off".Translate();
            Map home = Neighbourhood.Home;
            if (home == null || home.Parent is not Settlement) return "LW_Dipl_NoColony".Translate();
            if (f == null || f.IsPlayer || f.defeated || !f.HasGoodwill || f.def.permanentEnemy) return "LW_Dipl_NoTalk".Translate();
            FactionState fs = LivingWorldComponent.Instance.StateFor(f);
            if (fs == null || fs.settlements.Count == 0) return "LW_Dipl_NoTowns".Translate();
            if (EnvoyTo(f) is WorldParty envoy) return EnvoyStatus(envoy);
            int wait = fs.lastPlayerEnvoyTick + CooldownTicks - Find.TickManager.TicksGame;
            if (wait > 0) return "LW_Dipl_Cooldown".Translate(wait.ToStringTicksToPeriod());
            if (Destination(f, home) == null) return "LW_Dipl_NoRoute".Translate();
            return null;
        }

        /// <summary>The colony's envoy dealing with this faction (going there or coming back), if any. A mercenary on contract
        /// is not an envoy: it does not keep the colony from sending one.</summary>
        public static WorldParty EnvoyTo(Faction f)
        {
            foreach (WorldParty p in WorldParty.All)
                if (p.kind == LWPartyDefOf.LW_Envoy && p.Faction == Faction.OfPlayer && p.targetFactionId == f.loadID && p.mission != (byte)EnvoyMission.PlayerMercenary && !p.Destroyed) return p;
            return null;
        }

        /// <summary>"Your envoy (peace offer) reaches Tarn in 9 hours" / "… is coming home with 300 silver, back in 5 hours".</summary>
        public static string EnvoyStatus(WorldParty p)
        {
            string mission = ("LW_Dipl_MissionShort_" + (EnvoyMission)p.mission).Translate().Resolve();
            string eta = p.TicksToArrive().ToStringTicksToPeriod();
            if (p.phase == PartyPhase.Outbound) return "LW_Dipl_EnvoyGoing".Translate(mission, p.Destination?.Label ?? "?", eta).Resolve();
            return (p.silver >= 1f ? "LW_Dipl_EnvoyReturningSilver".Translate(mission, Mathf.RoundToInt(p.silver), eta) : "LW_Dipl_EnvoyReturning".Translate(mission, eta)).Resolve();
        }

        /// <summary>The faction's town the envoy walks to: the nearest one the colony can reach.</summary>
        public static Settlement Destination(Faction f, Map home)
        {
            FactionState fs = LivingWorldComponent.Instance.StateFor(f);
            Settlement best = null;
            float bestD = float.MaxValue;
            if (fs == null) return null;
            foreach (CompSettlementLife s in fs.settlements)
            {
                if (!s.Active || s.Settlement.Tile.Layer != home.Tile.Layer) continue;
                float d = Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Settlement.Tile);
                if (d < bestD && Find.WorldReachability.CanReach(home.Tile, s.Settlement.Tile))
                {
                    bestD = d;
                    best = s.Settlement;
                }
            }
            return best;
        }

        /// <summary>
        /// What the colony could field, as other factions see it: its fighters' combat power (colonists, mechs), not the
        /// storyteller's threat points (those grow with difficulty settings, which would make bullying cheaper).
        /// </summary>
        internal static float ColonyPoints()
        {
            float might = 0f;
            if (Neighbourhood.Home is Map m)
                foreach (Pawn p in m.mapPawns.PawnsInFaction(Faction.OfPlayer))
                    if (p.Spawned && !p.Downed && !p.Dead && (p.IsFreeColonist || p.RaceProps.IsMechanoid)) might += Mathf.Max(30f, p.kindDef.combatPower) * 2f;
            return Mathf.Max(100f, might);
        }

        // ---- Peace -------------------------------------------------------------------------------------------

        public static int SuggestedGift()
        {
            float wealth = Neighbourhood.Home?.wealthWatcher.WealthTotal ?? 0f;
            return Mathf.RoundToInt(Mathf.Clamp(200f + wealth * 0.01f, 300f, 2500f) / 50f) * 50;
        }

        public static float PeaceChance(Faction f, int gift)
        {
            FactionState fs = LivingWorldComponent.Instance.StateFor(f);
            if (fs == null || !f.HostileTo(Faction.OfPlayer) || PeaceCapped(f)) return 0f;
            float grudge = Mathf.Min(0.4f, Diplomacy.GrudgeAgainst(fs, Faction.OfPlayer) / 150f);
            float mightier = Mathf.Clamp((fs.strength / ColonyPoints() - 3f) / 20f, 0f, 0.25f);
            float giftBonus = gift <= 0 ? 0f : 0.45f * gift / (gift + SuggestedGift());
            float temper = (fs.TraitFactor(t => t.treatyKeeping) - 1f) * 0.3f + (fs.TraitFactor(t => t.playerTolerance) - 1f) * 0.3f;
            return Mathf.Clamp(0.1f + 0.5f * fs.warWeariness + giftBonus - grudge - mightier + temper, 0.05f, 0.9f);
        }

        // ---- Call to arms ------------------------------------------------------------------------------------

        /// <summary>Enemies of the colony an ally could be asked to march against.</summary>
        public static IEnumerable<Faction> CallToArmsTargets(Faction ally)
        {
            if (ally.PlayerRelationKind != FactionRelationKind.Ally) yield break;
            foreach (FactionState fs in LivingWorldComponent.Instance.factionStates)
                if (CallToArmsValid(ally, fs.faction)) yield return fs.faction;
        }

        /// <summary>The ally can still be asked to march on this enemy (checked when asking and again on arrival).</summary>
        public static bool CallToArmsValid(Faction ally, Faction enemy)
        {
            if (ally == null || enemy == null || enemy == ally || enemy.defeated || ally.defeated) return false;
            if (ally.PlayerRelationKind != FactionRelationKind.Ally || !enemy.HostileTo(Faction.OfPlayer)) return false;
            if (!(LivingWorldComponent.Instance.StateFor(enemy)?.settlements.Count > 0)) return false;
            // An ally does not turn on its own allies.
            return enemy.def.permanentEnemy || ally.RelationKindWith(enemy) != FactionRelationKind.Ally;
        }

        public static float CallToArmsChance(Faction ally, Faction enemy)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            FactionState a = comp.StateFor(ally), e = comp.StateFor(enemy);
            if (a == null || e == null) return 0f;
            float atWar = ally.HostileTo(enemy) ? 0.35f : 0f;
            float stronger = a.strength >= e.strength ? 0.15f : -0.15f;
            float grudge = Mathf.Min(0.3f, Diplomacy.GrudgeAgainst(a, enemy) / 150f);
            return Mathf.Clamp(0.3f + 0.3f * (a.TraitFactor(t => t.warPropensity) - 1f) - 0.6f * a.warWeariness + atWar + stronger + grudge, 0.05f, 0.95f);
        }

        // ---- Tribute -----------------------------------------------------------------------------------------

        public static bool CanDemandTribute(Faction f)
        {
            FactionState fs = LivingWorldComponent.Instance.StateFor(f);
            return fs != null && f.PlayerRelationKind != FactionRelationKind.Ally && fs.strength < ColonyPoints() * 1.5f
                   && Find.TickManager.TicksGame - fs.lastPlayerTributeTick >= TributeCooldownTicks;
        }

        public static int TributeAmount(Faction f)
        {
            FactionState fs = LivingWorldComponent.Instance.StateFor(f);
            return fs == null ? 0 : Mathf.RoundToInt(Mathf.Clamp(fs.treasury * 0.15f, 100f, 2000f) / 50f) * 50;
        }

        public static float TributeChance(Faction f)
        {
            FactionState fs = LivingWorldComponent.Instance.StateFor(f);
            if (fs == null) return 0f;
            float weak = Mathf.Clamp01(1f - fs.strength / (ColonyPoints() * 1.5f));
            float grudge = Diplomacy.GrudgeAgainst(fs, Faction.OfPlayer) / 150f; // every tribute paid makes the next one harder
            return Mathf.Clamp(0.15f + 0.6f * weak - 0.3f * (fs.TraitFactor(t => t.warPropensity) - 1f) + 0.2f * fs.warWeariness - grudge, 0.05f, 0.85f);
        }

        // ---- Sending and answers -----------------------------------------------------------------------------

        public static WorldParty Send(Faction f, EnvoyMission mission, int gift = 0, Faction enemy = null)
        {
            Map home = Neighbourhood.Home;
            if (CannotSend(f) != null) return null;
            Settlement dest = Destination(f, home);
            if (gift > 0)
            {
                if (Demands.ColonySilver(home) < gift) return null;
                Demands.TakeColonySilver(home, gift);
            }
            WorldParty p = WorldParty.Launch(LWPartyDefOf.LW_Envoy, Faction.OfPlayer, (Settlement)home.Parent, dest);
            p.targetFactionId = f.loadID;
            p.secondFactionId = enemy?.loadID ?? -1;
            p.mission = (byte)mission;
            p.silver = gift;
            p.strength = 10f;
            LivingWorldComponent.Instance.StateFor(f).lastPlayerEnvoyTick = Find.TickManager.TicksGame;
            Messages.Message("LW_Dipl_EnvoySent".Translate(dest.Label, f.Name, p.TicksToArrive().ToStringTicksToPeriod()), new LookTargets(p), MessageTypeDefOf.NeutralEvent, historical: false);
            return p;
        }

        /// <summary>The envoy reached the faction's town: they answer. The envoy then walks home (or not).</summary>
        public static void Answer(WorldParty p)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Faction f = comp.FactionById(p.targetFactionId);
            if (f == null || f.defeated)
            {
                p.TurnBack();
                return;
            }
            FactionState fs = comp.StateFor(f);
            StatAnswers++;
            switch ((EnvoyMission)p.mission)
            {
                case EnvoyMission.PlayerPeace:
                    AnswerPeace(comp, p, f, fs);
                    break;
                case EnvoyMission.PlayerCallToArms:
                    AnswerCallToArms(comp, p, f, fs, comp.FactionById(p.secondFactionId));
                    break;
                case EnvoyMission.PlayerTribute:
                    AnswerTribute(comp, p, f, fs);
                    break;
                case EnvoyMission.PlayerMercenary:
                    Mercenaries.Fight(comp, p, f);
                    break;
                case EnvoyMission.PlayerWeregild:
                    AnswerWeregild(comp, p, f, fs);
                    break;
            }
            if (!p.Destroyed) p.TurnBack();
        }

        private static void AnswerPeace(LivingWorldComponent comp, WorldParty p, Faction f, FactionState fs)
        {
            int gift = Mathf.RoundToInt(p.silver);
            if (!f.HostileTo(Faction.OfPlayer))
            {
                Letter(p, "LW_Dipl_PeaceMootLabel", "LW_Dipl_PeaceMootText", f, LetterDefOf.NeutralEvent);
                return;
            }
            if (Roll(PeaceChance(f, gift)))
            {
                f.TryAffectGoodwillWith(Faction.OfPlayer, 5 - f.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false, reason: LWHistoryDefOf.LW_MadePeace);
                if (f.HostileTo(Faction.OfPlayer))
                {
                    // Something vanilla holds fast (a quest): the offer cannot be taken up; the gift goes home.
                    Letter(p, "LW_Dipl_PeaceBlockedLabel", "LW_Dipl_PeaceBlockedText", f, LetterDefOf.NeutralEvent);
                    return;
                }
                p.silver = 0f;
                if (fs != null) fs.treasury += gift;
                comp.revenge.RemoveAll(r => r.factionId == f.loadID);
                if (fs != null)
                {
                    foreach (Grievance g in fs.grievances)
                        if (g.otherFactionId == Faction.OfPlayer.loadID) g.amount *= 0.5f;
                    fs.warWeariness *= 0.7f;
                }
                Record(comp, LWChronicleDefOf.LW_PlayerPeace, f, gift);
                Letter(p, "LW_Dipl_PeaceYesLabel", "LW_Dipl_PeaceYesText", f, LetterDefOf.PositiveEvent, gift);
                return;
            }
            // Refused. Treacherous hosts may keep the gift and the envoy.
            if (gift > 0 && fs != null && fs.TraitFactor(t => t.treatyKeeping) < 0.8f && Rand.Chance(0.35f))
            {
                fs.treasury += gift;
                Letter(p, "LW_Dipl_EnvoyKilledLabel", "LW_Dipl_EnvoyKilledText", f, LetterDefOf.NegativeEvent, gift);
                Record(comp, LWChronicleDefOf.LW_PlayerEnvoyKilled, f, gift);
                p.Disband();
                return;
            }
            // They keep half the gift for the trouble of listening: asking again is not free.
            int kept = gift / 2;
            p.silver -= kept;
            if (fs != null) fs.treasury += kept;
            Letter(p, "LW_Dipl_PeaceNoLabel", gift > 0 ? "LW_Dipl_PeaceNoText" : "LW_Dipl_PeaceNoTextNoGift", f, LetterDefOf.NegativeEvent, kept);
        }

        private static void AnswerCallToArms(LivingWorldComponent comp, WorldParty p, Faction ally, FactionState a, Faction enemy)
        {
            FactionState e = enemy == null ? null : comp.StateFor(enemy);
            if (a == null || e == null || !CallToArmsValid(ally, enemy))
            {
                Letter(p, "LW_Dipl_CallMootLabel", "LW_Dipl_CallMootText", ally, LetterDefOf.NeutralEvent, 0, enemy);
                return;
            }
            if (!Roll(CallToArmsChance(ally, enemy)))
            {
                Letter(p, "LW_Dipl_CallNoLabel", "LW_Dipl_CallNoText", ally, LetterDefOf.NegativeEvent, 0, enemy);
                return;
            }
            if (!ally.HostileTo(enemy))
            {
                Diplomacy.DeclareWar(comp, ally, enemy, "LW_WarCause_PlayerCall".Translate(Faction.OfPlayer.Name).Resolve());
                if (!ally.HostileTo(enemy))
                {
                    Letter(p, "LW_Dipl_CallMootLabel", "LW_Dipl_CallMootText", ally, LetterDefOf.NeutralEvent, 0, enemy);
                    return;
                }
            }
            // They march at once on the enemy town nearest the colony.
            Map home = Neighbourhood.Home;
            CompSettlementLife target = null, from = null;
            float best = float.MaxValue;
            foreach (CompSettlementLife t in e.settlements)
            {
                if (!t.Active || home == null) continue;
                float d = Find.WorldGrid.ApproxDistanceInTiles(home.Tile, t.Settlement.Tile);
                if (d < best)
                {
                    best = d;
                    target = t;
                }
            }
            best = float.MaxValue;
            if (target != null)
                foreach (CompSettlementLife s in a.settlements)
                {
                    if (!s.Active || s.garrison < 20f) continue;
                    float d = Find.WorldGrid.ApproxDistanceInTiles(s.Settlement.Tile, target.Settlement.Tile);
                    if (d < best && Find.WorldReachability.CanReach(s.Settlement.Tile, target.Settlement.Tile))
                    {
                        best = d;
                        from = s;
                    }
                }
            if (from != null) Military.Attack(comp, a, from, target, from.garrison * 0.6f);
            Diplomacy.AddGrudge(e, Faction.OfPlayer, 20f, "LW_Grudge_CalledAllies".Translate(ally.Name).Resolve());
            Record(comp, LWChronicleDefOf.LW_PlayerCallToArms, ally, 0, enemy);
            Letter(p, "LW_Dipl_CallYesLabel", from != null ? "LW_Dipl_CallYesText" : "LW_Dipl_CallYesNoArmyText", ally, LetterDefOf.PositiveEvent, 0, enemy,
                target?.Settlement.Label);
        }

        /// <summary>A blood price for the faction's open feuds: taken, the avengers renounce their oaths; spurned, the silver comes home.</summary>
        private static void AnswerWeregild(LivingWorldComponent comp, WorldParty p, Faction f, FactionState fs)
        {
            int gift = Mathf.RoundToInt(p.silver);
            if (!Feuds.HasOpenFeud(f))
            {
                Letter(p, "LW_Dipl_WeregildMootLabel", "LW_Dipl_WeregildMootText", f, LetterDefOf.NeutralEvent, gift);
                return;
            }
            if (Roll(Feuds.BloodPriceChance(f)))
            {
                p.silver = 0f;
                if (fs != null) fs.treasury += gift;
                string names = Feuds.Settle(comp, f, gift);
                f.TryAffectGoodwillWith(Faction.OfPlayer, 5, canSendMessage: false, reason: LWHistoryDefOf.LW_MadePeace);
                Diplomacy.AddGrudge(fs, Faction.OfPlayer, -15f, "LW_Grudge_BloodPrice".Translate().Resolve());
                Letter(p, "LW_Dipl_WeregildYesLabel", "LW_Dipl_WeregildYesText", f, LetterDefOf.PositiveEvent, gift, town: names);
                return;
            }
            Letter(p, "LW_Dipl_WeregildNoLabel", "LW_Dipl_WeregildNoText", f, LetterDefOf.NegativeEvent, gift);
        }

        private static void AnswerTribute(LivingWorldComponent comp, WorldParty p, Faction f, FactionState fs)
        {
            if (fs == null || !CanDemandTribute(f))
            {
                // Grown too strong (or friendly) meanwhile: they laugh.
                f.TryAffectGoodwillWith(Faction.OfPlayer, -10, canSendMessage: false, reason: LWHistoryDefOf.LW_DemandedTribute);
                Letter(p, "LW_Dipl_TributeNoLabel", "LW_Dipl_TributeNoText", f, LetterDefOf.NegativeEvent);
                return;
            }
            int amount = TributeAmount(f);
            if (Roll(TributeChance(f)))
            {
                fs.treasury = Mathf.Max(0f, fs.treasury - amount);
                p.silver += amount;
                f.TryAffectGoodwillWith(Faction.OfPlayer, -15, canSendMessage: false, reason: LWHistoryDefOf.LW_DemandedTribute);
                Diplomacy.AddGrudge(fs, Faction.OfPlayer, 15f, "LW_Grudge_Tribute".Translate().Resolve());
                Record(comp, LWChronicleDefOf.LW_PlayerTributePaid, f, amount);
                fs.lastPlayerTributeTick = Find.TickManager.TicksGame;
                Letter(p, "LW_Dipl_TributeYesLabel", "LW_Dipl_TributeYesText", f, LetterDefOf.PositiveEvent, amount);
                return;
            }
            f.TryAffectGoodwillWith(Faction.OfPlayer, -25, canSendMessage: false, reason: LWHistoryDefOf.LW_DemandedTribute);
            Diplomacy.AddGrudge(fs, Faction.OfPlayer, 25f, "LW_Grudge_Tribute".Translate().Resolve());
            Record(comp, LWChronicleDefOf.LW_PlayerTributeRefused, f, amount);
            Letter(p, "LW_Dipl_TributeNoLabel", "LW_Dipl_TributeNoText", f, LetterDefOf.NegativeEvent);
        }

        /// <summary>The envoy is home: whatever silver it carries (a refused gift, tribute) goes into the colony's stores.</summary>
        public static void Notify_Home(WorldParty p)
        {
            Map map = (p.Origin as MapParent)?.Map ?? Neighbourhood.Home;
            // Mercenaries walk back in (with no home map they go on as a caravan when the party is removed).
            foreach (Pawn back in p.Disembark(map))
                Messages.Message("LW_Merc_Back".Translate(back.Named("PAWN")), back, MessageTypeDefOf.PositiveEvent);
            int silver = Mathf.RoundToInt(p.silver);
            if (silver <= 0 || map == null) return;
            Thing t = ThingMaker.MakeThing(ThingDefOf.Silver);
            t.stackCount = silver;
            GenPlace.TryPlaceThing(t, DropCellFinder.TradeDropSpot(map), map, ThingPlaceMode.Near);
            Messages.Message("LW_Dipl_EnvoyHome".Translate(silver), new LookTargets(t), MessageTypeDefOf.PositiveEvent);
        }

        private static void Letter(WorldParty p, string label, string text, Faction f, LetterDef def, int silver = 0, Faction other = null, string town = null)
        {
            string at = p.Destination?.Label ?? p.originName ?? "?";
            Find.LetterStack.ReceiveLetter(label.Translate(f.Name), text.Translate(f.NameColored.Named("FACTION"), at.Named("TOWN"), silver.Named("SILVER"),
                (other?.NameColored ?? (TaggedString)"?").Named("ENEMY"), (town ?? "?").Named("TARGET")), def, new LookTargets(p), f);
        }

        private static void Record(LivingWorldComponent comp, ChronicleEventDef def, Faction f, int silver, Faction other = null) =>
            comp.chronicle.Record(def, def.text.Formatted(Faction.OfPlayer.Name.Named("COLONY"), f.Name.Named("FACTION"), silver.Named("SILVER"),
                (other?.Name ?? "?").Named("ENEMY")).Resolve(), Faction.OfPlayer, f);
    }
}
