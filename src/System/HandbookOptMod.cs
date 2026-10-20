using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace HandbookOpt
{
    /// <summary>
    /// Cliente 1.22.7: handbook (abrir/cerrar, busqueda, detalle, comidas) + grid de crafteo.
    /// </summary>
    public class HandbookOptMod : ModSystem
    {
        public const string HarmonyId = "handbookopt";
        public const string GridHarmonyId = "handbookopt.grid";
        const string TogglePickerHotKeyCode = "handbookopt-toggle-picker";

        bool welcomeShownThisWorld;

        public static ICoreAPI CoreApi;
        public static ICoreClientAPI Capi;
        public static ICoreServerAPI ServerApi;
        public static Harmony HarmonyInst;
        public static Harmony GridHarmony;
        public static bool GridPatchInstalled;
        static readonly object GridPatchLock = new object();
        static readonly object ServerIndexLock = new object();
        static int ServerIndexGeneration;
        static bool ServerIndexBuildInProgress;
        static IWorldAccessor ServerIndexBuildWorld;
        static int ServerIndexBuildCount;

        // Índices separados: el mundo cliente y el servidor pueden tener instancias
        // diferentes de GridRecipe incluso en una partida de un jugador integrada.
        public static GridRecipeIndex ClientGridIndex;
        public static GridRecipeIndex ServerGridIndex;

        // Detalle de items
        public static ConcurrentDictionary<string, RichTextComponentBase[]> DetailCache =
            new ConcurrentDictionary<string, RichTextComponentBase[]>(StringComparer.Ordinal);

        // Ultimo resultado de FilterItems (search|category -> lista)
        public static string LastFilterKey;
        public static List<IFlatListItem> LastFilterResult;

        // Indice de recetas de grid: itemId -> candidatos (orden de carga)
        public static Dictionary<int, List<GridRecipe>> RecipesByItemId;
        public static Dictionary<GridRecipe, int> RecipeOrder;
        public static List<GridRecipe> WildcardRecipes;
        public static int IndexedRecipeCount;
        public static int IndexTotal;
        public static int IndexCursor;
        public static bool IndexReady;
        public static CraftPickerDialog Picker;
        public static ModGuideBrowserDialog GuideBrowser;

        // Ultimo match por instancia de inventario (reuso)
        public static ConditionalWeakTable<object, GridSearchState> GridStates =
            new ConditionalWeakTable<object, GridSearchState>();

        public class GridSearchState
        {
            public GridRecipeIndex Index;
            public GridRecipe Previous;
            public string GridKey;
        }

        // Este mod debe ejecutarse en cliente y servidor: la búsqueda nativa de recetas
        // ocurre en ambos lados. El cliente además conserva la interfaz del handbook.
        public override bool ShouldLoad(EnumAppSide forSide) => true;

        public override void StartClientSide(ICoreClientAPI api)
        {
            CoreApi = api;
            Capi = api;
            HarmonyInst = new Harmony(HarmonyId);
            try
            {
                // Parches de interfaz/handbook únicamente en cliente. CraftingGridPatch
                // se instala por separado para compartir una sola instancia de Harmony
                // con el servidor integrado cuando se juega en singleplayer.
                HarmonyInst.PatchAll(typeof(HandbookOptMod).Assembly);
            }
            catch (Exception e)
            {
                api.Logger.Error("[HandbookOpt] Fallo al aplicar parches de cliente: {0}", e);
            }

            EnsureGridPatch(api);
            api.Event.LevelFinalize += OnLevelFinalize;
            api.Event.LeaveWorld += OnLeaveWorld;

            Picker = new CraftPickerDialog(api);
            api.Gui.RegisterDialog(Picker);

            GuideBrowser = new ModGuideBrowserDialog(api);
            api.Gui.RegisterDialog(GuideBrowser);
            GuideBrowser.RegisterLinkProtocols();

            // GUIOrOtherControls permite alternar el panel mientras el inventario
            // (y por tanto otro diálogo) está abierto. El manejador devuelve false si
            // el inventario no está abierto o si el campo de búsqueda tiene el foco.
            api.Input.RegisterHotKey(
                TogglePickerHotKeyCode,
                HandbookOptLang.Get("hotkey-toggle-recipe-panel"),
                GlKeys.B,
                HotkeyType.GUIOrOtherControls);
            api.Input.SetHotKeyHandler(TogglePickerHotKeyCode, OnTogglePickerHotKey);

        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            CoreApi = api;
            ServerApi = api;
            EnsureGridPatch(api);
            // El servidor usa fases de ejecución; LevelFinalize/LeaveWorld son eventos
            // del cliente y no existen en IServerEventAPI en Vintage Story 1.22.7.
            // RunGame reintenta la construcción tras cargar el mundo; Exit limpia el índice.
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, OnServerLevelFinalize);
            api.Event.ServerRunPhase(EnumServerRunPhase.Exit, OnServerLeaveWorld);

            // Primer intento inmediato si las recetas ya están resueltas. RunGame reintentará
            // si el mundo aún no estaba listo cuando se llamó este método.
            BeginServerIndexBuild(api);
        }

        static void EnsureGridPatch(ICoreAPI api)
        {
            lock (GridPatchLock)
            {
                if (GridPatchInstalled) return;
                try
                {
                    MethodBase target = CraftingGridPatch.TargetMethod();
                    MethodInfo prefix = AccessTools.Method(typeof(CraftingGridPatch), nameof(CraftingGridPatch.Prefix));
                    if (target == null || prefix == null)
                    {
                        api?.Logger.Warning("[HandbookOpt] No se encontró InventoryCraftingGrid.FindMatchingRecipe; se conserva la búsqueda nativa.");
                        return;
                    }

                    if (GridHarmony == null) GridHarmony = new Harmony(GridHarmonyId);
                    var prefixHarmony = new HarmonyMethod(prefix) { priority = Priority.First };
                    GridHarmony.Patch(target, prefix: prefixHarmony);
                    GridPatchInstalled = true;
                }
                catch (Exception e)
                {
                    GridPatchInstalled = false;
                    api?.Logger.Error("[HandbookOpt] No se pudo activar el índice de la cuadrícula; se conserva la búsqueda nativa: {0}", e);
                }
            }
        }

        private void OnLevelFinalize()
        {
            try
            {
                CraftCatalog.Begin(Capi, Capi.World);
            }
            catch (Exception e)
            {
                Capi.Logger.Warning("[HandbookOpt] Índice del catálogo no listo: {0}", e.Message);
                IndexReady = false;
            }

            // Mensaje local una vez por entrada al mundo; no se manda al chat del servidor.
            if (!welcomeShownThisWorld && Capi?.World?.Player != null)
            {
                Capi.ShowChatMessage(HandbookOptLang.Get("welcome-toggle-recipe-panel"));
                welcomeShownThisWorld = true;
            }
        }

        private bool OnTogglePickerHotKey(KeyCombination keyCombination)
        {
            try
            {
                return Picker != null && Picker.TogglePanelVisibility();
            }
            catch (Exception e)
            {
                Capi?.Logger.Warning("[HandbookOpt] No se pudo alternar el panel con la tecla B: {0}", e.Message);
                return false;
            }
        }

        private void OnServerLevelFinalize()
        {
            BeginServerIndexBuild(ServerApi);
        }

        private void OnServerLeaveWorld()
        {
            lock (ServerIndexLock)
            {
                ServerIndexGeneration++;
                ServerIndexBuildInProgress = false;
                ServerIndexBuildWorld = null;
                ServerIndexBuildCount = 0;
                ServerGridIndex = null;
            }
            GridStates = new ConditionalWeakTable<object, GridSearchState>();
        }

        static void BeginServerIndexBuild(ICoreServerAPI api)
        {
            IWorldAccessor world = api?.World;
            List<GridRecipe> gridRecipes = world?.GridRecipes;
            if (world == null || gridRecipes == null || gridRecipes.Count == 0) return;

            GridRecipe[] snapshot;
            int generation;
            lock (ServerIndexLock)
            {
                if (ServerGridIndex != null && ServerGridIndex.IsReady &&
                    ReferenceEquals(ServerGridIndex.World, world) && ServerGridIndex.SourceRecipeCount == gridRecipes.Count)
                    return;

                if (ServerIndexBuildInProgress && ReferenceEquals(ServerIndexBuildWorld, world) && ServerIndexBuildCount == gridRecipes.Count)
                    return;

                snapshot = gridRecipes.ToArray();
                ServerIndexGeneration++;
                generation = ServerIndexGeneration;
                ServerIndexBuildInProgress = true;
                ServerIndexBuildWorld = world;
                ServerIndexBuildCount = snapshot.Length;
                ServerGridIndex = null;
            }

            Task.Run(() => GridRecipeIndexBuilder.Build(world, snapshot))
                .ContinueWith(task =>
                {
                    bool retryWithCurrentRecipes = false;
                    lock (ServerIndexLock)
                    {
                        if (generation != ServerIndexGeneration || !ReferenceEquals(ServerApi?.World, world)) return;
                        ServerIndexBuildInProgress = false;
                        if (task.IsFaulted || task.IsCanceled)
                        {
                            Exception error = task.Exception?.GetBaseException();
                            ServerGridIndex = null;
                            api?.Logger.Error("[HandbookOpt] Falló la construcción del índice de cuadrícula del servidor; se usará el buscador nativo: {0}", error?.ToString() ?? "tarea cancelada");
                            return;
                        }

                        GridRecipeIndex index = task.Result;
                        // Si un mod añadió recetas durante la construcción, no activar un
                        // índice parcial: se vuelve a tomar una instantánea de la lista actual.
                        if (world.GridRecipes == null || world.GridRecipes.Count != index.SourceRecipeCount)
                        {
                            ServerGridIndex = null;
                            retryWithCurrentRecipes = world.GridRecipes != null && world.GridRecipes.Count > 0;
                        }
                        else
                        {
                            ServerGridIndex = index;
                        }
                    }

                    if (retryWithCurrentRecipes)
                    {
                        BeginServerIndexBuild(api);
                    }
                }, TaskScheduler.Default);
        }

        private void OnLeaveWorld()
        {
            welcomeShownThisWorld = false;
            CraftCatalog.Stop(Capi);
            try { Picker?.TryClose(); } catch { }
            try { GuideBrowser?.ResetCatalog(); } catch { }
            HandbookOptPauseController.Reset(Capi);
            try { Picker?.ResetSelection(); } catch { }
            try { Picker?.ResetModFilterCache(); } catch { }
            try { Picker?.ClearTooltipSampleCache(); } catch { }
            DetailCache.Clear();
            LastFilterKey = null;
            LastFilterResult = null;
            RecipesByItemId = null;
            RecipeOrder = null;
            WildcardRecipes = null;
            ClientGridIndex = null;
            GridStates = new ConditionalWeakTable<object, GridSearchState>();
            IndexReady = false;
            IndexedRecipeCount = 0;
            IndexCursor = 0;
            IndexTotal = 0;
            CraftCatalog.Recipes = new List<CompactRecipe>();
            CraftCatalog.ByExactId = new Dictionary<int, List<int>>();
            CraftCatalog.WildcardOnly = new List<int>();
        }

        public override void Dispose()
        {
            HandbookOptPauseController.Reset(Capi);
            try { HarmonyInst?.UnpatchAll(HarmonyId); } catch { /* ignore */ }
            try { GuideBrowser?.Dispose(); } catch { }
            GuideBrowser = null;
            try { Picker?.Dispose(); } catch { }
            // Cada proceso mantiene un único parche de cuadrícula, compartido por ambos
            // lados en singleplayer. Se desinstala al cerrar el mod/proceso.
            try { GridHarmony?.UnpatchAll(GridHarmonyId); } catch { /* ignore */ }
            GridPatchInstalled = false;
            ServerGridIndex = null;
            ClientGridIndex = null;
            DetailCache.Clear();
            base.Dispose();
        }
    }

    /// <summary>Índice inmutable publicado solo cuando la construcción terminó.</summary>
    public sealed class GridRecipeIndex
    {
        public readonly IWorldAccessor World;
        public readonly int SourceRecipeCount;
        public readonly Dictionary<int, List<GridRecipe>> RecipesByItemId;
        public readonly Dictionary<GridRecipe, int> RecipeOrder;
        public readonly List<GridRecipe> WildcardRecipes;
        public readonly bool IsReady;

        public GridRecipeIndex(IWorldAccessor world, int sourceRecipeCount,
            Dictionary<int, List<GridRecipe>> recipesByItemId,
            Dictionary<GridRecipe, int> recipeOrder,
            List<GridRecipe> wildcardRecipes)
        {
            World = world;
            SourceRecipeCount = sourceRecipeCount;
            RecipesByItemId = recipesByItemId ?? new Dictionary<int, List<GridRecipe>>();
            RecipeOrder = recipeOrder ?? new Dictionary<GridRecipe, int>();
            WildcardRecipes = wildcardRecipes ?? new List<GridRecipe>();
            IsReady = world != null;
        }
    }

    /// <summary>Construye un índice de búsqueda por ingrediente ancla rara.</summary>
    internal static class GridRecipeIndexBuilder
    {
        public static GridRecipeIndex Build(IWorldAccessor world, GridRecipe[] recipes)
        {
            var byPrimaryId = new Dictionary<int, List<GridRecipe>>(4096);
            var order = new Dictionary<GridRecipe, int>();
            var wildcard = new List<GridRecipe>();
            var orderedUnique = new List<GridRecipe>();
            var unique = new HashSet<GridRecipe>();
            var exactIdsByRecipe = new Dictionary<GridRecipe, HashSet<int>>();
            var exactIdFrequency = new Dictionary<int, int>(4096);

            if (recipes != null)
            {
                // Pasada 1: capturar el orden nativo, los IDs exactos de cada receta y
                // la frecuencia de cada ID entre recetas únicas.
                for (int i = 0; i < recipes.Length; i++)
                {
                    GridRecipe recipe = recipes[i];
                    if (recipe == null || !unique.Add(recipe)) continue;
                    order[recipe] = i;
                    orderedUnique.Add(recipe);

                    var ids = new HashSet<int>();
                    bool canUseExactAnchor = true;
                    var resolved = recipe.ResolvedIngredients;
                    if (resolved != null)
                    {
                        for (int j = 0; j < resolved.Length; j++)
                        {
                            CraftingRecipeIngredient ingredient = resolved[j] as CraftingRecipeIngredient;
                            if (ingredient == null || ingredient.MatchingType != EnumRecipeMatchType.Exact) continue;
                            // Custom exact matchers may accept stacks that an ID-only index
                            // cannot predict. Keep these recipes in the broad candidate list.
                            if (ingredient.GetType() != typeof(CraftingRecipeIngredient))
                            {
                                canUseExactAnchor = false;
                                break;
                            }
                            CollectibleObject collectible = ingredient.ResolvedItemStack?.Collectible;
                            if (collectible == null)
                            {
                                canUseExactAnchor = false;
                                break;
                            }
                            ids.Add(CraftCatalog.Pack(collectible));
                        }
                    }

                    if (!canUseExactAnchor || ids.Count == 0)
                    {
                        // Recetas basadas solo en etiquetas, comodines o ingredientes
                        // no resolubles por ID: no podemos descartarlas por un ID exacto.
                        wildcard.Add(recipe);
                        continue;
                    }

                    exactIdsByRecipe[recipe] = ids;
                    foreach (int id in ids)
                    {
                        exactIdFrequency.TryGetValue(id, out int count);
                        exactIdFrequency[id] = count + 1;
                    }
                }

                // Pasada 2: cada receta vive en un solo bucket: el ID exacto menos común
                // entre todas sus recetas. Toda coincidencia válida debe contener ese ID,
                // por lo que el índice reduce candidatos sin alterar Matches() ni atributos.
                for (int i = 0; i < orderedUnique.Count; i++)
                {
                    GridRecipe recipe = orderedUnique[i];
                    if (!exactIdsByRecipe.TryGetValue(recipe, out HashSet<int> ids) || ids.Count == 0) continue;

                    int primaryId = 0;
                    int lowestFrequency = int.MaxValue;
                    foreach (int id in ids)
                    {
                        int frequency = exactIdFrequency.TryGetValue(id, out int count) ? count : int.MaxValue;
                        if (frequency < lowestFrequency)
                        {
                            lowestFrequency = frequency;
                            primaryId = id;
                        }
                    }

                    if (!byPrimaryId.TryGetValue(primaryId, out List<GridRecipe> list))
                    {
                        list = new List<GridRecipe>(4);
                        byPrimaryId[primaryId] = list;
                    }
                    list.Add(recipe);
                }
            }

            return new GridRecipeIndex(world, recipes?.Length ?? 0, byPrimaryId, order, wildcard);
        }
    }

    // -------------------------------------------------------------------------
    // Atajo vanilla H: dirigirlo al resultado de HandbookOpt solo cuando el cursor
    // está sobre nuestra cuadrícula. En cualquier otro caso, dejar intacto el handler
    // original de SurvivalHandbook para conservar su comportamiento normal.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(ModSystemSurvivalHandbook), "OnSurvivalHandbookHotkey")]
    public static class SurvivalHandbookHotkeyPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(KeyCombination key, ref bool __result)
        {
            // Shift+H es el atajo vanilla de selección de bloque; no interceptarlo.
            if (key?.Shift == true) return true;

            try
            {
                if (HandbookOptMod.Picker != null && HandbookOptMod.Picker.OpenHoveredRecipeInVanillaHandbook())
                {
                    // Notificar que el atajo ya se consumió además de omitir el cuerpo
                    // vanilla; así otra vinculación H no vuelve a alternar/cerrar el diálogo.
                    __result = true;
                    return false;
                }
            }
            catch (Exception e)
            {
                HandbookOptMod.Capi?.Logger.Warning(
                    "[HandbookOpt] Falló el acceso al Handbook desde la cuadrícula; se conserva el atajo vanilla: {0}",
                    e.GetBaseException().Message);
            }

            return true;
        }
    }

    // -------------------------------------------------------------------------
    // Filtro de mods de HandbookOpt: no cerrar la lista al mover el cursor a su popup y
    // enviar la rueda del ratón a esa lista aunque el control ya haya perdido el foco.
    // Las comprobaciones están limitadas por identidad al desplegable "modfilter".
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(GuiElementListMenu), nameof(GuiElementListMenu.OnFocusLost))]
    public static class ModFilterPopupFocusLostPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(GuiElementListMenu __instance)
        {
            try
            {
                if (HandbookOptMod.Picker?.ShouldKeepModFilterPopupOpen(__instance) == true)
                    return false;
            }
            catch { }
            return true;
        }
    }

    [HarmonyPatch(typeof(GuiElementDropDown), nameof(GuiElementDropDown.OnMouseWheel))]
    public static class ModFilterPopupMouseWheelPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(GuiElementDropDown __instance, ICoreClientAPI api, MouseWheelEventArgs args)
        {
            try
            {
                if (HandbookOptMod.Picker?.ShouldRouteModFilterPopupWheel(__instance) == true)
                {
                    __instance.listMenu.OnMouseWheel(api, args);
                    args.SetHandled(true);
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    // -------------------------------------------------------------------------
    // Handbook: FilterItems (vacio + texto con cache y busqueda simple optimizada)
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(GuiDialogHandbook), nameof(GuiDialogHandbook.FilterItems))]
    public static class FilterItemsPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(GuiDialogHandbook __instance)
        {
            try
            {
                var fiLoading = AccessTools.Field(typeof(GuiDialogHandbook), "loadingPagesAsync");
                var fiSearch = AccessTools.Field(typeof(GuiDialogHandbook), "currentSearchText");
                var fiAll = AccessTools.Field(typeof(GuiDialogHandbook), "allHandbookPages");
                var fiShown = AccessTools.Field(typeof(GuiDialogHandbook), "shownHandbookPages");
                var fiOverview = AccessTools.Field(typeof(GuiDialogHandbook), "overviewGui");
                var fiListH = AccessTools.Field(typeof(GuiDialogHandbook), "listHeight");

                if (fiLoading == null || fiSearch == null || fiAll == null || fiShown == null) return true;
                if ((bool)fiLoading.GetValue(__instance)) return true;

                string search = (fiSearch.GetValue(__instance) as string) ?? "";
                search = search.Trim();
                string cat = __instance.currentCatgoryCode;
                string cacheKey = (cat ?? "") + "|" + search;

                var shown = fiShown.GetValue(__instance) as List<IFlatListItem>;
                var all = fiAll.GetValue(__instance) as List<GuiHandbookPage>;
                if (shown == null || all == null) return true;

                // Cache hit
                if (HandbookOptMod.LastFilterKey == cacheKey && HandbookOptMod.LastFilterResult != null)
                {
                    shown.Clear();
                    shown.AddRange(HandbookOptMod.LastFilterResult);
                    UpdateScrollbar(__instance, fiOverview, fiListH, shown);
                    return false;
                }

                // Busqueda vacia: solo categoria + no duplicados (sin GetPageText ni regex)
                if (search.Length == 0)
                {
                    shown.Clear();
                    for (int i = 0; i < all.Count; i++)
                    {
                        var page = all[i];
                        if (page == null || page.IsDuplicate) continue;
                        if (cat != null && page.CategoryCode != cat) continue;
                        shown.Add(page);
                    }
                    SaveCache(cacheKey, shown);
                    UpdateScrollbar(__instance, fiOverview, fiListH, shown);
                    return false;
                }

                // Busqueda con texto: titulo primero (barato), luego cuerpo si hace falta
                // Usa Contains case-insensitive en vez de Regex completo cuando no hay metacaracteres
                bool simple = IsSimpleQuery(search);
                string needle = simple ? search.ToSearchFriendly() : null;
                if (!simple)
                {
                    // Dejar vanilla/ARL si la query es compleja (regex/mod: etc.)
                    return true;
                }

                var weighted = new List<WeightedPage>(256);
                for (int i = 0; i < all.Count; i++)
                {
                    var page = all[i];
                    if (page == null || page.IsDuplicate) continue;
                    if (cat != null && page.CategoryCode != cat) continue;

                    var pt = page.GetPageText();
                    string title = pt.Title ?? "";
                    int titleHits = CountContains(title, needle);
                    if (titleHits == 0)
                    {
                        string body = pt.Text ?? "";
                        int bodyHits = CountContains(body, needle);
                        if (bodyHits == 0) continue;
                        weighted.Add(new WeightedPage
                        {
                            Page = page,
                            TitleHits = 0,
                            BodyHits = bodyHits,
                            TitleLen = title.Length
                        });
                    }
                    else
                    {
                        weighted.Add(new WeightedPage
                        {
                            Page = page,
                            TitleHits = titleHits,
                            BodyHits = 0,
                            TitleLen = title.Length
                        });
                    }
                }

                weighted.Sort((a, b) =>
                {
                    int t = b.TitleHits - a.TitleHits;
                    if (t != 0) return t;
                    int l = a.TitleLen - b.TitleLen;
                    if (l != 0) return l;
                    return b.BodyHits - a.BodyHits;
                });

                shown.Clear();
                for (int i = 0; i < weighted.Count; i++) shown.Add(weighted[i].Page);

                SaveCache(cacheKey, shown);
                UpdateScrollbar(__instance, fiOverview, fiListH, shown);
                return false;
            }
            catch (Exception e)
            {
                HandbookOptMod.Capi?.Logger.Warning("[HandbookOpt] FilterItems fallback: {0}", e.Message);
                return true;
            }
        }

        private struct WeightedPage
        {
            public GuiHandbookPage Page;
            public int TitleHits;
            public int BodyHits;
            public int TitleLen;
        }

        private static bool IsSimpleQuery(string s)
        {
            // Queries con "mod:", regex /.../ o metacaracteres: vanilla
            if (s.IndexOf(':') >= 0) return false;
            if (s.Length >= 2 && s[0] == '/' && s[s.Length - 1] == '/') return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '.' || c == '*' || c == '+' || c == '?' || c == '[' || c == ']' || c == '(' || c == ')' || c == '|' || c == '^' || c == '$' || c == '{' || c == '}' || c == '\\')
                    return false;
            }
            return true;
        }

        private static int CountContains(string hay, string needle)
        {
            if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(needle)) return 0;
            int count = 0;
            int idx = 0;
            while (idx < hay.Length)
            {
                int found = hay.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                count++;
                idx = found + needle.Length;
                if (count > 20) break;
            }
            return count;
        }

        private static void SaveCache(string key, List<IFlatListItem> shown)
        {
            HandbookOptMod.LastFilterKey = key;
            HandbookOptMod.LastFilterResult = new List<IFlatListItem>(shown);
        }

        private static void UpdateScrollbar(GuiDialogHandbook inst, FieldInfo fiOverview, FieldInfo fiListH, List<IFlatListItem> shown)
        {
            try
            {
                var overview = fiOverview?.GetValue(inst) as GuiComposer;
                if (overview == null) return;
                var stacklist = overview.GetFlatList("stacklist");
                stacklist.CalcTotalHeight();
                double listH = fiListH != null ? (double)fiListH.GetValue(inst) : 500;
                overview.GetScrollbar("scrollbar").SetHeights((float)listH, (float)stacklist.insideBounds.fixedHeight);
            }
            catch { /* overview no listo */ }
        }
    }

    // -------------------------------------------------------------------------
    // Abrir / cerrar
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(GuiDialogHandbook), nameof(GuiDialogHandbook.OnGuiClosed))]
    public static class OnClosedPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(GuiDialogHandbook __instance)
        {
            try
            {
                var fiHistory = AccessTools.Field(typeof(GuiDialogHandbook), "browseHistory");
                var fiSearch = AccessTools.Field(typeof(GuiDialogHandbook), "currentSearchText");
                var fiOverview = AccessTools.Field(typeof(GuiDialogHandbook), "overviewGui");

                if (fiHistory?.GetValue(__instance) is Stack<BrowseHistoryElement> hist)
                    hist.Clear();

                fiSearch?.SetValue(__instance, null);
                HandbookOptMod.LastFilterKey = null;
                HandbookOptMod.LastFilterResult = null;

                var overview = fiOverview?.GetValue(__instance) as GuiComposer;
                var input = overview?.GetTextInput("searchField");
                if (input != null)
                {
                    // Evitar SetValue("") que refiltra: tocar campo interno si existe
                    var fiText = AccessTools.Field(input.GetType(), "text")
                                 ?? AccessTools.Field(input.GetType(), "Text");
                    if (fiText != null) fiText.SetValue(input, "");
                    else input.SetValue("");
                }
            }
            catch { /* OnGuiClosed vanilla sigue */ }
        }
    }

    // -------------------------------------------------------------------------
    // Detalle: cache GetHandbookInfo
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(CollectibleBehaviorHandbookTextAndExtraInfo), "GetHandbookInfo")]
    public static class GetHandbookInfoPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        public static bool Prefix(ItemSlot inSlot, ref RichTextComponentBase[] __result)
        {
            try
            {
                var stack = inSlot?.Itemstack;
                if (stack?.Collectible == null) return true;
                string key = GuiHandbookItemStackPage.PageCodeForStack(stack);
                if (HandbookOptMod.DetailCache.TryGetValue(key, out var cached) && cached != null)
                {
                    __result = cached;
                    return false;
                }
            }
            catch { }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(ItemSlot inSlot, RichTextComponentBase[] __result)
        {
            try
            {
                var stack = inSlot?.Itemstack;
                if (stack?.Collectible == null || __result == null) return;
                string key = GuiHandbookItemStackPage.PageCodeForStack(stack);
                HandbookOptMod.DetailCache[key] = __result;
            }
            catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Comidas: RenderListEntryTo regenera el meal cada 1s -> cada 5s
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(GuiHandbookMealRecipePage), nameof(GuiHandbookMealRecipePage.RenderListEntryTo))]
    public static class MealListRenderPatch
    {
        // page instance -> next allowed regen time (seconds remaining style)
        static readonly ConditionalWeakTable<object, MealRenderState> States =
            new ConditionalWeakTable<object, MealRenderState>();

        class MealRenderState
        {
            public float SecondsVisible = 5f;
            public bool Initialized;
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(GuiHandbookMealRecipePage __instance, float dt)
        {
            try
            {
                // El metodo original hace: secondsVisible -= dt; if <= 0 then secondsVisible = 1; regenerate
                // Forzamos el campo a un intervalo mayor retrasando la regeneracion.
                var fi = AccessTools.Field(typeof(GuiHandbookMealRecipePage), "secondsVisible");
                if (fi == null) return;

                float cur = (float)fi.GetValue(__instance);
                var st = States.GetValue(__instance, _ => new MealRenderState());

                if (!st.Initialized)
                {
                    // Primera vez: dejar que genere, luego espaciar
                    st.Initialized = true;
                    st.SecondsVisible = 5f;
                    return;
                }

                // Si vanilla acaba de regenerar (puso ~1), alargar a 5s
                if (cur > 0.9f && cur <= 1.05f)
                {
                    fi.SetValue(__instance, 5f);
                }
            }
            catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Grid de crafteo: índice inverso + comparador nativo de GridRecipe.
    // La salida se genera mediante el método nativo FoundMatch(GridRecipe); nunca
    // se llama manualmente a GenerateOutputStack ni se crea una transacción de salida.
    // -------------------------------------------------------------------------
    [HarmonyPatch]
    public static class CraftingGridPatch
    {
        // PatchAll debe saltarlo: la cuadrícula se instala manualmente una sola vez
        // para evitar aplicar dos prefijos en el servidor integrado.
        static bool Prepare() => false;

        public static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Vintagestory.Common.InventoryCraftingGrid")
                    ?? AccessTools.TypeByName("Vintagestory.API.Common.InventoryCraftingGrid");
            return t == null ? null : AccessTools.Method(t, "FindMatchingRecipe");
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(object __instance)
        {
            try
            {
                var inv = __instance as InventoryBase;
                if (inv == null) return true;

                if (!TryResolveContext(inv, out IPlayer player, out IWorldAccessor world, out GridRecipeIndex index))
                    return true;
                if (index == null || !index.IsReady || index.RecipesByItemId == null)
                    return true;
                if (!ReferenceEquals(index.World, world) || world?.GridRecipes == null || world.GridRecipes.Count != index.SourceRecipeCount)
                {
                    return true;
                }

                int count = inv.Count;
                if (count < 2) return true;
                int slotCount = count - 1;
                int gridWidth = (int)Math.Sqrt(slotCount);
                if (gridWidth * gridWidth != slotCount) return true;

                var slots = new ItemSlot[slotCount];
                var filledIds = new HashSet<int>();
                int filled = 0;
                var keySb = new StringBuilder(slotCount * 10);
                for (int i = 0; i < slotCount; i++)
                {
                    ItemSlot slot = inv[i];
                    slots[i] = slot;
                    ItemStack stack = slot?.Itemstack;
                    if (stack?.Collectible != null && stack.StackSize > 0)
                    {
                        int id = CraftCatalog.Pack(stack.Collectible);
                        filledIds.Add(id);
                        filled++;
                        // No se usa GetHashCode como criterio de identidad de la receta;
                        // StackSize + atributos representados en el stack ayudan a evitar
                        // reutilizar una entrada anterior después de mover una pila.
                        keySb.Append(id).Append('x').Append(stack.StackSize).Append('#')
                             .Append(stack.GetHashCode()).Append('#')
                             .Append(stack.Attributes?.GetHashCode() ?? 0).Append(';');
                    }
                    else keySb.Append("0;");
                }

                ItemSlot outputSlot = inv[slotCount];
                if (outputSlot == null) return true;

                string gridKey = keySb.ToString();
                HandbookOptMod.GridSearchState state = HandbookOptMod.GridStates.GetValue(__instance, _ => new HandbookOptMod.GridSearchState());
                if (!ReferenceEquals(state.Index, index))
                {
                    state.Index = index;
                    state.Previous = null;
                    state.GridKey = null;
                }

                // Atajo para llamadas repetidas con la misma configuración. Aun así se
                // revalida la receta en vivo para respetar ingredientes con atributos.
                if (state.Previous != null && state.GridKey == gridKey && state.Previous.Enabled)
                {
                    if (state.Previous.Matches(player, world, slots, gridWidth) && InvokeNativeFoundMatch(__instance, state.Previous))
                        return false;
                }

                if (filled == 0)
                {
                    ClearNativeMatchState(__instance, outputSlot);
                    state.Previous = null;
                    state.GridKey = gridKey;
                    return false;
                }

                // Candidatos a partir de los IDs exactos presentes, sin escanear todos los
                // tipos de ingrediente y sin List.AddIfNotPresent (la ruta O(n²) nativa).
                var candidateSet = new HashSet<GridRecipe>();
                foreach (int id in filledIds)
                {
                    if (!index.RecipesByItemId.TryGetValue(id, out List<GridRecipe> list)) continue;
                    for (int i = 0; i < list.Count; i++) candidateSet.Add(list[i]);
                }
                List<GridRecipe> wild = index.WildcardRecipes;
                for (int i = 0; i < wild.Count; i++) candidateSet.Add(wild[i]);

                if (candidateSet.Count == 0)
                {
                    // El índice está completo: no hay receta que pueda satisfacer una
                    // cuadrícula con estos IDs. No delegar el caso negativo al escaneo total.
                    ClearNativeMatchState(__instance, outputSlot);
                    state.Previous = null;
                    state.GridKey = gridKey;
                    return false;
                }

                var candidates = new List<GridRecipe>(candidateSet);
                candidates.Sort((a, b) =>
                {
                    int ai = index.RecipeOrder.TryGetValue(a, out int av) ? av : int.MaxValue;
                    int bi = index.RecipeOrder.TryGetValue(b, out int bv) ? bv : int.MaxValue;
                    return ai.CompareTo(bi);
                });

                GridRecipe found = null;
                for (int i = 0; i < candidates.Count; i++)
                {
                    GridRecipe recipe = candidates[i];
                    if (recipe == null || !recipe.Enabled) continue;
                    if (!HasAllExactIngredientsPresent(recipe, filledIds, gridWidth)) continue;
                    if (recipe.Matches(player, world, slots, gridWidth))
                    {
                        found = recipe;
                        break;
                    }
                }

                if (found != null)
                {
                    // Mantener el ciclo nativo de salida. Si FoundMatch no está presente
                    // en este build del juego o falla, dejamos que el juego resuelva todo.
                    if (!InvokeNativeFoundMatch(__instance, found)) return true;
                    state.Previous = found;
                    state.GridKey = gridKey;
                    return false;
                }

                // Índice completo + ninguna candidata válida: reflejar el estado nativo
                // de "sin receta" sin ejecutar la búsqueda global que acabamos de evitar.
                ClearNativeMatchState(__instance, outputSlot);
                state.Previous = null;
                state.GridKey = gridKey;
                return false;
            }
            catch
            {
                return true;
            }
        }

        static bool TryResolveContext(InventoryBase inventory, out IPlayer player, out IWorldAccessor world, out GridRecipeIndex index)
        {
            player = null;
            world = null;
            index = null;

            // Cliente: solo aceptar la cuadrícula que realmente pertenece al jugador local.
            ICoreClientAPI client = HandbookOptMod.Capi;
            IPlayer local = client?.World?.Player;
            if (local != null && PlayerOwnsInventory(local, inventory) &&
                HandbookOptMod.ClientGridIndex != null && ReferenceEquals(HandbookOptMod.ClientGridIndex.World, client.World))
            {
                player = local;
                world = client.World;
                index = HandbookOptMod.ClientGridIndex;
                return true;
            }

            // Servidor: resolver por referencia de inventario; no inventar un jugador
            // cuando no es posible demostrar qué inventario está siendo actualizado.
            ICoreServerAPI server = HandbookOptMod.ServerApi;
            IWorldAccessor serverWorld = server?.World;
            IPlayer[] online = serverWorld?.AllOnlinePlayers;
            if (online != null)
            {
                for (int i = 0; i < online.Length; i++)
                {
                    IPlayer candidate = online[i];
                    if (candidate == null || candidate.InventoryManager == null) continue;
                    if (!PlayerOwnsInventory(candidate, inventory)) continue;
                    player = candidate;
                    world = serverWorld;
                    index = HandbookOptMod.ServerGridIndex;
                    return true;
                }
            }

            return false;
        }

        static bool PlayerOwnsInventory(IPlayer player, InventoryBase target)
        {
            try
            {
                var manager = player?.InventoryManager;
                if (manager == null || target == null) return false;
                foreach (InventoryBase inventory in manager.InventoriesOrdered)
                    if (ReferenceEquals(inventory, target)) return true;
                foreach (IInventory inventory in manager.OpenedInventories)
                    if (ReferenceEquals(inventory, target)) return true;
            }
            catch { }
            return false;
        }

        static bool HasAllExactIngredientsPresent(GridRecipe recipe, HashSet<int> filledIds, int gridWidth)
        {
            // Filtros necesarios y conservadores. El tamaño solo descarta recetas que
            // físicamente exceden la cuadrícula; IDs exactos ausentes también las descartan.
            // No hacemos comparación posicional propia: recipe.Matches conserva las reglas
            // nativas de formas, comodines, etiquetas, herramientas y atributos.
            if (recipe == null) return false;
            if (!recipe.Shapeless && ((recipe.Width > 0 && recipe.Width > gridWidth) ||
                                      (recipe.Height > 0 && recipe.Height > gridWidth))) return false;

            var resolved = recipe.ResolvedIngredients;
            if (resolved == null) return true;
            for (int i = 0; i < resolved.Length; i++)
            {
                CraftingRecipeIngredient ingredient = resolved[i] as CraftingRecipeIngredient;
                if (ingredient == null || ingredient.MatchingType != EnumRecipeMatchType.Exact) continue;
                if (ingredient.GetType() != typeof(CraftingRecipeIngredient)) continue;
                CollectibleObject collectible = ingredient.ResolvedItemStack?.Collectible;
                if (collectible == null) continue;
                if (!filledIds.Contains(CraftCatalog.Pack(collectible))) return false;
            }
            return true;
        }

        static bool InvokeNativeFoundMatch(object inventory, GridRecipe recipe)
        {
            try
            {
                if (inventory == null || recipe == null) return false;
                MethodInfo method = AccessTools.Method(inventory.GetType(), "FoundMatch", new[] { typeof(GridRecipe) });
                if (method == null) return false;
                method.Invoke(inventory, new object[] { recipe });
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void ClearNativeMatchState(object inventory, ItemSlot outputSlot)
        {
            var property = AccessTools.Property(inventory.GetType(), "MatchingRecipe");
            property?.SetValue(inventory, null);
            if (outputSlot != null)
            {
                outputSlot.Itemstack = null;
                outputSlot.MarkDirty();
            }
        }
    }
}
