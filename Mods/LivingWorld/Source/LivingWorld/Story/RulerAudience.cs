using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    public enum AudienceProposal : byte { Gift, TradePact, MarchWithUs, SettleNearby }

    /// <summary>
    /// A ruler who visits comes with a proposal shaped by their goal: a trade pact (prosper), a call to march against
    /// their enemy (conquer/avenge), leave to found a town near the colony (expand), or just a gift and friendship (hold).
    /// Accepting is rewarded; declining costs goodwill, and the letter says how much.
    /// </summary>
    public static class RulerAudience
    {
        public const int DeclineGoodwill = -6;
        public const int TradePactDays = 60;

        public static AudienceProposal ProposalFor(LivingWorldComponent comp, FactionState fs, out Faction enemy, out Settlement target)
        {
            enemy = null;
            target = null;
            Map home = Neighbourhood.Home;
            switch (fs.goal)
            {
                case RulerGoalKind.Prosper:
                    return AudienceProposal.TradePact;
                case RulerGoalKind.Conquer:
                case RulerGoalKind.Avenge:
                    enemy = comp.FactionById(fs.goalTargetId);
                    if (enemy != null && !enemy.IsPlayer && !enemy.defeated && home != null)
                    {
                        target = NearestTown(comp, enemy, home.Tile);
                        if (target != null) return AudienceProposal.MarchWithUs;
                    }
                    return AudienceProposal.Gift;
                case RulerGoalKind.Expand:
                    return home != null && Expansion.TryFindSite(home.Tile, out _) ? AudienceProposal.SettleNearby : AudienceProposal.Gift;
                default:
                    return AudienceProposal.Gift;
            }
        }

        private static Settlement NearestTown(LivingWorldComponent comp, Faction f, PlanetTile tile)
        {
            Settlement best = null;
            float bestD = Neighbourhood.Radius(tile, Demands.Range);
            foreach (CompSettlementLife s in comp.StateFor(f)?.settlements ?? new List<CompSettlementLife>())
            {
                if (!s.Active || !SettlementOps.CanFall(s.Settlement)) continue;
                float d = Find.WorldGrid.ApproxDistanceInTiles(tile, s.Settlement.Tile);
                if (d < bestD)
                {
                    bestD = d;
                    best = s.Settlement;
                }
            }
            return best;
        }

        public static int GiftSilver(FactionState fs) => Mathf.RoundToInt(Mathf.Clamp(fs.treasury * 0.05f, 100f, 600f) / 10f) * 10;

        /// <summary>Sends the audience letter for a ruler who just arrived with a visit.</summary>
        public static void Open(Pawn ruler, Faction f, Settlement home)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            FactionState fs = comp.StateFor(f);
            if (fs == null) return;
            AudienceProposal proposal = ProposalFor(comp, fs, out Faction enemy, out Settlement target);
            var letter = (ChoiceLetter_RulerAudience)LetterMaker.MakeLetter(LWLetterDefOf.LW_RulerAudience);
            letter.Label = "LW_RulerVisitsLabel".Translate(f.LeaderTitle.CapitalizeFirst(), f.Name);
            string intro = "LW_RulerVisitsText".Translate(ruler.Named("PAWN"), f.LeaderTitle.Named("TITLE"), f.NameColored.Named("FACTION"), home.Label.Named("HOME"));
            string ask = ("LW_Audience_" + proposal).Translate(ruler.Named("PAWN"), (enemy?.NameColored ?? (TaggedString)"?").Named("ENEMY"), (target?.Label ?? "?").Named("TARGET"),
                GiftSilver(fs).Named("SILVER"), TradePactDays.Named("DAYS"), (-DeclineGoodwill).Named("GOODWILL"));
            letter.Text = intro + "\n\n" + ask;
            // Colonists with roots in the places this touches speak up.
            string voices = Voices(f, proposal, enemy, target, ruler.MapHeld ?? Neighbourhood.Home, out List<int> objectors, out List<int> supporters);
            if (voices != null) letter.Text += "\n\n" + voices;
            letter.objectors = objectors;
            letter.supporters = supporters;
            letter.lookTargets = ruler;
            letter.relatedFaction = f;
            letter.faction = f;
            letter.ruler = ruler;
            letter.proposal = proposal;
            letter.enemyId = enemy?.loadID ?? -1;
            letter.targetId = target?.ID ?? -1;
            letter.giftSilver = GiftSilver(fs); // the amount the letter promises is the amount given
            letter.StartTimeout(GenDate.TicksPerDay * 2);
            Find.LetterStack.ReceiveLetter(letter);
            // Hosting a ruler is an occasion for the colonists.
            if (ruler.MapHeld is Map map)
                foreach (Pawn c in map.mapPawns.FreeColonistsSpawned) c.needs?.mood?.thoughts.memories.TryGainMemory(LWThoughtDefOf.LW_HostedRuler);
        }

        /// <summary>
        /// Who in the colony cares: a colonist whose home town is the target of "march with us" objects; one whose home
        /// belongs to a faction proposing a trade pact supports it.
        /// </summary>
        private static string Voices(Faction f, AudienceProposal proposal, Faction enemy, Settlement target, Map map, out List<int> objectors, out List<int> supporters)
        {
            objectors = new List<int>();
            supporters = new List<int>();
            if (map == null) return null;
            var lines = new List<string>();
            foreach (Pawn c in map.mapPawns.FreeColonists)
            {
                PawnRoot root = Roots.Of(c);
                if (root == null) continue;
                if (proposal == AudienceProposal.MarchWithUs && (root.settlementId == target?.ID || (enemy != null && root.factionId == enemy.loadID)))
                {
                    objectors.Add(c.thingIDNumber);
                    lines.Add("LW_Audience_Objects".Translate(c.Named("PAWN"), root.settlementName.Named("HOME")).Resolve());
                }
                else if (proposal == AudienceProposal.TradePact && root.factionId == f.loadID)
                {
                    supporters.Add(c.thingIDNumber);
                    lines.Add("LW_Audience_Supports".Translate(c.Named("PAWN"), root.settlementName.Named("HOME")).Resolve());
                }
            }
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        /// <summary>The colonists who spoke up remember what the colony decided.</summary>
        private static void Remember(ChoiceLetter_RulerAudience l, bool accepted)
        {
            Map map = l.ruler?.MapHeld ?? Neighbourhood.Home;
            if (map == null) return;
            foreach (Pawn c in map.mapPawns.FreeColonists)
            {
                if (l.objectors.Contains(c.thingIDNumber))
                {
                    c.needs?.mood?.thoughts.memories.TryGainMemory(accepted ? LWThoughtDefOf.LW_BetrayedHome : LWThoughtDefOf.LW_StoodUpForHome);
                    if (!accepted) Legends.Add(c, "LW_Deed_SpokeForHome".Translate(Roots.Of(c)?.settlementName ?? "?").Resolve());
                }
                if (l.supporters.Contains(c.thingIDNumber) && accepted) c.needs?.mood?.thoughts.memories.TryGainMemory(LWThoughtDefOf.LW_PactWithHome);
            }
        }

        public static void Accept(ChoiceLetter_RulerAudience l)
        {
            Remember(l, accepted: true);
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Faction f = l.faction;
            FactionState fs = comp.StateFor(f);
            if (f == null || fs == null) return;
            switch (l.proposal)
            {
                case AudienceProposal.TradePact:
                    fs.tradePactUntil = Find.TickManager.TicksGame + GenDate.TicksPerDay * TradePactDays;
                    foreach (CompSettlementLife s in fs.settlements) s.fame = Mathf.Clamp(s.fame + 0.15f, -1f, 1f);
                    f.TryAffectGoodwillWith(Faction.OfPlayer, 8, canSendMessage: true, reason: LWHistoryDefOf.LW_AcceptedProposal);
                    break;
                case AudienceProposal.MarchWithUs:
                    if (LivingWorldComponent.WorldObjectById(l.targetId) is Settlement target && comp.FactionById(l.enemyId) is Faction enemy && target.Faction == enemy)
                    {
                        int reward = Mathf.RoundToInt(Mathf.Clamp(fs.treasury * 0.1f, 400f, 3000f) / 50f) * 50;
                        Demands.AddWarRequest(f, target, reward, 30);
                        Messages.Message("LW_Audience_MarchAccepted".Translate(target.Label, reward), target, MessageTypeDefOf.PositiveEvent);
                    }
                    f.TryAffectGoodwillWith(Faction.OfPlayer, 8, canSendMessage: true, reason: LWHistoryDefOf.LW_AcceptedProposal);
                    break;
                case AudienceProposal.SettleNearby:
                    Map home = Neighbourhood.Home;
                    Settlement capital = SettlementOps.CapitalOf(f);
                    if (home != null && Expansion.TryFindSite(home.Tile, out PlanetTile site) && capital?.GetComponent<CompSettlementLife>() is CompSettlementLife cap)
                    {
                        float people = Mathf.Max(Tuning.T.minPopulation, cap.population * 0.08f);
                        cap.population -= people;
                        Settlement town = Expansion.Settle(comp, f, capital, site, people);
                        if (town != null) Messages.Message("LW_Audience_SettleAccepted".Translate(f.Name, town.Label), town, MessageTypeDefOf.PositiveEvent);
                    }
                    f.TryAffectGoodwillWith(Faction.OfPlayer, 12, canSendMessage: true, reason: LWHistoryDefOf.LW_AcceptedProposal);
                    break;
                default:
                    // The gift is left at the colony (even if the ruler has already gone home).
                    if ((l.ruler?.MapHeld ?? Neighbourhood.Home) is Map map)
                    {
                        int silver = l.giftSilver > 0 ? l.giftSilver : GiftSilver(fs);
                        fs.treasury = Mathf.Max(0f, fs.treasury - silver);
                        Thing t = ThingMaker.MakeThing(ThingDefOf.Silver);
                        t.stackCount = silver;
                        GenPlace.TryPlaceThing(t, DropCellFinder.TradeDropSpot(map), map, ThingPlaceMode.Near);
                    }
                    f.TryAffectGoodwillWith(Faction.OfPlayer, 5, canSendMessage: true, reason: LWHistoryDefOf.LW_AcceptedProposal);
                    break;
            }
            StatAccepted++;
        }

        public static void Decline(ChoiceLetter_RulerAudience l)
        {
            Remember(l, accepted: false);
            l.faction?.TryAffectGoodwillWith(Faction.OfPlayer, DeclineGoodwill, canSendMessage: true, reason: LWHistoryDefOf.LW_DeclinedProposal);
        }

        public static int StatAccepted;
    }

    /// <summary>The ruler's proposal: accept or decline (a gift can only be accepted).</summary>
    public class ChoiceLetter_RulerAudience : ChoiceLetter
    {
        public Faction faction;
        public Pawn ruler;
        public AudienceProposal proposal;
        public int enemyId = -1, targetId = -1, giftSilver;
        public bool answered;
        /// <summary>Colonists (thingIDNumber) who objected to / supported the proposal.</summary>
        public List<int> objectors = new List<int>(), supporters = new List<int>();

        public override bool CanDismissWithRightClick => false;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                if (ArchivedOnly)
                {
                    yield return Option_Close;
                    yield break;
                }
                yield return new DiaOption(("LW_Audience_Accept_" + proposal).Translate())
                {
                    action = () =>
                    {
                        answered = true;
                        RulerAudience.Accept(this);
                        Find.LetterStack.RemoveLetter(this);
                    },
                    resolveTree = true,
                };
                if (proposal != AudienceProposal.Gift)
                    yield return new DiaOption("LW_Audience_Decline".Translate(-RulerAudience.DeclineGoodwill))
                    {
                        action = () =>
                        {
                            answered = true;
                            RulerAudience.Decline(this);
                            Find.LetterStack.RemoveLetter(this);
                        },
                        resolveTree = true,
                    };
                yield return Option_Postpone;
            }
        }

        /// <summary>Left unanswered until the ruler leaves: a gift is still given; a proposal counts as declined.</summary>
        public override void Removed()
        {
            base.Removed();
            if (answered) return;
            answered = true;
            if (proposal == AudienceProposal.Gift) RulerAudience.Accept(this);
            else RulerAudience.Decline(this);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref faction, "faction");
            Scribe_References.Look(ref ruler, "ruler");
            Scribe_Values.Look(ref proposal, "proposal");
            Scribe_Values.Look(ref enemyId, "enemy", -1);
            Scribe_Values.Look(ref targetId, "target", -1);
            Scribe_Values.Look(ref answered, "answered");
            Scribe_Values.Look(ref giftSilver, "giftSilver");
            Scribe_Collections.Look(ref objectors, "objectors", LookMode.Value);
            Scribe_Collections.Look(ref supporters, "supporters", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                objectors ??= new List<int>();
                supporters ??= new List<int>();
            }
        }
    }
}
