using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Config;

namespace HandbookOpt
{
    /// <summary>
    /// Captura el arrastre de la ventana de crafteo de HandbookOpt mientras el botón izquierdo
    /// permanece pulsado. El título nativo solo recibe OnMouseMove cuando el gestor GUI
    /// entrega el evento a su diálogo; una ventana superpuesta puede consumirlo y hacer
    /// que el arrastre parezca pegarse a sus bordes. Durante el arrastre capturado movemos
    /// los Bounds directamente desde el evento de ratón de bajo nivel y consumimos ese
    /// evento para que otra GUI no robe la captura.
    /// </summary>
    internal static class HandbookOptDialogDragCapture
    {
        const double DragThresholdPixels = 3.0;

        static ICoreClientAPI capi;
        static GuiElementDialogTitleBar candidateTitleBar;
        static ElementBounds candidateRootBounds;
        static double startMouseX;
        static double startMouseY;
        static double startBoundsX;
        static double startBoundsY;
        static bool dragging;

        static readonly FieldInfo BaseComposerField = AccessTools.Field(typeof(GuiElementDialogTitleBar), "baseComposer");
        static readonly FieldInfo MovingField = AccessTools.Field(typeof(GuiElementDialogTitleBar), "moving");

        public static bool IsDraggingPicker => dragging;

        // True desde que se pulsa la barra de título del selector hasta soltar el botón.
        public static bool IsTitleBarCaptured => candidateTitleBar != null;

        static bool IsManagedComposer(string name)
        {
            return string.Equals(name, "handbookopt-picker", StringComparison.Ordinal);
        }

        public static void Initialize(ICoreClientAPI api)
        {
            if (api == null) return;
            if (ReferenceEquals(capi, api)) return;

            DetachEvents();
            capi = api;
            capi.Event.MouseMove += OnRawMouseMove;
            capi.Event.MouseUp += OnRawMouseUp;
        }

        static void DetachEvents()
        {
            if (capi == null) return;
            try { capi.Event.MouseMove -= OnRawMouseMove; } catch { }
            try { capi.Event.MouseUp -= OnRawMouseUp; } catch { }
        }

        public static void Dispose()
        {
            Cancel();
            DetachEvents();
            capi = null;
        }

        public static void Cancel()
        {
            if (candidateTitleBar != null)
            {
                try { MovingField?.SetValue(candidateTitleBar, false); } catch { }
            }
            ClearCapture();
        }

        static void ClearCapture()
        {
            candidateTitleBar = null;
            candidateRootBounds = null;
            startMouseX = 0;
            startMouseY = 0;
            startBoundsX = 0;
            startBoundsY = 0;
            dragging = false;
        }

        public static bool ShouldSuppressNativeMove(GuiElementDialogTitleBar titleBar)
        {
            return titleBar != null && ReferenceEquals(titleBar, candidateTitleBar);
        }

        public static void OnTitleBarMouseDown(GuiElementDialogTitleBar titleBar, MouseEvent args)
        {
            try
            {
                if (titleBar == null || args == null || args.Button != EnumMouseButton.Left) return;
                if (candidateTitleBar != null || !titleBar.Movable) return;
                if (MovingField == null || !(MovingField.GetValue(titleBar) is bool moving) || !moving) return;

                GuiComposer composer = BaseComposerField?.GetValue(titleBar) as GuiComposer;
                string composerName = composer?.DialogName;
                if (!IsManagedComposer(composerName)) return;

                ElementBounds root = titleBar.Bounds?.ParentBounds;
                if (root == null) return;

                candidateTitleBar = titleBar;
                candidateRootBounds = root;
                startMouseX = args.X;
                startMouseY = args.Y;
                startBoundsX = root.fixedX;
                startBoundsY = root.fixedY;
                dragging = false;
            }
            catch (Exception e)
            {
                try { capi?.Logger?.Debug("[HandbookOpt] No se pudo iniciar la captura de arrastre: {0}", e.Message); } catch { }
                ClearCapture();
            }
        }

        static bool IsOwnerStillOpen()
        {
            try { return HandbookOptMod.Picker != null && HandbookOptMod.Picker.IsOpened(); }
            catch { return false; }
        }

        static void OnRawMouseMove(MouseEvent args)
        {
            if (candidateTitleBar == null || args == null) return;
            if (!IsOwnerStillOpen() || candidateRootBounds == null)
            {
                Cancel();
                return;
            }

            try
            {
                double dx = args.X - startMouseX;
                double dy = args.Y - startMouseY;
                if (!dragging && dx * dx + dy * dy < DragThresholdPixels * DragThresholdPixels) return;

                dragging = true;
                double scale = RuntimeEnv.GUIScale;
                if (scale <= 0) scale = 1;

                // Usar la posición inicial evita saltos aunque el evento de arrastre
                // normal se haya consumido justo al cruzar el límite de otra ventana.
                candidateRootBounds.fixedX = startBoundsX + dx / scale;
                candidateRootBounds.fixedY = startBoundsY + dy / scale;
                candidateRootBounds.CalcWorldBounds();

                // Captura exclusiva durante el movimiento: el inventario, el mapa u otra
                // GUI no deben consumir el siguiente MouseMove mientras arrastramos.
                args.Handled = true;
            }
            catch (Exception e)
            {
                try { capi?.Logger?.Debug("[HandbookOpt] Error al mover una ventana capturada: {0}", e.Message); } catch { }
                Cancel();
            }
        }

        static void OnRawMouseUp(MouseEvent args)
        {
            if (candidateTitleBar == null || args == null || args.Button != EnumMouseButton.Left) return;

            bool wasDragging = dragging;
            try
            {
                if (wasDragging)
                {
                    // La suelta también se consume globalmente, porque el puntero puede
                    // estar encima de otra ventana y esta podría quedarse con el MouseUp.
                    args.Handled = true;
                    if (candidateRootBounds != null)
                    {
                        candidateRootBounds.CalcWorldBounds();
                        capi?.Gui?.SetDialogPosition("handbookopt-picker",
                            new Vec2i((int)candidateRootBounds.fixedX, (int)candidateRootBounds.fixedY));
                    }
                }
            }
            catch (Exception e)
            {
                try { capi?.Logger?.Debug("[HandbookOpt] Error al finalizar el arrastre: {0}", e.Message); } catch { }
            }
            finally
            {
                // También limpiar el estado nativo para un clic breve cuyo MouseUp haya
                // caído encima de otra ventana; el movimiento custom nunca se activó en ese caso.
                try { MovingField?.SetValue(candidateTitleBar, false); } catch { }
                ClearCapture();
            }
        }
    }

    [HarmonyPatch(typeof(GuiElementDialogTitleBar), "OnMouseDown", new Type[] { typeof(ICoreClientAPI), typeof(MouseEvent) })]
    internal static class HandbookOptTitleBarDragStartPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GuiElementDialogTitleBar __instance, MouseEvent args)
        {
            HandbookOptDialogDragCapture.OnTitleBarMouseDown(__instance, args);
        }
    }

    [HarmonyPatch(typeof(GuiElementDialogTitleBar), "OnMouseMove", new Type[] { typeof(ICoreClientAPI), typeof(MouseEvent) })]
    internal static class HandbookOptTitleBarDragMovePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(GuiElementDialogTitleBar __instance)
        {
            // Mientras capturemos este título, el movimiento ocurre desde MouseMove global;
            // evita que el mecanismo integrado aplique un segundo desplazamiento.
            return !HandbookOptDialogDragCapture.ShouldSuppressNativeMove(__instance);
        }
    }
}
