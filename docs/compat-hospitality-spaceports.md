# Hospitality and Spaceports with Living World (RimWorld 1.7)

## Hospitality (Continued)

- Mod: "Hospitality (Continued)" by Zaljerem, packageId `Orion.Hospitality` (GPL-3), github.com/Zaljerem/Hospitality.
  It has 1.7 load folders upstream; no fix needed.
- Living World integration (soft, by reflection, `Compat/HospitalityCompat.cs`):
  - Hospitality's visitor groups set out from a real friendly settlement and walk to the colony as a world party
    (prefix on `Hospitality.IncidentWorker_VisitorGroup.TryExecuteWorker`; the arrival runs Hospitality's own worker,
    including its "is it safe?" prompt and bed checks).
  - Guests' lords (`LordJob_VisitColony`) are tied to their home settlement; when they leave, their goodwill change
    moves the colony's fame in that town (better prices there, chronicle entries), and they travel home.
- Verified in game (native Linux 1.7.4979 build, Hospitality built from source): plan `hospitality` passes — visitors
  dispatched, arrive, guests spawn, merchants/warlords recur, save/load round trip identical, no errors.

## Rimsential Spaceports (Continued) — the "space visitors"

- Mod: "Rimsential - Spaceports (Continued)" by Zaljerem, packageId `zal.spaceports` (MIT),
  github.com/Zaljerem/RimsentialSpaceportsContinued. Visitors, traders and Hospitality guests arrive by shuttle at
  landing pads.
- Upstream state (1.1.5): `LoadFolders.xml` and `supportedVersions` stop at 1.6. RimWorld 1.7 then falls back to the
  1.6 folder (its 1.6 DLL compiled against Unity).
- Problems found building it for 1.7:
  1. `SharpUtils` uses `Material`; in 1.7 that type lives in the new engine assembly `Waylith.dll` (UnityStubs only
     forwards it), so the build needs a `Waylith` reference.
  2. .NET 10 adds `System.Reflection.MethodInvoker`, which makes Spaceports' unqualified `MethodInvoker` (HarmonyLib's)
     ambiguous at compile time.
  3. No `v1.7` load folder.
  Its Hospitality hooks find Hospitality by display name ("Hospitality (Continued)") and patch methods by string name;
  they still resolve against the current Hospitality.
- Fix: `tools/compat/build-spaceports-1.7.sh <spaceports checkout> <output mod dir> [RimWorld dir]` builds a 1.7 copy
  from upstream source with only those three changes (`tools/compat/spaceports/`). These are also what to report
  upstream.
- Verified in game with Hospitality + Living World (plan `spaceports`): loads with no errors, its Hospitality patches
  apply, a powered spaceport (pad + beacon) is clear for landing, shuttles land, visitor groups arrive.
- Living World side (`Compat/SpaceportsCompat.cs`): when the colony runs a spaceport, visitors and traders of
  non-tribal factions are left to Spaceports (they fly in) instead of also walking across the world map; shuttle
  visitor/trader lords (`LordJob_ShuttleVisitColony`, `LordJob_ShuttleTradeWithColony`) are tied to their home
  settlement like any other group.

## Test setup (local)

- Built test copies live in `.work/testmods/` (gitignored) and are symlinked into the native Linux build's `Mods/`
  (`~/RimWorld-linux/Mods/Hospitality`, `~/RimWorld-linux/Mods/Spaceports`); save-data folders `.work/savedata-hosp`
  and `.work/savedata-sp` enable them.
- Hospitality was built from source with an own project file (net10.0, Krafs.Publicizer, Multiplayer API from NuGet),
  not with its shipped DLLs.
