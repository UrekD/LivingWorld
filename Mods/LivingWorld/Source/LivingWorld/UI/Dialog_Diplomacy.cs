using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>What the colony can ask of a faction, each with its odds and cost; sending closes the dialog.</summary>
    public class Dialog_Diplomacy : Window
    {
        private readonly Faction faction;
        private Vector2 scroll;

        private struct Option
        {
            public string label, desc, disabled;
            public float chance;
            public Action send;
        }

        // Worked out once when the dialog opens (pathfinding and threat points are too costly for every GUI pass).
        private List<Option> options;
        private string cannot, intro;

        public Dialog_Diplomacy(Faction faction)
        {
            this.faction = faction;
            forcePause = true;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(620f, 520f);

        private List<Option> Options()
        {
            var list = new List<Option>();
            Map home = Neighbourhood.Home;
            int silver = home == null ? 0 : Demands.ColonySilver(home);
            if (faction.HostileTo(Faction.OfPlayer) && !PlayerDiplomacy.PeaceCapped(faction))
            {
                int gift = PlayerDiplomacy.SuggestedGift();
                list.Add(new Option
                {
                    label = "LW_Dipl_PeaceGift".Translate(gift), desc = "LW_Dipl_PeaceGiftDesc".Translate(), chance = PlayerDiplomacy.PeaceChance(faction, gift),
                    disabled = silver < gift ? "LW_Dipl_NotEnoughSilver".Translate().Resolve() : null,
                    send = () => PlayerDiplomacy.Send(faction, EnvoyMission.PlayerPeace, gift),
                });
                list.Add(new Option
                {
                    label = "LW_Dipl_PeaceNoGift".Translate(), desc = "LW_Dipl_PeaceNoGiftDesc".Translate(), chance = PlayerDiplomacy.PeaceChance(faction, 0),
                    send = () => PlayerDiplomacy.Send(faction, EnvoyMission.PlayerPeace),
                });
            }
            foreach (Faction enemy in PlayerDiplomacy.CallToArmsTargets(faction))
            {
                Faction e = enemy;
                list.Add(new Option
                {
                    label = "LW_Dipl_Call".Translate(e.Name), desc = "LW_Dipl_CallDesc".Translate(e.Name), chance = PlayerDiplomacy.CallToArmsChance(faction, e),
                    send = () => PlayerDiplomacy.Send(faction, EnvoyMission.PlayerCallToArms, enemy: e),
                });
            }
            if (Feuds.HasOpenFeud(faction))
            {
                int price = Feuds.BloodPrice();
                list.Add(new Option
                {
                    label = "LW_Dipl_Weregild".Translate(price), desc = "LW_Dipl_WeregildDesc".Translate(Feuds.OpenFeudNames(faction)), chance = Feuds.BloodPriceChance(faction),
                    disabled = silver < price ? "LW_Dipl_NotEnoughSilver".Translate().Resolve() : null,
                    send = () => PlayerDiplomacy.Send(faction, EnvoyMission.PlayerWeregild, price),
                });
            }
            if (PlayerDiplomacy.CanDemandTribute(faction))
                list.Add(new Option
                {
                    label = "LW_Dipl_Tribute".Translate(PlayerDiplomacy.TributeAmount(faction)), desc = "LW_Dipl_TributeDesc".Translate(),
                    chance = PlayerDiplomacy.TributeChance(faction), send = () => PlayerDiplomacy.Send(faction, EnvoyMission.PlayerTribute),
                });
            foreach (HireOffer offer in HiredBands.Offers(faction))
            {
                HireOffer o = offer;
                if (o.target == null || o.source == null)
                {
                    // Cannot be made at all (no town of theirs in reach): the reason, with nothing to send.
                    list.Add(new Option { label = "LW_Hire_NoneLabel".Translate(o.enemy?.Name ?? "?"), desc = "LW_Hire_NoneDesc".Translate(), chance = -1f, disabled = o.cannot });
                    continue;
                }
                list.Add(new Option
                {
                    label = "LW_Hire_Option".Translate(o.enemy.Name, Mathf.RoundToInt(o.strength), o.source.Label, o.target.Label, o.price), desc = HiredBands.Describe(o),
                    chance = o.acceptChance, disabled = o.cannot ?? (silver < o.price ? "LW_Dipl_NotEnoughSilver".Translate().Resolve() : null),
                    send = () => HiredBands.Send(o),
                });
            }
            return list;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            cannot = PlayerDiplomacy.CannotSend(faction);
            Map home = Neighbourhood.Home;
            Settlement dest = home == null ? null : PlayerDiplomacy.Destination(faction, home);
            if (dest != null)
            {
                int eta = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(home.Tile, dest.Tile, null);
                intro = "LW_Dipl_Intro".Translate(dest.Label.Named("TOWN"), eta.ToStringTicksToPeriod().Named("ETA"),
                    (faction.PlayerRelationKind.GetLabelCap() + " (" + faction.PlayerGoodwill.ToStringWithSign() + ")").Named("RELATION"));
            }
            options = Options();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "LW_Dipl_Title".Translate(faction.Name));
            Text.Font = GameFont.Small;
            float y = inRect.y + 40f;
            if (intro != null)
            {
                float h = Text.CalcHeight(intro, inRect.width);
                Widgets.Label(new Rect(inRect.x, y, inRect.width, h), intro);
                y += h + 10f;
            }
            if (cannot != null)
            {
                GUI.color = ColorLibrary.RedReadable;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), cannot);
                GUI.color = Color.white;
                y += 30f;
            }
            if (options.Count == 0)
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 60f), "LW_Dipl_NoOptions".Translate());
                return;
            }
            var outer = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
            // Each row as tall as its description needs (Tiny font, wrapped).
            float descW = outer.width - 16f - 12f - 110f;
            Text.Font = GameFont.Tiny;
            var descH = new List<float>();
            float total = 0f;
            foreach (Option o in options)
            {
                float h = Mathf.Max(32f, Text.CalcHeight(o.desc, descW));
                descH.Add(h);
                total += 54f + h + 6f;
            }
            Text.Font = GameFont.Small;
            var view = new Rect(0f, 0f, outer.width - 16f, total);
            Widgets.BeginScrollView(outer, ref scroll, view);
            y = 0f;
            for (int oi = 0; oi < options.Count; oi++)
            {
                Option o = options[oi];
                float dh = descH[oi];
                var row = new Rect(0f, y, view.width, 54f + dh);
                Widgets.DrawMenuSection(row);
                Rect inner = row.ContractedBy(6f);
                Widgets.Label(new Rect(inner.x, inner.y, inner.width - 110f, 22f), o.label);
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.8f, 0.8f, 0.8f);
                Widgets.Label(new Rect(inner.x, inner.y + 22f, inner.width - 110f, dh), o.desc);
                GUI.color = o.chance < 0f ? new Color(1f, 0.6f, 0.5f) : o.chance >= 0.6f ? new Color(0.5f, 1f, 0.5f) : o.chance >= 0.3f ? Color.white : new Color(1f, 0.6f, 0.5f);
                Widgets.Label(new Rect(inner.x, inner.y + 24f + dh, inner.width - 110f, 18f),
                    o.chance < 0f ? o.disabled : ("LW_Dipl_Chance".Translate(o.chance.ToStringPercent()) + (o.disabled != null ? " — " + o.disabled : "")).Resolve());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                var button = new Rect(inner.xMax - 100f, inner.y + 22f, 100f, 30f);
                bool can = cannot == null && o.disabled == null;
                if (!can) GUI.color = new Color(1f, 1f, 1f, 0.4f); // greyed out: cannot send this one now
                bool clicked = Widgets.ButtonText(button, "LW_Dipl_Send".Translate(), active: can);
                GUI.color = Color.white;
                if (clicked && can)
                {
                    o.send();
                    Close();
                }
                y += 54f + dh + 6f;
            }
            Widgets.EndScrollView();
        }
    }
}
