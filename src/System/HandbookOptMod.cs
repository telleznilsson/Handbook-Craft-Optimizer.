using System;
using System.Collections;
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
        const string RecipeVariantChannelName = "handbookopt-recipevariants";
        static IClientNetworkChannel recipeVariantClientChannel;
        static IServerNetworkChannel recipeVariantServerChannel;
        static long recipeVariantTickListener;
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
            // Ingredient identity by grid position, deliberately excluding stack counts and
            // mutable tool durability/attributes so batch crafting can retain a selected recipe.
            public string GridCompositionKey;
            // All distinct, currently matching outputs in native recipe order.
            public List<GridRecipe> MatchingRecipes;
            public int ActiveRecipeIndex = -1;
            public long NextVariantAt;
            public long LastCarouselTickAt;
            // Server-only sticky selection accepted from the client's output carousel.
            public bool ExplicitServerSelection;
        }

        // Este mod debe ejecutarse en cliente y servidor: la búsqueda nativa de recetas
        // ocurre en ambos lados. El cliente además conserva la interfaz del handbook.
        public override bool ShouldLoad(EnumAppSide forSide) => true;

        public override void StartClientSide(ICoreClientAPI api)
        {
            CoreApi = api;
            Capi = api;
            try
            {
                recipeVariantClientChannel = api.Network.RegisterChannel(RecipeVariantChannelName);
                recipeVariantClientChannel.RegisterMessageType<RecipeVariantSelectionPacket>();
            }
            catch (Exception e)
            {
                recipeVariantClientChannel = null;
                api.Logger.Warning("[HandbookOpt] No se pudo registrar el canal de selección de recetas alternativas; se conservará la receta nativa: {0}", e.Message);
            }
            // La rotación ahora vive en el mosaico de recetas alternativas del selector,
            // no en la casilla de salida nativa de la cuadrícula. El resultado de crafteo
            // se mantiene estable; al hacer clic se aplica una receta concreta.
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
            try
            {
                recipeVariantServerChannel = api.Network.RegisterChannel(RecipeVariantChannelName);
                recipeVariantServerChannel.RegisterMessageType<RecipeVariantSelectionPacket>();
                recipeVariantServerChannel.SetMessageHandler<RecipeVariantSelectionPacket>(OnRecipeVariantSelectionPacket);
            }
            catch (Exception e)
            {
                recipeVariantServerChannel = null;
                api.Logger.Warning("[HandbookOpt] No se pudo registrar el canal de selección de recetas alternativas; se conservará la receta nativa: {0}", e.Message);
            }
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

        static void OnRecipeVariantSelectionPacket(IPlayer fromPlayer, RecipeVariantSelectionPacket packet)
        {
            try
            {
                if (fromPlayer == null || packet == null || string.IsNullOrWhiteSpace(packet.RecipeKey)) return;
                CraftingGridPatch.ApplyServerRecipeSelection(fromPlayer, packet.RecipeKey);
            }
            catch (Exception e)
            {
                try { ServerApi?.Logger?.Debug("[HandbookOpt] Se descartó una selección de receta alternativa no aplicable: {0}", e.Message); }
                catch { }
            }
        }

        public static bool RecipeVariantClientChannelAvailable => recipeVariantClientChannel != null;

        public static void SendRecipeVariantSelection(GridRecipe recipe)
        {
            try
            {
                if (recipeVariantClientChannel == null || recipe == null) return;
                string key = CraftingGridPatch.RecipeIdentity(recipe);
                if (string.IsNullOrWhiteSpace(key)) return;
                recipeVariantClientChannel.SendPacket(new RecipeVariantSelectionPacket { RecipeKey = key });
            }
            catch (Exception e)
            {
                try { Capi?.Logger?.Debug("[HandbookOpt] No se pudo sincronizar la receta mostrada en el carrusel: {0}", e.Message); }
                catch { }
            }
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
            if (recipeVariantTickListener != 0)
            {
                try { Capi?.Event?.UnregisterGameTickListener(recipeVariantTickListener); } catch { }
                recipeVariantTickListener = 0;
            }
            recipeVariantClientChannel = null;
            recipeVariantServerChannel = null;
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
        // Precomputed recipe-identity lookup: server selection packets must not rescan
        // thousands of recipes and rebuild output signatures every two seconds. The main
        // map stores one recipe per key; a list is allocated only for actual key collisions.
        public readonly Dictionary<string, GridRecipe> RecipeByIdentity;
        public readonly Dictionary<string, List<GridRecipe>> DuplicateRecipesByIdentity;
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
            RecipeByIdentity = new Dictionary<string, GridRecipe>(RecipeOrder.Count, StringComparer.Ordinal);
            DuplicateRecipesByIdentity = new Dictionary<string, List<GridRecipe>>(StringComparer.Ordinal);
            foreach (KeyValuePair<GridRecipe, int> entry in RecipeOrder)
            {
                string key = CraftingGridPatch.RecipeIdentity(entry.Key);
                if (string.IsNullOrEmpty(key)) continue;
                if (!RecipeByIdentity.TryGetValue(key, out GridRecipe existing))
                {
                    RecipeByIdentity[key] = entry.Key;
                    continue;
                }

                if (!DuplicateRecipesByIdentity.TryGetValue(key, out List<GridRecipe> duplicates))
                {
                    duplicates = new List<GridRecipe>(2) { existing };
                    DuplicateRecipesByIdentity[key] = duplicates;
                }
                duplicates.Add(entry.Key);
            }
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
                if (world?.GridRecipes == null) return true;
                bool indexUsable = index != null && index.IsReady && index.RecipesByItemId != null
                    && ReferenceEquals(index.World, world) && world.GridRecipes.Count == index.SourceRecipeCount;

                if (!TryReadGrid(inv, out ItemSlot[] slots, out HashSet<int> filledIds, out int filled, out int gridWidth, out string gridKey))
                    return true;
                int slotCount = slots.Length;
                ItemSlot outputSlot = inv[slotCount];
                if (outputSlot == null) return true;

                HandbookOptMod.GridSearchState state = HandbookOptMod.GridStates.GetValue(__instance, _ => new HandbookOptMod.GridSearchState());
                bool sameGrid = state.GridKey == gridKey;
                string compositionKey = BuildGridCompositionKey(slots);
                bool sameComposition = !string.IsNullOrEmpty(state.GridCompositionKey)
                    && string.Equals(state.GridCompositionKey, compositionKey, StringComparison.Ordinal);

                // Bulk crafting changes stack counts (and hammer durability) after each craft.
                // Keep the selected recipe when the same ingredient types remain in the same
                // slots and that recipe still matches. A genuinely different grid or an invalid
                // selected recipe still resets the chooser to native catalog order.
                bool preserveSelectionAcrossCountChange = !sameGrid && sameComposition
                    && state.Previous != null && state.Previous.Enabled
                    && state.Previous.Matches(player, world, slots, gridWidth);
                if (!sameGrid && !preserveSelectionAcrossCountChange)
                {
                    state.Previous = null;
                    state.MatchingRecipes = null;
                    state.ActiveRecipeIndex = -1;
                    state.NextVariantAt = 0;
                    state.LastCarouselTickAt = 0;
                    state.ExplicitServerSelection = false;
                }
                else if (preserveSelectionAcrossCountChange)
                {
                    state.GridKey = gridKey;
                    state.NextVariantAt = 0;
                    state.LastCarouselTickAt = 0;
                }
                if (!ReferenceEquals(state.Index, index))
                {
                    bool preserveServerSelection = state.ExplicitServerSelection
                        && (sameGrid || preserveSelectionAcrossCountChange)
                        && state.Previous != null && ContainsRecipeReference(world.GridRecipes, state.Previous);
                    state.Index = index;
                    if (!preserveServerSelection)
                    {
                        state.Previous = null;
                        state.GridKey = null;
                        state.MatchingRecipes = null;
                        state.ActiveRecipeIndex = -1;
                        state.NextVariantAt = 0;
                        state.LastCarouselTickAt = 0;
                        state.ExplicitServerSelection = false;
                    }
                }
                state.GridCompositionKey = compositionKey;

                // Reusar a receita seleccionada, inclusive cuando la selección actual llegó
                // desde el carrusel del cliente. FoundMatch mantiene la salida nativa y su
                // consumo; no escribimos Itemstack manualmente en la ranura de resultado.
                if (state.Previous != null && state.GridKey == gridKey && state.Previous.Enabled)
                {
                    if (state.Previous.Matches(player, world, slots, gridWidth) && InvokeNativeFoundMatch(__instance, state.Previous))
                        return false;
                }

                // The asynchronous index can still be building just after world entry.
                // Preserve a recipe already validated by the network handler, but otherwise
                // leave initial matching to vanilla until the optimization index is ready.
                if (!indexUsable)
                {
                    state.GridKey = gridKey;
                    return true;
                }

                if (filled == 0)
                {
                    ClearNativeMatchState(__instance, outputSlot);
                    state.Previous = null;
                    state.GridKey = gridKey;
                    state.MatchingRecipes = null;
                    state.ActiveRecipeIndex = -1;
                    state.NextVariantAt = 0;
                    state.LastCarouselTickAt = 0;
                    state.ExplicitServerSelection = false;
                    return false;
                }

                // Candidatos desde el índice por IDs exactos. La verificación final sigue
                // usando GridRecipe.Matches para conservar comodines, tags, atributos,
                // formas y reglas propias de los mods.
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
                    ClearNativeMatchState(__instance, outputSlot);
                    ClearRecipeMatches(state, gridKey);
                    return false;
                }

                var candidates = new List<GridRecipe>(candidateSet);
                candidates.Sort((a, b) =>
                {
                    int ai = index.RecipeOrder.TryGetValue(a, out int av) ? av : int.MaxValue;
                    int bi = index.RecipeOrder.TryGetValue(b, out int bv) ? bv : int.MaxValue;
                    return ai.CompareTo(bi);
                });

                // Collect every live match, not just the first. Remove only recipes whose
                // resolved outputs are demonstrably identical, so the carousel represents
                // visibly distinct results without rescanning the catalogue every frame.
                var variants = new List<GridRecipe>(4);
                for (int i = 0; i < candidates.Count; i++)
                {
                    GridRecipe recipe = candidates[i];
                    if (recipe == null || !recipe.Enabled) continue;
                    if (!HasAllExactIngredientsPresent(recipe, filledIds, gridWidth)) continue;
                    if (!recipe.Matches(player, world, slots, gridWidth)) continue;
                    if (HasDistinctOutput(variants, recipe, world)) variants.Add(recipe);
                }

                // If a custom recipe cannot produce a stable cross-endpoint identity, keep
                // the native first result but do not rotate through recipes that the server
                // cannot unambiguously identify. Better no carousel than a mismatched craft.
                bool canSynchronizeVariants = true;
                for (int i = 0; i < variants.Count; i++)
                {
                    if (string.IsNullOrEmpty(RecipeIdentity(variants[i])))
                    {
                        canSynchronizeVariants = false;
                        break;
                    }
                }

                GridRecipe found = variants.Count > 0 ? variants[0] : null;
                if (found != null)
                {
                    // Mantener la salida nativa en ambos extremos. El primer resultado
                    // conserva el orden del catálogo; el temporizador cliente iniciará
                    // después el pase por las salidas alternativas cada 2 segundos.
                    if (!InvokeNativeFoundMatch(__instance, found)) return true;
                    state.Previous = found;
                    state.GridKey = gridKey;
                    state.MatchingRecipes = canSynchronizeVariants ? variants : null;
                    state.ActiveRecipeIndex = 0;
                    state.NextVariantAt = 0;
                    state.LastCarouselTickAt = 0;
                    state.ExplicitServerSelection = false;
                    return false;
                }

                // Índice completo + ninguna candidata válida: reflejar el estado nativo
                // de "sin receta" sin ejecutar la búsqueda global que acabamos de evitar.
                ClearNativeMatchState(__instance, outputSlot);
                ClearRecipeMatches(state, gridKey);
                return false;
            }
            catch
            {
                return true;
            }
        }

        static void ClearRecipeMatches(HandbookOptMod.GridSearchState state, string gridKey)
        {
            if (state == null) return;
            state.Previous = null;
            state.GridKey = gridKey;
            state.MatchingRecipes = null;
            state.ActiveRecipeIndex = -1;
            state.NextVariantAt = 0;
            state.LastCarouselTickAt = 0;
            state.ExplicitServerSelection = false;
        }

        static bool HasDistinctOutput(List<GridRecipe> alreadyAdded, GridRecipe candidate, IWorldAccessor world)
        {
            if (candidate == null) return false;
            ItemStack candidateOutput = candidate.Output?.ResolvedItemStack;
            // Unresolved/custom outputs cannot safely be declared duplicates. Keep them
            // in native order rather than accidentally hiding a valid mod recipe.
            if (candidateOutput?.Collectible == null) return true;
            for (int i = 0; i < alreadyAdded.Count; i++)
            {
                ItemStack existingOutput = alreadyAdded[i]?.Output?.ResolvedItemStack;
                if (existingOutput?.Collectible == null) continue;
                try
                {
                    // IItemStack.Equals intentionally ignores StackSize, so quantity must
                    // be compared explicitly before asking the game to compare attributes.
                    if (existingOutput.StackSize != candidateOutput.StackSize) continue;
                    ItemStack left = existingOutput.Clone();
                    ItemStack right = candidateOutput.Clone();
                    if (!left.Equals(world, right)) continue;
                    string existingOutputAttributes = alreadyAdded[i]?.Output?.Attributes?.Token?.ToString() ?? string.Empty;
                    string candidateOutputAttributes = candidate.Output?.Attributes?.Token?.ToString() ?? string.Empty;
                    if (!string.Equals(existingOutputAttributes, candidateOutputAttributes, StringComparison.Ordinal)) continue;

                    // A same-looking base output can still copy/merge different input
                    // attributes or be modified by recipe attributes. Keep such recipes
                    // selectable rather than collapsing their crafting semantics.
                    GridRecipe existingRecipe = alreadyAdded[i];
                    if (!string.Equals(existingRecipe?.CopyAttributesFrom, candidate.CopyAttributesFrom, StringComparison.Ordinal)) continue;
                    if (!string.Equals(existingRecipe?.RequiresTrait, candidate.RequiresTrait, StringComparison.Ordinal)) continue;
                    if (existingRecipe?.AverageDurability != candidate.AverageDurability) continue;
                    string existingMerge = string.Join("\u001f", existingRecipe?.MergeAttributesFrom ?? Array.Empty<string>());
                    string candidateMerge = string.Join("\u001f", candidate.MergeAttributesFrom ?? Array.Empty<string>());
                    if (!string.Equals(existingMerge, candidateMerge, StringComparison.Ordinal)) continue;
                    string existingAttributes = existingRecipe?.Attributes?.Token?.ToString() ?? string.Empty;
                    string candidateAttributes = candidate.Attributes?.Token?.ToString() ?? string.Empty;
                    if (!string.Equals(existingAttributes, candidateAttributes, StringComparison.Ordinal)) continue;
                    return false;
                }
                catch
                {
                    // Si la equivalencia no se puede demostrar (por ejemplo, un
                    // ItemStack personalizado de un mod), conservar la alternativa.
                }
            }
            return true;
        }

        static string BuildGridCompositionKey(ItemSlot[] slots)
        {
            if (slots == null) return string.Empty;
            var key = new StringBuilder(slots.Length * 6);
            for (int i = 0; i < slots.Length; i++)
            {
                ItemStack stack = slots[i]?.Itemstack;
                int id = stack?.Collectible != null && stack.StackSize > 0
                    ? CraftCatalog.Pack(stack.Collectible)
                    : 0;
                key.Append(id).Append(';');
            }
            return key.ToString();
        }

        static bool TryReadGrid(
            InventoryBase inventory,
            out ItemSlot[] slots,
            out HashSet<int> filledIds,
            out int filled,
            out int gridWidth,
            out string gridKey)
        {
            slots = null;
            filledIds = new HashSet<int>();
            filled = 0;
            gridWidth = 0;
            gridKey = null;
            if (inventory == null || inventory.Count < 2) return false;

            int slotCount = inventory.Count - 1;
            gridWidth = (int)Math.Sqrt(slotCount);
            if (gridWidth * gridWidth != slotCount) return false;

            slots = new ItemSlot[slotCount];
            var keySb = new StringBuilder(slotCount * 10);
            for (int i = 0; i < slotCount; i++)
            {
                ItemSlot slot = inventory[i];
                slots[i] = slot;
                ItemStack stack = slot?.Itemstack;
                if (stack?.Collectible != null && stack.StackSize > 0)
                {
                    int id = CraftCatalog.Pack(stack.Collectible);
                    filledIds.Add(id);
                    filled++;
                    // StackSize + la firma local de los atributos invalida estados antiguos.
                    keySb.Append(id).Append('x').Append(stack.StackSize).Append('#')
                         .Append(stack.GetHashCode()).Append('#')
                         .Append(stack.Attributes?.GetHashCode() ?? 0).Append(';');
                }
                else keySb.Append("0;");
            }
            gridKey = keySb.ToString();
            return true;
        }

        sealed class RecipeIdentityCacheEntry
        {
            public string Value;
        }

        static readonly ConditionalWeakTable<GridRecipe, RecipeIdentityCacheEntry> RecipeIdentityCache =
            new ConditionalWeakTable<GridRecipe, RecipeIdentityCacheEntry>();

        public static string RecipeIdentity(GridRecipe recipe)
        {
            if (recipe == null) return string.Empty;
            try
            {
                return RecipeIdentityCache.GetValue(recipe, BuildRecipeIdentity).Value ?? string.Empty;
            }
            catch
            {
                // A partial identity could collide across mod recipes. Returning no key
                // deliberately disables the carousel for this matching grid rather than
                // sending a recipe identifier that the server might resolve ambiguously.
                return string.Empty;
            }
        }

        static RecipeIdentityCacheEntry BuildRecipeIdentity(GridRecipe recipe)
        {
            ItemStack output = recipe.Output?.ResolvedItemStack;
            string outputCode = output?.Collectible?.Code?.ToString()
                ?? recipe.Output?.Code?.ToString() ?? string.Empty;
            string outputClass = output?.Class.ToString() ?? string.Empty;
            int outputQuantity = output?.StackSize ?? recipe.Output?.StackSize ?? 0;

            // The network key must be deterministic between client and server. Do not use
            // ItemStack.ToBytes(): its numeric collectible IDs are runtime registry values.
            // Recipe asset name + code + quantity + stable JSON/recipe fields distinguish
            // normal asset recipes as well as output variants with different attributes.
            string outputAttributes = string.Empty;
            string recipeAttributes = string.Empty;
            try { outputAttributes = recipe.Output?.Attributes?.Token?.ToString() ?? string.Empty; } catch { }
            try { recipeAttributes = recipe.Attributes?.Token?.ToString() ?? string.Empty; } catch { }

            StringBuilder properties = new StringBuilder(160);
            AppendIdentityField(properties, outputAttributes);
            AppendIdentityField(properties, recipe.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendIdentityField(properties, recipe.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendIdentityField(properties, recipe.Shapeless ? "1" : "0");
            AppendIdentityField(properties, recipe.RecipeGroup.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendIdentityField(properties, recipe.RequiresTrait ?? string.Empty);
            AppendIdentityField(properties, recipe.CopyAttributesFrom ?? string.Empty);
            AppendIdentityField(properties, recipe.AverageDurability ? "1" : "0");
            AppendIdentityField(properties, string.Join("\u001f", recipe.MergeAttributesFrom ?? Array.Empty<string>()));
            AppendIdentityField(properties, recipeAttributes);

            StringBuilder key = new StringBuilder(144);
            AppendIdentityField(key, recipe.Name?.ToShortString() ?? string.Empty);
            AppendIdentityField(key, outputCode);
            AppendIdentityField(key, outputClass);
            AppendIdentityField(key, outputQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendIdentityField(key, HashText(properties.ToString()));
            return new RecipeIdentityCacheEntry { Value = key.ToString() };
        }

        static void AppendIdentityField(StringBuilder builder, string value)
        {
            value = value ?? string.Empty;
            builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(value).Append(';');
        }

        static string HashText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            // A compact deterministic string signature; no registry IDs or object hashes.
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty);
        }

        /// <summary>
        /// Client-side, low-frequency carousel update. It changes the real native
        /// matching recipe (not just a drawn icon), and sends that choice to the server.
        /// </summary>
        public static void ClientCarouselTick(float dt)
        {
            try
            {
                ICoreClientAPI api = HandbookOptMod.Capi;
                IPlayer player = api?.World?.Player;
                IInventory ownGrid = player?.InventoryManager?.GetOwnInventory("craftinggrid");
                InventoryBase inventory = ownGrid as InventoryBase;
                if (api == null || player == null || inventory == null) return;
                // A cosmetic carousel without a working client channel could show one
                // recipe while the server crafts another. Fail closed instead.
                if (!HandbookOptMod.RecipeVariantClientChannelAvailable) return;
                if (!HandbookOptMod.GridStates.TryGetValue(inventory, out HandbookOptMod.GridSearchState state) || state == null)
                    return;
                List<GridRecipe> variants = state.MatchingRecipes;
                if (variants == null || variants.Count < 2 || state.Previous == null)
                {
                    state.NextVariantAt = 0;
                    state.LastCarouselTickAt = api.ElapsedMilliseconds;
                    return;
                }
                if (!TryReadGrid(inventory, out ItemSlot[] slots, out _, out int filled, out int gridWidth, out string gridKey)) return;

                GridRecipeIndex index = HandbookOptMod.ClientGridIndex;
                if (index == null || !index.IsReady || !ReferenceEquals(index.World, api.World)
                    || !ReferenceEquals(state.Index, index) || state.GridKey != gridKey)
                {
                    state.LastCarouselTickAt = api.ElapsedMilliseconds;
                    state.NextVariantAt = 0;
                    return;
                }

                long now = api.ElapsedMilliseconds;
                long elapsed = state.LastCarouselTickAt > 0 ? Math.Max(0, now - state.LastCarouselTickAt) : 0;
                state.LastCarouselTickAt = now;

                if (filled <= 0 || variants.Count < 2 || state.Previous == null)
                {
                    state.NextVariantAt = 0;
                    return;
                }

                if (!state.Previous.Enabled)
                {
                    state.NextVariantAt = 0;
                    return;
                }

                if (!IsInventoryDialogOpen(api))
                {
                    // Hidden inventory: hold the carousel rather than cycling a hidden
                    // output slot; opening it restarts the 2-second delay.
                    state.NextVariantAt = now + 2000;
                    return;
                }

                bool hoveringOutput = IsMouseOverCraftingOutput(api, inventory);
                if (hoveringOutput)
                {
                    // Freeze the remaining delay while hovered, then continue it on leave.
                    if (state.NextVariantAt > 0) state.NextVariantAt += elapsed;
                    return;
                }

                if (state.NextVariantAt <= 0) state.NextVariantAt = now + 2000;
                if (now < state.NextVariantAt) return;

                int currentIndex = state.ActiveRecipeIndex;
                if (currentIndex < 0 || currentIndex >= variants.Count || !ReferenceEquals(variants[currentIndex], state.Previous))
                {
                    currentIndex = variants.IndexOf(state.Previous);
                    if (currentIndex < 0) currentIndex = 0;
                }

                for (int offset = 1; offset <= variants.Count; offset++)
                {
                    int nextIndex = (currentIndex + offset) % variants.Count;
                    GridRecipe next = variants[nextIndex];
                    if (next == null || !next.Enabled || !next.Matches(player, api.World, slots, gridWidth)) continue;
                    if (!InvokeNativeFoundMatch(inventory, next)) continue;

                    state.Previous = next;
                    state.GridKey = gridKey;
                    state.ActiveRecipeIndex = nextIndex;
                    state.NextVariantAt = now + 2000;
                    HandbookOptMod.SendRecipeVariantSelection(next);
                    return;
                }

                // Do not keep displaying an output whose custom Matches rule expired.
                // Validation happens only at a carousel boundary, not every 100 ms.
                if (!state.Previous.Enabled || !state.Previous.Matches(player, api.World, slots, gridWidth))
                {
                    ItemSlot outputSlot = inventory[inventory.Count - 1];
                    ClearNativeMatchState(inventory, outputSlot);
                    ClearRecipeMatches(state, gridKey);
                    return;
                }
                state.NextVariantAt = now + 2000;
            }
            catch (Exception e)
            {
                try { HandbookOptMod.Capi?.Logger?.Debug("[HandbookOpt] Se omitió un paso del carrusel de recetas: {0}", e.Message); } catch { }
            }
        }

        static bool IsInventoryDialogOpen(ICoreClientAPI api)
        {
            try
            {
                var opened = api?.Gui?.OpenedGuis;
                if (opened == null) return false;
                for (int i = 0; i < opened.Count; i++)
                {
                    GuiDialog dialog = opened[i];
                    if (dialog == null || !dialog.IsOpened()) continue;
                    if (string.Equals(dialog.ToggleKeyCombinationCode, "inventory", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(dialog.GetType().Name, "GuiDialogInventory", StringComparison.Ordinal)) return true;
                }
            }
            catch { }
            return false;
        }

        static bool IsMouseOverCraftingOutput(ICoreClientAPI api, IInventory craftInventory)
        {
            try
            {
                var opened = api?.Gui?.OpenedGuis;
                if (opened == null) return false;
                for (int i = 0; i < opened.Count; i++)
                {
                    GuiDialog dialog = opened[i];
                    if (dialog == null || !dialog.IsOpened()) continue;
                    if (!string.Equals(dialog.ToggleKeyCombinationCode, "inventory", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(dialog.GetType().Name, "GuiDialogInventory", StringComparison.Ordinal)) continue;

                    GuiComposer composer = dialog.SingleComposer;
                    if (composer == null) continue;
                    FieldInfo[] fields = composer.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int f = 0; f < fields.Length; f++)
                    {
                        object value;
                        try { value = fields[f].GetValue(composer); }
                        catch { continue; }
                        if (ContainsHoveredOutputGrid(value, craftInventory, new HashSet<object>(), 0)) return true;
                    }

                    // Common inventory element keys as a fallback for game UI revisions.
                    string[] keys = { "craftinggrid", "craftingGrid", "craftingGridSlots", "craftinggrid-slots" };
                    for (int k = 0; k < keys.Length; k++)
                    {
                        try
                        {
                            GuiElementItemSlotGridBase grid = composer.GetElement(keys[k]) as GuiElementItemSlotGridBase;
                            if (IsHoveredOutputGrid(grid, craftInventory)) return true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return false;
        }

        static bool ContainsHoveredOutputGrid(object value, IInventory craftInventory, HashSet<object> visited, int depth)
        {
            if (value == null || depth > 2) return false;
            if (value is GuiElementItemSlotGridBase grid) return IsHoveredOutputGrid(grid, craftInventory);
            if (value is string || value.GetType().IsPrimitive) return false;
            if (!value.GetType().IsValueType && !visited.Add(value)) return false;

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                    if (ContainsHoveredOutputGrid(entry.Value, craftInventory, visited, depth + 1)) return true;
                return false;
            }
            if (value is IEnumerable enumerable)
            {
                foreach (object entry in enumerable)
                    if (ContainsHoveredOutputGrid(entry, craftInventory, visited, depth + 1)) return true;
            }
            return false;
        }

        static bool IsHoveredOutputGrid(GuiElementItemSlotGridBase grid, IInventory craftInventory)
        {
            return grid != null && ReferenceEquals(grid.hoverInv, craftInventory)
                && grid.hoverSlotId == craftInventory.Count - 1;
        }

        /// <summary>
        /// Apply the selected catalog variant to the real client crafting grid after its
        /// ingredients have been transferred. This suppresses output-slot animation while
        /// keeping the displayed result tied to the selected recipe.
        /// </summary>
        public static bool ApplyClientRecipeSelection(GridRecipe recipe)
        {
            try
            {
                ICoreClientAPI api = HandbookOptMod.Capi;
                IPlayer player = api?.World?.Player;
                InventoryBase inventory = player?.InventoryManager?.GetOwnInventory("craftinggrid") as InventoryBase;
                if (api == null || player == null || inventory == null || recipe == null || !recipe.Enabled) return false;
                if (!TryReadGrid(inventory, out ItemSlot[] slots, out _, out int filled, out int gridWidth, out string gridKey) || filled == 0)
                    return false;
                if (!recipe.Matches(player, api.World, slots, gridWidth)) return false;
                if (!InvokeNativeFoundMatch(inventory, recipe)) return false;

                GridRecipeIndex index = HandbookOptMod.ClientGridIndex;
                HandbookOptMod.GridSearchState state = HandbookOptMod.GridStates.GetValue(inventory, _ => new HandbookOptMod.GridSearchState());
                state.Index = index;
                state.Previous = recipe;
                state.GridKey = gridKey;
                state.GridCompositionKey = BuildGridCompositionKey(slots);
                state.MatchingRecipes = null; // output-slot carousel intentionally disabled
                state.ActiveRecipeIndex = -1;
                state.NextVariantAt = 0;
                state.LastCarouselTickAt = api.ElapsedMilliseconds;
                state.ExplicitServerSelection = false;
                HandbookOptMod.SendRecipeVariantSelection(recipe);
                return true;
            }
            catch (Exception e)
            {
                try { HandbookOptMod.Capi?.Logger?.Debug("[HandbookOpt] No se pudo fijar la receta seleccionada en la cuadrícula cliente: {0}", e.Message); } catch { }
                return false;
            }
        }

        public static void ApplyServerRecipeSelection(IPlayer player, string recipeKey)
        {
            try
            {
                ICoreServerAPI api = HandbookOptMod.ServerApi;
                IWorldAccessor world = api?.World;
                InventoryBase inventory = player?.InventoryManager?.GetOwnInventory("craftinggrid") as InventoryBase;
                if (api == null || world?.GridRecipes == null || player == null || inventory == null) return;
                GridRecipeIndex index = HandbookOptMod.ServerGridIndex;
                bool indexUsable = index != null && index.IsReady && ReferenceEquals(index.World, world)
                    && world.GridRecipes.Count == index.SourceRecipeCount;

                if (!TryReadGrid(inventory, out ItemSlot[] slots, out HashSet<int> filledIds, out int filled, out int gridWidth, out string gridKey) || filled == 0)
                    return;

                GridRecipe selected = null;
                if (indexUsable && index.DuplicateRecipesByIdentity.TryGetValue(recipeKey, out List<GridRecipe> matchingKeys))
                {
                    for (int i = 0; i < matchingKeys.Count; i++)
                    {
                        GridRecipe recipe = matchingKeys[i];
                        if (recipe == null || !recipe.Enabled) continue;
                        if (!HasAllExactIngredientsPresent(recipe, filledIds, gridWidth)) continue;
                        if (!recipe.Matches(player, world, slots, gridWidth)) continue;
                        selected = recipe;
                        break;
                    }
                }
                else if (indexUsable && index.RecipeByIdentity.TryGetValue(recipeKey, out GridRecipe singleRecipe)
                    && singleRecipe != null && singleRecipe.Enabled
                    && HasAllExactIngredientsPresent(singleRecipe, filledIds, gridWidth)
                    && singleRecipe.Matches(player, world, slots, gridWidth))
                {
                    selected = singleRecipe;
                }
                else if (!indexUsable)
                {
                    // Rare startup/rebuild fallback only: the optimized index is not ready,
                    // so resolve the requested key against the live server recipe list. The
                    // normal carousel path uses the precomputed maps above.
                    for (int i = 0; i < world.GridRecipes.Count; i++)
                    {
                        GridRecipe recipe = world.GridRecipes[i];
                        if (recipe == null || !recipe.Enabled) continue;
                        if (!string.Equals(RecipeIdentity(recipe), recipeKey, StringComparison.Ordinal)) continue;
                        if (!HasAllExactIngredientsPresent(recipe, filledIds, gridWidth)) continue;
                        if (!recipe.Matches(player, world, slots, gridWidth)) continue;
                        selected = recipe;
                        break;
                    }
                }
                if (selected == null) return;

                object inventoryKey = inventory;
                HandbookOptMod.GridSearchState state = HandbookOptMod.GridStates.GetValue(inventoryKey, _ => new HandbookOptMod.GridSearchState());
                state.Index = index;
                state.Previous = selected;
                state.GridKey = gridKey;
                state.GridCompositionKey = BuildGridCompositionKey(slots);
                state.ExplicitServerSelection = true;
                if (state.MatchingRecipes != null)
                {
                    int selectedIndex = state.MatchingRecipes.FindIndex(r => string.Equals(RecipeIdentity(r), recipeKey, StringComparison.Ordinal));
                    state.ActiveRecipeIndex = selectedIndex;
                }
                // This is a validated request from the owner of the inventory. The result
                // is recalculated through the native method, never copied from the client.
                if (!InvokeNativeFoundMatch(inventory, selected))
                {
                    state.Previous = null;
                    state.ActiveRecipeIndex = -1;
                    state.ExplicitServerSelection = false;
                    return;
                }
                try { api.Logger.Debug("[HandbookOpt] Aceptada selección de receta alternativa {0} para {1}.", recipeKey, player.PlayerName); }
                catch { }
            }
            catch (Exception e)
            {
                try { HandbookOptMod.ServerApi?.Logger?.Debug("[HandbookOpt] No se pudo aplicar la selección alternativa en el servidor: {0}", e.Message); } catch { }
            }
        }

        static bool ContainsRecipeReference(IList<GridRecipe> recipes, GridRecipe target)
        {
            if (recipes == null || target == null) return false;
            for (int i = 0; i < recipes.Count; i++)
                if (ReferenceEquals(recipes[i], target)) return true;
            return false;
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

    /// <summary>
    /// Sends the current carousel choice before the normal inventory click is processed.
    /// This reduces the chance of the server crafting the previous variant if the player
    /// clicks immediately after the displayed result changes.
    /// </summary>
    [HarmonyPatch]
    public static class CraftingOutputVariantClickPatch
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(GuiElementItemSlotGridBase), nameof(GuiElementItemSlotGridBase.OnMouseDownOnElement),
                new[] { typeof(ICoreClientAPI), typeof(MouseEvent) });
        }

        static bool Prepare() => TargetMethod() != null;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(GuiElementItemSlotGridBase __instance)
        {
            try
            {
                ICoreClientAPI api = HandbookOptMod.Capi;
                IInventory expectedGrid = api?.World?.Player?.InventoryManager?.GetOwnInventory("craftinggrid");
                if (__instance == null || expectedGrid == null) return;
                if (!ReferenceEquals(__instance.hoverInv, expectedGrid) || __instance.hoverSlotId != expectedGrid.Count - 1) return;
                if (!HandbookOptMod.GridStates.TryGetValue(expectedGrid, out HandbookOptMod.GridSearchState state) || state?.Previous == null) return;
                HandbookOptMod.SendRecipeVariantSelection(state.Previous);
            }
            catch { /* A failed hint must never cancel the native inventory click. */ }
        }
    }
}
