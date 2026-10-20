namespace LivingWorld
{
    /// <summary>How many parties may be on the map: NPC-to-NPC traffic leaves room for parties heading to the player.</summary>
    public static class PartyBudget
    {
        public const int ReservedForPlayer = 6;

        public static bool CanLaunchNpc()
        {
            LivingWorldSettings s = LivingWorldMod.Settings;
            return !SimClock.InHistory && s.worldParties && WorldParty.All.Count < s.maxParties - ReservedForPlayer;
        }

        public static int CountOf(PartyKindDef kind, RimWorld.Faction faction)
        {
            int n = 0;
            foreach (WorldParty p in WorldParty.All)
                if (p.kind == kind && p.Faction == faction) n++;
            return n;
        }
    }
}
