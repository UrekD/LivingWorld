using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// The world view's button row gets the world overlay button (the mode is saved in the mod's settings). A click opens a menu of the modes
    /// (<see cref="WorldOverlay.MenuOptions"/>); the tooltip names the mode showing, and the button is lit while any mode but Off does.
    /// </summary>
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class Patch_PlaySettings_OverlayButton
    {
        private static readonly Color OnBackground = new Color(1f, 1f, 1f, 0.25f);
        private static Texture2D icon;

        public static void Postfix(WidgetRow row, bool worldView)
        {
            if (!worldView || Current.ProgramState != ProgramState.Playing || !WorldOverlay.ButtonVisible) return;
            LivingWorldSettings stored = LivingWorldMod.Stored; // not Settings: that may be the shared all-off set
            WorldOverlayMode mode = stored.overlayMode;
            icon ??= ContentFinder<Texture2D>.Get("LivingWorld/UI/Borders", reportFailure: true) ?? BaseContent.BadTex;
            string tip = WorldOverlay.ModeTooltip(mode, mode == WorldOverlayMode.Shortage ? WorldOverlay.ShortageCommodity : null);
            Color? on = mode != WorldOverlayMode.Off ? OnBackground : (Color?)null; // the button shows that an overlay is on, not only its tooltip
            if (row.ButtonIcon(icon, tip, backgroundColor: on)) Find.WindowStack.Add(new FloatMenu(WorldOverlay.MenuOptions()));
        }
    }
}
