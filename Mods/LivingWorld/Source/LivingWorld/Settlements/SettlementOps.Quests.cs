using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace LivingWorld
{
    public static partial class SettlementOps
    {
        // Quest id -> quest, rebuilt when a tick passes, the quest list changes size or a new game's manager comes in.
        // One quest removed and another added in the same tick (same count) leaves the map as it was until the next tick: one tick, and wrong
        // only for the quest that just ended or began.
        private static readonly Dictionary<int, Quest> questById = new Dictionary<int, Quest>();
        private static QuestManager questByIdFor;
        private static int questByIdTick = -1, questByIdCount = -1;

        /// <summary>
        /// A live quest refers to this town. Quests tag the world objects they name (<c>Quest&lt;id&gt;.&lt;name&gt;</c>) and never remove the tag,
        /// so a tag only counts while its quest still exists and is not over; a tag of another format (another mod's) counts.
        /// </summary>
        public static bool QuestRelated(Settlement s)
        {
            List<string> tags = s.questTags;
            if (tags.NullOrEmpty()) return false;
            return QuestRelated(tags, QuestLive);
        }

        /// <summary>The pure core: <paramref name="questLive"/> answers true for a live quest, false for a historical one, null for an id with no quest.</summary>
        internal static bool QuestRelated(IReadOnlyList<string> tags, Func<int, bool?> questLive)
        {
            for (int i = 0; i < tags.Count; i++)
            {
                if (!TryParseQuestId(tags[i], out int id)) return true;
                if (questLive(id) == true) return true;
            }
            return false;
        }

        /// <summary>"Quest12.settlement" gives 12; anything else (no prefix, no digits, no dot after them) is not a quest tag of ours to read.</summary>
        internal static bool TryParseQuestId(string tag, out int id)
        {
            id = 0;
            const string prefix = "Quest";
            if (tag == null || tag.Length <= prefix.Length + 1 || !tag.StartsWith(prefix, StringComparison.Ordinal)) return false;
            int i = prefix.Length;
            long value = 0;
            while (i < tag.Length && tag[i] >= '0' && tag[i] <= '9')
            {
                value = value * 10 + (tag[i] - '0');
                if (value > int.MaxValue) return false;
                i++;
            }
            if (i == prefix.Length || i >= tag.Length || tag[i] != '.') return false;
            id = (int)value;
            return true;
        }

        private static bool? QuestLive(int id)
        {
            QuestManager qm = Find.QuestManager;
            if (qm == null) return null;
            List<Quest> quests = qm.QuestsListForReading;
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (qm != questByIdFor || tick != questByIdTick || quests.Count != questByIdCount)
            {
                questById.Clear();
                foreach (Quest q in quests) questById[q.id] = q;
                questByIdFor = qm;
                questByIdTick = tick;
                questByIdCount = quests.Count;
            }
            return questById.TryGetValue(id, out Quest quest) ? !quest.Historical : (bool?)null;
        }
    }
}
