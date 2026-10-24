using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace HandbookOpt
{
    /// <summary>
    /// Evita que los diálogos que quedan detrás del selector de recetas reciban ratón
    /// cuando el puntero está sobre la superficie visible del selector. GuiDialog no
    /// ofrece una modalidad global: varios diálogos abiertos pueden recibir el mismo
    /// evento, y los overrides de cada mod requieren interceptarse individualmente.
    /// </summary>
    internal static class HandbookOptModalInputGuard
    {
        static bool installed;
        static readonly HashSet<MethodBase> patched = new HashSet<MethodBase>();

        static readonly Type[] NoArgs = Type.EmptyTypes;
        static readonly Type[] MouseArgs = { typeof(MouseEvent) };
        static readonly Type[] WheelArgs = { typeof(MouseWheelEventArgs) };
        static readonly Type[] SlotArgs = { typeof(ItemSlot) };

        static MethodInfo mousePrefix;
        static MethodInfo wheelPrefix;
        static MethodInfo slotPrefix;
        static MethodInfo receiveMousePrefix;
        static MethodInfo moveMousePrefix;
        static MethodInfo moveMousePostfix;

        public static void Install(ICoreClientAPI api, Harmony harmony)
        {
            if (installed || api == null || harmony == null) return;

            mousePrefix = AccessTools.Method(typeof(HandbookOptModalInputGuard), nameof(MouseEventPrefix));
            wheelPrefix = AccessTools.Method(typeof(HandbookOptModalInputGuard), nameof(MouseWheelPrefix));
            slotPrefix = AccessTools.Method(typeof(HandbookOptModalInputGuard), nameof(SlotEventPrefix));
            receiveMousePrefix = AccessTools.Method(typeof(HandbookOptModalInputGuard), nameof(ShouldReceiveMouseEventsPrefix));
            moveMousePrefix = AccessTools.Method(typeof(HandbookOptModalInputGuard), nameof(MouseMovePrefix));
            moveMousePostfix = AccessTools.Method(typeof(HandbookOptModalInputGuard), nameof(MouseMovePostfix));

            int scannedTypes = 0;
            int patchedMethods = 0;
            int failures = 0;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // Patch the API base implementations as a fallback for dialogs that do not
            // override the event. Overrides are also patched below, including mod GUIs.
            PatchType(typeof(GuiDialog), harmony, ref patchedMethods, ref failures);

            foreach (Assembly assembly in assemblies)
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                if (types == null) continue;
                foreach (Type type in types)
                {
                    if (type == null || type == typeof(GuiDialog) || type.IsInterface || type.ContainsGenericParameters) continue;
                    try
                    {
                        if (!typeof(GuiDialog).IsAssignableFrom(type)) continue;
                        scannedTypes++;
                        PatchType(type, harmony, ref patchedMethods, ref failures);
                    }
                    catch { /* Un tipo de un mod no debe impedir proteger los demás diálogos. */ }
                }
            }

            installed = true;
            api.Logger.Notification(
                "[HandbookOpt][ModalInputGuard] Bloqueo de clics de fondo instalado: {0} tipos GUI inspeccionados, {1} métodos interceptados, {2} fallos aislados.",
                scannedTypes, patchedMethods, failures);
        }

        static void PatchType(Type type, Harmony harmony, ref int patchedMethods, ref int failures)
        {
            PatchMethod(type, nameof(GuiDialog.OnMouseDown), MouseArgs, mousePrefix,
                harmony, ref patchedMethods, ref failures);
            PatchMethod(type, nameof(GuiDialog.OnMouseUp), MouseArgs, mousePrefix,
                harmony, ref patchedMethods, ref failures);
            PatchMethod(type, nameof(GuiDialog.OnMouseMove), MouseArgs, moveMousePrefix,
                harmony, ref patchedMethods, ref failures, moveMousePostfix);
            PatchMethod(type, nameof(GuiDialog.OnMouseWheel), WheelArgs, wheelPrefix,
                harmony, ref patchedMethods, ref failures);
            PatchMethod(type, nameof(GuiDialog.OnMouseClickSlot), SlotArgs, slotPrefix,
                harmony, ref patchedMethods, ref failures);
            PatchMethod(type, nameof(GuiDialog.OnMouseEnterSlot), SlotArgs, slotPrefix,
                harmony, ref patchedMethods, ref failures);
        }

        static void PatchMethod(
            Type type,
            string methodName,
            Type[] parameterTypes,
            MethodInfo prefix,
            Harmony harmony,
            ref int patchedMethods,
            ref int failures,
            MethodInfo postfix = null)
        {
            MethodInfo target;
            try
            {
                target = type.GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null,
                    parameterTypes,
                    null);
            }
            catch { return; }

            if (target == null || target.IsStatic || prefix == null || !patched.Add(target)) return;

            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                    postfix: postfix != null ? new HarmonyMethod(postfix) { priority = Priority.Last } : null);
                patchedMethods++;
            }
            catch
            {
                patched.Remove(target);
                failures++;
            }
        }

        static bool IsPickerSurfaceAt(double x, double y)
        {
            try
            {
                CraftPickerDialog picker = HandbookOptMod.Picker;
                if (picker == null || !picker.IsOpened() || picker.SingleComposer == null) return false;

                ElementBounds bounds = picker.SingleComposer.Bounds;
                if (bounds != null && bounds.PointInside(x, y)) return true;

                // El menú desplegable puede sobresalir por debajo del rectángulo principal.
                // También cuenta como superficie del diálogo delantero para que una ranura
                // del inventario que quede detrás de la lista no reciba clics.
                GuiElementListMenu menu = picker.SingleComposer.GetDropDown("modfilter")?.listMenu;
                return menu != null && menu.IsOpened && menu.IsPositionInside((int)x, (int)y);
            }
            catch { return false; }
        }

        /// <summary>
        /// Indica si <paramref name="front"/> se dibuja por encima de <paramref name="back"/>
        /// según el orden real: primero DrawOrder y, si empatan, la posición en OpenedGuis
        /// (el gestor mueve al final la ventana que recibe el foco). Si hay dudas se usa Focused.
        /// </summary>
        static bool IsInFrontOf(GuiDialog front, GuiDialog back)
        {
            double fo = front.DrawOrder;
            double bo = back.DrawOrder;
            if (fo != bo) return fo > bo;

            // Empate de capa: la ventana con foco es la que está delante.
            if (front.Focused && !back.Focused) return true;
            if (back.Focused && !front.Focused) return false;

            try
            {
                var opened = HandbookOptMod.Capi?.Gui?.OpenedGuis;
                if (opened != null)
                {
                    int fi = opened.IndexOf(front);
                    int bi = opened.IndexOf(back);
                    if (fi >= 0 && bi >= 0 && fi != bi) return fi > bi;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Superficie visible de un diálogo en (x, y): cualquiera de sus compositores
        /// (el del personaje tiene tres: personaje, estadísticas y medio ambiente) o, en el
        /// selector, también la lista desplegable que sobresale.
        /// </summary>
        static bool SurfaceContains(GuiDialog dialog, double x, double y)
        {
            try
            {
                if (dialog is CraftPickerDialog) return IsPickerSurfaceAt(x, y);

                GuiComposer single = dialog.SingleComposer;
                if (single?.Bounds != null && single.Bounds.PointInside(x, y)) return true;

                var composers = dialog.Composers;
                if (composers != null)
                {
                    foreach (GuiComposer c in composers.Values)
                    {
                        if (c?.Bounds != null && c.Bounds.PointInside(x, y)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// True si otra ventana abierta cubre (x, y) y está por delante de <paramref name="target"/>.
        /// Es simétrico: sirve para el inventario cubierto por el selector y para el selector
        /// cubierto por el inventario, el personaje, el Handbook, etc.
        /// </summary>
        internal static bool IsCoveredByFrontDialog(GuiDialog target, double x, double y)
        {
            try
            {
                ICoreClientAPI api = HandbookOptMod.Capi;
                var opened = api?.Gui?.OpenedGuis;
                if (opened == null || target == null) return false;

                // Durante el arrastre del selector, ninguna otra ventana debe recibir el ratón.
                CraftPickerDialog picker = HandbookOptMod.Picker;
                if (HandbookOptDialogDragCapture.IsDraggingPicker && picker != null && !ReferenceEquals(target, picker))
                    return IsPickerSurfaceAt(x, y);

                // Copia: el evento puede abrir/cerrar diálogos mientras se itera.
                var snapshot = new List<GuiDialog>(opened);
                foreach (GuiDialog other in snapshot)
                {
                    if (other == null || ReferenceEquals(other, target)) continue;
                    if (!other.IsOpened() || other.DialogType != EnumDialogType.Dialog) continue;
                    if (!SurfaceContains(other, x, y)) continue;

                    bool otherInFront;
                    try { otherInFront = IsInFrontOf(other, target); }
                    catch { otherInFront = ReferenceEquals(other, picker); }
                    if (otherInFront) return true;
                }
            }
            catch { }
            return false;
        }

        static bool ShouldBlock(GuiDialog target, double x, double y)
        {
            return IsCoveredByFrontDialog(target, x, y);
        }

        const int OffScreen = -100000;

        static bool IsAnyMouseButtonDown()
        {
            try
            {
                MouseButtonState b = HandbookOptMod.Capi?.Input?.MouseButton;
                return b != null && (b.Left || b.Right);
            }
            catch { return false; }
        }

        // Mover el ratón: no basta con saltarse el evento, porque la ventana de atrás se
        // quedaría con la última ranura marcada. Se le entrega una posición fuera de pantalla
        // para que limpie su hover, y el postfix restaura las coordenadas reales para el resto.
        static bool MouseMovePrefix(GuiDialog __instance, MouseEvent args, out int[] __state)
        {
            __state = null;
            if (args == null) return true;

            // Con un botón pulsado puede haber un arrastre en curso (título de una ventana,
            // reparto de pilas...). Esos arrastres calculan el desplazamiento con las coordenadas
            // del evento: falsearlas lanzaría la ventana fuera de pantalla y parpadearía.
            // Un arrastre debe llegar siempre intacto a quien lo empezó.
            if (IsAnyMouseButtonDown()) return true;

            if (!ShouldBlock(__instance, args.X, args.Y)) return true;

            __state = new[] { args.X, args.Y };
            args.X = OffScreen;
            args.Y = OffScreen;
            return true;
        }

        static void MouseMovePostfix(MouseEvent args, int[] __state)
        {
            if (args == null || __state == null) return;
            args.X = __state[0];
            args.Y = __state[1];
        }

        static bool MouseEventPrefix(GuiDialog __instance, [HarmonyArgument(0)] MouseEvent args)
        {
            if (args == null) return true;
            // No marcar args.Handled aquí: el evento aún debe llegar al selector que está
            // delante. Solo omitimos la implementación del diálogo de fondo.
            return !ShouldBlock(__instance, args.X, args.Y);
        }

        static bool MouseWheelPrefix(GuiDialog __instance, [HarmonyArgument(0)] MouseWheelEventArgs args)
        {
            // MouseWheelEventArgs no expone coordenadas; el API del cliente mantiene
            // el puntero actual en MouseX/MouseY.
            ICoreClientAPI api = HandbookOptMod.Capi;
            if (api?.Input == null) return true;
            return !ShouldBlock(__instance, api.Input.MouseX, api.Input.MouseY);
        }

        static bool SlotEventPrefix(GuiDialog __instance, ref bool __result)
        {
            try
            {
                ICoreClientAPI api = HandbookOptMod.Capi;
                if (api?.Input != null && ShouldBlock(__instance, api.Input.MouseX, api.Input.MouseY))
                {
                    // La ranura pertenece a la GUI que queda detrás; consumir su callback
                    // sin ejecutar el movimiento/cambio de pila del inventario oculto.
                    __result = true;
                    return false;
                }
            }
            catch { }
            return true;
        }

        static bool ShouldReceiveMouseEventsPrefix(GuiDialog __instance, ref bool __result)
        {
            try
            {
                ICoreClientAPI api = HandbookOptMod.Capi;
                if (api?.Input != null && ShouldBlock(__instance, api.Input.MouseX, api.Input.MouseY))
                {
                    __result = false;
                    return false;
                }
            }
            catch { }
            return true;
        }

        public static void Reset()
        {
            installed = false;
            patched.Clear();
            mousePrefix = null;
            wheelPrefix = null;
            slotPrefix = null;
            receiveMousePrefix = null;
            moveMousePrefix = null;
            moveMousePostfix = null;
        }
    }
}
