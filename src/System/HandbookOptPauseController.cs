using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace HandbookOpt
{
    /// <summary>
    /// Optional native single-player pause shared by the recipe panel and guide browser.
    /// It only resumes the game if this controller was the code that paused it.
    /// Multiplayer sessions never call PauseGame.
    /// </summary>
    internal static class HandbookOptPauseController
    {
        static readonly HashSet<GuiDialog> openDialogs = new HashSet<GuiDialog>();
        static ICoreClientAPI capi;
        static bool enabled;
        static bool pausedByController;

        public static bool CanPause(ICoreClientAPI api)
        {
            try { return api != null && api.IsSinglePlayer; }
            catch { return false; }
        }

        public static bool Enabled => enabled && CanPause(capi);

        public static void DialogOpened(GuiDialog dialog, ICoreClientAPI api)
        {
            if (dialog == null || !CanPause(api)) return;
            capi = api;
            openDialogs.Add(dialog);
            Apply();
        }

        public static void DialogClosed(GuiDialog dialog, ICoreClientAPI api)
        {
            if (dialog != null) openDialogs.Remove(dialog);
            if (CanPause(api)) capi = api;
            Apply();
        }

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
            Apply();
            SyncSwitches();
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

            bool shouldPause = enabled && openDialogs.Count > 0;
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
            openDialogs.Clear();
            SyncSwitches();
            capi = null;
        }

        static void SyncSwitches()
        {
            try { HandbookOptMod.Picker?.SyncPauseSwitch(); } catch { }
            try { HandbookOptMod.GuideBrowser?.SyncPauseSwitch(); } catch { }
        }
    }
}
