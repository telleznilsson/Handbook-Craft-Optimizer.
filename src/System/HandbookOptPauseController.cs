using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace HandbookOpt
{
    /// <summary>
    /// Pausa nativa opcional (solo singleplayer) compartida entre el selector de recetas del
    /// mod y el Handbook vanilla. El estado "pausado" es único: si se activa en cualquiera de
    /// las dos ventanas, la otra lo refleja; si se desactiva en una, se desactiva en la otra.
    /// Solo se reanuda el juego si fue este controlador quien lo pausó, y en multijugador
    /// nunca se llama a PauseGame.
    /// </summary>
    internal static class HandbookOptPauseController
    {
        static readonly HashSet<GuiDialog> openDialogs = new HashSet<GuiDialog>();
        static ICoreClientAPI capi;
        static bool enabled;
        static bool pausedByController;
        // Último estado de IsGamePaused observado mientras el Handbook vanilla está abierto.
        static bool lastObservedPaused;
        static bool vanillaWasOpen;

        const string VanillaPauseToggleName = "pausegame";

        public static bool CanPause(ICoreClientAPI api)
        {
            try { return api != null && api.IsSinglePlayer; }
            catch { return false; }
        }

        public static bool Enabled => enabled && CanPause(capi);

        static GuiDialog ignoredHandbook;

        static bool VanillaHandbook(out GuiDialogHandbook handbook)
        {
            handbook = null;
            try
            {
                var opened = capi?.Gui?.OpenedGuis;
                if (opened == null) return false;
                foreach (GuiDialog d in opened)
                {
                    if (d is GuiDialogHandbook h && !ReferenceEquals(h, ignoredHandbook) && h.IsOpened()) { handbook = h; return true; }
                }
            }
            catch { }
            return false;
        }

        public static void DialogOpened(GuiDialog dialog, ICoreClientAPI api)
        {
            if (dialog == null || !CanPause(api)) return;
            capi = api;
            openDialogs.Add(dialog);
            // Si el Handbook vanilla ya tenía el juego pausado, el selector nace con el
            // interruptor activado en lugar de mostrar un estado distinto.
            if (!pausedByController && VanillaHandbook(out _) && SafePaused()) enabled = true;
            Apply();
            SyncSwitches();
        }

        public static void DialogClosed(GuiDialog dialog, ICoreClientAPI api)
        {
            if (dialog != null) openDialogs.Remove(dialog);
            if (CanPause(api)) capi = api;
            Apply();
            SyncSwitches();
        }

        /// <summary>Llamado por el interruptor del selector de recetas.</summary>
        public static void SetEnabled(ICoreClientAPI api, bool value)
        {
            if (!CanPause(api))
            {
                enabled = false;
                SyncSwitches();
                return;
            }

            capi = api;
            enabled = value;
            // The vanilla Handbook toggle also stores the inverse preference under this key.
            // Keep it aligned when the selector is the control the player used, otherwise a
            // later Handbook page rebuild could restore the previous visual state.
            try { capi.Settings.Bool["noHandbookPause"] = !value; } catch { }

            if (!value && !pausedByController && SafePaused() && VanillaHandbook(out _))
            {
                // El pausado lo hizo el Handbook vanilla: el interruptor del selector manda.
                try { capi.PauseGame(false); } catch { }
            }

            Apply();
            lastObservedPaused = SafePaused();
            SyncSwitches();
        }


        /// <summary>
        /// Called immediately after the vanilla Handbook's own pause-toggle handler. The
        /// vanilla handler has already called PauseGame(on) and updated noHandbookPause, so
        /// adopt its state directly instead of relying on a game-tick poll while paused.
        /// </summary>
        public static void VanillaPauseToggled(ICoreClientAPI api, bool value)
        {
            if (!CanPause(api)) return;
            capi = api;
            enabled = value;
            // The most recent pause action came directly from the vanilla UI. Ownership is no
            // longer exclusively ours; an explicit off action has already resumed the game.
            pausedByController = false;
            vanillaWasOpen = true;
            lastObservedPaused = SafePaused();
            SyncSwitches();
        }

        /// <summary>Llamado tras abrirse/cerrarse el Handbook vanilla (parches Harmony).</summary>
        public static void VanillaHandbookChanged(ICoreClientAPI api, bool opened, GuiDialog instance = null)
        {
            if (!CanPause(api)) return;
            capi = api;
            // Al cerrarse, ignorar esa instancia aunque IsOpened() aún no haya cambiado.
            ignoredHandbook = opened ? null : instance;
            try {
                VanillaHandbookChangedCore(opened);
            }
            finally { ignoredHandbook = null; }
        }

        static void VanillaHandbookChangedCore(bool opened)
        {

            if (opened)
            {
                vanillaWasOpen = true;
                // Si el Handbook vanilla pausó el juego al abrirse, reflejarlo en el selector.
                if (!pausedByController && SafePaused()) enabled = true;
                // Si el selector tenía la pausa activa, aplicarla también al Handbook.
                Apply();
            }
            else
            {
                vanillaWasOpen = false;
                // El Handbook vanilla reanuda el juego al cerrarse. Si el selector sigue abierto
                // con la pausa activa, hay que volver a pausar; si no, soltar el estado.
                if (!SafePaused()) pausedByController = false;
                if (openDialogs.Count == 0) enabled = false;
                Apply();
            }

            lastObservedPaused = SafePaused();
            SyncSwitches();
        }

        /// <summary>Sondeo ligero (cada 200 ms) para detectar el interruptor del Handbook vanilla.</summary>
        public static void Poll(ICoreClientAPI api)
        {
            if (!CanPause(api)) return;
            capi = api;

            bool vanillaOpen = VanillaHandbook(out _);
            if (!vanillaOpen)
            {
                vanillaWasOpen = false;
                return;
            }

            bool paused = SafePaused();
            if (!vanillaWasOpen)
            {
                vanillaWasOpen = true;
                lastObservedPaused = paused;
                return;
            }
            if (paused == lastObservedPaused) return;
            lastObservedPaused = paused;

            if (paused)
            {
                // El usuario activó la pausa en el Handbook vanilla.
                if (!pausedByController) enabled = true;
            }
            else
            {
                // El usuario la desactivó en el Handbook vanilla (o se cerró desde fuera).
                enabled = false;
                pausedByController = false;
            }
            SyncSwitches();
        }

        static bool SafePaused()
        {
            try { return capi != null && capi.IsGamePaused; }
            catch { return false; }
        }

        static void Apply()
        {
            if (!CanPause(capi))
            {
                // A multiplayer session must never be paused through this controller.
                enabled = false;
                openDialogs.Clear();
                pausedByController = false;
                return;
            }

            if (capi.World?.Player == null)
            {
                // The world is unloading. Release a pause we own before dropping its state.
                if (pausedByController)
                {
                    try { if (capi.IsGamePaused) capi.PauseGame(false); }
                    catch { }
                }
                openDialogs.Clear();
                enabled = false;
                pausedByController = false;
                return;
            }

            bool anyOpen = openDialogs.Count > 0 || VanillaHandbook(out _);
            bool shouldPause = enabled && anyOpen;
            try
            {
                if (shouldPause)
                {
                    // Respect a pause that was already active (for example, from the
                    // native pause menu); only release pauses we initiated ourselves.
                    if (!capi.IsGamePaused)
                    {
                        capi.PauseGame(true);
                        pausedByController = true;
                    }
                }
                else if (pausedByController)
                {
                    if (capi.IsGamePaused) capi.PauseGame(false);
                    pausedByController = false;
                }
            }
            catch (Exception e)
            {
                try { capi.Logger.Warning("[HandbookOpt] Could not update single-player pause state: {0}", e.Message); }
                catch { }
                pausedByController = false;
            }
        }

        public static void Reset(ICoreClientAPI api)
        {
            if (CanPause(api)) capi = api;
            if (pausedByController && CanPause(capi))
            {
                try { if (capi.IsGamePaused) capi.PauseGame(false); }
                catch { }
            }
            pausedByController = false;
            enabled = false;
            vanillaWasOpen = false;
            lastObservedPaused = false;
            openDialogs.Clear();
            SyncSwitches();
            capi = null;
        }

        static void SyncSwitches()
        {
            try { HandbookOptMod.Picker?.SyncPauseSwitch(); } catch { }
            SyncVanillaSwitch();
        }

        /// <summary>
        /// Synchronizes the actual vanilla Handbook control. In Vintage Story the control is a
        /// GuiElementToggleButton named "pausegame" (not a GuiElementSwitch). The Handbook has
        /// separate overview and detail composers, so update every composer we can reach.
        /// </summary>
        static void SyncVanillaSwitch()
        {
            try
            {
                if (!VanillaHandbook(out GuiDialogHandbook handbook)) return;
                bool value = enabled && CanPause(capi);
                var composers = new HashSet<GuiComposer>();

                try
                {
                    if (handbook.SingleComposer != null) composers.Add(handbook.SingleComposer);
                    var registered = handbook.Composers?.Values;
                    if (registered != null)
                    {
                        foreach (GuiComposer composer in registered)
                        {
                            if (composer != null) composers.Add(composer);
                        }
                    }
                }
                catch { }

                // GuiDialogHandbook owns overviewGui and detailViewGui fields. Include these as
                // a fallback because an inactive page composer may not be the SingleComposer.
                foreach (FieldInfo field in typeof(GuiDialogHandbook).GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (!typeof(GuiComposer).IsAssignableFrom(field.FieldType)) continue;
                        if (field.GetValue(handbook) is GuiComposer composer) composers.Add(composer);
                    }
                    catch { }
                }

                foreach (GuiComposer composer in composers)
                {
                    try
                    {
                        GuiElementToggleButton toggle = composer.GetToggleButton(VanillaPauseToggleName);
                        if (toggle != null && toggle.On != value) toggle.SetValue(value);
                    }
                    catch { }
                }
            }
            catch { }
        }

    }
}
