using HarmonyLib;
using Vintagestory.API.Client;

namespace HandbookOpt
{
    /// <summary>
    /// Capa de dibujo compartida por las ventanas principales (inventario, Handbook, selector
    /// de recetas y diálogo del personaje). El juego usa 0.2 para inventarios y Handbook, pero
    /// 0.1 (valor por defecto) para el diálogo del personaje (que agrupa Personaje, Estadísticas
    /// y Medio ambiente), por lo que éste quedaba siempre detrás. Con una capa común, el foco
    /// decide cuál se ve delante, como entre el inventario y el Handbook vanilla.
    /// </summary>
    internal static class HandbookOptDrawLayer
    {
        public const double SharedLayer = 0.2;
    }

    [HarmonyPatch(typeof(GuiDialog), nameof(GuiDialog.DrawOrder), MethodType.Getter)]
    internal static class CharacterDialogDrawLayerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(GuiDialog __instance, ref double __result)
        {
            // Solo el diálogo del personaje (base de Personaje/Atributos + paneles extra).
            // Cualquier otro diálogo conserva su DrawOrder original.
            if (__instance is GuiDialogCharacterBase && __result < HandbookOptDrawLayer.SharedLayer)
                __result = HandbookOptDrawLayer.SharedLayer;
        }
    }
}
