using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
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
        public const int RecipeVariantProtocolVersion = 6;
        static long clientPendingSelectionTickListener;
        static long serverPendingSelectionTickListener;
        static int packetVersionMismatchLogged;
        static IClientNetworkChannel recipeVariantClientChannel;
        static IServerNetworkChannel recipeVariantServerChannel;
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
            // Runtime indexes are kept in memory; no persistent disk-cache code is included.
            // Retain the established world-entry indexing behavior.
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

            // Install the three pause-sync postfixes explicitly. They must not depend on
            // PatchAll discovering a protected, lowercase vanilla callback, and must still
            // be installed if an unrelated patch in the assembly fails during PatchAll.
            try { InstallPauseSyncPatches(api); }
            catch (Exception e) { api.Logger.Error("[HandbookOpt][PauseSync] No se pudo instalar la sincronización de pausa: {0}", e); }

            // Bloquea el input de diálogos que quedan debajo del selector mientras el
            // puntero está sobre el panel visible (incluido el desplegable de mods).
            // Se instala por separado porque las ventanas de otros mods pueden sobrescribir
            // los métodos virtuales de GuiDialog.
            try { HandbookOptModalInputGuard.Install(api, HarmonyInst); }
            catch (Exception e) { api.Logger.Warning("[HandbookOpt][ModalInputGuard] No se pudo instalar el bloqueo de clics de fondo: {0}", e); }

            EnsureGridPatch(api);
            try
            {
                clientPendingSelectionTickListener = api.Event.RegisterGameTickListener(CraftingGridPatch.ProcessPendingClientSelection, 100);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[HandbookOpt] No se pudo registrar el reintento de selección pendiente en cliente: {0}", e.Message);
            }
            api.Event.LevelFinalize += OnLevelFinalize;
            api.Event.LeaveWorld += OnLeaveWorld;

            Picker = new CraftPickerDialog(api);
            api.Gui.RegisterDialog(Picker);

            // Captura global del arrastre solo para la barra de título del selector;
            // se limita al botón izquierdo durante un arrastre real.
            HandbookOptDialogDragCapture.Initialize(api);

            // GUIOrOtherControls permite alternar el panel mientras el inventario
            // (y por tanto otro diálogo) está abierto. El manejador devuelve false si
            // el inventario no está abierto o si el campo de búsqueda tiene el foco.
            api.Input.RegisterHotKey(
                TogglePickerHotKeyCode,
                HandbookOptLang.Get("hotkey-toggle-recipe-panel"),
                GlKeys.R,
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
            try
            {
                // Solo una selección pendiente por jugador; el reintento usa los diccionarios
                // del índice publicado y no recorre el catálogo durante la reconstrucción.
                serverPendingSelectionTickListener = api.Event.RegisterGameTickListener(CraftingGridPatch.ProcessPendingServerSelections, 250);
            }
            catch (Exception e)
            {
                api.Logger.Warning("[HandbookOpt] No se pudo registrar el reintento de selección pendiente en servidor: {0}", e.Message);
            }
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
            // Begin indexing immediately at LevelFinalize, in fixed batches of up to 700 recipes per client tick.
            try
            {
                CraftCatalog.Begin(Capi, Capi?.World);
            }
            catch (Exception e)
            {
                Capi?.Logger?.Warning("[HandbookOpt] Índice del catálogo no listo: {0}", e.Message);
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
                Capi?.Logger.Warning("[HandbookOpt] No se pudo alternar el panel con la tecla R: {0}", e.Message);
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
                if (fromPlayer == null || packet == null ||
                    (string.IsNullOrWhiteSpace(packet.RecipeAssetKey) && string.IsNullOrWhiteSpace(packet.RecipeKey)
                     && string.IsNullOrWhiteSpace(packet.FallbackRecipeKey))) return;
                // Reject peers with another packet/selection protocol. Log this only once.
                if (packet.ProtocolVersion != RecipeVariantProtocolVersion)
                {
                    if (Interlocked.Exchange(ref packetVersionMismatchLogged, 1) == 0)
                    {
                        try { ServerApi?.Logger?.Warning("[HandbookOpt] Se descartaron paquetes de selección de recetas con versión de protocolo incompatible."); } catch { }
                    }
                    return;
                }
                CraftingGridPatch.ApplyServerRecipeSelection(fromPlayer, packet.RecipeAssetKey, packet.RecipeKey, packet.FallbackRecipeKey, queueOnFailure: true);
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
                string assetKey = recipe.Name?.ToString() ?? string.Empty;
                string key = CraftingGridPatch.RecipeIdentity(recipe);
                string fallbackKey = CraftingGridPatch.RecipeFallbackIdentity(recipe);
                if (string.IsNullOrWhiteSpace(assetKey) && string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(fallbackKey)) return;
                recipeVariantClientChannel.SendPacket(new RecipeVariantSelectionPacket
                {
                    RecipeAssetKey = assetKey,
                    RecipeKey = key,
                    FallbackRecipeKey = fallbackKey,
                    ProtocolVersion = RecipeVariantProtocolVersion
                });
            }
            catch (Exception e)
            {
                try { Capi?.Logger?.Debug("[HandbookOpt] No se pudo sincronizar la receta mostrada en el carrusel: {0}", e.Message); }
                catch { }
            }
        }

        private void OnServerLeaveWorld()
        {
            CraftingGridPatch.ClearPendingServerSelections();
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

        // Pause synchronization is installed separately from PatchAll so an unrelated patch
        // failure cannot silently disable this user-facing behavior.
        static void InstallPauseSyncPatches(ICoreClientAPI api)
        {
            if (HarmonyInst == null) throw new InvalidOperationException("Harmony del cliente no está inicializado.");

            InstallPausePostfix(nameof(GuiDialogHandbook.OnGuiOpened), Type.EmptyTypes, typeof(HandbookPauseOpenedPatch));
            InstallPausePostfix(nameof(GuiDialogHandbook.OnGuiClosed), Type.EmptyTypes, typeof(HandbookPauseClosedPatch));
            InstallPausePostfix("onTogglePause", new[] { typeof(bool) }, typeof(HandbookPauseTogglePatch));

            api.Logger.Notification("[HandbookOpt][PauseSync] Sincronización Handbook ↔ selector instalada (OnGuiOpened, OnGuiClosed, onTogglePause).");
        }

        static void InstallPausePostfix(string originalName, Type[] originalParameters, Type patchType)
        {
            MethodInfo original = AccessTools.Method(typeof(GuiDialogHandbook), originalName, originalParameters);
            MethodInfo postfix = AccessTools.Method(patchType, "Postfix");
            if (original == null) throw new MissingMethodException(typeof(GuiDialogHandbook).FullName, originalName);
            if (postfix == null) throw new MissingMethodException(patchType.FullName, "Postfix");
            HarmonyInst.Patch(original, postfix: new HarmonyMethod(postfix));
        }

        private void OnLeaveWorld()
        {
            CraftingGridPatch.ClearPendingClientSelection();
            VanillaHandbookSearchIndex.Reset();
            HandbookOptDialogDragCapture.Cancel();
            welcomeShownThisWorld = false;
            CraftCatalog.Stop(Capi);
            try { Picker?.TryClose(); } catch { }
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
            VanillaHandbookSearchIndex.Reset();
            if (clientPendingSelectionTickListener != 0)
            {
                try { Capi?.Event?.UnregisterGameTickListener(clientPendingSelectionTickListener); } catch { }
                clientPendingSelectionTickListener = 0;
            }
            if (serverPendingSelectionTickListener != 0)
            {
                try { ServerApi?.Event?.UnregisterGameTickListener(serverPendingSelectionTickListener); } catch { }
                serverPendingSelectionTickListener = 0;
            }
            CraftingGridPatch.ClearPendingClientSelection();
            CraftingGridPatch.ClearPendingServerSelections();
            recipeVariantClientChannel = null;
            recipeVariantServerChannel = null;
            HandbookOptDialogDragCapture.Dispose();
            HandbookOptPauseController.Reset(Capi);
            try { HarmonyInst?.UnpatchAll(HarmonyId); } catch { /* ignore */ }
            HandbookOptModalInputGuard.Reset();
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
        // The fully qualified recipe asset name is the primary network selector. It avoids
        // relying on peer-specific serialization of complex ingredient conditions.
        public readonly Dictionary<string, GridRecipe> RecipeByAssetName;
        public readonly Dictionary<string, List<GridRecipe>> DuplicateRecipesByAssetName;
        // Stable fallback key excludes Tags conditions. Only used if the full asset name and
        // normal fingerprint are unavailable; resolution still must be unique and pass Matches.
        public readonly Dictionary<string, GridRecipe> RecipeByFallbackIdentity;
        public readonly Dictionary<string, List<GridRecipe>> DuplicateRecipesByFallbackIdentity;
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
            RecipeByAssetName = new Dictionary<string, GridRecipe>(RecipeOrder.Count, StringComparer.OrdinalIgnoreCase);
            DuplicateRecipesByAssetName = new Dictionary<string, List<GridRecipe>>(StringComparer.OrdinalIgnoreCase);
            RecipeByFallbackIdentity = new Dictionary<string, GridRecipe>(RecipeOrder.Count, StringComparer.Ordinal);
            DuplicateRecipesByFallbackIdentity = new Dictionary<string, List<GridRecipe>>(StringComparer.Ordinal);
            foreach (KeyValuePair<GridRecipe, int> entry in RecipeOrder)
            {
                AddIdentityLookup(RecipeByIdentity, DuplicateRecipesByIdentity,
                    CraftingGridPatch.RecipeIdentity(entry.Key), entry.Key);
                AddIdentityLookup(RecipeByAssetName, DuplicateRecipesByAssetName,
                    entry.Key?.Name?.ToString(), entry.Key);
                if (CraftingGridPatch.HasTagConditions(entry.Key))
                {
                    AddIdentityLookup(RecipeByFallbackIdentity, DuplicateRecipesByFallbackIdentity,
                        CraftingGridPatch.RecipeFallbackIdentity(entry.Key), entry.Key);
                }
            }
            IsReady = world != null;
        }

        static void AddIdentityLookup(Dictionary<string, GridRecipe> single,
            Dictionary<string, List<GridRecipe>> duplicates, string key, GridRecipe recipe)
        {
            if (string.IsNullOrEmpty(key) || recipe == null) return;
            if (!single.TryGetValue(key, out GridRecipe existing))
            {
                single[key] = recipe;
                return;
            }
            if (!duplicates.TryGetValue(key, out List<GridRecipe> values))
            {
                values = new List<GridRecipe>(2) { existing };
                duplicates[key] = values;
            }
            values.Add(recipe);
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

                var shown = fiShown.GetValue(__instance) as List<IFlatListItem>;
                var all = fiAll.GetValue(__instance) as List<GuiHandbookPage>;
                if (shown == null || all == null) return true;

                // Precalienta la búsqueda del Handbook vanilla por pequeños lotes en el
                // hilo de cliente. No interviene en CraftCatalog ni en el índice de recetas.
                VanillaHandbookSearchIndex.EnsurePolling(HandbookOptMod.Capi, __instance, all);
                if ((bool)fiLoading.GetValue(__instance)) return true;

                string search = (fiSearch.GetValue(__instance) as string) ?? "";
                search = search.Trim();
                string cat = __instance.currentCatgoryCode;
                string cacheKey = (cat ?? "") + "|" + search + "|" + RuntimeHelpers.GetHashCode(all) + "|" + all.Count;

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

                bool hasSearchIndex = VanillaHandbookSearchIndex.TryGetEntries(all, out var indexedPages);
                int sourceCount = hasSearchIndex ? indexedPages.Count : all.Count;
                var weighted = new List<WeightedPage>(Math.Min(sourceCount, 512));

                if (hasSearchIndex)
                {
                    List<int> candidates = VanillaHandbookSearchIndex.GetCandidateIndices(needle, out bool scanAll, out bool fallbackOnly);
                    if (scanAll)
                    {
                        for (int i = 0; i < indexedPages.Count; i++)
                            AddIndexedPageIfMatches(weighted, indexedPages[i], cat, needle);
                    }
                    else if (fallbackOnly)
                    {
                        var fallbackIndices = VanillaHandbookSearchIndex.FallbackEntryIndices;
                        if (fallbackIndices != null)
                            for (int i = 0; i < fallbackIndices.Count; i++)
                                AddIndexedPageIfMatches(weighted, indexedPages[fallbackIndices[i]], cat, needle);
                    }
                    else
                    {
                        if (candidates != null)
                            for (int i = 0; i < candidates.Count; i++)
                                AddIndexedPageIfMatches(weighted, indexedPages[candidates[i]], cat, needle);

                        // The indexed posting lists deliberately omit very long or unreadable
                        // third-party pages. Check those few entries too, preserving correctness.
                        var fallbackIndices = VanillaHandbookSearchIndex.FallbackEntryIndices;
                        if (fallbackIndices != null)
                            for (int i = 0; i < fallbackIndices.Count; i++)
                                AddIndexedPageIfMatches(weighted, indexedPages[fallbackIndices[i]], cat, needle);
                    }
                }
                else
                {
                    // First search / index build fallback. Keep the previous behavior until
                    // the complete immutable snapshot is ready; no partial result is exposed.
                    for (int i = 0; i < all.Count; i++)
                    {
                        var page = all[i];
                        if (page == null || page.IsDuplicate) continue;
                        if (cat != null && page.CategoryCode != cat) continue;
                        var pageText = page.GetPageText();
                        AddPageIfMatches(weighted, page, page.CategoryCode, pageText.Title ?? string.Empty, pageText.Text ?? string.Empty, cat, needle);
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

        private static void AddIndexedPageIfMatches(List<WeightedPage> weighted, VanillaHandbookSearchIndex.IndexedPage indexed, string cat, string needle)
        {
            if (indexed == null || indexed.Page == null) return;
            if (cat != null && indexed.CategoryCode != cat) return;
            string title = indexed.Title ?? string.Empty;
            string body = indexed.Body ?? string.Empty;
            if (!indexed.TextCaptured)
            {
                var pageText = indexed.Page.GetPageText();
                title = pageText.Title ?? string.Empty;
                body = pageText.Text ?? string.Empty;
            }
            AddPageIfMatches(weighted, indexed.Page, indexed.CategoryCode, title, body, cat, needle);
        }

        private static void AddPageIfMatches(List<WeightedPage> weighted, GuiHandbookPage page, string pageCategory, string title, string body, string cat, string needle)
        {
            if (page == null || (cat != null && pageCategory != cat)) return;

            int titleHits = CountContains(title, needle);
            if (titleHits == 0)
            {
                int bodyHits = CountContains(body, needle);
                if (bodyHits == 0) return;
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
    // Pausa: sincronizar el selector de recetas con el Handbook vanilla
    // -------------------------------------------------------------------------
    public static class HandbookPauseOpenedPatch
    {
        public static void Postfix(GuiDialog __instance)
        {
            if (!(__instance is GuiDialogHandbook)) return; // por si OnGuiOpened es heredado
            try { HandbookOptPauseController.VanillaHandbookChanged(HandbookOptMod.Capi, true, __instance); } catch { }
        }
    }

    public static class HandbookPauseClosedPatch
    {
        public static void Postfix(GuiDialog __instance)
        {
            if (!(__instance is GuiDialogHandbook)) return;
            try { HandbookOptPauseController.VanillaHandbookChanged(HandbookOptMod.Capi, false, __instance); } catch { }
        }
    }


    // The vanilla Handbook toggles pause from its protected onTogglePause(bool) callback.
    // Polling IsGamePaused is too late/unreliable while the world is paused, so synchronize
    // from that exact user action. This patch is state-only; it does not change window behavior.
    public static class HandbookPauseTogglePatch
    {
        public static void Postfix(GuiDialogHandbook __instance, bool __0)
        {
            // __0 binds by argument position, avoiding dependence on the vanilla parameter name.
            try { HandbookOptPauseController.VanillaPauseToggled(HandbookOptMod.Capi, __0); } catch { }
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
        sealed class PendingServerSelection
        {
            public string RecipeAssetKey;
            public string RecipeKey;
            public string FallbackRecipeKey;
            public IPlayer Player;
            public IWorldAccessor World;
            public long ExpiresAt;
        }

        sealed class PendingClientSelection
        {
            public GridRecipe Recipe;
            public InventoryBase Inventory;
            public IWorldAccessor World;
            public long ExpiresAt;
        }

        enum PendingClientSelectionAttempt
        {
            None,
            Applied,
            Waiting,
            Discarded
        }

        static readonly object PendingServerSelectionLock = new object();
        static readonly Dictionary<string, PendingServerSelection> PendingServerSelections =
            new Dictionary<string, PendingServerSelection>(StringComparer.Ordinal);
        static readonly object PendingClientSelectionLock = new object();
        static PendingClientSelection pendingClientSelection;
        static int prefixExceptionLogged;
        static int serverSelectionExceptionLogged;
        static int pendingClientTickExceptionLogged;
        static int pendingServerTickExceptionLogged;
        const long PendingServerSelectionLifetimeMs = 10000;
        const long PendingClientSelectionLifetimeMs = 10000;

        static string ServerPlayerKey(IPlayer player) => !string.IsNullOrWhiteSpace(player?.PlayerUID)
            ? player.PlayerUID : player?.PlayerName ?? string.Empty;

        static void QueuePendingServerSelection(IPlayer player, string recipeAssetKey, string recipeKey, string fallbackRecipeKey)
        {
            string playerKey = ServerPlayerKey(player);
            IWorldAccessor world = HandbookOptMod.ServerApi?.World;
            if (string.IsNullOrWhiteSpace(playerKey) || world == null) return;
            lock (PendingServerSelectionLock)
            {
                // Replacing the map value makes the most recent request win.
                PendingServerSelections[playerKey] = new PendingServerSelection
                {
                    RecipeAssetKey = recipeAssetKey ?? string.Empty,
                    RecipeKey = recipeKey ?? string.Empty,
                    FallbackRecipeKey = fallbackRecipeKey ?? string.Empty,
                    Player = player,
                    World = world,
                    ExpiresAt = Environment.TickCount64 + PendingServerSelectionLifetimeMs
                };
            }
        }

        static bool TryGetPendingServerSelection(IPlayer player, out PendingServerSelection pending)
        {
            pending = null;
            string playerKey = ServerPlayerKey(player);
            if (string.IsNullOrWhiteSpace(playerKey)) return false;
            lock (PendingServerSelectionLock)
            {
                if (!PendingServerSelections.TryGetValue(playerKey, out PendingServerSelection current)) return false;
                if (Environment.TickCount64 >= current.ExpiresAt)
                {
                    PendingServerSelections.Remove(playerKey);
                    return false;
                }
                pending = current;
                return true;
            }
        }

        static bool IsPendingServerSelectionCurrent(string playerKey, PendingServerSelection expected)
        {
            if (string.IsNullOrWhiteSpace(playerKey) || expected == null) return false;
            lock (PendingServerSelectionLock)
                return PendingServerSelections.TryGetValue(playerKey, out PendingServerSelection current)
                    && ReferenceEquals(current, expected);
        }

        static void RemovePendingServerSelection(string playerKey, PendingServerSelection expected)
        {
            if (string.IsNullOrWhiteSpace(playerKey)) return;
            lock (PendingServerSelectionLock)
            {
                if (!PendingServerSelections.TryGetValue(playerKey, out PendingServerSelection current)) return;
                if (expected == null || ReferenceEquals(current, expected)) PendingServerSelections.Remove(playerKey);
            }
        }

        static void RemovePendingServerSelection(IPlayer player, string recipeAssetKey, string recipeKey, string fallbackRecipeKey)
        {
            string playerKey = ServerPlayerKey(player);
            if (string.IsNullOrWhiteSpace(playerKey)) return;
            lock (PendingServerSelectionLock)
            {
                if (!PendingServerSelections.TryGetValue(playerKey, out PendingServerSelection current)) return;
                // An older attempt must not remove a newer, different selection.
                if (string.Equals(current.RecipeAssetKey, recipeAssetKey ?? string.Empty, StringComparison.Ordinal)
                    && string.Equals(current.RecipeKey, recipeKey ?? string.Empty, StringComparison.Ordinal)
                    && string.Equals(current.FallbackRecipeKey, fallbackRecipeKey ?? string.Empty, StringComparison.Ordinal))
                    PendingServerSelections.Remove(playerKey);
            }
        }

        static bool CanIdentifyRecipeSelection(GridRecipe recipe)
        {
            if (recipe == null) return false;
            return !string.IsNullOrWhiteSpace(recipe.Name?.ToString())
                || !string.IsNullOrWhiteSpace(RecipeIdentity(recipe))
                || !string.IsNullOrWhiteSpace(RecipeFallbackIdentity(recipe));
        }

        static bool QueuePendingClientSelection(GridRecipe recipe, InventoryBase inventory, IWorldAccessor world)
        {
            if (recipe == null || inventory == null || world == null || !CanIdentifyRecipeSelection(recipe))
            {
                ClearPendingClientSelection();
                return false;
            }
            lock (PendingClientSelectionLock)
            {
                pendingClientSelection = new PendingClientSelection
                {
                    Recipe = recipe,
                    Inventory = inventory,
                    World = world,
                    ExpiresAt = Environment.TickCount64 + PendingClientSelectionLifetimeMs
                };
            }
            return true;
        }

        static bool TryPeekPendingClientSelection(out PendingClientSelection pending)
        {
            lock (PendingClientSelectionLock)
            {
                pending = pendingClientSelection;
                if (pending == null) return false;
                if (Environment.TickCount64 >= pending.ExpiresAt)
                {
                    pendingClientSelection = null;
                    pending = null;
                    return false;
                }
                return true;
            }
        }

        static bool TryGetPendingClientSelection(InventoryBase inventory, IWorldAccessor world, out PendingClientSelection pending)
        {
            pending = null;
            if (!TryPeekPendingClientSelection(out PendingClientSelection current)) return false;
            if (!ReferenceEquals(current.Inventory, inventory) || !ReferenceEquals(current.World, world)) return false;
            pending = current;
            return true;
        }

        static void RemovePendingClientSelection(PendingClientSelection expected)
        {
            lock (PendingClientSelectionLock)
            {
                if (expected == null || ReferenceEquals(pendingClientSelection, expected)) pendingClientSelection = null;
            }
        }

        public static void ClearPendingClientSelection()
        {
            lock (PendingClientSelectionLock) pendingClientSelection = null;
        }

        public static void ClearPendingServerSelections()
        {
            lock (PendingServerSelectionLock) PendingServerSelections.Clear();
        }

        static void ClearMatchWhileSelectionIsPending(InventoryBase inventory, string gridKey = null)
        {
            if (inventory == null || inventory.Count < 2) return;
            ItemSlot outputSlot = inventory[inventory.Count - 1];
            bool hasMatchingRecipe = false;
            try
            {
                PropertyInfo property = AccessTools.Property(inventory.GetType(), "MatchingRecipe");
                hasMatchingRecipe = property != null && property.GetValue(inventory) != null;
            }
            catch { hasMatchingRecipe = true; }
            if (hasMatchingRecipe || outputSlot?.Itemstack != null) ClearNativeMatchState(inventory, outputSlot);

            HandbookOptMod.GridSearchState state = HandbookOptMod.GridStates.GetValue(inventory, _ => new HandbookOptMod.GridSearchState());
            state.Previous = null;
            state.GridKey = gridKey;
            state.GridCompositionKey = string.Empty;
            state.ExplicitServerSelection = false;
        }

        static bool HasAnyKnownSelectionKey(GridRecipeIndex index, PendingServerSelection pending)
        {
            if (index == null || pending == null) return false;
            if (!string.IsNullOrWhiteSpace(pending.RecipeAssetKey)
                && (index.RecipeByAssetName.ContainsKey(pending.RecipeAssetKey)
                    || index.DuplicateRecipesByAssetName.ContainsKey(pending.RecipeAssetKey))) return true;
            if (!string.IsNullOrWhiteSpace(pending.RecipeKey)
                && (index.RecipeByIdentity.ContainsKey(pending.RecipeKey)
                    || index.DuplicateRecipesByIdentity.ContainsKey(pending.RecipeKey))) return true;
            if (!string.IsNullOrWhiteSpace(pending.FallbackRecipeKey)
                && (index.RecipeByFallbackIdentity.ContainsKey(pending.FallbackRecipeKey)
                    || index.DuplicateRecipesByFallbackIdentity.ContainsKey(pending.FallbackRecipeKey))) return true;
            return false;
        }

        public static void ProcessPendingServerSelections(float dt)
        {
            try
            {
                ICoreServerAPI api = HandbookOptMod.ServerApi;
                IWorldAccessor world = api?.World;
                if (api == null || world == null) return;

                List<KeyValuePair<string, PendingServerSelection>> pendingItems;
                lock (PendingServerSelectionLock)
                    pendingItems = new List<KeyValuePair<string, PendingServerSelection>>(PendingServerSelections);

                long now = Environment.TickCount64;
                GridRecipeIndex index = HandbookOptMod.ServerGridIndex;
                bool indexUsable = index != null && index.IsReady && ReferenceEquals(index.World, world)
                    && world.GridRecipes != null && world.GridRecipes.Count == index.SourceRecipeCount;
                HashSet<string> onlinePlayerKeys = null;
                if (indexUsable)
                {
                    // Build this once per tick: do not scan AllOnlinePlayers separately for every queued request.
                    onlinePlayerKeys = new HashSet<string>(StringComparer.Ordinal);
                    IPlayer[] online = world.AllOnlinePlayers;
                    if (online != null)
                    {
                        for (int i = 0; i < online.Length; i++)
                        {
                            string onlineKey = ServerPlayerKey(online[i]);
                            if (!string.IsNullOrWhiteSpace(onlineKey)) onlinePlayerKeys.Add(onlineKey);
                        }
                    }
                }

                for (int i = 0; i < pendingItems.Count; i++)
                {
                    try
                    {
                        string key = pendingItems[i].Key;
                        PendingServerSelection pending = pendingItems[i].Value;
                        if (pending == null) { RemovePendingServerSelection(key, null); continue; }
                        if (now >= pending.ExpiresAt || !ReferenceEquals(pending.World, world))
                        {
                            RemovePendingServerSelection(key, pending);
                            continue;
                        }
                        if (!indexUsable) continue;
                        if (!onlinePlayerKeys.Contains(key))
                        {
                            RemovePendingServerSelection(key, pending);
                            continue;
                        }
                        if (!IsPendingServerSelectionCurrent(key, pending)) continue;
                        // If none of the identities exists on this server, retrying cannot succeed.
                        // Drop it so a mismatched mod set does not suppress native results for 10s.
                        if (!HasAnyKnownSelectionKey(index, pending))
                        {
                            RemovePendingServerSelection(key, pending);
                            continue;
                        }

                        // The latest request is attempted only through ready indexes. A temporarily
                        // incomplete grid leaves it pending until the next tick or its 10s expiry.
                        ApplyServerRecipeSelection(pending.Player, pending.RecipeAssetKey, pending.RecipeKey,
                            pending.FallbackRecipeKey, queueOnFailure: false);
                    }
                    catch (Exception e)
                    {
                        if (Interlocked.Exchange(ref pendingServerTickExceptionLogged, 1) == 0)
                        {
                            try { HandbookOptMod.ServerApi?.Logger?.Error("[HandbookOpt] Falló un reintento de selección de receta pendiente en el servidor. {0}", e.ToString()); } catch { }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref pendingServerTickExceptionLogged, 1) == 0)
                {
                    try { HandbookOptMod.ServerApi?.Logger?.Error("[HandbookOpt] Falló el procesador de selecciones pendientes del servidor. {0}", e.ToString()); } catch { }
                }
            }
        }

        public static void ProcessPendingClientSelection(float dt)
        {
            try
            {
                if (!TryPeekPendingClientSelection(out PendingClientSelection pending)) return;
                ICoreClientAPI api = HandbookOptMod.Capi;
                IPlayer player = api?.World?.Player;
                InventoryBase inventory = player?.InventoryManager?.GetOwnInventory("craftinggrid") as InventoryBase;
                if (api == null || player == null || inventory == null) return;
                if (!ReferenceEquals(api.World, pending.World) || !ReferenceEquals(inventory, pending.Inventory))
                {
                    RemovePendingClientSelection(pending);
                    return;
                }

                PendingClientSelectionAttempt attempt = TryApplyPendingClientSelection(inventory, player, api.World, out _);
                if (attempt == PendingClientSelectionAttempt.Waiting)
                {
                    string gridKey = null;
                    try { if (TryReadGrid(inventory, out _, out _, out _, out _, out gridKey)) { } } catch { }
                    ClearMatchWhileSelectionIsPending(inventory, gridKey);
                }
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref pendingClientTickExceptionLogged, 1) == 0)
                {
                    try { HandbookOptMod.Capi?.Logger?.Error("[HandbookOpt] Falló un reintento de selección de receta pendiente en el cliente. {0}", e.ToString()); } catch { }
                }
            }
        }

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
                string compositionKey = BuildGridCompositionKey(slots);

                // A recipe selected from the picker may need multiple inventory transfers.
                // Until that exact recipe matches, suppress any competing intermediate result.
                IPlayer localClientPlayer = HandbookOptMod.Capi?.World?.Player;
                bool isClientInventory = localClientPlayer != null && PlayerOwnsInventory(localClientPlayer, inv);
                if (isClientInventory && TryGetPendingClientSelection(inv, HandbookOptMod.Capi?.World, out _))
                {
                    PendingClientSelectionAttempt clientAttempt = TryApplyPendingClientSelection(
                        inv, localClientPlayer, HandbookOptMod.Capi.World, out _);
                    if (clientAttempt == PendingClientSelectionAttempt.Applied) return false;
                    if (clientAttempt == PendingClientSelectionAttempt.Waiting)
                    {
                        ClearNativeMatchStateIfNeeded(__instance, outputSlot);
                        state.Previous = null;
                        state.GridKey = gridKey;
                        state.GridCompositionKey = compositionKey;
                        state.ExplicitServerSelection = false;
                        return false;
                    }
                }

                bool isServerInventory = !isClientInventory && ReferenceEquals(world, HandbookOptMod.ServerApi?.World);
                if (isServerInventory && player != null
                    && TryGetPendingServerSelection(player, out PendingServerSelection pendingSelection))
                {
                    // Do not run native/optimized matching while an explicit choice is waiting:
                    // the queued selection wins once the index and real grid are ready.
                    if (indexUsable && ApplyServerRecipeSelection(player, pendingSelection.RecipeAssetKey,
                        pendingSelection.RecipeKey, pendingSelection.FallbackRecipeKey, queueOnFailure: false))
                        return false;
                    ClearNativeMatchStateIfNeeded(__instance, outputSlot);
                    state.Previous = null;
                    state.GridKey = gridKey;
                    state.GridCompositionKey = compositionKey;
                    state.ExplicitServerSelection = false;
                    return false;
                }

                bool sameGrid = state.GridKey == gridKey;
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
                    state.ExplicitServerSelection = false;
                }
                else if (preserveSelectionAcrossCountChange)
                {
                    state.GridKey = gridKey;
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

                // Preserve native catalog order and stop after the first valid match. The
                // old output-slot carousel is not active; only the picker UI selects variants.
                GridRecipe found = null;
                for (int i = 0; i < candidates.Count; i++)
                {
                    GridRecipe recipe = candidates[i];
                    if (recipe == null || !recipe.Enabled) continue;
                    if (!HasAllExactIngredientsPresent(recipe, filledIds, gridWidth)) continue;
                    if (!recipe.Matches(player, world, slots, gridWidth)) continue;
                    found = recipe;
                    break;
                }
                if (found != null)
                {
                    // Mantener la salida nativa: la primera coincidencia en orden de catálogo.
                    if (!InvokeNativeFoundMatch(__instance, found)) return true;
                    state.Previous = found;
                    state.GridKey = gridKey;
                    state.ExplicitServerSelection = false;
                    return false;
                }

                // Índice completo + ninguna candidata válida: reflejar el estado nativo
                // de "sin receta" sin ejecutar la búsqueda global que acabamos de evitar.
                ClearNativeMatchState(__instance, outputSlot);
                ClearRecipeMatches(state, gridKey);
                return false;
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref prefixExceptionLogged, 1) == 0)
                {
                    try
                    {
                        if (HandbookOptMod.ServerApi != null)
                            HandbookOptMod.ServerApi.Logger.Error("[HandbookOpt] Falló el prefijo de la cuadrícula; se recupera la búsqueda nativa. {0}", e.ToString());
                        else
                            HandbookOptMod.Capi?.Logger?.Error("[HandbookOpt] Falló el prefijo de la cuadrícula; se recupera la búsqueda nativa. {0}", e.ToString());
                    }
                    catch { }
                }
                return true;
            }
        }

        static void ClearRecipeMatches(HandbookOptMod.GridSearchState state, string gridKey)
        {
            if (state == null) return;
            state.Previous = null;
            state.GridKey = gridKey;
            state.ExplicitServerSelection = false;
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
        static readonly ConditionalWeakTable<GridRecipe, RecipeIdentityCacheEntry> RecipeFallbackIdentityCache =
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
                // A partial fingerprint could collide across mod recipes. Returning no key
                // disables this fallback fingerprint; the full asset-name selector remains usable.
                return string.Empty;
            }
        }

        public static bool HasTagConditions(GridRecipe recipe)
        {
            try
            {
                CraftingRecipeIngredient[] ingredients = recipe?.ResolvedIngredients;
                if (ingredients == null) return false;
                for (int i = 0; i < ingredients.Length; i++)
                {
                    CraftingRecipeIngredient ingredient = ingredients[i];
                    if (ingredient != null && !ingredient.Tags.IsEmpty) return true;
                }
            }
            catch { return false; }
            return false;
        }

        public static string RecipeFallbackIdentity(GridRecipe recipe)
        {
            if (recipe == null) return string.Empty;
            try
            {
                return RecipeFallbackIdentityCache.GetValue(recipe, BuildRecipeFallbackIdentity).Value ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        static RecipeIdentityCacheEntry BuildRecipeFallbackIdentity(GridRecipe recipe)
        {
            if (!HasTagConditions(recipe)) return new RecipeIdentityCacheEntry { Value = string.Empty };
            // Fallback is deliberately less specific: it excludes Tags conditions entirely,
            // while retaining the recipe name, output, ordinary ingredient fields, and consumption
            // rules. The server accepts it only for one candidate that matches the live grid.
            string ingredients = BuildResolvedIngredientsIdentity(recipe, includeTagConditions: false);
            if (string.IsNullOrEmpty(ingredients)) return new RecipeIdentityCacheEntry { Value = string.Empty };

            ItemStack output = recipe.Output?.ResolvedItemStack;
            string outputCode = output?.Collectible?.Code?.ToString()
                ?? recipe.Output?.Code?.ToString() ?? string.Empty;
            string outputClass = output?.Class.ToString() ?? string.Empty;
            int outputQuantity = output?.StackSize ?? recipe.Output?.StackSize ?? 0;
            string outputAttributes = string.Empty;
            string recipeAttributes = string.Empty;
            try { outputAttributes = recipe.Output?.Attributes?.Token?.ToString() ?? string.Empty; } catch { }
            try { recipeAttributes = recipe.Attributes?.Token?.ToString() ?? string.Empty; } catch { }

            StringBuilder properties = new StringBuilder(256);
            AppendIdentityField(properties, "recipe-identity-fallback-v1");
            AppendIdentityField(properties, ingredients);
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

            StringBuilder key = new StringBuilder(192);
            AppendIdentityField(key, "handbookopt-recipe-fallback-v1");
            AppendIdentityField(key, recipe.Name?.ToString() ?? string.Empty);
            AppendIdentityField(key, outputCode);
            AppendIdentityField(key, outputClass);
            AppendIdentityField(key, outputQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendIdentityField(key, HashText(properties.ToString()));
            return new RecipeIdentityCacheEntry { Value = key.ToString() };
        }

        static RecipeIdentityCacheEntry BuildRecipeIdentity(GridRecipe recipe)
        {
            // This fingerprint is a secondary selector, not the main network identity.
            // Tag-based conditions are deliberately left unkeyed here because their runtime
            // serialization may differ by peer. The full domain-qualified recipe asset name
            // is sent separately and validated against the actual server-side grid.
            string ingredients = BuildResolvedIngredientsIdentity(recipe, includeTagConditions: true);
            if (string.IsNullOrEmpty(ingredients))
                return new RecipeIdentityCacheEntry { Value = string.Empty };

            ItemStack output = recipe.Output?.ResolvedItemStack;
            string outputCode = output?.Collectible?.Code?.ToString()
                ?? recipe.Output?.Code?.ToString() ?? string.Empty;
            string outputClass = output?.Class.ToString() ?? string.Empty;
            int outputQuantity = output?.StackSize ?? recipe.Output?.StackSize ?? 0;

            string outputAttributes = string.Empty;
            string recipeAttributes = string.Empty;
            try { outputAttributes = recipe.Output?.Attributes?.Token?.ToString() ?? string.Empty; } catch { }
            try { recipeAttributes = recipe.Attributes?.Token?.ToString() ?? string.Empty; } catch { }

            StringBuilder properties = new StringBuilder(256);
            AppendIdentityField(properties, "recipe-identity-v2");
            AppendIdentityField(properties, ingredients);
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

            StringBuilder key = new StringBuilder(192);
            AppendIdentityField(key, "handbookopt-recipe-key-v2");
            // Use the complete domain-qualified asset location, not only its short name.
            AppendIdentityField(key, recipe.Name?.ToString() ?? string.Empty);
            AppendIdentityField(key, outputCode);
            AppendIdentityField(key, outputClass);
            AppendIdentityField(key, outputQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendIdentityField(key, HashText(properties.ToString()));
            return new RecipeIdentityCacheEntry { Value = key.ToString() };
        }

        static string BuildResolvedIngredientsIdentity(GridRecipe recipe, bool includeTagConditions)
        {
            CraftingRecipeIngredient[] resolved = recipe?.ResolvedIngredients;
            if (resolved == null || resolved.Length == 0) return string.Empty;

            var cells = new List<string>(resolved.Length);
            bool hasIngredient = false;
            for (int i = 0; i < resolved.Length; i++)
            {
                CraftingRecipeIngredient ingredient = resolved[i];
                if (ingredient == null)
                {
                    // Shaped recipes need empty cells and positions to remain distinct.
                    if (!recipe.Shapeless) cells.Add(string.Empty);
                    continue;
                }
                hasIngredient = true;

                try
                {
                    if (ingredient.GetType() != typeof(CraftingRecipeIngredient)) return string.Empty;
                    // Do not serialize ComplexTagCondition into a key. Its effective semantic
                    // representation is not guaranteed to be byte-stable across peers. The
                    // full domain-qualified recipe asset name is the primary network selector.
                    if (includeTagConditions && !ingredient.Tags.IsEmpty) return string.Empty;
                    // Returned stacks can be modified by custom recipe logic; do not guess.
                    if (ingredient.ReturnedStack != null) return string.Empty;
                }
                catch { return string.Empty; }

                string code;
                string attributes;
                string ingredientRecipeAttributes;
                try
                {
                    code = ingredient.MatchingType == EnumRecipeMatchType.Exact
                        ? ingredient.ResolvedItemStack?.Collectible?.Code?.ToString() ?? ingredient.Code?.ToString() ?? string.Empty
                        : ingredient.Code?.ToString() ?? ingredient.ResolvedItemStack?.Collectible?.Code?.ToString() ?? string.Empty;
                    attributes = ingredient.Attributes?.Token?.ToString() ?? string.Empty;
                    ingredientRecipeAttributes = ingredient.RecipeAttributes?.Token?.ToString() ?? string.Empty;
                }
                catch { return string.Empty; }
                if (string.IsNullOrWhiteSpace(code)) return string.Empty;

                var allowedVariants = ingredient.MatchingType == EnumRecipeMatchType.Exact
                    ? new List<string>()
                    : new List<string>(ingredient.AllowedVariants ?? Array.Empty<string>());
                var skippedVariants = ingredient.MatchingType == EnumRecipeMatchType.Exact
                    ? new List<string>()
                    : new List<string>(ingredient.SkipVariants ?? Array.Empty<string>());
                allowedVariants.Sort(StringComparer.Ordinal);
                skippedVariants.Sort(StringComparer.Ordinal);

                StringBuilder item = new StringBuilder(192);
                AppendIdentityField(item, ((int)ingredient.Type).ToString(System.Globalization.CultureInfo.InvariantCulture));
                AppendIdentityField(item, ((int)ingredient.MatchingType).ToString(System.Globalization.CultureInfo.InvariantCulture));
                AppendIdentityField(item, code);
                AppendIdentityField(item, (ingredient.Quantity > 0 ? ingredient.Quantity : 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                AppendIdentityField(item, ingredient.MatchingType == EnumRecipeMatchType.Exact ? string.Empty : ingredient.Name ?? string.Empty);
                AppendIdentityField(item, string.Join("\u001f", allowedVariants));
                AppendIdentityField(item, string.Join("\u001f", skippedVariants));
                AppendIdentityField(item, attributes);
                AppendIdentityField(item, ingredientRecipeAttributes);
                AppendIdentityField(item, ingredient.Id ?? string.Empty);
                AppendIdentityField(item, ingredient.Consume ? "1" : "0");
                AppendIdentityField(item, ingredient.IsTool ? "1" : "0");
                AppendIdentityField(item, ingredient.Break ? "1" : "0");
                AppendIdentityField(item, ingredient.ToolDurabilityCost.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AppendIdentityField(item, ingredient.DurabilityChange.ToString(System.Globalization.CultureInfo.InvariantCulture));
                cells.Add(item.ToString());
            }
            if (!hasIngredient) return string.Empty;

            // Shapeless recipes are a multiset; their source order is not meaningful.
            if (recipe.Shapeless) cells.Sort(StringComparer.Ordinal);
            StringBuilder signature = new StringBuilder(cells.Count * 64);
            AppendIdentityField(signature, recipe.Shapeless ? "shapeless" : "shaped");
            AppendIdentityField(signature, cells.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < cells.Count; i++) AppendIdentityField(signature, cells[i]);
            return HashText(signature.ToString());
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
                IWorldAccessor world = api?.World;
                InventoryBase inventory = player?.InventoryManager?.GetOwnInventory("craftinggrid") as InventoryBase;
                if (api == null || player == null || world == null || inventory == null || recipe == null || !recipe.Enabled)
                    return false;
                if (!CanIdentifyRecipeSelection(recipe)) return false;

                if (!TryReadGrid(inventory, out ItemSlot[] slots, out _, out int filled, out int gridWidth, out string gridKey))
                {
                    if (QueuePendingClientSelection(recipe, inventory, world))
                        HandbookOptMod.SendRecipeVariantSelection(recipe);
                    return false;
                }

                GridRecipeIndex clientIndex = HandbookOptMod.ClientGridIndex;
                bool clientIndexUsable = clientIndex != null && clientIndex.IsReady
                    && ReferenceEquals(clientIndex.World, world) && world.GridRecipes != null
                    && world.GridRecipes.Count == clientIndex.SourceRecipeCount;
                if (!clientIndexUsable || filled == 0 || !recipe.Matches(player, world, slots, gridWidth))
                {
                    if (QueuePendingClientSelection(recipe, inventory, world))
                    {
                        // Tell the server immediately so it can retain the latest explicit
                        // choice while transfers or either side's index are still pending.
                        HandbookOptMod.SendRecipeVariantSelection(recipe);
                        ClearMatchWhileSelectionIsPending(inventory, gridKey);
                    }
                    return false;
                }

                ClearPendingClientSelection();
                if (!InvokeNativeFoundMatch(inventory, recipe))
                {
                    if (QueuePendingClientSelection(recipe, inventory, world))
                        HandbookOptMod.SendRecipeVariantSelection(recipe);
                    return false;
                }

                GridRecipeIndex index = HandbookOptMod.ClientGridIndex;
                HandbookOptMod.GridSearchState state = HandbookOptMod.GridStates.GetValue(inventory, _ => new HandbookOptMod.GridSearchState());
                state.Index = index;
                state.Previous = recipe;
                state.GridKey = gridKey;
                state.GridCompositionKey = BuildGridCompositionKey(slots);
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

        static PendingClientSelectionAttempt TryApplyPendingClientSelection(
            InventoryBase inventory, IPlayer player, IWorldAccessor world, out PendingClientSelection pending)
        {
            pending = null;
            if (!TryGetPendingClientSelection(inventory, world, out PendingClientSelection current))
                return PendingClientSelectionAttempt.None;
            pending = current;
            GridRecipe recipe = current.Recipe;
            if (recipe == null || !recipe.Enabled || !CanIdentifyRecipeSelection(recipe))
            {
                RemovePendingClientSelection(current);
                pending = null;
                return PendingClientSelectionAttempt.Discarded;
            }
            GridRecipeIndex index = HandbookOptMod.ClientGridIndex;
            if (index == null || !index.IsReady || !ReferenceEquals(index.World, world)
                || world.GridRecipes == null || world.GridRecipes.Count != index.SourceRecipeCount)
                return PendingClientSelectionAttempt.Waiting;
            if (player == null || !TryReadGrid(inventory, out ItemSlot[] slots, out _, out int filled, out int gridWidth, out string gridKey)
                || filled == 0)
                return PendingClientSelectionAttempt.Waiting;

            bool matches;
            try { matches = recipe.Matches(player, world, slots, gridWidth); }
            catch { matches = false; }
            if (!matches) return PendingClientSelectionAttempt.Waiting;
            if (!InvokeNativeFoundMatch(inventory, recipe)) return PendingClientSelectionAttempt.Waiting;

            HandbookOptMod.GridSearchState state = HandbookOptMod.GridStates.GetValue(inventory, _ => new HandbookOptMod.GridSearchState());
            state.Index = index;
            state.Previous = recipe;
            state.GridKey = gridKey;
            state.GridCompositionKey = BuildGridCompositionKey(slots);
            state.ExplicitServerSelection = false;
            RemovePendingClientSelection(current);
            HandbookOptMod.SendRecipeVariantSelection(recipe);
            return PendingClientSelectionAttempt.Applied;
        }

        public static bool ApplyServerRecipeSelection(IPlayer player, string recipeAssetKey, string recipeKey, string fallbackRecipeKey, bool queueOnFailure = false)
        {
            try
            {
                ICoreServerAPI api = HandbookOptMod.ServerApi;
                IWorldAccessor world = api?.World;
                InventoryBase inventory = player?.InventoryManager?.GetOwnInventory("craftinggrid") as InventoryBase;
                if (api == null || world?.GridRecipes == null || player == null || inventory == null) return false;
                if (string.IsNullOrWhiteSpace(recipeAssetKey) && string.IsNullOrWhiteSpace(recipeKey)
                    && string.IsNullOrWhiteSpace(fallbackRecipeKey)) return false;
                GridRecipeIndex index = HandbookOptMod.ServerGridIndex;
                bool indexUsable = index != null && index.IsReady && ReferenceEquals(index.World, world)
                    && world.GridRecipes.Count == index.SourceRecipeCount;
                if (!indexUsable)
                {
                    // Queue only the latest client request and wait for the published index.
                    // In particular, do not scan every live GridRecipe per incoming packet.
                    if (queueOnFailure) QueuePendingServerSelection(player, recipeAssetKey, recipeKey, fallbackRecipeKey);
                    return false;
                }

                if (!TryReadGrid(inventory, out ItemSlot[] slots, out HashSet<int> filledIds, out int filled, out int gridWidth, out string gridKey))
                {
                    if (queueOnFailure) QueuePendingServerSelection(player, recipeAssetKey, recipeKey, fallbackRecipeKey);
                    return false;
                }
                if (filled == 0)
                {
                    if (queueOnFailure) QueuePendingServerSelection(player, recipeAssetKey, recipeKey, fallbackRecipeKey);
                    return false;
                }

                GridRecipe selected = null;
                bool assetIdentityKnown = false;
                bool primaryIdentityKnown = false;
                bool usedAssetIdentity = false;
                bool usedFallbackIdentity = false;

                // First resolve by the full domain-qualified recipe asset name. This is the
                // stable selector: it doesn't depend on serializing complex Tags conditions
                // into byte-identical JSON on client and server. Always validate against the
                // server's real grid and reject ambiguous same-name candidates.
                if (!string.IsNullOrWhiteSpace(recipeAssetKey))
                {
                    List<GridRecipe> assetCandidates = null;
                    GridRecipe singleAsset = null;
                    if (indexUsable)
                    {
                        if (index.DuplicateRecipesByAssetName.TryGetValue(recipeAssetKey, out List<GridRecipe> duplicateAssets))
                        {
                            assetIdentityKnown = true;
                            assetCandidates = duplicateAssets;
                        }
                        else if (index.RecipeByAssetName.TryGetValue(recipeAssetKey, out GridRecipe assetRecipe))
                        {
                            assetIdentityKnown = true;
                            singleAsset = assetRecipe;
                        }
                    }
                    else
                    {
                        assetCandidates = new List<GridRecipe>();
                        for (int i = 0; i < world.GridRecipes.Count; i++)
                        {
                            GridRecipe candidate = world.GridRecipes[i];
                            if (candidate != null && string.Equals(candidate.Name?.ToString(), recipeAssetKey, StringComparison.OrdinalIgnoreCase))
                                assetCandidates.Add(candidate);
                        }
                        assetIdentityKnown = assetCandidates.Count > 0;
                    }

                    if (assetIdentityKnown)
                    {
                        int matchCount = 0;
                        IEnumerable<GridRecipe> candidates;
                        if (assetCandidates != null) candidates = assetCandidates;
                        else if (singleAsset != null) candidates = new[] { singleAsset };
                        else candidates = Array.Empty<GridRecipe>();
                        foreach (GridRecipe candidate in candidates)
                        {
                            if (candidate == null || !candidate.Enabled) continue;
                            if (!HasAllExactIngredientsPresent(candidate, filledIds, gridWidth)) continue;
                            if (!candidate.Matches(player, world, slots, gridWidth)) continue;
                            selected = candidate;
                            matchCount++;
                            if (matchCount > 1) { selected = null; break; }
                        }
                        if (selected != null) usedAssetIdentity = true;
                        else
                        {
                            // The recipe name exists on the server but the grid isn't valid
                            // for it yet (or the name is ambiguous). Do not choose a different
                            // recipe just because it happens to match the intermediate grid.
                            if (queueOnFailure) QueuePendingServerSelection(player, recipeAssetKey, recipeKey, fallbackRecipeKey);
                            return false;
                        }
                    }
                }

                // If the server doesn't have that asset name (e.g. differing mod sets), try
                // the full ingredient/output identity. It must match exactly and uniquely.
                if (selected == null && !assetIdentityKnown && !string.IsNullOrWhiteSpace(recipeKey))
                {
                    List<GridRecipe> identityCandidates = null;
                    GridRecipe singleIdentity = null;
                    if (indexUsable)
                    {
                        if (index.DuplicateRecipesByIdentity.TryGetValue(recipeKey, out List<GridRecipe> duplicateIdentity))
                            identityCandidates = duplicateIdentity;
                        else if (index.RecipeByIdentity.TryGetValue(recipeKey, out GridRecipe identityRecipe))
                            singleIdentity = identityRecipe;
                    }
                    else
                    {
                        identityCandidates = new List<GridRecipe>();
                        for (int i = 0; i < world.GridRecipes.Count; i++)
                        {
                            GridRecipe candidate = world.GridRecipes[i];
                            if (candidate != null && string.Equals(RecipeIdentity(candidate), recipeKey, StringComparison.Ordinal))
                                identityCandidates.Add(candidate);
                        }
                    }

                    int matchCount = 0;
                    IEnumerable<GridRecipe> candidates;
                    if (identityCandidates != null) candidates = identityCandidates;
                    else if (singleIdentity != null) candidates = new[] { singleIdentity };
                    else candidates = Array.Empty<GridRecipe>();
                    foreach (GridRecipe candidate in candidates)
                    {
                        if (candidate == null || !candidate.Enabled) continue;
                        primaryIdentityKnown = true;
                        if (!HasAllExactIngredientsPresent(candidate, filledIds, gridWidth)) continue;
                        if (!candidate.Matches(player, world, slots, gridWidth)) continue;
                        selected = candidate;
                        matchCount++;
                        if (matchCount > 1) { selected = null; break; }
                    }
                    if (selected == null && primaryIdentityKnown)
                    {
                        if (queueOnFailure) QueuePendingServerSelection(player, recipeAssetKey, recipeKey, fallbackRecipeKey);
                        return false;
                    }
                }

                // Last resort only when the full asset name and fingerprint are absent on the
                // server. The fallback omits Tags but keeps ordinary ingredient details, output,
                // and recipe properties; exactly one actual server-grid match is mandatory.
                if (selected == null && !assetIdentityKnown && !primaryIdentityKnown
                    && !string.IsNullOrWhiteSpace(fallbackRecipeKey))
                {
                    List<GridRecipe> fallbackCandidates = null;
                    GridRecipe singleFallback = null;
                    if (indexUsable)
                    {
                        if (index.DuplicateRecipesByFallbackIdentity.TryGetValue(fallbackRecipeKey, out List<GridRecipe> duplicates))
                            fallbackCandidates = duplicates;
                        else if (index.RecipeByFallbackIdentity.TryGetValue(fallbackRecipeKey, out GridRecipe fallback))
                            singleFallback = fallback;
                    }
                    else
                    {
                        fallbackCandidates = new List<GridRecipe>();
                        for (int i = 0; i < world.GridRecipes.Count; i++)
                        {
                            GridRecipe candidate = world.GridRecipes[i];
                            if (candidate != null && string.Equals(RecipeFallbackIdentity(candidate), fallbackRecipeKey, StringComparison.Ordinal))
                                fallbackCandidates.Add(candidate);
                        }
                    }

                    int matchCount = 0;
                    IEnumerable<GridRecipe> candidates;
                    if (fallbackCandidates != null) candidates = fallbackCandidates;
                    else if (singleFallback != null) candidates = new[] { singleFallback };
                    else candidates = Array.Empty<GridRecipe>();
                    foreach (GridRecipe candidate in candidates)
                    {
                        if (candidate == null || !candidate.Enabled) continue;
                        if (!HasAllExactIngredientsPresent(candidate, filledIds, gridWidth)) continue;
                        if (!candidate.Matches(player, world, slots, gridWidth)) continue;
                        selected = candidate;
                        matchCount++;
                        if (matchCount > 1) { selected = null; break; }
                    }
                    if (selected != null) usedFallbackIdentity = true;
                    else
                    {
                        if (queueOnFailure) QueuePendingServerSelection(player, recipeAssetKey, recipeKey, fallbackRecipeKey);
                        return false;
                    }
                }

                if (selected == null)
                {
                    if (queueOnFailure) QueuePendingServerSelection(player, recipeAssetKey, recipeKey, fallbackRecipeKey);
                    return false;
                }

                object inventoryKey = inventory;
                HandbookOptMod.GridSearchState state = HandbookOptMod.GridStates.GetValue(inventoryKey, _ => new HandbookOptMod.GridSearchState());
                state.Index = index;
                state.Previous = selected;
                state.GridKey = gridKey;
                state.GridCompositionKey = BuildGridCompositionKey(slots);
                state.ExplicitServerSelection = true;
                // This is a validated request from the owner of the inventory. The result
                // is recalculated through the native method, never copied from the client.
                if (!InvokeNativeFoundMatch(inventory, selected))
                {
                    state.Previous = null;
                    state.ExplicitServerSelection = false;
                    if (queueOnFailure) QueuePendingServerSelection(player, recipeAssetKey, recipeKey, fallbackRecipeKey);
                    return false;
                }
                try
                {
                    if (usedAssetIdentity)
                        api.Logger.Debug("[HandbookOpt] Selección aplicada por nombre completo de receta {0} para {1}.", selected.Name, player.PlayerName);
                    else if (usedFallbackIdentity)
                        api.Logger.Debug("[HandbookOpt] Selección aplicada mediante identidad alternativa única para {0} ({1}).", player.PlayerName, selected.Name);
                    else
                        api.Logger.Debug("[HandbookOpt] Aceptada selección de receta alternativa {0} para {1}.", recipeKey, player.PlayerName);
                }
                catch { }
                RemovePendingServerSelection(player, recipeAssetKey, recipeKey, fallbackRecipeKey);
                return true;
            }
            catch (Exception e)
            {
                if (Interlocked.Exchange(ref serverSelectionExceptionLogged, 1) == 0)
                {
                    try { HandbookOptMod.ServerApi?.Logger?.Error("[HandbookOpt] Falló la validación de una selección explícita de receta. {0}", e.ToString()); } catch { }
                }
                return false;
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
            // Resolve the local crafting grid even while its index is still building. In that
            // state the prefix must use vanilla matching unless a pending explicit choice blocks it;
            // it must not accidentally fall through and treat a client inventory as server state.
            if (local != null && client?.World != null && PlayerOwnsInventory(local, inventory))
            {
                player = local;
                world = client.World;
                GridRecipeIndex clientIndex = HandbookOptMod.ClientGridIndex;
                index = clientIndex != null && ReferenceEquals(clientIndex.World, client.World) ? clientIndex : null;
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

        static void ClearNativeMatchStateIfNeeded(object inventory, ItemSlot outputSlot)
        {
            if (inventory == null) return;
            bool hasMatchingRecipe = false;
            try
            {
                PropertyInfo property = AccessTools.Property(inventory.GetType(), "MatchingRecipe");
                hasMatchingRecipe = property != null && property.GetValue(inventory) != null;
            }
            catch { hasMatchingRecipe = true; }
            if (hasMatchingRecipe || outputSlot?.Itemstack != null)
                ClearNativeMatchState(inventory, outputSlot);
        }

        static void ClearNativeMatchState(object inventory, ItemSlot outputSlot)
        {
            if (inventory == null) return;
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
    /// Sends the selected recipe before the native output click is processed. This reduces
    /// the chance of the server crafting a previous selection if the player clicks quickly.
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


    /// <summary>
    /// Captures a player inventory stack only when the crafting selector has armed
    /// the one-shot "recipes using this ingredient" action. All ordinary inventory
    /// clicks continue through Vintage Story's native handler.
    /// </summary>
    [HarmonyPatch]
    public static class IngredientRecipeSearchSlotClickPatch
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(GuiElementItemSlotGridBase), nameof(GuiElementItemSlotGridBase.OnMouseDownOnElement),
                new[] { typeof(ICoreClientAPI), typeof(MouseEvent) });
        }

        static bool Prepare() => TargetMethod() != null;

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(GuiElementItemSlotGridBase __instance, ICoreClientAPI api, MouseEvent args)
        {
            try
            {
                CraftPickerDialog picker = HandbookOptMod.Picker;
                if (picker != null && picker.TryCaptureIngredientFromSlot(__instance, args))
                    return false;
            }
            catch (Exception e)
            {
                try { HandbookOptMod.Capi?.Logger.Debug("[HandbookOpt] Ingredient search click capture skipped: {0}", e.Message); }
                catch { }
            }
            return true;
        }
    }
}
