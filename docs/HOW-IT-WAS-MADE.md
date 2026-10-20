# How Living World was made and tested

Living World is a **proof of concept and a work in progress**. AI coding agents wrote it over about three days in
October 2026 (16–18), directed by one person. This page says who did what, how it was checked, and what was not
checked.

## Who did what

**The maintainer (human)** set the goal and the direction. The goal: a world beyond the colony that lives on its own,
in the spirit of Dwarf Fortress. Factions, towns, markets and people change over time, and the storyteller's raids,
caravans and visitors come from that world. The maintainer named the mods it should work with (Hospitality and the
"space visitors" mod, which turned out to be Rimsential Spaceports), chose what to build next, decided what to
publish, and supplied the game install. They did not write the code by hand. The code has not had a line-by-line
human review.

**AI agents** did the research, design, implementation, testing, reviews, icons and documentation:

| Role | Tool and model |
|---|---|
| Design, implementation, fixes, test harness, test runs, docs | [Claude Code](https://claude.com/claude-code) with Claude Opus 5.5 |
| Research (other world-simulation mods, Hospitality and Spaceports, the 1.7 game API) | Claude agents run by a local agent orchestrator in isolated git worktrees |
| Independent reviews | Claude Fable 5.1 and GPT-6 Astra (OpenAI Codex CLI) reviewed the design, then each batch of features |
| Icons | Drawn by code: `tools/gen-art.py` (written by GPT-6.1 Sol in the OpenAI Codex CLI, after a first glyph version by Claude) paints shaded greyscale icons to PNG that the game tints with the faction colour. No image-generation model was used |

The game's code was decompiled locally with ILSpy so the agents could read vanilla methods. The decompiled code was
used only as reference and is not in this repository.

## Process

1. **Research.** Agents surveyed existing mods that simulate the world (RimWar, World Domination, Dynamic Diplomacy,
   Empire and others) for what players liked and complained about. They read the source of Hospitality (Continued),
   Rimsential Spaceports (Continued) and Space Services to plan the integrations, and wrote a reference of the 1.7
   game API.
2. **Design.** A design document went to both reviewers before any code was written. Their amendments covered save
   keys and IDs, using vanilla goodwill as the single source of truth for war and peace, the incident worker swap
   protocol, applying the price factor exactly once, and keeping strong factions from snowballing. All were
   adopted.
3. **Milestones.** The world simulation came first: settlement economy, faction states, wars, expansion and history
   before landing. Then world parties on the map and the bridge from storyteller incidents to them, markets,
   recurring figures, the integrations, and the colony-facing features (diplomacy, demands, prisoners, feuds,
   careers, legends).
4. **Review and fix rounds.** After each batch of features, a reviewer read the change and reported defects. There
   were 16 Fable reviews and 5 Astra reviews, counting the design reviews (Astra ran out of credits several times;
   Fable covered those rounds). They found real bugs, for example:
   - a succession split that changed the faction list while it was being iterated;
   - a peace gift that was credited twice;
   - a retiring figure whose heir could be pruned at the figure cap;
   - a colony whose only colonists were away on a mercenary contract counting as lost.

   Every finding was fixed or explicitly declined with a reason, and most fixes got a regression check in the
   autotest.

## How it was tested

**Offline def check** (`make lint`, no game process). It loads the real `Assembly-CSharp.dll` and checks every XML
def field and cross-reference of the mod against the game's types.

**In-game autotests** (`tools/autotest.sh`, `make test`). The real game runs headless: the Windows 1.7.4980 build under
Wine, or the native Linux 1.7.4979 build, both offline, with all expansions. Each run starts a quick game on a fixed
seed, runs the given in-game days at full speed, saves and reloads halfway, and checks invariants. It fails on any
error in the log that isn't a known vanilla or third-party one. The regression suite has 10 plans, each aimed at a
group of features, with forced events where waiting would take too long; the README lists them. All 10 passed with 0
errors on the code published here. Hospitality and Spaceports were built from source for 1.7 to run the
two integration plans.

Longer runs:

- **life:** 30 days on Peaceful, nothing forced. It shows what happens on its own.
- **soak:** a 30-day run.
- **stress:** a full planet of about 920 towns and 314,000 people after 5 history years. The mod took about 1.2 s of
  the 25 s of tick time per in-game day.

All three passed with 0 errors.

**UI tour** (`tools/uitour.sh`): a windowed run under Xvfb with software Vulkan that opens every Chronicle page,
dialog and inspect tab and takes a screenshot of each. The screenshots in the README come from it.

## What has not been tested

- **Real play.** As of publishing it has only run in the automated harness. It has not been played on a normal install
  or on a real long-running save. Balance (prices, war frequency, how often visitors come) is tuned from automated
  runs only.
- **Large mod lists.** Other mods that replace the raid, trader or visitor incident workers are detected and left
  alone, but no real modded save was tried. Only Hospitality and Spaceports were tested together with it.
- **Old saves.** Adding the mod to a running save and the *Prepare this save for removing the mod* switch are covered
  by code and the autotest, not by a real save.
- **Multiplayer.** Not considered.

Use it on a copy of your save. Every subsystem can be switched off in the mod settings.
