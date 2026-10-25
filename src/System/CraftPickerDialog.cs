using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace HandbookOpt
{
    public class CompactRecipe
    {
        public GridRecipe Recipe;
        public int[] Ids;
        public int[] Qtys;
        public int[] Slots;
        public CraftingRecipeIngredient[] Ings;
        public bool[] Wild;
        public ItemStack Output;
        public string Name;
        public string Search;
        // Dominio del asset de la receta (normalmente coincide con el mod que la aporta).
        public string SourceDomain;
        // RecipeBase.RequiresTrait identifies recipes restricted by class/traits.
        public string RequiresTrait;
    }

    public class CraftCatalog
    {
        public static List<CompactRecipe> Recipes = new List<CompactRecipe>();
        public static Dictionary<int, List<int>> ByExactId = new Dictionary<int, List<int>>();
        public static List<int> WildcardOnly = new List<int>();

        static List<GridRecipe> all;
        static int cursor;
        static Dictionary<int, List<GridRecipe>> byIdBuild;
        static Dictionary<GridRecipe, int> recipeOrderBuild;
        static List<GridRecipe> indexRecipeOrderBuild;
        static Dictionary<GridRecipe, HashSet<int>> exactIdsByRecipeBuild;
        static Dictionary<int, int> exactIdFrequencyBuild;
        static List<GridRecipe> wildBuild;
        static int enabled;
        static long listener;
        static ICoreClientAPI capi;

        public static void Begin(ICoreClientAPI api, IWorldAccessor world)
        {
            Stop(api);
            HandbookOptMod.IndexReady = false;
            HandbookOptMod.ClientGridIndex = null;
            HandbookOptMod.IndexCursor = 0;
            Recipes = new List<CompactRecipe>();
            ByExactId = new Dictionary<int, List<int>>();
            WildcardOnly = new List<int>();
            byIdBuild = new Dictionary<int, List<GridRecipe>>(4096);
            recipeOrderBuild = new Dictionary<GridRecipe, int>();
            indexRecipeOrderBuild = new List<GridRecipe>(world?.GridRecipes?.Count ?? 0);
            exactIdsByRecipeBuild = new Dictionary<GridRecipe, HashSet<int>>();
            exactIdFrequencyBuild = new Dictionary<int, int>(4096);
            wildBuild = new List<GridRecipe>();
            enabled = 0;
            cursor = 0;
            all = world?.GridRecipes;
            HandbookOptMod.IndexTotal = all?.Count ?? 0;
            capi = api;
            if (all == null || all.Count == 0)
            {
                Publish();
                return;
            }
            listener = api.Event.RegisterGameTickListener(Step, 40);
        }

        public static void Stop(ICoreClientAPI api)
        {
            if (listener != 0 && api != null)
            {
                api.Event.UnregisterGameTickListener(listener);
                listener = 0;
            }
            all = null;
        }

        static void Step(float dt)
        {
            if (all == null)
            {
                Stop(capi);
                return;
            }

            if (all.Count != HandbookOptMod.IndexTotal)
            {
                capi?.Logger.Warning("[HandbookOpt] Cambió la lista de recetas durante el índice cliente; se reinicia la construcción para no usar un catálogo parcial.");
                Begin(capi, capi?.World);
                return;
            }

            // Process up to 700 recipes per client tick without a time-budget early exit.
            // This keeps indexing concentrated instead of spreading it across many frames.
            const int budget = 700;
            int end = Math.Min(all.Count, cursor + budget);
            for (int i = cursor; i < end; i++)
            {
                GridRecipe recipe = all[i];
                bool uniqueRecipe = recipe != null && !recipeOrderBuild.ContainsKey(recipe);
                if (uniqueRecipe)
                {
                    // One entry per recipe according to native equality/order; retain the first occurrence.
                    recipeOrderBuild[recipe] = i;
                    indexRecipeOrderBuild.Add(recipe);
                    IndexGridRecipe(recipe);
                }

                Accept(recipe);
            }
            cursor = end;
            HandbookOptMod.IndexCursor = cursor;

            if (cursor < all.Count) return;

            Publish();
            Stop(capi);
        }

        static void IndexGridRecipe(GridRecipe recipe)
        {
            var resolved = recipe?.ResolvedIngredients;
            if (resolved == null || resolved.Length == 0)
            {
                wildBuild.Add(recipe);
                return;
            }

            var ids = new HashSet<int>();
            bool canUseExactAnchor = true;
            for (int i = 0; i < resolved.Length; i++)
            {
                CraftingRecipeIngredient ingredient = resolved[i] as CraftingRecipeIngredient;
                if (ingredient == null) continue;
                if (ingredient.MatchingType != EnumRecipeMatchType.Exact) continue;

                // Los subtipos de mods pueden personalizar la coincidencia. Si no es el
                // comparador exacto estándar, se conserva la receta en el bucket amplio.
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
                ids.Add(Pack(collectible));
            }

            if (!canUseExactAnchor || ids.Count == 0)
            {
                wildBuild.Add(recipe);
                return;
            }

            // Guardamos una sola vez los IDs exactos requeridos. Al publicar el índice
            // asignaremos cada receta a su ID más raro, no a todos los IDs. Si una receta
            // puede coincidir, ese ID también tiene que estar presente en la cuadrícula.
            exactIdsByRecipeBuild[recipe] = ids;
            foreach (int id in ids)
            {
                exactIdFrequencyBuild.TryGetValue(id, out int count);
                exactIdFrequencyBuild[id] = count + 1;
            }
        }

        static void Accept(GridRecipe recipe)
        {
            if (recipe == null || !recipe.Enabled) return;
            enabled++;

            var resolved = recipe.ResolvedIngredients;
            if (resolved == null || resolved.Length == 0) return;

            int gridW = 3;
            int recipeW = recipe.Width > 0 ? recipe.Width : (recipe.Shapeless ? 1 : 3);
            int recipeH = recipe.Height > 0 ? recipe.Height : (recipe.Shapeless ? resolved.Length : 3);

            var ids = new List<int>(resolved.Length);
            var qtys = new List<int>(resolved.Length);
            var slots = new List<int>(resolved.Length);
            var ings = new List<CraftingRecipeIngredient>(resolved.Length);
            var wilds = new List<bool>(resolved.Length);
            int seq = 0;

            for (int j = 0; j < resolved.Length; j++)
            {
                var ing = resolved[j] as CraftingRecipeIngredient;
                if (ing == null) continue;

                int qty = ing.Quantity > 0 ? ing.Quantity : 1;
                bool exact = ing.MatchingType == EnumRecipeMatchType.Exact && ing.ResolvedItemStack?.Collectible != null;
                int id = exact ? Pack(ing.ResolvedItemStack.Collectible) : 0;

                int slot;
                if (recipe.Shapeless)
                {
                    slot = seq++;
                }
                else
                {
                    int x = j % recipeW;
                    int y = j / recipeW;
                    slot = y * gridW + x;
                }
                if (slot < 0 || slot > 15) continue;

                ids.Add(id);
                qtys.Add(qty);
                slots.Add(slot);
                ings.Add(ing);
                wilds.Add(!exact);
            }

            ItemStack output = null;
            try { output = recipe.Output?.ResolvedItemStack?.Clone(); }
            catch { /* sin salida resuelta */ }
            if (output == null) return;

            string name;
            try { name = output.GetName(); }
            catch { name = output.Collectible?.Code?.ToShortString() ?? "?"; }

            var compact = new CompactRecipe
            {
                Recipe = recipe,
                Ids = ids.ToArray(),
                Qtys = qtys.ToArray(),
                Slots = slots.ToArray(),
                Ings = ings.ToArray(),
                Wild = wilds.ToArray(),
                Output = output,
                Name = name,
                Search = (name + " " + (output.Collectible?.Code?.ToShortString() ?? string.Empty) + " " + (recipe.Name?.ToString() ?? string.Empty)).ToLowerInvariant(),
                SourceDomain = recipe.Name?.Domain?.ToLowerInvariant() ?? string.Empty,
                RequiresTrait = recipe.RequiresTrait
            };
            int index = Recipes.Count;
            Recipes.Add(compact);

            bool any = false;
            for (int k = 0; k < compact.Wild.Length; k++)
            {
                if (compact.Wild[k]) continue;
                any = true;
                if (!ByExactId.TryGetValue(compact.Ids[k], out var li))
                {
                    li = new List<int>(4);
                    ByExactId[compact.Ids[k]] = li;
                }
                li.Add(index);
            }
            if (!any) WildcardOnly.Add(index);
        }

        public static int Pack(CollectibleObject c)
        {
            if (c == null) return 0;
            int cls = c is Block ? 1 : 2;
            return (cls << 28) | (c.Id & 0x0FFFFFFF);
        }

        static void Publish()
        {
            // Índice primario: cada receta se guarda en el bucket del ingrediente exacto
            // que aparece en menos recetas. Esto evita que materiales comunes como tablones
            // hagan candidatos a todas las recetas que también exigen otro material ausente.
            var primaryById = new Dictionary<int, List<GridRecipe>>(4096);
            if (indexRecipeOrderBuild != null && exactIdsByRecipeBuild != null)
            {
                for (int i = 0; i < indexRecipeOrderBuild.Count; i++)
                {
                    GridRecipe recipe = indexRecipeOrderBuild[i];
                    if (!exactIdsByRecipeBuild.TryGetValue(recipe, out HashSet<int> ids) || ids.Count == 0) continue;
                    int primaryId = 0;
                    int lowestFrequency = int.MaxValue;
                    foreach (int id in ids)
                    {
                        int frequency = exactIdFrequencyBuild.TryGetValue(id, out int count) ? count : int.MaxValue;
                        if (frequency < lowestFrequency)
                        {
                            lowestFrequency = frequency;
                            primaryId = id;
                        }
                    }
                    if (!primaryById.TryGetValue(primaryId, out var list))
                    {
                        list = new List<GridRecipe>(4);
                        primaryById[primaryId] = list;
                    }
                    list.Add(recipe);
                }
            }

            byIdBuild = primaryById;
            HandbookOptMod.RecipesByItemId = byIdBuild ?? new Dictionary<int, List<GridRecipe>>();
            HandbookOptMod.RecipeOrder = recipeOrderBuild ?? new Dictionary<GridRecipe, int>();
            HandbookOptMod.WildcardRecipes = wildBuild ?? new List<GridRecipe>();
            HandbookOptMod.IndexedRecipeCount = enabled;
            HandbookOptMod.ClientGridIndex = new GridRecipeIndex(
                capi?.World,
                HandbookOptMod.IndexTotal,
                HandbookOptMod.RecipesByItemId,
                HandbookOptMod.RecipeOrder,
                HandbookOptMod.WildcardRecipes);
            HandbookOptMod.IndexReady = HandbookOptMod.ClientGridIndex.IsReady;
        }
    }

    public class CraftPickerDialog : GuiDialog
    {
        sealed class ModFilterGroup
        {
            public string Key;
            public string Label;
            public HashSet<string> Domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public int RecipeCount;
        }

        sealed class SourceGroup
        {
            public ItemStack Representative;
            public int Available;
            public int MaxStackSize;
        }

        // A single visible recipe tile can represent several recipes only when their
        // ingredient rules and layout are demonstrably identical but their outputs differ.
        // The active recipe index is also the recipe OnPick() will load, so the badge and
        // the selected craft can never drift apart.
        sealed class RecipeVariantDisplayGroup
        {
            public string InputKey;
            public readonly List<int> RecipeIndexes = new List<int>(2);
            public readonly Dictionary<int, int> TimesByRecipe = new Dictionary<int, int>();
            public int ActiveVariantIndex;
            public long NextSlideAt;
            public long RemainingWhenPaused;
            public bool IsPaused;
            public DummySlot DisplaySlot;
            public SkillItem VisibleSkill;
        }

        // GuiElementSkillItemGrid's Action<int> does not include mouse modifiers.
        // Vanilla bulk-crafting uses Shift+left-click on the output slot, so this
        // custom recipe grid mirrors that same modifier for its "load max batches" action.
        // Capture Shift in the actual mouse-down event before the base grid invokes OnSlotClick.
        sealed class ModifierAwareSkillItemGrid : GuiElementSkillItemGrid
        {
            readonly Action<bool> captureShift;
            const int OpenTkShiftModifierMask = 1; // GLFW/OpenTK KeyModifiers.Shift

            public ModifierAwareSkillItemGrid(
                ICoreClientAPI capi,
                List<SkillItem> items,
                int columns,
                int rows,
                Action<int> onSlotClick,
                ElementBounds bounds,
                Action<bool> captureShift)
                : base(capi, items, columns, rows, onSlotClick, bounds)
            {
                this.captureShift = captureShift;
            }

            // Devuelve true cuando otra ventana tapa el puntero: no debe marcarse ninguna casilla.
            public Func<bool> HoverSuppressed;

            static readonly FieldInfo SkillItemsField = HarmonyLib.AccessTools.Field(typeof(GuiElementSkillItemGrid), "skillItems");
            static readonly FieldInfo ColsField = HarmonyLib.AccessTools.Field(typeof(GuiElementSkillItemGrid), "cols");
            static readonly FieldInfo RowsField = HarmonyLib.AccessTools.Field(typeof(GuiElementSkillItemGrid), "rows");
            static readonly FieldInfo HoverTextureField = HarmonyLib.AccessTools.Field(typeof(GuiElementSkillItemGrid), "hoverTexture");

            // Igual que el original, salvo que la casilla "bajo el ratón" no se marca (ni se avisa
            // a OnSlotOver) mientras se arrastra la ventana o si otra ventana tapa el puntero, y
            // que se compara el ratón con la posición realmente dibujada (renderX/renderY).
            public override void RenderInteractiveElements(float deltaTime)
            {
                List<SkillItem> items = SkillItemsField?.GetValue(this) as List<SkillItem>;
                LoadedTexture hoverTexture = HoverTextureField?.GetValue(this) as LoadedTexture;
                if (items == null || hoverTexture == null || ColsField == null || RowsField == null)
                {
                    base.RenderInteractiveElements(deltaTime);
                    return;
                }

                int cols = (int)ColsField.GetValue(this);
                int rows = (int)RowsField.GetValue(this);
                if (cols <= 0) return;

                double slotPadding = scaled(GuiElementItemSlotGrid.unscaledSlotPadding);
                double slotWidth = scaled(GuiElementPassiveItemSlot.unscaledSlotSize);
                double slotHeight = scaled(GuiElementPassiveItemSlot.unscaledSlotSize);

                bool allowHover = !HandbookOptDialogDragCapture.IsTitleBarCaptured;
                if (allowHover)
                {
                    try { if (HoverSuppressed != null && HoverSuppressed()) allowHover = false; }
                    catch { }
                }

                int dx = api.Input.MouseX - (int)Bounds.renderX;
                int dy = api.Input.MouseY - (int)Bounds.renderY;
                double scale = RuntimeEnv.GUIScale;

                for (int i = 0; i < rows * cols; i++)
                {
                    int row = i / cols;
                    int col = i % cols;

                    double posX = col * (slotWidth + slotPadding);
                    double posY = row * (slotHeight + slotPadding);

                    bool over = allowHover
                        && dx >= posX && dy >= posY
                        && dx < posX + slotWidth + slotPadding && dy < posY + slotHeight + slotPadding;

                    if (over || i == selectedIndex)
                    {
                        api.Render.Render2DTexture(hoverTexture.TextureId, (float)(Bounds.renderX + posX), (float)(Bounds.renderY + posY), (float)slotWidth, (float)slotHeight);
                        if (over) OnSlotOver?.Invoke(i);
                    }

                    if (items.Count <= i) continue;
                    SkillItem skillItem = items[i];
                    if (skillItem == null) continue;

                    ElementBounds sb = ElementBounds.Fixed((Bounds.renderX + posX + 1) / scale, (Bounds.renderY + posY + 1) / scale,
                        GuiElementPassiveItemSlot.unscaledSlotSize - 2, GuiElementPassiveItemSlot.unscaledSlotSize - 2).WithParent(api.Gui.WindowBounds);
                    sb.CalcWorldBounds();

                    api.Render.PushScissor(sb, true);

                    if (skillItem.Texture != null)
                    {
                        if (skillItem.TexturePremultipliedAlpha)
                            api.Render.Render2DTexturePremultipliedAlpha(skillItem.Texture.TextureId, Bounds.renderX + posX + 1, Bounds.renderY + posY + 1, slotWidth, slotHeight);
                        else
                            api.Render.Render2DTexture(skillItem.Texture.TextureId, (float)(Bounds.renderX + posX + 1), (float)(Bounds.renderY + posY + 1), (float)slotWidth, (float)slotHeight);
                    }

                    skillItem.RenderHandler?.Invoke(skillItem.Code, deltaTime, Bounds.renderX + posX + 1, Bounds.renderY + posY + 1);

                    api.Render.PopScissor();
                }
            }

            public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
            {
                bool isLeftClick = args != null && args.Button == EnumMouseButton.Left;
                bool shiftPressed = false;
                if (isLeftClick)
                {
                    // MouseEvent.Modifiers uses the GLFW/OpenTK modifier mask; Shift is bit 1.
                    shiftPressed = (args.Modifiers & OpenTkShiftModifierMask) != 0;

                    // Raw left/right Shift state is a fallback for API/input paths that do
                    // not preserve the modifier bit on the specific mouse event.
                    if (!shiftPressed)
                    {
                        try
                        {
                            bool[] keys = api?.Input?.KeyboardKeyStateRaw;
                            int leftShift = (int)GlKeys.LShift;
                            int rightShift = (int)GlKeys.RShift;
                            shiftPressed = keys != null &&
                                ((leftShift >= 0 && leftShift < keys.Length && keys[leftShift]) ||
                                 (rightShift >= 0 && rightShift < keys.Length && keys[rightShift]));
                        }
                        catch { }
                    }
                }

                captureShift?.Invoke(shiftPressed);
                // The base method invokes the slot callback synchronously, so OnPick
                // reads the captured modifier rather than a later keyboard snapshot.
                base.OnMouseDownOnElement(api, args);
            }
        }

        // Tooltip de lista para las celdas de resultado del selector.
        // Los ingredientes wildcard buscan ejemplos reales en el catálogo de coleccionables;
        // estos ejemplos rotan cada segundo sin volver a recorrer el catálogo en cada fotograma.
        sealed class RecipeIngredientTooltip : GuiElement
        {
            const int HoverDelayMs = 115;
            const int VariantSlideIntervalMs = 1000;
            const int MaxVisibleIngredientRows = 9;
            const int MaxSampleVariants = 16;
            // Profundidad del fondo del tooltip (sin cambios respecto a la versión anterior).
            const float TooltipBackgroundZ = 1000f;
            const int MaxSampleCacheEntries = 128;
            // Evita recorrer decenas de miles de objetos en el hilo de render si una receta
            // solo define etiquetas y no tiene code. Los casos con code usan SearchItems/Blocks.
            const int MaxTagCollectiblesToInspect = 12000;

            sealed class TooltipRow
            {
                public CraftingRecipeIngredient Ingredient;
                public List<ItemStack> Samples;
                public ItemSlot Slot;
                public string MainText;
                public string SecondaryText;
                public string QuantityText;
                public string CodeText;
                public int Quantity;
                public int SampleIndex;
                public int OwnedCount;
                public int MissingCount;
                public bool ShowAvailability;
                public bool IsPattern;
                public bool ReusableTool;
                public bool HasIcon;
            }

            sealed class IngredientDisplayEntry
            {
                public CraftingRecipeIngredient Ingredient;
                public int TotalQuantity;
            }

            readonly ICoreClientAPI client;
            LoadedTexture texture;
            readonly List<TooltipRow> rows = new List<TooltipRow>(MaxVisibleIngredientRows);
            readonly Dictionary<string, List<ItemStack>> sampleCache = new Dictionary<string, List<ItemStack>>(StringComparer.Ordinal);
            readonly Queue<string> sampleCacheOrder = new Queue<string>();
            IWorldAccessor sampleCacheWorld;
            CompactRecipe recipe;
            int recipeIndex = -1;
            bool craftable;
            bool visible;
            bool composed;
            bool hasVariantSlides;
            Dictionary<int, int> ownedCounts = new Dictionary<int, int>();
            int possibleBatches;
            long hoverStartedAtMs;
            int currentSlideStep = -1;
            int textureWidth;
            int textureHeight;
            int rowTop;
            int rowHeight;
            int iconSize;
            int padding;
            int detailTextX;

            public RecipeIngredientTooltip(ICoreClientAPI capi)
                : base(capi, ElementBounds.Fixed(0, 0, 1, 1))
            {
                client = capi;
                texture = new LoadedTexture(capi);
            }

            public void SetRecipe(CompactRecipe nextRecipe, int nextRecipeIndex, bool canCraft,
                Dictionary<int, int> availableItems, int batchesAvailable)
            {
                bool countsUnchanged = DictionariesEqual(ownedCounts, availableItems);
                if (visible && ReferenceEquals(recipe, nextRecipe) && recipeIndex == nextRecipeIndex
                    && craftable == canCraft && possibleBatches == batchesAvailable && countsUnchanged)
                    return;

                recipe = nextRecipe;
                recipeIndex = nextRecipeIndex;
                craftable = canCraft;
                possibleBatches = Math.Max(0, batchesAvailable);
                ownedCounts = availableItems != null
                    ? new Dictionary<int, int>(availableItems)
                    : new Dictionary<int, int>();
                visible = nextRecipe != null;
                composed = false;
                hasVariantSlides = false;
                currentSlideStep = -1;
                rows.Clear();
                hoverStartedAtMs = Environment.TickCount64;
            }

            static bool DictionariesEqual(Dictionary<int, int> left, Dictionary<int, int> right)
            {
                if (ReferenceEquals(left, right)) return true;
                if (left == null || right == null || left.Count != right.Count) return false;
                foreach (var entry in left)
                {
                    if (!right.TryGetValue(entry.Key, out int value) || value != entry.Value) return false;
                }
                return true;
            }

            public void Clear()
            {
                if (!visible && recipe == null) return;
                visible = false;
                recipe = null;
                recipeIndex = -1;
                composed = false;
                hasVariantSlides = false;
                currentSlideStep = -1;
                rows.Clear();
            }

            public void ClearSampleCache()
            {
                sampleCache.Clear();
                sampleCacheOrder.Clear();
                sampleCacheWorld = null;
            }

            public void RenderPopup()
            {
                if (!visible || recipe == null || client == null) return;

                long elapsed = Environment.TickCount64 - hoverStartedAtMs;
                if (!composed)
                {
                    if (elapsed < HoverDelayMs) return;
                    try
                    {
                        BuildRows();
                        currentSlideStep = 0;
                        UpdateRowsForSlide(currentSlideStep);
                        ComposeTooltipTexture();
                        composed = true;
                    }
                    catch
                    {
                        // Un fallo visual nunca debe interrumpir el selector de recetas.
                        rows.Clear();
                        composed = true;
                        return;
                    }
                }
                else if (hasVariantSlides)
                {
                    int requestedStep = (int)Math.Max(0, (elapsed - HoverDelayMs) / VariantSlideIntervalMs);
                    if (requestedStep != currentSlideStep)
                    {
                        try
                        {
                            currentSlideStep = requestedStep;
                            UpdateRowsForSlide(currentSlideStep);
                            ComposeTooltipTexture();
                        }
                        catch
                        {
                            // Conserva la última composición válida ante una variante anómala de un mod.
                        }
                    }
                }

                if (texture == null || texture.TextureId <= 0 || textureWidth <= 0 || textureHeight <= 0) return;

                int mouseX = client.Input?.MouseX ?? 0;
                int mouseY = client.Input?.MouseY ?? 0;
                int offset = (int)GuiElement.scaled(16);
                int screenWidth = Math.Max(textureWidth, client.Render?.FrameWidth ?? textureWidth);
                int screenHeight = Math.Max(textureHeight, client.Render?.FrameHeight ?? textureHeight);
                int x = mouseX + offset;
                int y = mouseY + offset;
                if (x + textureWidth > screenWidth - 4) x = mouseX - textureWidth - offset;
                if (y + textureHeight > screenHeight - 4) y = mouseY - textureHeight - offset;
                x = Math.Max(2, Math.Min(x, screenWidth - textureWidth - 2));
                y = Math.Max(2, Math.Min(y, screenHeight - textureHeight - 2));

                client.Render.Render2DTexturePremultipliedAlpha(texture.TextureId, x, y, textureWidth, textureHeight, TooltipBackgroundZ);

                // RenderItemstackToGui dibuja un modelo 3D escalado a "size" en todos los ejes,
                // no un sprite plano. Su profundidad se extiende unos +/-size/2 alrededor de posZ.
                // Con el icono a solo 1 unidad del fondo (z 1001 vs 1000), parte del modelo queda
                // DETRÁS del fondo y falla la prueba de profundidad. En objetos finos con una
                // transformación GUI que gira o invierte el eje Z, las caras texturizadas son las
                // ocultas y solo se ven las traseras sin textura: una silueta negra.
                // El tooltip vanilla (GuiElementItemstackInfo) evita esto situando el icono
                // 1000 + 2*tamaño por encima: todo el volumen queda delante del fondo.
                // Solo se SUBE el icono; el fondo conserva 1000, así que ninguna otra parte
                // de la interfaz cambia de orden.
                double iconZ = TooltipBackgroundZ + Math.Max(2.0, iconSize * 2.0);

                // Los iconos se dibujan a partir de muestras ya resueltas/cachéadas.
                // Solo se crean DummySlot al cambiar de diapositiva, nunca por fotograma.
                for (int i = 0; i < rows.Count; i++)
                {
                    TooltipRow row = rows[i];
                    if (!row.HasIcon || row.Slot == null) continue;
                    double iconX = x + padding + (iconSize * 0.08);
                    double iconY = y + rowTop + i * rowHeight + (rowHeight - iconSize) / 2.0;
                    try
                    {
                        client.Render.RenderItemstackToGui(row.Slot, iconX + iconSize * 0.5, iconY + iconSize * 0.5,
                            iconZ, iconSize, ColorUtil.WhiteArgb, true, false, false);
                    }
                    catch { /* una pila sin modelo válido no debe ocultar el resto del tooltip */ }
                }
            }

            void BuildRows()
            {
                rows.Clear();
                hasVariantSlides = false;

                var ingredients = recipe?.Ings ?? Array.Empty<CraftingRecipeIngredient>();
                var groupedIngredients = new List<IngredientDisplayEntry>(ingredients.Length);
                var groupedIndices = new Dictionary<string, int>(StringComparer.Ordinal);

                // Agrupamos primero toda la receta, antes de aplicar el límite visual de filas.
                // Así, por ejemplo, siete palos en ranuras separadas pasan a una fila ×7.
                // La clave solo agrupa condiciones de ingrediente demostrablemente equivalentes;
                // condiciones basadas en tags, subclases o atributos/retornos especiales permanecen aparte.
                for (int i = 0; i < ingredients.Length; i++)
                {
                    CraftingRecipeIngredient ingredient = ingredients[i];
                    if (ingredient == null) continue;

                    int quantity = ingredient.Quantity > 0 ? ingredient.Quantity : 1;
                    string groupKey = IngredientDisplayGroupingKey(ingredient);
                    if (groupKey != null && groupedIndices.TryGetValue(groupKey, out int existingIndex))
                    {
                        IngredientDisplayEntry existing = groupedIngredients[existingIndex];
                        existing.TotalQuantity = existing.TotalQuantity > int.MaxValue - quantity
                            ? int.MaxValue
                            : existing.TotalQuantity + quantity;
                        continue;
                    }

                    if (groupKey != null) groupedIndices[groupKey] = groupedIngredients.Count;
                    groupedIngredients.Add(new IngredientDisplayEntry
                    {
                        Ingredient = ingredient,
                        TotalQuantity = quantity
                    });
                }

                bool showExactAvailability = CanShowExactAvailability(groupedIngredients);
                int groupedCount = groupedIngredients.Count;
                int normalRows = groupedCount > MaxVisibleIngredientRows
                    ? MaxVisibleIngredientRows - 1
                    : groupedCount;

                for (int i = 0; i < normalRows; i++)
                {
                    IngredientDisplayEntry entry = groupedIngredients[i];
                    CraftingRecipeIngredient ingredient = entry.Ingredient;
                    bool isPattern = ingredient.MatchingType != EnumRecipeMatchType.Exact;
                    try
                    {
                        // En 1.22 algunas recetas se definen solo con tags y no tienen Code.
                        if (ingredient.Code == null || !ingredient.Tags.IsEmpty) isPattern = true;
                    }
                    catch { }

                    var samples = ResolveSampleStacks(ingredient);
                    if (samples.Count > 1) hasVariantSlides = true;

                    int ownedCount = 0;
                    bool showAvailability = showExactAvailability;
                    if (showAvailability)
                    {
                        try
                        {
                            int packedId = CraftCatalog.Pack(ingredient.ResolvedItemStack.Collectible);
                            ownedCounts.TryGetValue(packedId, out ownedCount);
                        }
                        catch { showAvailability = false; }
                    }

                    rows.Add(new TooltipRow
                    {
                        Ingredient = ingredient,
                        Samples = samples,
                        CodeText = SafeCodeText(ingredient),
                        Quantity = entry.TotalQuantity,
                        OwnedCount = ownedCount,
                        MissingCount = showAvailability ? Math.Max(0, entry.TotalQuantity - ownedCount) : 0,
                        ShowAvailability = showAvailability,
                        IsPattern = isPattern,
                        ReusableTool = !ingredient.Consume,
                        MainText = string.Empty,
                        SecondaryText = string.Empty,
                        QuantityText = string.Empty,
                        HasIcon = false
                    });
                }

                if (groupedCount > MaxVisibleIngredientRows)
                {
                    int remaining = groupedCount - normalRows;
                    rows.Add(new TooltipRow
                    {
                        MainText = HandbookOptLang.Get("hover-tooltip-more-ingredients", remaining),
                        SecondaryText = string.Empty,
                        QuantityText = string.Empty,
                        HasIcon = false
                    });
                }
            }

            bool CanShowExactAvailability(List<IngredientDisplayEntry> groupedIngredients)
            {
                // Per-ingredient deficits are shown only when the recipe is simple enough
                // to allocate counts without overlap. Recipes with wildcard/tag rules still
                // get the authoritative batch estimate in the footer.
                try
                {
                    if (recipe?.Ings == null || recipe.Wild == null || recipe.Wild.Length != recipe.Ings.Length) return false;
                    for (int i = 0; i < recipe.Ings.Length; i++)
                    {
                        CraftingRecipeIngredient ingredient = recipe.Ings[i];
                        if (ingredient == null || ingredient.GetType() != typeof(CraftingRecipeIngredient)
                            || ingredient.MatchingType != EnumRecipeMatchType.Exact || recipe.Wild[i]) return false;
                        if (!ingredient.Tags.IsEmpty || ingredient.Attributes != null || ingredient.ReturnedStack != null) return false;
                    }

                    var seenIds = new HashSet<int>();
                    for (int i = 0; i < groupedIngredients.Count; i++)
                    {
                        var ingredient = groupedIngredients[i]?.Ingredient;
                        var collectible = ingredient?.ResolvedItemStack?.Collectible;
                        if (collectible == null) return false;
                        int id = CraftCatalog.Pack(collectible);
                        if (!seenIds.Add(id)) return false;
                    }
                    return groupedIngredients.Count > 0;
                }
                catch { return false; }
            }

            static string IngredientDisplayGroupingKey(CraftingRecipeIngredient ingredient)
            {
                if (ingredient == null || ingredient.GetType() != typeof(CraftingRecipeIngredient)) return null;

                try
                {
                    // Tags pueden contener condiciones complejas cuyo ToString() no es una
                    // representación fiable; no agrupamos esas recetas por una clave aproximada.
                    if (!ingredient.Tags.IsEmpty) return null;
                    if (ingredient.Attributes != null || ingredient.ReturnedStack != null) return null;
                }
                catch { return null; }

                string code = SafeCodeText(ingredient);
                if (string.IsNullOrWhiteSpace(code))
                {
                    try { code = ingredient.ResolvedItemStack?.Collectible?.Code?.ToString() ?? string.Empty; }
                    catch { return null; }
                }
                if (string.IsNullOrWhiteSpace(code)) return null;

                string allowed = string.Join(",", ingredient.AllowedVariants ?? Array.Empty<string>());
                string skipped = string.Join(",", ingredient.SkipVariants ?? Array.Empty<string>());
                return ((int)ingredient.Type).ToString(CultureInfo.InvariantCulture) + "|" +
                    ((int)ingredient.MatchingType).ToString(CultureInfo.InvariantCulture) + "|" + code + "|" +
                    (ingredient.Name ?? string.Empty) + "|" + allowed + "|" + skipped + "|" +
                    (ingredient.Consume ? "1" : "0") + "|" +
                    (ingredient.Break ? "1" : "0") + "|" +
                    ingredient.DurabilityChange.ToString(CultureInfo.InvariantCulture) + "|" +
                    ingredient.ToolDurabilityCost.ToString(CultureInfo.InvariantCulture);
            }

            void UpdateRowsForSlide(int slideStep)
            {
                hasVariantSlides = false;
                for (int i = 0; i < rows.Count; i++)
                {
                    TooltipRow row = rows[i];
                    if (row.Ingredient == null) continue;
                    List<ItemStack> samples = row.Samples;
                    int selected = samples != null && samples.Count > 1 ? slideStep % samples.Count : 0;
                    row.SampleIndex = selected;
                    ItemStack sample = samples != null && samples.Count > 0 ? samples[selected] : null;
                    if (samples != null && samples.Count > 1) hasVariantSlides = true;

                    row.Slot = null;
                    row.HasIcon = false;
                    if (sample?.Collectible != null)
                    {
                        try
                        {
                            row.Slot = new DummySlot(sample.Clone());
                            row.HasIcon = row.Slot != null;
                        }
                        catch { row.Slot = null; }
                    }

                    string label = SafeStackName(sample);
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        label = SafeStackName(row.Ingredient.ResolvedItemStack);
                    }

                    bool rawCodeName = IsRawCollectibleCode(label, sample) ||
                        IsRawCollectibleCode(label, row.Ingredient.ResolvedItemStack);
                    var notes = new List<string>(3);
                    if (rawCodeName)
                    {
                        string shownCode = SafeStackCode(sample);
                        if (string.IsNullOrWhiteSpace(shownCode)) shownCode = SafeCodeText(row.Ingredient);
                        label = HandbookOptLang.Get(row.IsPattern
                            ? "hover-tooltip-compatible-ingredient"
                            : "hover-tooltip-unlocalized-item");
                        if (!string.IsNullOrWhiteSpace(shownCode))
                        {
                            notes.Add(HandbookOptLang.Get("hover-tooltip-pattern-code", FitText(shownCode, 38)));
                        }
                    }
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        label = HandbookOptLang.Get(row.IsPattern
                            ? "hover-tooltip-compatible-ingredient"
                            : "hover-tooltip-unknown-ingredient");
                    }

                    row.MainText = FitText(label, 46);
                    row.QuantityText = "× " + row.Quantity.ToString(CultureInfo.InvariantCulture);

                    if (row.ShowAvailability)
                    {
                        notes.Add(HandbookOptLang.Get("hover-tooltip-availability", row.OwnedCount, row.MissingCount));
                    }
                    if (row.ReusableTool)
                    {
                        notes.Add(HandbookOptLang.Get("hover-tooltip-reusable-tool"));
                    }
                    if (samples != null && samples.Count > 1)
                    {
                        notes.Add(HandbookOptLang.Get("hover-tooltip-variant-slide", selected + 1, samples.Count));
                    }
                    else if (row.IsPattern && samples != null && samples.Count == 1)
                    {
                        notes.Add(HandbookOptLang.Get("hover-tooltip-compatible-example"));
                    }
                    else if (samples == null || samples.Count == 0)
                    {
                        notes.Add(HandbookOptLang.Get("hover-tooltip-sample-missing"));
                        if (!string.IsNullOrWhiteSpace(row.CodeText))
                        {
                            notes.Add(HandbookOptLang.Get("hover-tooltip-pattern-code", FitText(row.CodeText, 38)));
                        }
                    }
                    row.SecondaryText = FitText(string.Join(" · ", notes), 86);
                }
            }

            List<ItemStack> ResolveSampleStacks(CraftingRecipeIngredient ingredient)
            {
                var result = new List<ItemStack>(4);
                if (ingredient == null || client?.World == null) return result;
                EnsureSampleCacheWorld();

                string key = IngredientCacheKey(ingredient);
                if (sampleCache.TryGetValue(key, out var cached)) return cached;

                var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ItemStack resolved = null;
                try { resolved = ingredient.ResolvedItemStack?.Clone(); } catch { }
                if (resolved?.Collectible != null)
                {
                    // El stack resuelto es una muestra preferente para los ingredientes exactos.
                    // En un wildcard se combina con los ejemplos del catálogo.
                    if (ingredient.MatchingType == EnumRecipeMatchType.Exact)
                    {
                        TryAddCandidate(result, seenCodes, ingredient, resolved, false);
                    }
                    else
                    {
                        TryAddCandidate(result, seenCodes, ingredient, resolved, true);
                    }
                }

                AssetLocation code = null;
                try { code = ingredient.Code; } catch { }
                if (code != null && (ingredient.MatchingType != EnumRecipeMatchType.Exact || result.Count == 0))
                {
                    try
                    {
                        if (ingredient.Type == EnumItemClass.Block)
                        {
                            Block[] found = client.World.SearchBlocks(code);
                            if (found != null)
                            {
                                for (int i = 0; i < found.Length && result.Count < MaxSampleVariants; i++)
                                {
                                    Block block = found[i];
                                    if (block == null) continue;
                                    TryAddCandidate(result, seenCodes, ingredient, new ItemStack(block), false);
                                }
                            }
                        }
                        else
                        {
                            Item[] found = client.World.SearchItems(code);
                            if (found != null)
                            {
                                for (int i = 0; i < found.Length && result.Count < MaxSampleVariants; i++)
                                {
                                    Item item = found[i];
                                    if (item == null) continue;
                                    TryAddCandidate(result, seenCodes, ingredient, new ItemStack(item), false);
                                }
                            }
                        }
                    }
                    catch { /* una receta externa puede tener un patrón no compatible con SearchItems/Blocks */ }
                }

                // Respaldo acotado para recetas basadas en tags sin code y para ciertos
                // patrones de mods que SearchItems/Blocks no haya podido resolver.
                // Solo se ejecuta si aún no encontramos una muestra visual.
                bool hasTagCondition = false;
                try { hasTagCondition = !ingredient.Tags.IsEmpty; } catch { }
                if ((code == null || result.Count == 0) &&
                    (ingredient.MatchingType != EnumRecipeMatchType.Exact || hasTagCondition) &&
                    result.Count < MaxSampleVariants)
                {
                    try
                    {
                        var collectibles = client.World.Collectibles;
                        if (collectibles != null)
                        {
                            for (int i = 0; i < collectibles.Count && i < MaxTagCollectiblesToInspect && result.Count < MaxSampleVariants; i++)
                            {
                                CollectibleObject collectible = collectibles[i];
                                if (collectible == null) continue;
                                if (ingredient.Type == EnumItemClass.Block && !(collectible is Block)) continue;
                                if (ingredient.Type == EnumItemClass.Item && !(collectible is Item)) continue;
                                TryAddCandidate(result, seenCodes, ingredient, new ItemStack(collectible), false);
                            }
                        }
                    }
                    catch { /* si el ingrediente usa un comparador propio, se mantiene el texto explicativo */ }
                }

                // Si una implementación de un mod no permite comprobar el ingrediente pero sí
                // entrega una pila resuelta, conservar esa muestra es mejor que dejar solo texto.
                if (result.Count == 0 && resolved?.Collectible != null)
                {
                    try
                    {
                        resolved.StackSize = 1;
                        result.Add(resolved.Clone());
                    }
                    catch { }
                }

                result.Sort((a, b) => string.Compare(
                    a?.Collectible?.Code?.ToString() ?? string.Empty,
                    b?.Collectible?.Code?.ToString() ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase));
                StoreSampleCache(key, result);
                return result;
            }

            void TryAddCandidate(List<ItemStack> result, HashSet<string> seenCodes,
                CraftingRecipeIngredient ingredient, ItemStack candidate, bool allowResolvedFallback)
            {
                if (candidate?.Collectible == null) return;
                string code = candidate.Collectible.Code?.ToString() ?? candidate.Collectible.Code?.ToShortString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(code) || seenCodes.Contains(code)) return;

                bool matches;
                try { matches = ingredient.SatisfiesAsIngredient(candidate, false); }
                catch
                {
                    matches = allowResolvedFallback && ReferenceEquals(
                        candidate.Collectible, ingredient.ResolvedItemStack?.Collectible);
                }
                if (!matches) return;

                try
                {
                    ItemStack clone = candidate.Clone();
                    clone.StackSize = 1;
                    result.Add(clone);
                    seenCodes.Add(code);
                }
                catch { }
            }

            void EnsureSampleCacheWorld()
            {
                IWorldAccessor world = client?.World;
                if (ReferenceEquals(sampleCacheWorld, world)) return;
                sampleCacheWorld = world;
                sampleCache.Clear();
                sampleCacheOrder.Clear();
            }

            void StoreSampleCache(string key, List<ItemStack> samples)
            {
                if (!sampleCache.ContainsKey(key)) sampleCacheOrder.Enqueue(key);
                sampleCache[key] = samples;
                while (sampleCache.Count > MaxSampleCacheEntries && sampleCacheOrder.Count > 0)
                {
                    string oldest = sampleCacheOrder.Dequeue();
                    sampleCache.Remove(oldest);
                }
            }

            static string IngredientCacheKey(CraftingRecipeIngredient ingredient)
            {
                string code = SafeCodeText(ingredient);
                // La condición de tags es un struct complejo: su ToString() no representa
                // su contenido. La identidad evita reutilizar muestras de otra receta que
                // tenga el mismo code pero restricciones distintas.
                string identity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ingredient).ToString(CultureInfo.InvariantCulture);
                string allowed = string.Join(",", ingredient.AllowedVariants ?? Array.Empty<string>());
                string skipped = string.Join(",", ingredient.SkipVariants ?? Array.Empty<string>());
                string attributes = string.Empty;
                try { attributes = ingredient.Attributes?.ToString() ?? string.Empty; } catch { }
                return ((int)ingredient.Type).ToString(CultureInfo.InvariantCulture) + "|" +
                    ((int)ingredient.MatchingType).ToString(CultureInfo.InvariantCulture) + "|" + code + "|" +
                    allowed + "|" + skipped + "|" + (ingredient.Name ?? string.Empty) + "|" + attributes + "|" + identity;
            }

            static string SafeCodeText(CraftingRecipeIngredient ingredient)
            {
                try { return ingredient?.Code?.ToString() ?? string.Empty; }
                catch { return string.Empty; }
            }

            static string SafeStackName(ItemStack stack)
            {
                try { return stack?.GetName() ?? string.Empty; }
                catch { return string.Empty; }
            }

            static string SafeStackCode(ItemStack stack)
            {
                try { return stack?.Collectible?.Code?.ToString() ?? string.Empty; }
                catch { return string.Empty; }
            }

            static bool IsRawCollectibleCode(string name, ItemStack stack)
            {
                if (string.IsNullOrWhiteSpace(name) || stack?.Collectible?.Code == null) return false;
                string value = name.Trim();
                try
                {
                    AssetLocation code = stack.Collectible.Code;
                    string full = code.ToString();
                    string shortCode = code.ToShortString();
                    string itemKey = "item-" + code.Path;
                    string blockKey = "block-" + code.Path;
                    string domainItemKey = code.Domain + ":" + itemKey;
                    string domainBlockKey = code.Domain + ":" + blockKey;
                    return string.Equals(value, full, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(value, shortCode, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(value, itemKey, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(value, blockKey, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(value, domainItemKey, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(value, domainBlockKey, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }

            void ComposeTooltipTexture()
            {
                double scale = GuiElement.scaled(1);
                if (scale <= 0) scale = 1;
                padding = Math.Max(8, (int)Math.Round(10 * scale));
                iconSize = Math.Max(20, (int)Math.Round(27 * scale));
                rowHeight = Math.Max(32, (int)Math.Round(39 * scale));
                int titleFontSize = Math.Max(12, (int)Math.Round(14 * scale));
                int bodyFontSize = Math.Max(10, (int)Math.Round(12 * scale));
                int smallFontSize = Math.Max(9, (int)Math.Round(10 * scale));
                textureWidth = Math.Max(300, (int)Math.Round(390 * scale));

                detailTextX = padding + iconSize + Math.Max(5, (int)Math.Round(7 * scale));
                int titleY = padding + titleFontSize;
                int statusY = titleY + Math.Max(17, (int)Math.Round(21 * scale));
                int sectionY = statusY + Math.Max(17, (int)Math.Round(20 * scale));
                rowTop = sectionY + Math.Max(19, (int)Math.Round(24 * scale));
                int footerHeight = Math.Max(25, (int)Math.Round(29 * scale));
                textureHeight = rowTop + rows.Count * rowHeight + footerHeight + padding;
                textureHeight = Math.Max(textureHeight, rowTop + footerHeight + (int)Math.Round(8 * scale));

                using (ImageSurface surface = new ImageSurface(Format.Argb32, textureWidth, textureHeight))
                {
                    using (Context ctx = genContext(surface))
                    {
                        ctx.SetSourceRGBA(0.055, 0.06, 0.07, 0.97);
                        ctx.Rectangle(0, 0, textureWidth, textureHeight);
                        ctx.Fill();

                        ctx.SetSourceRGBA(0.27, 0.29, 0.32, 0.98);
                        ctx.LineWidth = Math.Max(1, scale);
                        ctx.Rectangle(0.5, 0.5, textureWidth - 1, textureHeight - 1);
                        ctx.Stroke();

                        ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Bold);
                        ctx.SetFontSize(titleFontSize);
                        ctx.SetSourceRGBA(1, 1, 1, 1);
                        string title = FitText(string.IsNullOrWhiteSpace(recipe.Name) ? SafeStackName(recipe.Output) : recipe.Name, 48);
                        ctx.MoveTo(padding, titleY);
                        ctx.ShowText(title);

                        ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Normal);
                        ctx.SetFontSize(smallFontSize);
                        if (craftable)
                            ctx.SetSourceRGBA(0.49, 0.91, 0.60, 1);
                        else
                            ctx.SetSourceRGBA(1.0, 0.72, 0.38, 1);
                        ctx.MoveTo(padding, statusY);
                        ctx.ShowText(craftable
                            ? HandbookOptLang.Get("hover-tooltip-craftable")
                            : HandbookOptLang.Get("hover-tooltip-missing"));

                        ctx.SetSourceRGBA(0.83, 0.85, 0.88, 1);
                        ctx.SetFontSize(bodyFontSize);
                        string section = hasVariantSlides
                            ? HandbookOptLang.Get("hover-tooltip-ingredients-slideshow")
                            : HandbookOptLang.Get("hover-tooltip-ingredients");
                        ctx.MoveTo(padding, sectionY);
                        ctx.ShowText(section);

                        for (int i = 0; i < rows.Count; i++)
                        {
                            TooltipRow row = rows[i];
                            int rowY = rowTop + i * rowHeight;
                            if ((i & 1) == 1)
                            {
                                ctx.SetSourceRGBA(1, 1, 1, 0.035);
                                ctx.Rectangle(2, rowY, textureWidth - 4, rowHeight);
                                ctx.Fill();
                            }

                            int textX = row.HasIcon ? detailTextX : padding;
                            if (!row.HasIcon && row.Ingredient != null)
                            {
                                // Un patrón de mod sin muestra recibe un marcador visual claro,
                                // en vez de una casilla vacía con únicamente texto/posición.
                                double markerSize = Math.Max(16, Math.Round(19 * scale));
                                double markerX = padding + markerSize * 0.15;
                                double markerY = rowY + (rowHeight - markerSize) / 2.0;
                                ctx.SetSourceRGBA(0.16, 0.18, 0.21, 1);
                                ctx.Rectangle(markerX, markerY, markerSize, markerSize);
                                ctx.Fill();
                                ctx.SetSourceRGBA(0.78, 0.81, 0.85, 1);
                                ctx.LineWidth = Math.Max(1, scale);
                                ctx.Rectangle(markerX + 0.5, markerY + 0.5, markerSize - 1, markerSize - 1);
                                ctx.Stroke();
                                ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Bold);
                                ctx.SetFontSize(Math.Max(11, (int)Math.Round(13 * scale)));
                                ctx.MoveTo(markerX + markerSize * 0.34, markerY + markerSize * 0.72);
                                ctx.ShowText("?");
                                ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Normal);
                                textX = padding + (int)markerSize + Math.Max(5, (int)Math.Round(7 * scale));
                            }

                            int quantityReserve = string.IsNullOrEmpty(row.QuantityText) ? 0 : (int)Math.Round(50 * scale);
                            ctx.SetSourceRGBA(0.86, 0.88, 0.91, 1);
                            ctx.SetFontSize(bodyFontSize);
                            int maxMainChars = Math.Max(8,
                                (textureWidth - textX - padding - quantityReserve) / Math.Max(6, bodyFontSize / 2));
                            ctx.MoveTo(textX, rowY + Math.Max(14, (int)Math.Round(16 * scale)));
                            ctx.ShowText(FitText(row.MainText, maxMainChars));

                            if (!string.IsNullOrWhiteSpace(row.QuantityText))
                            {
                                ctx.SetSourceRGBA(0.95, 0.95, 0.97, 1);
                                ctx.SetFontSize(bodyFontSize);
                                ctx.MoveTo(textureWidth - padding - (int)Math.Round(44 * scale), rowY + Math.Max(14, (int)Math.Round(16 * scale)));
                                ctx.ShowText(row.QuantityText);
                            }
                            if (!string.IsNullOrWhiteSpace(row.SecondaryText))
                            {
                                ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Normal);
                                ctx.SetFontSize(smallFontSize);
                                if (row.ShowAvailability && row.MissingCount > 0)
                                    ctx.SetSourceRGBA(1.0, 0.48, 0.43, 1);
                                else if (row.ShowAvailability)
                                    ctx.SetSourceRGBA(0.49, 0.91, 0.60, 1);
                                else
                                    ctx.SetSourceRGBA(0.67, 0.72, 0.78, 1);
                                ctx.MoveTo(textX, rowY + Math.Max(27, (int)Math.Round(30 * scale)));
                                ctx.ShowText(FitText(row.SecondaryText,
                                    Math.Max(12, (textureWidth - textX - padding) / Math.Max(5, smallFontSize / 2))));
                            }
                        }

                        int footerY = rowTop + rows.Count * rowHeight + Math.Max(3, (int)Math.Round(4 * scale));
                        ctx.SetSourceRGBA(0.34, 0.37, 0.41, 0.95);
                        ctx.LineWidth = Math.Max(1, scale);
                        ctx.MoveTo(padding, footerY);
                        ctx.LineTo(textureWidth - padding, footerY);
                        ctx.Stroke();
                        ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Bold);
                        ctx.SetFontSize(smallFontSize);
                        if (possibleBatches > 0) ctx.SetSourceRGBA(0.49, 0.91, 0.60, 1);
                        else ctx.SetSourceRGBA(1.0, 0.58, 0.48, 1);
                        string batchSummary = possibleBatches > 0
                            ? HandbookOptLang.Get("hover-tooltip-batches-available", possibleBatches)
                            : HandbookOptLang.Get("hover-tooltip-batches-missing");
                        ctx.MoveTo(padding, footerY + Math.Max(17, (int)Math.Round(21 * scale)));
                        ctx.ShowText(FitText(batchSummary, Math.Max(18, (textureWidth - padding * 2) / Math.Max(5, smallFontSize / 2))));
                    }

                    // Reemplaza la textura anterior solo después de dibujar la nueva, y la libera
                    // inmediatamente para que una diapositiva cada segundo no acumule texturas.
                    var replacement = new LoadedTexture(client);
                    try
                    {
                        generateTexture(surface, ref replacement, true);
                        var previous = texture;
                        texture = replacement;
                        replacement = null;
                        try { previous?.Dispose(); } catch { }
                    }
                    finally
                    {
                        try { replacement?.Dispose(); } catch { }
                    }
                }
            }

            static string FitText(string text, int maxChars)
            {
                if (string.IsNullOrEmpty(text)) return string.Empty;
                text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (text.Length <= maxChars) return text;
                if (maxChars <= 3) return text.Substring(0, Math.Max(0, maxChars));
                return text.Substring(0, maxChars - 1) + "…";
            }

            public override void Dispose()
            {
                try { texture?.Dispose(); } catch { }
                texture = null;
                rows.Clear();
                ClearSampleCache();
                recipe = null;
                base.Dispose();
            }
        }

        const int Cols = 6;
        const int Rows = 4;
        const int PageSize = Cols * Rows;

        List<SkillItem> shown = new List<SkillItem>();
        List<bool> shownCraftable = new List<bool>();
        List<int> matches = new List<int>();
        List<int> times = new List<int>();
        // Parallel to matches/times: one catalog tile can cycle between output-distinct
        // recipes with exactly the same input requirements.
        List<RecipeVariantDisplayGroup> matchVariantGroups = new List<RecipeVariantDisplayGroup>();
        List<CompactRecipe> variantKeyCacheCatalog;
        readonly Dictionary<int, string> variantInputKeyCache = new Dictionary<int, string>();
        LoadedTexture recipeVariantsBadgeTexture;
        bool recipeVariantsBadgeLoadAttempted;
        int page;
        string search = "";
        // El clic normal añade una tanda. Shift+clic calcula el máximo.
        const int RequestedTandadasPerClick = 1;
        const string FilterAvailable = "__available";
        const string FilterAllMods = "__allmods";
        const string FilterVanilla = "__vanilla";
        const string FilterRecent = "__recent";
        const string FilterUsesIngredient = "__usesingredient";
        const int MaxRecentRecipes = 12;
        const string PickerPreferencesFilename = "handbookopt-picker-preferences.json";
        readonly List<int> recentRecipeIndexes = new List<int>(MaxRecentRecipes);
        ItemStack reverseSearchIngredient;
        CraftPickerPreferences pickerPreferences;
        bool preferencesLoaded;
        bool preferencesCanWrite = true;
        bool preferencesNeedValidation;
        bool pendingIngredientPreferenceRestore;
        bool preferencesSaveWarningLogged;
        bool ingredientPickArmed;
        string selectedModFilter = FilterAvailable;
        string[] modFilterValues = new[] { FilterAvailable, FilterAllMods, FilterRecent };
        string[] modFilterNames = GetBaseModFilterNames();
        readonly Dictionary<string, HashSet<string>> modFilterDomains =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        bool modFilterOptionsBuilt;
        LoadedTexture unavailableRecipeTexture;
        bool unavailableRecipeTextureLoadAttempted;
        RecipeIngredientTooltip hoverTooltip;
        bool userHid = false; // se activa al cerrar el panel con el título o con el atajo B
        bool wasOpen;
        // El cierre del diálogo de inventario es la señal autoritativa para cerrar el selector.
        // La marca evita que el polling vuelva a abrirlo por un frame si otro GUI comparte
        // temporalmente la tecla de inventario o el estado de OpenedGuis cambia durante el cierre.
        bool inventoryCloseSuppressedUntilOpen;
        GuiDialog hookedInventoryDialog;
        long nextInventoryHookScanAt;
        // Captured from the actual skill-grid mouse event before the click callback runs.
        bool shiftPressedForCurrentGridClick;
        int lastPanelRecipeIndex = -1;
        // Posición actual en la lista filtrada; permite que H use la receta realmente
        // señalada en esta cuadrícula, sin leer el ItemSlot que haya detrás de la ventana.
        int hoveredRecipeMatchIndex = -1;
        string lastSig = "";
        string lastInventorySignature;
        long dirtyAt;
        bool dirty = true;
        long watchListener;

        public override string ToggleKeyCombinationCode => null;

        // Misma capa que el inventario del jugador, los cofres y el Handbook vanilla (0.2).
        // Con la misma capa, el gestor de GUI decide quién queda delante según el foco
        // (la ventana pulsada o abierta pasa al frente). No es una prioridad propia: es la
        // capa estándar compartida, así que ninguna ventana queda fija encima o debajo.
        public override double DrawOrder => HandbookOptDrawLayer.SharedLayer;

        // InputOrder solo afecta al orden de entrada de teclado. No se usa como sustituto
        // de DrawOrder ni del guardia que filtra los eventos de ratón de ventanas de fondo.
        public override double InputOrder => Focused ? 0.45 : 0.55;

        bool IsVanillaHandbookOpen()
        {
            try
            {
                var opened = capi?.Gui?.OpenedGuis;
                if (opened == null) return false;

                foreach (GuiDialog dialog in opened)
                {
                    if (dialog is GuiDialogHandbook && dialog.IsOpened()) return true;
                }
            }
            catch { }

            return false;
        }
        public override bool PrefersUngrabbedMouse => true;
        public override bool CaptureAllInputs() => false;

        public CraftPickerDialog(ICoreClientAPI capi) : base(capi)
        {
            hoverTooltip = new RecipeIngredientTooltip(capi);
            LoadPickerPreferences();
            ApplySavedPickerPreferences();
            watchListener = capi.Event.RegisterGameTickListener(Watch, 200);
        }

        void LoadPickerPreferences()
        {
            try
            {
                pickerPreferences = capi.LoadModConfig<CraftPickerPreferences>(PickerPreferencesFilename);
                preferencesLoaded = true;
                preferencesCanWrite = true;
                if (pickerPreferences == null)
                {
                    pickerPreferences = new CraftPickerPreferences();
                    capi.StoreModConfig(pickerPreferences, PickerPreferencesFilename);
                }
            }
            catch (Exception e)
            {
                // Never overwrite a preference file that could not be read: it may
                // contain user data that should be recoverable after correcting it.
                pickerPreferences = new CraftPickerPreferences();
                preferencesLoaded = true;
                preferencesCanWrite = false;
                capi.Logger.Warning("[HandbookOpt] No se pudieron cargar las preferencias del selector; se usarán valores predeterminados sin sobrescribir el archivo: {0}", e.GetBaseException().Message);
            }
        }

        static string NormalizePreferenceSearch(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            const int MaxSavedSearchLength = 256;
            return value.Length <= MaxSavedSearchLength ? value : value.Substring(0, MaxSavedSearchLength);
        }

        void ApplySavedPickerPreferences()
        {
            if (!preferencesLoaded || pickerPreferences == null) return;

            search = NormalizePreferenceSearch(pickerPreferences.Search);
            page = Math.Max(0, pickerPreferences.Page);
            selectedModFilter = string.IsNullOrWhiteSpace(pickerPreferences.SelectedFilter)
                ? FilterAvailable
                : pickerPreferences.SelectedFilter.Trim();
            reverseSearchIngredient = null;
            pendingIngredientPreferenceRestore = !string.IsNullOrWhiteSpace(pickerPreferences.ReverseIngredientData);
            preferencesNeedValidation = true;
            lastSig = string.Empty;
            dirty = true;
            dirtyAt = 0;
        }

        void TryRestoreIngredientPreference()
        {
            if (!pendingIngredientPreferenceRestore || capi?.World == null || pickerPreferences == null) return;

            // Attempt this only once a world resolver exists. The saved ItemStack includes
            // its attributes, so variant/tag-sensitive recipe matchers receive the same
            // selected ingredient instead of a reconstructed item with lost attributes.
            pendingIngredientPreferenceRestore = false;
            try
            {
                string encoded = pickerPreferences.ReverseIngredientData;
                if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 262144) return;
                byte[] data = Convert.FromBase64String(encoded);
                var restored = new ItemStack(data);
                if (!restored.ResolveBlockOrItem(capi.World) || restored.Collectible == null) return;

                string actualCode = restored.Collectible.Code?.ToString() ?? string.Empty;
                string expectedCode = pickerPreferences.ReverseIngredientCode ?? string.Empty;
                if (expectedCode.Length == 0 || !string.Equals(actualCode, expectedCode, StringComparison.OrdinalIgnoreCase))
                    return;

                reverseSearchIngredient = restored;
                RefreshIngredientFilterOption();
            }
            catch
            {
                // A stale item, removed mod, or invalid preference must not prevent
                // the dialog from opening; validation below falls back to a safe filter.
            }
        }

        void ValidateRestoredPickerFilter()
        {
            if (!preferencesNeedValidation || !HandbookOptMod.IndexReady) return;
            preferencesNeedValidation = false;

            if (string.Equals(selectedModFilter, FilterRecent, StringComparison.OrdinalIgnoreCase)
                && recentRecipeIndexes.Count == 0)
            {
                selectedModFilter = FilterAvailable;
                page = 0;
                SavePickerPreferences();
                return;
            }

            if (string.Equals(selectedModFilter, FilterUsesIngredient, StringComparison.OrdinalIgnoreCase)
                && reverseSearchIngredient?.Collectible == null)
            {
                selectedModFilter = FilterAvailable;
                page = 0;
                SavePickerPreferences();
                return;
            }

            bool filterExists = false;
            for (int i = 0; i < (modFilterValues?.Length ?? 0); i++)
            {
                if (string.Equals(modFilterValues[i], selectedModFilter, StringComparison.OrdinalIgnoreCase))
                {
                    filterExists = true;
                    break;
                }
            }

            if (!filterExists)
            {
                // Mod-specific filters can disappear when a mod is removed or renamed.
                // Keep the user's search useful by opening the full catalog instead.
                selectedModFilter = string.Equals(selectedModFilter, FilterAvailable, StringComparison.OrdinalIgnoreCase)
                    ? FilterAvailable
                    : FilterAllMods;
                page = 0;
                SavePickerPreferences();
            }
        }

        void SavePickerPreferences()
        {
            if (!preferencesLoaded) return;
            if (pickerPreferences == null) pickerPreferences = new CraftPickerPreferences();

            pickerPreferences.Version = 1;
            pickerPreferences.Search = NormalizePreferenceSearch(search);
            pickerPreferences.SelectedFilter = string.IsNullOrWhiteSpace(selectedModFilter)
                ? FilterAvailable
                : selectedModFilter;
            pickerPreferences.Page = Math.Max(0, page);

            if (reverseSearchIngredient?.Collectible != null)
            {
                try
                {
                    pickerPreferences.ReverseIngredientData = Convert.ToBase64String(reverseSearchIngredient.ToBytes());
                    pickerPreferences.ReverseIngredientCode = reverseSearchIngredient.Collectible.Code?.ToString() ?? string.Empty;
                }
                catch
                {
                    // Keep the last valid serialized ingredient if a custom stack fails serialization.
                }
            }
            else if (!pendingIngredientPreferenceRestore)
            {
                pickerPreferences.ReverseIngredientData = string.Empty;
                pickerPreferences.ReverseIngredientCode = string.Empty;
            }

            if (!preferencesCanWrite) return;
            try
            {
                capi.StoreModConfig(pickerPreferences, PickerPreferencesFilename);
            }
            catch (Exception e)
            {
                if (!preferencesSaveWarningLogged)
                {
                    preferencesSaveWarningLogged = true;
                    capi?.Logger?.Warning("[HandbookOpt] No se pudieron guardar las preferencias del selector: {0}", e.GetBaseException().Message);
                }
            }
        }

        // GuiElementDropDown dibuja la lista mediante un GuiElementListMenu separado.
        // Su rectángulo emergente puede quedar encima de las casillas de recetas; en ese
        // caso la cuadrícula subyacente sigue notificando hover aunque no sea visible.
        bool IsMouseOverModFilterPopup()
        {
            try
            {
                if (capi?.Input == null || SingleComposer == null) return false;
                GuiElementDropDown dropDown = SingleComposer.GetDropDown("modfilter");
                GuiElementListMenu menu = dropDown?.listMenu;
                return menu != null
                    && menu.IsOpened
                    && menu.IsPositionInside(capi.Input.MouseX, capi.Input.MouseY);
            }
            catch { return false; }
        }

        // El control y su lista emergente tienen límites de foco distintos. Al mover el
        // cursor a la lista (que puede extenderse por debajo del control), el GUI base
        // puede interpretar que se perdió el foco y cerrar el menú. Limitar la excepción
        // únicamente al desplegable de filtro de mods de este selector.
        public bool IsModFilterPopupOpen()
        {
            try
            {
                return SingleComposer?.GetDropDown("modfilter")?.listMenu?.IsOpened == true;
            }
            catch { return false; }
        }

        public bool ShouldKeepModFilterPopupOpen(GuiElementListMenu menu)
        {
            try
            {
                if (menu == null || capi?.Input == null || SingleComposer == null || !menu.IsOpened) return false;
                GuiElementDropDown dropDown = SingleComposer.GetDropDown("modfilter");
                if (dropDown == null || !ReferenceEquals(dropDown.listMenu, menu)) return false;

                int mouseX = capi.Input.MouseX;
                int mouseY = capi.Input.MouseY;
                double popupTop = dropDown.Bounds.renderY + dropDown.Bounds.InnerHeight;
                return mouseY >= popupTop && menu.IsPositionInside(mouseX, mouseY);
            }
            catch { return false; }
        }

        public bool ShouldRouteModFilterPopupWheel(GuiElementDropDown dropDown)
        {
            try
            {
                if (dropDown == null || capi?.Input == null || SingleComposer == null) return false;
                GuiElementDropDown current = SingleComposer.GetDropDown("modfilter");
                if (!ReferenceEquals(current, dropDown) || current.listMenu == null || !current.listMenu.IsOpened) return false;
                int mouseX = capi.Input.MouseX;
                int mouseY = capi.Input.MouseY;
                double popupTop = current.Bounds.renderY + current.Bounds.InnerHeight;
                return mouseY >= popupTop && current.listMenu.IsPositionInside(mouseX, mouseY);
            }
            catch { return false; }
        }

        // True si otra ventana (inventario, personaje, Handbook...) está delante del selector
        // en la posición del ratón: el hover de las recetas de atrás no debe contar.
        bool IsMouseCoveredByOtherDialog()
        {
            try
            {
                return capi?.Input != null
                    && HandbookOptModalInputGuard.IsCoveredByFrontDialog(this, capi.Input.MouseX, capi.Input.MouseY);
            }
            catch { return false; }
        }

        void ClearHoveredRecipe()
        {
            bool hadHoveredRecipe = hoveredRecipeMatchIndex >= 0;
            hoveredRecipeMatchIndex = -1;
            hoverTooltip?.Clear();
            if (hadHoveredRecipe)
            {
                try { SingleComposer?.GetDynamicText("info")?.SetNewText(GetStatusInfoText()); } catch { }
            }
        }

        public override void OnRenderGUI(float deltaTime)
        {
            base.OnRenderGUI(deltaTime);
            if (hoverTooltip == null || capi?.Input == null) return;

            try
            {
                // El popup del filtro se dibuja por encima de la cuadrícula, pero el
                // grid aún puede reportar que el mismo punto está dentro de sus bounds.
                // Priorizar el menú visible y suprimir el tooltip de la receta de atrás.
                if (IsMouseOverModFilterPopup() || IsMouseCoveredByOtherDialog())
                {
                    ClearHoveredRecipe();
                    return;
                }

                var grid = SingleComposer?.GetSkillItemGrid("recipes");
                int mx = capi.Input.MouseX;
                int my = capi.Input.MouseY;
                // La grilla incluye huecos entre iconos. OnOver limpia al salir del icono;
                // esta comprobación también limpia al salir del área completa del catálogo.
                if (grid == null || !grid.IsPositionInside(mx, my)) ClearHoveredRecipe();
                hoverTooltip.RenderPopup();
            }
            catch { /* un fallo del tooltip no debe interferir con el panel de recetas */ }
        }

        public override void OnGuiClosed()
        {
            SavePickerPreferences();
            ingredientPickArmed = false;
            hoveredRecipeMatchIndex = -1;
            hoverTooltip?.Clear();
            base.OnGuiClosed();
            HandbookOptPauseController.DialogClosed(this, capi);
        }

        void OnPauseGameToggled(bool value)
        {
            HandbookOptPauseController.SetEnabled(capi, value);
        }

        bool OnIngredientSearchButton()
        {
            ingredientPickArmed = !ingredientPickArmed;
            if (ingredientPickArmed)
            {
                ClearHoveredRecipe();
                try
                {
                    SingleComposer?.GetDynamicText("info")?.SetNewText(
                        HandbookOptLang.Get("ingredient-search-pick-prompt"));
                    capi?.Gui?.PlaySound("tick");
                }
                catch { }
            }
            else
            {
                try
                {
                    SingleComposer?.GetDynamicText("info")?.SetNewText(GetStatusInfoText());
                    capi?.Gui?.PlaySound("tick");
                }
                catch { }
            }
            return true;
        }

        /// <summary>
        /// Captures one left-click on a real player backpack/hotbar item while the
        /// ingredient-search button is armed. Returning true tells the Harmony patch
        /// to consume that click instead of moving or splitting the stack.
        /// </summary>
        public bool TryCaptureIngredientFromSlot(GuiElementItemSlotGridBase grid, MouseEvent args)
        {
            if (!ingredientPickArmed || grid == null || args == null || args.Button != EnumMouseButton.Left)
                return false;

            IPlayer player = capi?.World?.Player;
            IInventory inventory = grid.hoverInv;
            if (player == null || inventory == null) return false;

            IInventory backpack = null;
            IInventory hotbar = null;
            try
            {
                backpack = player.InventoryManager?.GetOwnInventory("backpack");
                hotbar = player.InventoryManager?.GetOwnInventory("hotbar");
            }
            catch { }

            // Only capture from the player's own material storage. Never intercept clicks
            // on chests, crafting outputs, equipment, or other mods' inventories.
            if (!ReferenceEquals(inventory, backpack) && !ReferenceEquals(inventory, hotbar)) return false;

            ItemSlot slot = null;
            try
            {
                int slotId = grid.hoverSlotId;
                if (slotId >= 0 && slotId < inventory.Count) slot = inventory[slotId];
            }
            catch { }

            ItemStack stack = slot?.Itemstack;
            if (stack?.Collectible == null || stack.StackSize <= 0) return false;

            try { reverseSearchIngredient = stack.Clone(); }
            catch { return false; }
            ingredientPickArmed = false;
            selectedModFilter = FilterUsesIngredient;
            page = 0;
            lastSig = string.Empty;
            search = string.Empty;
            try
            {
                settingSearch = true;
                SingleComposer?.GetTextInput("search")?.SetValue(string.Empty);
            }
            catch { }
            finally { settingSearch = false; }

            RefreshIngredientFilterOption();
            dirty = true;
            dirtyAt = 0;
            preferencesNeedValidation = false;
            if (HandbookOptMod.IndexReady) RebuildMatches();
            else ComposeAndOpen();
            SavePickerPreferences();

            try { capi?.Gui?.PlaySound("tick"); } catch { }
            return true;
        }

        public bool IsIngredientPickArmed => ingredientPickArmed;

        string ReverseSearchIngredientName()
        {
            if (reverseSearchIngredient == null) return string.Empty;
            try
            {
                string name = reverseSearchIngredient.GetName();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            try { return reverseSearchIngredient.Collectible?.Code?.ToShortString() ?? string.Empty; }
            catch { return string.Empty; }
        }

        public void SyncPauseSwitch()
        {
            try
            {
                if (HandbookOptPauseController.CanPause(capi))
                    SingleComposer?.GetSwitch("pauseGame")?.SetValue(HandbookOptPauseController.Enabled);
            }
            catch { }
        }

        void Watch(float dt)
        {
            if (capi?.World?.Player == null) return;

            // Mantiene sincronizado el interruptor de pausa con el del Handbook vanilla.
            HandbookOptPauseController.Poll(capi);

            EnsureInventoryLifecycleHooks();

            // El evento OnClosed del inventario es más fiable que inferir el cierre
            // solo desde OpenedGuis. Mantener esta barrera hasta el siguiente OnOpened
            // evita que la ventana de recetas reaparezca durante la transición de cierre.
            if (inventoryCloseSuppressedUntilOpen)
            {
                lastInventorySignature = null;
                wasOpen = false;
                if (IsOpened()) TryClose();
                return;
            }

            bool inv = InventoryIsOpen();
            if (inv && !wasOpen)
            {
                // Restablecemos los datos del inventario al abrirlo, pero no la
                // preferencia explícita de visibilidad: si el jugador ocultó el
                // selector, debe seguir oculto al cerrar/reabrir el inventario.
                dirty = true;
                dirtyAt = 0;
                lastSig = "";
                lastInventorySignature = null;
            }
            if (!inv)
            {
                // Cerrar el inventario oculta temporalmente el diálogo, pero no
                // cambia userHid. Así distinguimos el cierre automático de una
                // ocultación solicitada por el jugador con X o la tecla R.
                lastInventorySignature = null;
                if (IsOpened()) TryClose();
            }
            wasOpen = inv;

            if (!inv || userHid) return;

            // La vista de recetas hace rotar la insignia/miniatura; la salida nativa de la
            // cuadrícula permanece fija. El tiempo se congela únicamente sobre el mosaico.
            UpdateRecipeVariantCarousel();

            // Mantener el panel sincronizado si el jugador mueve o consume materiales
            // mientras el inventario sigue abierto.
            string inventorySignature = BuildInventorySignature();
            if (lastInventorySignature == null)
            {
                lastInventorySignature = inventorySignature;
            }
            else if (!string.Equals(lastInventorySignature, inventorySignature, StringComparison.Ordinal))
            {
                lastInventorySignature = inventorySignature;
                dirty = true;
                dirtyAt = capi.ElapsedMilliseconds;
            }
            if (!HandbookOptMod.IndexReady)
            {
                if (!IsOpened()) ComposeAndOpen();
                else RefreshStatusOnly();
                return;
            }

            if (dirty && capi.ElapsedMilliseconds - dirtyAt > 180)
            {
                // No reconstruir el compositor mientras el filtro está desplegado: una
                // reconstrucción destruiría su lista emergente a mitad del desplazamiento.
                // El cambio pendiente se aplica al cerrar el popup.
                if (!IsModFilterPopupOpen())
                {
                    dirty = false;
                    RebuildMatches();
                }
            }
            else if (!IsOpened())
            {
                ComposeAndOpen();
            }
        }

        public void MarkDirty()
        {
            dirty = true;
            dirtyAt = capi?.ElapsedMilliseconds ?? 0;
        }

        // Al abrir el panel con la hotkey, Vintage Story puede propagar la misma
        // pulsación al TextInput que acaba de crearse. Si llega el carácter "r"
        // en ese mismo intervalo, OnSearch lo retira una sola vez.
        long suppressHotkeyRUntilMs;
        string suppressHotkeyRExpectedText = string.Empty;

        /// <summary>
        /// Alterna la visibilidad del panel con el atajo registrado por el mod.
        /// Solo actúa cuando el inventario está abierto para no consumir la tecla R
        /// en el mundo o en otros menús. Si el campo de búsqueda tiene el foco,
        /// deja pasar la tecla para que el usuario pueda escribir normalmente.
        /// </summary>
        public bool TogglePanelVisibility()
        {
            if (capi?.World?.Player == null || !InventoryIsOpen()) return false;

            // No interceptar la R mientras el usuario está escribiendo una búsqueda.
            try
            {
                if (IsOpened() && SingleComposer?.GetTextInput("search")?.HasFocus == true)
                    return false;
            }
            catch { /* la interfaz puede no tener todavía el campo compuesto */ }

            if (IsOpened())
            {
                userHid = true;
                bool closed = TryClose();
                if (closed) capi.Gui.PlaySound("tick");
                return closed;
            }

            userHid = false;
            dirty = true;
            dirtyAt = capi.ElapsedMilliseconds;
            lastSig = string.Empty;

            // Capturamos el texto existente para ignorar únicamente el carácter R
            // residual de la misma pulsación que abre el diálogo, sin impedir que el
            // jugador escriba R normalmente después de que el diálogo esté abierto.
            suppressHotkeyRExpectedText = search ?? string.Empty;
            suppressHotkeyRUntilMs = (capi?.ElapsedMilliseconds ?? 0) + 200;

            ComposeAndOpen();
            bool opened = IsOpened();
            if (opened) capi.Gui.PlaySound("tick");
            return opened;
        }

        static string[] GetBaseModFilterNames()
        {
            return new[]
            {
                HandbookOptLang.Get("filter-available"),
                HandbookOptLang.Get("filter-all-mods"),
                HandbookOptLang.Get("filter-recent")
            };
        }

        public void ResetSelection()
        {
            lastPanelRecipeIndex = -1;
            lastSig = "";
            recentRecipeIndexes.Clear();
            reverseSearchIngredient = null;
            ingredientPickArmed = false;
        }

        public void ClearTooltipSampleCache()
        {
            hoverTooltip?.ClearSampleCache();
        }

        public void BrowseOutputCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            selectedModFilter = FilterAllMods;
            search = code.Trim().ToLowerInvariant();
            page = 0;
            lastSig = string.Empty;
            dirty = true;
            dirtyAt = 0;
            preferencesNeedValidation = false;
            SavePickerPreferences();
            if (HandbookOptMod.IndexReady) RebuildMatches();
            else ComposeAndOpen();
        }

        public void ResetModFilterCache()
        {
            modFilterOptionsBuilt = false;
            modFilterDomains.Clear();
            modFilterValues = new[] { FilterAvailable, FilterAllMods, FilterRecent };
            modFilterNames = GetBaseModFilterNames();
            selectedModFilter = FilterAvailable;
            reverseSearchIngredient = null;
            pendingIngredientPreferenceRestore = false;
            ingredientPickArmed = false;
            page = 0;
            lastSig = "";
            // LeaveWorld clears world-specific history and item references, but the user's
            // local picker preferences should be available again when another world loads.
            ApplySavedPickerPreferences();
        }

        string BuildInventorySignature()
        {
            var sb = new StringBuilder(512);
            AppendInventorySignature(sb, GetInv("backpack"));
            AppendInventorySignature(sb, GetInv("hotbar"));
            var craft = GetInv("craftinggrid");
            if (craft != null)
            {
                int inputs = Math.Max(0, craft.Count - 1);
                for (int i = 0; i < inputs; i++) AppendSlotSignature(sb, craft[i]);
            }
            return sb.ToString();
        }

        static void AppendInventorySignature(StringBuilder sb, IInventory inventory)
        {
            if (inventory == null) { sb.Append("<null>"); return; }
            for (int i = 0; i < inventory.Count; i++) AppendSlotSignature(sb, inventory[i]);
            sb.Append('|');
        }

        static void AppendSlotSignature(StringBuilder sb, ItemSlot slot)
        {
            var stack = slot?.Itemstack;
            if (stack?.Collectible == null || stack.StackSize <= 0)
            {
                sb.Append("0;");
                return;
            }
            sb.Append(CraftCatalog.Pack(stack.Collectible))
              .Append('x')
              .Append(stack.StackSize)
              .Append(':')
              .Append(stack.GetHashCode())
              .Append(';');
        }

        bool IsCreativeInventoryOpen()
        {
            // En algunas interfaces el juego reutiliza el mismo diálogo de inventario;
            // el modo de juego es una segunda salvaguarda para no mostrar el selector en Creative.
            try
            {
                if (capi?.World?.Player?.WorldData?.CurrentGameMode == EnumGameMode.Creative) return true;
            }
            catch { /* continuar comprobando el diálogo abierto */ }

            var guis = capi?.Gui?.OpenedGuis;
            if (guis == null) return false;
            for (int i = 0; i < guis.Count; i++)
            {
                var dlg = guis[i];
                if (dlg == null || ReferenceEquals(dlg, this)) continue;
                string key = dlg.ToggleKeyCombinationCode;
                if (string.Equals(key, "creative", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(dlg.GetType().Name, "GuiDialogCreative", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        void EnsureInventoryLifecycleHooks()
        {
            if (capi?.Gui == null) return;

            long now = capi.ElapsedMilliseconds;
            if (now < nextInventoryHookScanAt) return;
            nextInventoryHookScanAt = now + 2000;

            GuiDialog candidate = null;
            var loaded = capi.Gui.LoadedGuis;
            if (loaded != null)
            {
                for (int i = 0; i < loaded.Count; i++)
                {
                    GuiDialog dlg = loaded[i];
                    if (dlg == null) continue;
                    if (string.Equals(dlg.GetType().Name, "GuiDialogInventory", StringComparison.Ordinal))
                    {
                        candidate = dlg;
                        break;
                    }
                }
            }

            if (ReferenceEquals(candidate, hookedInventoryDialog)) return;

            // Retirar los eventos de la instancia anterior si el juego o un mod
            // reemplaza el diálogo de inventario durante la sesión.
            if (hookedInventoryDialog != null)
            {
                try { hookedInventoryDialog.OnClosed -= OnInventoryDialogClosed; } catch { }
                try { hookedInventoryDialog.OnOpened -= OnInventoryDialogOpened; } catch { }
            }

            hookedInventoryDialog = candidate;
            if (hookedInventoryDialog != null)
            {
                try
                {
                    hookedInventoryDialog.OnClosed += OnInventoryDialogClosed;
                    hookedInventoryDialog.OnOpened += OnInventoryDialogOpened;
                }
                catch (Exception e)
                {
                    capi?.Logger?.Warning("[HandbookOpt] No se pudieron enlazar los eventos del inventario; se mantiene la comprobación periódica: {0}", e.Message);
                    hookedInventoryDialog = null;
                }
            }
        }

        void OnInventoryDialogClosed()
        {
            inventoryCloseSuppressedUntilOpen = true;
            wasOpen = false;
            lastInventorySignature = null;

            try
            {
                hoverTooltip?.Clear();
                if (IsOpened()) TryClose();
            }
            catch (Exception e)
            {
                capi?.Logger?.Debug("[HandbookOpt] El selector se cerrará desde la comprobación periódica tras cerrar el inventario: {0}", e.Message);
            }
        }

        void OnInventoryDialogOpened()
        {
            inventoryCloseSuppressedUntilOpen = false;
            wasOpen = false;
            dirty = true;
            dirtyAt = 0;
            lastSig = string.Empty;
            lastInventorySignature = null;
            // No tocamos userHid: si el jugador ocultó expresamente el panel,
            // abrir de nuevo el inventario no debe cambiar esa preferencia.
        }

        void DetachInventoryLifecycleHooks()
        {
            if (hookedInventoryDialog == null) return;
            try { hookedInventoryDialog.OnClosed -= OnInventoryDialogClosed; } catch { }
            try { hookedInventoryDialog.OnOpened -= OnInventoryDialogOpened; } catch { }
            hookedInventoryDialog = null;
        }

        bool InventoryIsOpen()
        {
            // No abrir el selector de recetas en Creative, incluso si la implementación
            // del juego deja un diálogo de inventario normal abierto en paralelo.
            if (IsCreativeInventoryOpen()) return false;
            var guis = capi?.Gui?.OpenedGuis;
            if (guis == null) return false;
            for (int i = 0; i < guis.Count; i++)
            {
                var dlg = guis[i];
                if (dlg == null || ReferenceEquals(dlg, this)) continue;
                string key = dlg.ToggleKeyCombinationCode;
                if (string.Equals(key, "inventory", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(dlg.GetType().Name, "GuiDialogInventory", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        void RefreshStatusOnly()
        {
            if (SingleComposer == null) return;
            int pct = HandbookOptMod.IndexTotal <= 0
                ? 0
                : (int)(100.0 * HandbookOptMod.IndexCursor / HandbookOptMod.IndexTotal);
            try
            {
                SingleComposer.GetDynamicText("info")?.SetNewText(HandbookOptLang.Get("indexing-recipes-percent", pct));
            }
            catch { /* compositor viejo */ }
        }

        static string NormalizeFilterToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static bool TryResolveModLabel(string normalizedDomain, Dictionary<string, string> normalizedModNames, out string label)
        {
            label = null;
            if (string.IsNullOrWhiteSpace(normalizedDomain) || normalizedModNames == null) return false;

            // Coincidencia exacta tiene prioridad.
            if (normalizedModNames.TryGetValue(normalizedDomain, out label)) return true;

            // Un mod puede declarar dominios con un sufijo respecto a su ModID
            // (p. ej. <modid>assets), o usar como dominio una palabra distintiva del
            // nombre visible (p. ej. "armory" dentro de "Ancient Armory").
            // Seleccionar la coincidencia más específica y rechazar empates ambiguos.
            int bestScore = 0;
            string bestLabel = null;
            bool ambiguous = false;
            foreach (var alias in normalizedModNames)
            {
                string token = alias.Key;
                if (token == null || token.Length < 6) continue;

                int score = 0;
                if (normalizedDomain.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    score = token.Length;
                }
                else if (normalizedDomain.Length >= 6 && token.Length > normalizedDomain.Length
                    && token.EndsWith(normalizedDomain, StringComparison.OrdinalIgnoreCase))
                {
                    score = normalizedDomain.Length;
                }
                if (score == 0) continue;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestLabel = alias.Value;
                    ambiguous = false;
                }
                else if (score == bestScore && bestLabel != null
                    && !string.Equals(NormalizeFilterToken(bestLabel), NormalizeFilterToken(alias.Value), StringComparison.OrdinalIgnoreCase))
                {
                    ambiguous = true;
                }
            }

            if (bestLabel == null || ambiguous) return false;
            label = bestLabel;
            return true;
        }

        static string CompactModFilterName(string name)
        {
            // GuiElementListMenu determina el ancho del desplegable por la fila más
            // larga. Algunos mods declaran nombres muy largos; limitar solo el texto
            // visual mantiene el menú compacto sin alterar la clave ni el filtro real.
            const int MaxVisibleNameChars = 24;
            if (string.IsNullOrWhiteSpace(name) || name.Length <= MaxVisibleNameChars) return name ?? string.Empty;
            return name.Substring(0, MaxVisibleNameChars - 1).TrimEnd() + "…";
        }

        void EnsureModFilterOptions()
        {
            // La lista final se deriva EXCLUSIVAMENTE de los dominios presentes en
            // recetas de cuadrícula indexadas. No se agregan mods solo por estar cargados.
            if (!HandbookOptMod.IndexReady)
            {
                if (modFilterValues == null || modFilterValues.Length == 0)
                {
                    modFilterValues = new[] { FilterAvailable, FilterAllMods, FilterRecent };
                    modFilterNames = GetBaseModFilterNames();
                }
                RefreshIngredientFilterOption();
                return;
            }
            if (modFilterOptionsBuilt)
            {
                RefreshIngredientFilterOption();
                return;
            }

            var loadedModNamesByNormalizedId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var mod in capi.ModLoader.Mods)
                {
                    var info = mod?.Info;
                    string id = info?.ModID?.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(id)) continue;
                    string normalizedId = NormalizeFilterToken(id);
                    if (normalizedId.Length == 0) continue;
                    string name = string.IsNullOrWhiteSpace(info.Name) ? id : info.Name.Trim();
                    if (id == "game" || id == "survival" || id == "creative") name = HandbookOptLang.Get("vintage-story-base");
                    if (!loadedModNamesByNormalizedId.ContainsKey(normalizedId))
                        loadedModNamesByNormalizedId[normalizedId] = name;

                    // El ID y el nombre visible pueden diferir. Registrar ambas formas
                    // ayuda a asociar dominios de assets al mismo mod.
                    string normalizedName = NormalizeFilterToken(name);
                    if (normalizedName.Length > 0 && !loadedModNamesByNormalizedId.ContainsKey(normalizedName))
                        loadedModNamesByNormalizedId[normalizedName] = name;
                }
            }
            catch
            {
                // Si ModInfo no está disponible, el catálogo utiliza el dominio de receta.
            }

            var recipeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < CraftCatalog.Recipes.Count; i++)
            {
                string domain = CraftCatalog.Recipes[i].SourceDomain?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(domain)) continue;
                if (recipeCounts.TryGetValue(domain, out int count)) recipeCounts[domain] = count + 1;
                else recipeCounts[domain] = 1;
            }

            var groups = new Dictionary<string, ModFilterGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in recipeCounts)
            {
                string domain = entry.Key;
                string normalizedDomain = NormalizeFilterToken(domain);
                bool isVanillaDomain = domain == "game" || domain == "survival" || domain == "creative";

                string key;
                string label;
                if (isVanillaDomain)
                {
                    key = FilterVanilla;
                    label = HandbookOptLang.Get("vintage-story-base");
                }
                else if (TryResolveModLabel(normalizedDomain, loadedModNamesByNormalizedId, out string loadedName))
                {
                    label = loadedName;
                    // Si dos dominios pertenecen al mismo mod (ID exacto o dominio de
                    // assets que extiende el ID), se muestran bajo una sola entrada.
                    key = "modname:" + NormalizeFilterToken(label);
                }
                else
                {
                    // Un dominio sin ModInfo solo se muestra si aporta recetas reales.
                    label = domain;
                    key = "domain:" + domain;
                }

                if (!groups.TryGetValue(key, out ModFilterGroup group))
                {
                    group = new ModFilterGroup { Key = key, Label = label };
                    groups[key] = group;
                }
                group.Domains.Add(domain);
                group.RecipeCount += entry.Value;
            }

            var entries = new List<ModFilterGroup>(groups.Values);
            entries.Sort((a, b) =>
            {
                int byName = string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase);
                return byName != 0 ? byName : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            modFilterDomains.Clear();
            var values = new List<string> { FilterAvailable, FilterAllMods, FilterRecent };
            var names = new List<string>(GetBaseModFilterNames());
            for (int i = 0; i < entries.Count; i++)
            {
                ModFilterGroup group = entries[i];
                if (group.Domains.Count == 0 || group.RecipeCount <= 0) continue;
                values.Add(group.Key);
                names.Add(CompactModFilterName(group.Label) + " (" + group.RecipeCount + ")");
                modFilterDomains[group.Key] = group.Domains;
            }
            modFilterValues = values.ToArray();
            modFilterNames = names.ToArray();
            modFilterOptionsBuilt = true;
            RefreshIngredientFilterOption();
        }

        void RefreshIngredientFilterOption()
        {
            var values = new List<string>();
            var names = new List<string>();
            int currentCount = Math.Min(modFilterValues?.Length ?? 0, modFilterNames?.Length ?? 0);
            for (int i = 0; i < currentCount; i++)
            {
                if (string.Equals(modFilterValues[i], FilterUsesIngredient, StringComparison.OrdinalIgnoreCase)) continue;
                values.Add(modFilterValues[i]);
                names.Add(modFilterNames[i]);
            }

            if (reverseSearchIngredient?.Collectible != null)
            {
                values.Add(FilterUsesIngredient);
                string itemName = ReverseSearchIngredientName();
                string label = HandbookOptLang.Get("filter-uses-ingredient", itemName);
                names.Add(CompactModFilterName(label));
            }

            modFilterValues = values.ToArray();
            modFilterNames = names.ToArray();
        }

        int SelectedModFilterIndex()
        {
            for (int i = 0; i < modFilterValues.Length; i++)
                if (string.Equals(modFilterValues[i], selectedModFilter, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        void OnModFilterChanged(string code, bool selected)
        {
            if (!selected || string.IsNullOrWhiteSpace(code)) return;

            if (string.Equals(selectedModFilter, code, StringComparison.OrdinalIgnoreCase)) return;
            ingredientPickArmed = false;
            selectedModFilter = code;
            preferencesNeedValidation = false;
            page = 0;
            lastSig = "";
            dirty = true;
            dirtyAt = 0;
            SavePickerPreferences();
            if (HandbookOptMod.IndexReady) RebuildMatches();
        }

        bool IsBrowsingRecipeCatalog()
        {
            return !string.Equals(selectedModFilter, FilterAvailable, StringComparison.OrdinalIgnoreCase);
        }

        bool IsBrowsingRecentRecipes()
        {
            return string.Equals(selectedModFilter, FilterRecent, StringComparison.OrdinalIgnoreCase);
        }

        bool IsBrowsingIngredientRecipes()
        {
            return string.Equals(selectedModFilter, FilterUsesIngredient, StringComparison.OrdinalIgnoreCase)
                && reverseSearchIngredient?.Collectible != null;
        }

        static bool RecipeUsesIngredient(CompactRecipe recipe, ItemStack candidate)
        {
            if (recipe?.Ings == null || candidate?.Collectible == null) return false;
            for (int i = 0; i < recipe.Ings.Length; i++)
            {
                CraftingRecipeIngredient ingredient = recipe.Ings[i];
                if (ingredient == null) continue;
                try
                {
                    // Use the recipe's own matcher, so tags, variants and custom ingredient
                    // rules are respected instead of comparing display names or item codes.
                    if (ingredient.SatisfiesAsIngredient(candidate, false)) return true;
                }
                catch
                {
                    // A custom matcher that cannot be evaluated is not treated as a match.
                }
            }
            return false;
        }

        void RememberRecentRecipe(int recipeIndex)
        {
            if (recipeIndex < 0 || recipeIndex >= CraftCatalog.Recipes.Count) return;
            recentRecipeIndexes.Remove(recipeIndex);
            recentRecipeIndexes.Insert(0, recipeIndex);
            if (recentRecipeIndexes.Count > MaxRecentRecipes)
                recentRecipeIndexes.RemoveRange(MaxRecentRecipes, recentRecipeIndexes.Count - MaxRecentRecipes);

            if (IsBrowsingRecentRecipes())
            {
                dirty = true;
                dirtyAt = capi?.ElapsedMilliseconds ?? 0;
            }
        }

        bool RecipePassesModFilter(CompactRecipe rec)
        {
            if (string.Equals(selectedModFilter, FilterAllMods, StringComparison.OrdinalIgnoreCase)) return true;
            if (rec == null || string.IsNullOrWhiteSpace(rec.SourceDomain)) return false;
            if (!modFilterDomains.TryGetValue(selectedModFilter, out HashSet<string> domains)) return false;
            return domains.Contains(rec.SourceDomain);
        }

        void RebuildMatches()
        {
            // Keep the old group objects alive until we know whether their visible tiles
            // need rebuilding. RenderHandler closures keep references to these objects.
            var previousDisplayGroups = new List<RecipeVariantDisplayGroup>(matchVariantGroups);
            Dictionary<string, int> previousVariantByInput = CaptureActiveVariants();
            matches.Clear();
            times.Clear();
            matchVariantGroups = new List<RecipeVariantDisplayGroup>();
            if (!HandbookOptMod.IndexReady)
            {
                ComposeAndOpen();
                return;
            }

            EnsureModFilterOptions();
            TryRestoreIngredientPreference();
            RefreshIngredientFilterOption();
            ValidateRestoredPickerFilter();
            var counts = CountOwned();

            if (IsBrowsingRecentRecipes())
            {
                // El historial conserva solo recetas realmente seleccionadas en esta sesión.
                // No agrupar salidas ALT aquí: cada entrada reciente debe poder reabrirse tal cual.
                for (int i = 0; i < recentRecipeIndexes.Count; i++)
                {
                    int idx = recentRecipeIndexes[i];
                    if (idx < 0 || idx >= CraftCatalog.Recipes.Count) continue;
                    CompactRecipe rec = CraftCatalog.Recipes[idx];
                    if (!PassSearch(rec)) continue;
                    matches.Add(idx);
                    times.Add(-1);
                }
            }
            else if (IsBrowsingIngredientRecipes())
            {
                // Completeness first: visit every recipe in the already-built catalogue and
                // ask each ingredient's own matcher. Do not infer compatibility from IDs,
                // MatchingType, Tags, or the concrete ingredient class; mods can implement
                // matching rules that are not represented by those hints.
                // This is an on-demand read-only query. It does not modify CraftCatalog,
                // ByExactId, IndexGridRecipe(), recipe ordering, or any persistent index.
                // A matcher that throws is skipped safely by RecipeUsesIngredient, rather
                // than treating an unverified ingredient as a match.
                for (int idx = 0; idx < CraftCatalog.Recipes.Count; idx++)
                {
                    CompactRecipe rec = CraftCatalog.Recipes[idx];
                    if (!PassSearch(rec) || !RecipeUsesIngredient(rec, reverseSearchIngredient)) continue;
                    matches.Add(idx);
                    times.Add(-1);
                }
            }
            else if (IsBrowsingRecipeCatalog())
            {
                // En modo glosario, listar todas las recetas del dominio elegido,
                // no solo las que pueden fabricarse con el inventario actual.
                // times = -1 significa "vista de catálogo; no mostrar cantidad".
                for (int idx = 0; idx < CraftCatalog.Recipes.Count; idx++)
                {
                    CompactRecipe rec = CraftCatalog.Recipes[idx];
                    if (!PassSearch(rec) || !RecipePassesModFilter(rec)) continue;
                    matches.Add(idx);
                    times.Add(-1);
                }
            }
            else
            {
                // Vista anterior: solo recetas que pueden fabricarse con los materiales
                // del inventario o que ya están colocados en la cuadrícula.
                var seen = new HashSet<int>();
                foreach (var kv in counts)
                {
                    if (!CraftCatalog.ByExactId.TryGetValue(kv.Key, out var list)) continue;
                    for (int i = 0; i < list.Count; i++) seen.Add(list[i]);
                }
                var orderedCandidates = new List<int>(seen);
                orderedCandidates.Sort();

                int checkedN = 0;
                foreach (int idx in orderedCandidates)
                {
                    if (matches.Count >= 80) break;
                    if (++checkedN > 4000) break;
                    var rec = CraftCatalog.Recipes[idx];
                    if (!PassSearch(rec)) continue;
                    int n = HowMany(rec, counts, false);
                    if (n > 0) n = Math.Min(n, MaxFit(rec, n));
                    if (n <= 0) continue;
                    matches.Add(idx);
                    times.Add(n);
                }

                if (matches.Count < 80 && CraftCatalog.WildcardOnly.Count > 0 && CraftCatalog.WildcardOnly.Count < 2500)
                {
                    var wilds = CraftCatalog.WildcardOnly;
                    for (int i = 0; i < wilds.Count && matches.Count < 80; i++)
                    {
                        int idx = wilds[i];
                        var rec = CraftCatalog.Recipes[idx];
                        if (!PassSearch(rec)) continue;
                        int n = HowMany(rec, counts, false);
                        if (n > 0) n = Math.Min(n, MaxFit(rec, n));
                        if (n <= 0) continue;
                        matches.Add(idx);
                        times.Add(n);
                    }
                }

                // Contar completamente las recetas con comodines solo en la vista
                // disponible; la vista de glosario no tiene que calcular materiales.
                for (int i = 0; i < matches.Count; i++)
                {
                    CompactRecipe rec = CraftCatalog.Recipes[matches[i]];
                    bool hasWildcard = false;
                    for (int j = 0; j < rec.Wild.Length; j++)
                    {
                        if (rec.Wild[j]) { hasWildcard = true; break; }
                    }
                    if (!hasWildcard) continue;
                    int fullCount = HowMany(rec, counts, true);
                    if (fullCount > 0) times[i] = Math.Min(fullCount, MaxFit(rec, fullCount));
                }
            }

            // Historial: preservar cada receta seleccionada como una entrada independiente.
            // En los demás filtros se mantiene el agrupamiento ALT existente.
            if (IsBrowsingRecentRecipes())
            {
                matchVariantGroups.Clear();
                for (int i = 0; i < matches.Count; i++)
                {
                    var group = new RecipeVariantDisplayGroup { InputKey = string.Empty };
                    group.RecipeIndexes.Add(matches[i]);
                    group.TimesByRecipe[matches[i]] = times[i];
                    matchVariantGroups.Add(group);
                }
            }
            else
            {
                // Collapse only strict input-equivalent recipes with different outputs.
                // Searches remain output-specific: if the user searches for one result, only
                // matching outputs enter the group and no unrelated result is silently shown.
                GroupMatchesIntoOutputVariants(previousVariantByInput);
            }
            // Reuse a matching existing static tile as the visual owner of an ALT group,
            // preferring a vanilla recipe entry when its output is represented. The old
            // detached ALT row is removed only after the group has a host, avoiding a
            // second tile that then needs to be hidden afterward.
            PromoteVariantGroupsIntoExistingTiles();

            int pages = Math.Max(1, (matches.Count + PageSize - 1) / PageSize);
            if (page >= pages) page = pages - 1;
            if (page < 0) page = 0;

            var signatureBuilder = new StringBuilder(32 + matches.Count * 12);
            signatureBuilder.Append(page).Append('|').Append(search).Append('|').Append(selectedModFilter).Append('|');
            // En el glosario las recetas visibles no cambian al mover materiales, pero
            // sí cambia su resaltado. Incluir el inventario en la firma fuerza a recrear
            // los 24 iconos visibles cuando el contenido del inventario cambia.
            if (IsBrowsingRecipeCatalog()) signatureBuilder.Append(lastInventorySignature ?? string.Empty).Append('|');
            for (int i = 0; i < matches.Count; i++)
            {
                signatureBuilder.Append(matches[i]).Append(':').Append(times[i]).Append('[');
                RecipeVariantDisplayGroup group = i < matchVariantGroups.Count ? matchVariantGroups[i] : null;
                if (group != null)
                {
                    for (int v = 0; v < group.RecipeIndexes.Count; v++)
                        signatureBuilder.Append(group.RecipeIndexes[v]).Append(',');
                }
                signatureBuilder.Append("]; ");
            }
            string sig = signatureBuilder.ToString();
            if (sig == lastSig && IsOpened())
            {
                // The visible recipe groups are identical. Restore their original state so
                // the already-composed RenderHandlers keep pointing at the live carousel,
                // including the pause countdown and reusable DummySlot.
                matchVariantGroups = previousDisplayGroups;
                return;
            }
            lastSig = sig;
            ComposeAndOpen();
        }

        Dictionary<string, int> CaptureActiveVariants()
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            // Only the visible page can be rotating. Preserving these few selections is
            // enough and avoids allocating a dictionary for thousands of off-screen recipes.
            int start = Math.Max(0, page * PageSize);
            int end = Math.Min(Math.Min(matchVariantGroups.Count, matches.Count), start + PageSize);
            for (int i = start; i < end; i++)
            {
                RecipeVariantDisplayGroup group = matchVariantGroups[i];
                if (group == null || string.IsNullOrEmpty(group.InputKey) || matches[i] < 0) continue;
                result[group.InputKey] = matches[i];
            }
            return result;
        }

        void GroupMatchesIntoOutputVariants(Dictionary<string, int> previousVariantByInput)
        {
            if (matches.Count < 2)
            {
                matchVariantGroups.Clear();
                if (matches.Count == 1)
                {
                    var one = new RecipeVariantDisplayGroup { InputKey = GetRecipeInputKeyCached(matches[0]) };
                    one.RecipeIndexes.Add(matches[0]);
                    one.TimesByRecipe[matches[0]] = times[0];
                    matchVariantGroups.Add(one);
                }
                return;
            }

            var rawMatches = new List<int>(matches);
            var rawTimes = new List<int>(times);
            matches.Clear();
            times.Clear();
            matchVariantGroups.Clear();

            var groupByInput = new Dictionary<string, RecipeVariantDisplayGroup>(StringComparer.Ordinal);
            for (int i = 0; i < rawMatches.Count; i++)
            {
                int recipeIndex = rawMatches[i];
                CompactRecipe recipe = CraftCatalog.Recipes[recipeIndex];
                string inputKey = GetRecipeInputKeyCached(recipeIndex);

                // Unknown/custom matcher rules get a private key; never merge them merely
                // because their display text or resolved ingredient IDs happen to match.
                bool canGroup = !string.IsNullOrEmpty(inputKey);
                string dictionaryKey = canGroup ? inputKey : "\u0001single:" + recipeIndex.ToString(CultureInfo.InvariantCulture);

                if (canGroup && groupByInput.TryGetValue(dictionaryKey, out RecipeVariantDisplayGroup existing))
                {
                    bool duplicateOutput = false;
                    for (int v = 0; v < existing.RecipeIndexes.Count; v++)
                    {
                        CompactRecipe present = CraftCatalog.Recipes[existing.RecipeIndexes[v]];
                        if (RecipesDemonstrablyEquivalent(present, recipe))
                        {
                            duplicateOutput = true;
                            break;
                        }
                    }
                    if (!duplicateOutput)
                    {
                        existing.RecipeIndexes.Add(recipeIndex);
                        existing.TimesByRecipe[recipeIndex] = rawTimes[i];
                    }
                    continue;
                }

                var group = new RecipeVariantDisplayGroup { InputKey = canGroup ? inputKey : string.Empty };
                group.RecipeIndexes.Add(recipeIndex);
                group.TimesByRecipe[recipeIndex] = rawTimes[i];
                matchVariantGroups.Add(group);
                if (canGroup) groupByInput[dictionaryKey] = group;
            }

            for (int i = 0; i < matchVariantGroups.Count; i++)
            {
                RecipeVariantDisplayGroup group = matchVariantGroups[i];
                int activeRecipe = group.RecipeIndexes[0];
                if (!string.IsNullOrEmpty(group.InputKey)
                    && previousVariantByInput != null
                    && previousVariantByInput.TryGetValue(group.InputKey, out int previousRecipe)
                    && group.RecipeIndexes.Contains(previousRecipe))
                {
                    activeRecipe = previousRecipe;
                }
                group.ActiveVariantIndex = Math.Max(0, group.RecipeIndexes.IndexOf(activeRecipe));
                matches.Add(activeRecipe);
                times.Add(group.TimesByRecipe.TryGetValue(activeRecipe, out int n) ? n : 0);
            }
        }


        void PromoteVariantGroupsIntoExistingTiles()
        {
            // Compact Available view: keep one visual row for outputs represented by ALT.
            // First move each ALT carousel onto an existing single-recipe row with a
            // demonstrably equivalent output. Prefer the vanilla "game" row when possible.
            // The full recipe catalog returns above and is not rewritten by this UI rule.
            if (IsBrowsingRecipeCatalog() || matches.Count < 2 ||
                matchVariantGroups.Count != matches.Count || times.Count != matches.Count) return;

            var originalMatches = new List<int>(matches);
            var originalTimes = new List<int>(times);
            var originalGroups = new List<RecipeVariantDisplayGroup>(matchVariantGroups);

            // Each static row can host at most one ALT group. An ALT source row is removed
            // only if its group was successfully transferred to a host row.
            var hostToGroupSource = new Dictionary<int, int>();
            var consumedAltTiles = new HashSet<int>();
            var usedHosts = new HashSet<int>();

            for (int sourceIndex = 0; sourceIndex < originalGroups.Count; sourceIndex++)
            {
                RecipeVariantDisplayGroup altGroup = originalGroups[sourceIndex];
                if (altGroup == null || altGroup.RecipeIndexes.Count < 2) continue;

                int bestHost = -1;
                for (int candidateIndex = 0; candidateIndex < originalGroups.Count; candidateIndex++)
                {
                    if (candidateIndex == sourceIndex || usedHosts.Contains(candidateIndex) ||
                        consumedAltTiles.Contains(candidateIndex)) continue;

                    RecipeVariantDisplayGroup candidateGroup = originalGroups[candidateIndex];
                    if (candidateGroup == null || candidateGroup.RecipeIndexes.Count != 1) continue;

                    int candidateRecipeIndex = originalMatches[candidateIndex];
                    if (candidateRecipeIndex < 0 || candidateRecipeIndex >= CraftCatalog.Recipes.Count) continue;

                    ItemStack candidateOutput = CraftCatalog.Recipes[candidateRecipeIndex].Output;
                    if (candidateOutput?.Collectible == null) continue;

                    bool outputRepresented = false;
                    for (int variant = 0; variant < altGroup.RecipeIndexes.Count; variant++)
                    {
                        int variantRecipeIndex = altGroup.RecipeIndexes[variant];
                        if (variantRecipeIndex < 0 || variantRecipeIndex >= CraftCatalog.Recipes.Count) continue;
                        ItemStack variantOutput = CraftCatalog.Recipes[variantRecipeIndex].Output;
                        if (OutputsDemonstrablyEquivalent(candidateOutput, variantOutput))
                        {
                            outputRepresented = true;
                            break;
                        }
                    }
                    if (!outputRepresented) continue;

                    CompactRecipe candidateRecipe = CraftCatalog.Recipes[candidateRecipeIndex];
                    // Only promote into a vanilla row. Moving an ALT group onto an arbitrary
                    // mod recipe would unexpectedly relocate it and could obscure the mod's
                    // original list organization. If no vanilla host exists, keep the ALT row
                    // where it was; the conservative compaction below removes only a row whose
                    // input signature and output are both represented by an ALT group.
                    if (!string.Equals(candidateRecipe.SourceDomain, "game", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (bestHost < 0 || candidateIndex < bestHost)
                    {
                        bestHost = candidateIndex;
                    }
                }

                if (bestHost < 0) continue;
                hostToGroupSource[bestHost] = sourceIndex;
                usedHosts.Add(bestHost);
                consumedAltTiles.Add(sourceIndex);
            }

            // Rebuild rows in their existing order. The selected host gets the live ALT
            // group and active recipe index; the former detached ALT row is discarded.
            var movedMatches = new List<int>(originalMatches.Count);
            var movedTimes = new List<int>(originalTimes.Count);
            var movedGroups = new List<RecipeVariantDisplayGroup>(originalGroups.Count);
            for (int i = 0; i < originalMatches.Count; i++)
            {
                if (consumedAltTiles.Contains(i)) continue;

                if (hostToGroupSource.TryGetValue(i, out int sourceIndex))
                {
                    RecipeVariantDisplayGroup group = originalGroups[sourceIndex];
                    int activeRecipeIndex = originalMatches[sourceIndex];
                    movedMatches.Add(activeRecipeIndex);
                    movedTimes.Add(group.TimesByRecipe.TryGetValue(activeRecipeIndex, out int count) ? count : 0);
                    movedGroups.Add(group);
                }
                else
                {
                    movedMatches.Add(originalMatches[i]);
                    movedTimes.Add(originalTimes[i]);
                    movedGroups.Add(originalGroups[i]);
                }
            }

            // Keep references to the exact ALT groups. A singleton may be suppressed only
            // when BOTH its input signature and output are represented by one of these groups.
            // Output-only suppression is unsafe: custom ingredients can be uncomparable and
            // still define a separate crafting route to the same item (e.g. matrix test mods).
            var representedAltGroups = new List<RecipeVariantDisplayGroup>(8);
            for (int i = 0; i < movedGroups.Count; i++)
            {
                RecipeVariantDisplayGroup group = movedGroups[i];
                if (group == null || group.RecipeIndexes.Count < 2 || string.IsNullOrEmpty(group.InputKey)) continue;
                representedAltGroups.Add(group);
            }

            if (representedAltGroups.Count == 0) return;

            var keptMatches = new List<int>(movedMatches.Count);
            var keptTimes = new List<int>(movedTimes.Count);
            var keptGroups = new List<RecipeVariantDisplayGroup>(movedGroups.Count);
            for (int i = 0; i < movedMatches.Count; i++)
            {
                RecipeVariantDisplayGroup group = movedGroups[i];
                bool isAltOwner = group != null && group.RecipeIndexes.Count > 1;
                if (!isAltOwner)
                {
                    int recipeIndex = movedMatches[i];
                    if (recipeIndex >= 0 && recipeIndex < CraftCatalog.Recipes.Count)
                    {
                        string candidateInputKey = GetRecipeInputKeyCached(recipeIndex);
                        // Empty means the matcher could not be represented safely by our key.
                        // Never discard that recipe merely because its output appears on an ALT tile.
                        if (!string.IsNullOrEmpty(candidateInputKey))
                        {
                            ItemStack output = CraftCatalog.Recipes[recipeIndex].Output;
                            if (output?.Collectible != null)
                            {
                                bool alreadyRepresented = false;
                                for (int g = 0; g < representedAltGroups.Count && !alreadyRepresented; g++)
                                {
                                    RecipeVariantDisplayGroup altGroup = representedAltGroups[g];
                                    if (!string.Equals(candidateInputKey, altGroup.InputKey, StringComparison.Ordinal)) continue;

                                    for (int v = 0; v < altGroup.RecipeIndexes.Count; v++)
                                    {
                                        int variantRecipeIndex = altGroup.RecipeIndexes[v];
                                        if (variantRecipeIndex < 0 || variantRecipeIndex >= CraftCatalog.Recipes.Count) continue;
                                        ItemStack variantOutput = CraftCatalog.Recipes[variantRecipeIndex].Output;
                                        if (OutputsDemonstrablyEquivalent(output, variantOutput))
                                        {
                                            alreadyRepresented = true;
                                            break;
                                        }
                                    }
                                }
                                if (alreadyRepresented) continue;
                            }
                        }
                    }
                }

                keptMatches.Add(movedMatches[i]);
                keptTimes.Add(movedTimes[i]);
                keptGroups.Add(group);
            }

            matches.Clear();
            matches.AddRange(keptMatches);
            times.Clear();
            times.AddRange(keptTimes);
            matchVariantGroups = keptGroups;
        }

        string GetRecipeInputKeyCached(int recipeIndex)
        {
            if (!ReferenceEquals(variantKeyCacheCatalog, CraftCatalog.Recipes))
            {
                variantInputKeyCache.Clear();
                variantKeyCacheCatalog = CraftCatalog.Recipes;
            }
            if (variantInputKeyCache.TryGetValue(recipeIndex, out string cached))
                return string.IsNullOrEmpty(cached) ? null : cached;

            string key = recipeIndex >= 0 && recipeIndex < CraftCatalog.Recipes.Count
                ? BuildRecipeInputKey(CraftCatalog.Recipes[recipeIndex])
                : null;
            // Empty string caches a deliberately ungroupable recipe too.
            variantInputKeyCache[recipeIndex] = key ?? string.Empty;
            return key;
        }

        string BuildRecipeInputKey(CompactRecipe recipe)
        {
            if (recipe?.Recipe == null || recipe.Ings == null || recipe.Slots == null
                || recipe.Ings.Length == 0 || recipe.Ings.Length != recipe.Slots.Length)
                return null;

            GridRecipe gridRecipe = recipe.Recipe;
            var sb = new StringBuilder(256);
            AppendVariantKeyField(sb, gridRecipe.Shapeless ? "shapeless" : "shaped");
            // La identidad se basa en las celdas físicas efectivas (recipe.Slots), no
            // en Width/Height declarados: una receta 2x1 y otra 3x1 pueden ocupar las
            // mismas celdas en la cuadrícula 3x3 y no deben dejar una variante estática.
            AppendVariantKeyField(sb, recipe.RequiresTrait ?? string.Empty);

            var ingredientKeys = new List<string>(recipe.Ings.Length);
            for (int i = 0; i < recipe.Ings.Length; i++)
            {
                CraftingRecipeIngredient ingredient = recipe.Ings[i];
                if (ingredient == null || ingredient.GetType() != typeof(CraftingRecipeIngredient)) return null;

                try
                {
                    // Las condiciones de tags usan un comparador complejo del juego. Si no
                    // podemos expresar su semántica exactamente, dejamos esa receta aparte.
                    if (!ingredient.Tags.IsEmpty) return null;
                }
                catch { return null; }

                string code;
                try
                {
                    // Para una coincidencia exacta, la identidad canónica es el collectible
                    // resuelto. Los códigos originales o nombres de la clave de patrón pueden
                    // diferir entre mods aunque el ingrediente real sea el mismo.
                    code = ingredient.MatchingType == EnumRecipeMatchType.Exact
                        ? ingredient.ResolvedItemStack?.Collectible?.Code?.ToString() ?? ingredient.Code?.ToString() ?? string.Empty
                        : ingredient.Code?.ToString() ?? ingredient.ResolvedItemStack?.Collectible?.Code?.ToString() ?? string.Empty;
                }
                catch { return null; }
                if (string.IsNullOrWhiteSpace(code)) return null;

                string attributes;
                try { attributes = CanonicalJsonObject(ingredient.Attributes); }
                catch { return null; }

                // Allowed/skip variants sólo alteran la coincidencia del comodín; para
                // ingredientes exactos son metadatos irrelevantes y no deben separar una receta.
                var allowedVariants = ingredient.MatchingType == EnumRecipeMatchType.Exact
                    ? new List<string>()
                    : new List<string>(ingredient.AllowedVariants ?? Array.Empty<string>());
                var skippedVariants = ingredient.MatchingType == EnumRecipeMatchType.Exact
                    ? new List<string>()
                    : new List<string>(ingredient.SkipVariants ?? Array.Empty<string>());
                allowedVariants.Sort(StringComparer.Ordinal);
                skippedVariants.Sort(StringComparer.Ordinal);

                var ik = new StringBuilder(160);
                AppendVariantKeyField(ik, ((int)ingredient.Type).ToString(CultureInfo.InvariantCulture));
                AppendVariantKeyField(ik, ((int)ingredient.MatchingType).ToString(CultureInfo.InvariantCulture));
                AppendVariantKeyField(ik, code);
                AppendVariantKeyField(ik, (ingredient.Quantity > 0 ? ingredient.Quantity : 1).ToString(CultureInfo.InvariantCulture));
                // Name solo participa en la sustitución de comodines; para ingredientes
                // exactos no cambia qué objeto acepta la receta.
                AppendVariantKeyField(ik, ingredient.MatchingType == EnumRecipeMatchType.Exact ? string.Empty : ingredient.Name ?? string.Empty);
                AppendVariantKeyField(ik, string.Join("\u001f", allowedVariants));
                AppendVariantKeyField(ik, string.Join("\u001f", skippedVariants));
                // Consume/IsTool/Break/durabilidad describen el efecto posterior al
                // aceptar una entrada, no el conjunto de objetos que la satisface. Se
                // agrupan también estas variantes; al hacer clic se usa la receta real,
                // por lo que el servidor conserva su consumo/devolución específico.
                AppendVariantKeyField(ik, attributes);

                if (!gridRecipe.Shapeless)
                    ingredientKeys.Add(recipe.Slots[i].ToString(CultureInfo.InvariantCulture) + "=" + ik.ToString());
                else
                    ingredientKeys.Add(ik.ToString());
            }

            // Shaped recipes preserve cell positions. Shapeless recipes compare a sorted
            // multiset, so ingredient order alone doesn't prevent grouping equivalent inputs.
            if (gridRecipe.Shapeless) ingredientKeys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < ingredientKeys.Count; i++) AppendVariantKeyField(sb, ingredientKeys[i]);
            return sb.ToString();
        }

        static void AppendVariantKeyField(StringBuilder builder, string value)
        {
            value = value ?? string.Empty;
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(value).Append('|');
        }

        bool RecipesDemonstrablyEquivalent(CompactRecipe a, CompactRecipe b)
        {
            if (a == null || b == null || !OutputsDemonstrablyEquivalent(a.Output, b.Output)) return false;
            GridRecipe leftRecipe = a.Recipe;
            GridRecipe rightRecipe = b.Recipe;
            if (leftRecipe == null || rightRecipe == null) return false;

            try
            {
                // No ocultar recetas que producen el mismo icono pero tienen efectos
                // posteriores distintos (copiado de atributos, durabilidad o devoluciones).
                if (!string.Equals(leftRecipe.CopyAttributesFrom, rightRecipe.CopyAttributesFrom, StringComparison.Ordinal)) return false;
                if (!string.Equals(leftRecipe.RequiresTrait, rightRecipe.RequiresTrait, StringComparison.Ordinal)) return false;
                if (leftRecipe.AverageDurability != rightRecipe.AverageDurability) return false;
                if (!string.Equals(string.Join("\u001f", leftRecipe.MergeAttributesFrom ?? Array.Empty<string>()),
                                   string.Join("\u001f", rightRecipe.MergeAttributesFrom ?? Array.Empty<string>()), StringComparison.Ordinal)) return false;
                if (!string.Equals(CanonicalJsonObject(leftRecipe.Attributes),
                                   CanonicalJsonObject(rightRecipe.Attributes), StringComparison.Ordinal)) return false;
                if (!string.Equals(CanonicalJsonObject(leftRecipe.Output?.Attributes),
                                   CanonicalJsonObject(rightRecipe.Output?.Attributes), StringComparison.Ordinal)) return false;
                if (a.Ings == null || b.Ings == null || a.Ings.Length != b.Ings.Length) return false;
                for (int i = 0; i < a.Ings.Length; i++)
                {
                    CraftingRecipeIngredient li = a.Ings[i];
                    CraftingRecipeIngredient ri = b.Ings[i];
                    string leftReturn = ReturnedStackKey(li?.ReturnedStack);
                    string rightReturn = ReturnedStackKey(ri?.ReturnedStack);
                    if (!string.Equals(leftReturn, rightReturn, StringComparison.Ordinal)) return false;
                    if (li == null || ri == null) return false;
                    // Ahora estos campos no separan grupos (son efectos de crafteo,
                    // no identidad del ingrediente aceptado), pero sí deben impedir
                    // eliminar una segunda receta como duplicado cuando el resultado
                    // visible es igual y cambia el consumo o la durabilidad.
                    if (li.Consume != ri.Consume || li.IsTool != ri.IsTool || li.Break != ri.Break ||
                        li.DurabilityChange != ri.DurabilityChange || li.ToolDurabilityCost != ri.ToolDurabilityCost)
                        return false;
                    if (!string.Equals(CanonicalJsonObject(li?.RecipeAttributes),
                                       CanonicalJsonObject(ri?.RecipeAttributes), StringComparison.Ordinal)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        static string ReturnedStackKey(JsonItemStack stack)
        {
            if (stack == null) return string.Empty;
            try
            {
                return (stack.Type.ToString() ?? string.Empty) + "|" +
                    (stack.Code?.ToString() ?? string.Empty) + "|" +
                    stack.StackSize.ToString(CultureInfo.InvariantCulture) + "|" +
                    CanonicalJsonObject(stack.Attributes);
            }
            catch { return "<unresolved-return-stack>"; }
        }

        static string CanonicalJsonObject(JsonObject value)
        {
            try
            {
                string json = value?.Token?.ToString() ?? string.Empty;
                json = json.Trim();
                // Una propiedad Attributes vacía equivale a no declarar ninguna condición.
                if (json.Length == 0 || string.Equals(json, "{}", StringComparison.Ordinal) ||
                    string.Equals(json, "null", StringComparison.OrdinalIgnoreCase)) return string.Empty;
                return json;
            }
            catch { return "<unreadable-json-attributes>"; }
        }

        bool OutputsDemonstrablyEquivalent(ItemStack a, ItemStack b)
        {
            if (a?.Collectible == null || b?.Collectible == null || a.StackSize != b.StackSize) return false;
            try
            {
                // Unlike the inventory-transfer comparator, this comparison includes the
                // requested stack size. Compare clones so a custom collectible cannot
                // mutate catalog-owned output stacks during equality evaluation.
                ItemStack left = a.Clone();
                ItemStack right = b.Clone();
                return left.Equals(capi?.World, right);
            }
            catch { return false; }
        }

        static string RecipeVariantInfoText(CompactRecipe recipe, RecipeVariantDisplayGroup group)
        {
            if (group == null || group.RecipeIndexes.Count < 2)
                return recipe?.Name ?? string.Empty;
            return (recipe?.Name ?? string.Empty) + "\n" + HandbookOptLang.Get(
                "recipe-variants-info", group.ActiveVariantIndex + 1, group.RecipeIndexes.Count);
        }

        void EnsureRecipeVariantsBadgeTexture()
        {
            if (recipeVariantsBadgeLoadAttempted) return;
            recipeVariantsBadgeLoadAttempted = true;
            try
            {
                recipeVariantsBadgeTexture = capi.Gui.LoadSvg(
                    new AssetLocation("handbookopt:textures/gui/recipe-variants-badge.svg"), 32, 32, 32, 32);
                if (recipeVariantsBadgeTexture == null || recipeVariantsBadgeTexture.TextureId <= 0)
                    capi.Logger.Warning("[HandbookOpt] No se pudo cargar la insignia de recetas alternativas; la rotación seguirá disponible sin insignia.");
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[HandbookOpt] Error al cargar la insignia de recetas alternativas: {0}", e.Message);
            }
        }

        const long RecipeVariantSlideIntervalMs = 2000;

        void UpdateRecipeVariantCarousel()
        {
            if (!IsOpened() || !HandbookOptMod.IndexReady || IsModFilterPopupOpen()) return;
            long now = capi?.ElapsedMilliseconds ?? 0;
            int from = page * PageSize;
            int to = Math.Min(matchVariantGroups.Count, from + PageSize);
            for (int abs = from; abs < to; abs++)
            {
                RecipeVariantDisplayGroup group = matchVariantGroups[abs];
                if (group == null || group.RecipeIndexes.Count < 2) continue;

                // Iniciar el plazo ANTES de procesar hover. Si el cursor cae sobre una
                // ficha recién compuesta, NextSlideAt aún vale cero; guardar 1 ms aquí
                // hacía que la variante cambiara inmediatamente al retirar el cursor.
                if (group.NextSlideAt <= 0)
                    group.NextSlideAt = now + RecipeVariantSlideIntervalMs;

                bool hovered = hoveredRecipeMatchIndex == abs;
                if (hovered)
                {
                    if (!group.IsPaused)
                    {
                        group.RemainingWhenPaused = Math.Max(1, group.NextSlideAt - now);
                        group.IsPaused = true;
                    }
                    continue;
                }

                if (group.IsPaused)
                {
                    group.IsPaused = false;
                    group.NextSlideAt = now + Math.Max(1, group.RemainingWhenPaused);
                    group.RemainingWhenPaused = 0;
                }
                if (now < group.NextSlideAt) continue;

                group.ActiveVariantIndex = (group.ActiveVariantIndex + 1) % group.RecipeIndexes.Count;
                int recipeIndex = group.RecipeIndexes[group.ActiveVariantIndex];
                CompactRecipe recipe = CraftCatalog.Recipes[recipeIndex];
                matches[abs] = recipeIndex;
                times[abs] = group.TimesByRecipe.TryGetValue(recipeIndex, out int n) ? n : 0;
                group.NextSlideAt = now + RecipeVariantSlideIntervalMs;
                try
                {
                    if (group.DisplaySlot != null) group.DisplaySlot.Itemstack = recipe.Output?.Clone();
                    if (group.VisibleSkill != null)
                    {
                        group.VisibleSkill.Code = recipe.Output?.Collectible?.Code;
                        group.VisibleSkill.Name = recipe.Name + " · " + HandbookOptLang.Get("recipe-variants-info-short",
                            group.ActiveVariantIndex + 1, group.RecipeIndexes.Count);
                    }
                }
                catch { /* no permitir que la actualización visual interrumpa la interfaz */ }
            }
        }

        bool PassSearch(CompactRecipe rec)
        {
            if (search.Length == 0) return true;
            return rec.Search.IndexOf(search, StringComparison.Ordinal) >= 0;
        }

        static Dictionary<int, List<ItemStack>> Samples = new Dictionary<int, List<ItemStack>>();

        Dictionary<int, int> CountOwned()
        {
            var map = new Dictionary<int, int>(64);
            Samples.Clear();
            AddInv(map, GetInv("backpack"));
            AddInv(map, GetInv("hotbar"));
            var craft = GetInv("craftinggrid");
            if (craft != null)
            {
                int n = Math.Max(0, craft.Count - 1);
                for (int i = 0; i < n; i++) AddSlot(map, craft[i]);
            }
            return map;
        }

        Dictionary<int, int> CountAvailableForAdditional(CompactRecipe rec, IInventory craft)
        {
            var map = new Dictionary<int, int>(64);
            Samples.Clear();
            AddInv(map, GetInv("backpack"));
            AddInv(map, GetInv("hotbar"));

            // No sumar los consumibles ya colocados: el clic ha de aportar materiales
            // nuevos desde el inventario. Una herramienta reutilizable ya puesta sí
            // puede satisfacer el requisito de herramienta de la receta.
            if (craft != null && rec?.Ings != null && rec.Slots != null)
            {
                int inputs = Math.Max(0, craft.Count - 1);
                int len = Math.Min(rec.Ings.Length, rec.Slots.Length);
                for (int i = 0; i < len; i++)
                {
                    if (IngredientConsumes(rec, i)) continue;
                    int dest = rec.Slots[i];
                    if (dest < 0 || dest >= inputs) continue;
                    ItemSlot slot = craft[dest];
                    if (slot?.Itemstack == null || !IngredientMatches(rec, i, slot.Itemstack)) continue;
                    AddSlot(map, slot);
                }
            }
            return map;
        }

        static void AddInv(Dictionary<int, int> map, IInventory inv)
        {
            if (inv == null) return;
            for (int i = 0; i < inv.Count; i++) AddSlot(map, inv[i]);
        }

        static void AddSlot(Dictionary<int, int> map, ItemSlot slot)
        {
            var st = slot?.Itemstack;
            if (st?.Collectible == null || st.StackSize <= 0) return;
            int id = CraftCatalog.Pack(st.Collectible);
            map.TryGetValue(id, out int have);
            map[id] = have + st.StackSize;
            if (!Samples.TryGetValue(id, out var samples))
            {
                samples = new List<ItemStack>(2);
                Samples[id] = samples;
            }
            samples.Add(st);
        }

        IInventory GetInv(string className)
        {
            try { return capi.World.Player.InventoryManager.GetOwnInventory(className); }
            catch { return null; }
        }

        static int HowMany(CompactRecipe rec, Dictionary<int, int> counts, bool deepWild)
        {
            if (rec == null || rec.Ids == null || rec.Ids.Length == 0 || rec.Qtys == null || rec.Wild == null)
                return 0;

            bool anyWild = false;
            bool anyConsumed = false;
            var perBatch = new Dictionary<int, int>();
            for (int i = 0; i < rec.Wild.Length; i++)
            {
                if (rec.Wild[i]) anyWild = true;
                bool consumes = IngredientConsumes(rec, i);
                if (consumes) anyConsumed = true;
                if (rec.Wild[i] || !consumes) continue;

                int id = rec.Ids[i];
                int qty = Math.Max(1, rec.Qtys[i]);
                perBatch.TryGetValue(id, out int required);
                perBatch[id] = required + qty;
            }

            // Reservar las herramientas/ingredientes no consumibles antes de contar las
            // tandas, para no contar un mismo objeto como herramienta y como material que
            // se gasta. Se reservan una sola vez; las tandas solo consumen el resto.
            if (!TryReserveReusableIngredients(rec, counts, out Dictionary<int, int> consumableCounts))
                return 0;

            int toolLimit = GetToolUseLimit(rec);
            if (toolLimit <= 0) return 0;

            if (!anyWild)
            {
                int possible = int.MaxValue;
                foreach (var entry in perBatch)
                {
                    consumableCounts.TryGetValue(entry.Key, out int available);
                    possible = Math.Min(possible, available / entry.Value);
                    if (possible <= 0) return 0;
                }

                if (possible == int.MaxValue)
                {
                    // Una receta sin ingredientes consumibles no debe anunciar un número
                    // infinito: se limita por durabilidad si es calculable, o a una tanda.
                    possible = toolLimit == int.MaxValue ? 1 : toolLimit;
                }
                possible = Math.Min(possible, toolLimit);
                return Math.Min(possible, MaxFit(rec, possible));
            }

            // Para comodines se simula el consumo en orden de receta. Las herramientas
            // ya se reservaron arriba y no se descuentan una vez por cada tanda.
            var probe = new Dictionary<int, int>(consumableCounts);
            if (!ConsumeOnce(rec, probe)) return 0;
            if (!deepWild) return 1;

            int slotCap = MaxFit(rec, int.MaxValue);
            if (slotCap <= 0) return 0;
            slotCap = Math.Min(slotCap, toolLimit);

            if (!anyConsumed)
            {
                slotCap = Math.Min(slotCap, toolLimit == int.MaxValue ? 1 : toolLimit);
                return Math.Max(1, slotCap);
            }

            long totalOwned = 0;
            foreach (var entry in consumableCounts)
            {
                if (entry.Value > 0) totalOwned += entry.Value;
            }
            if (totalOwned < slotCap) slotCap = (int)Math.Min(int.MaxValue, totalOwned);

            int max = 1;
            while (max < slotCap && ConsumeOnce(rec, probe)) max++;
            return max;
        }

        static bool TryReserveReusableIngredients(
            CompactRecipe rec,
            Dictionary<int, int> counts,
            out Dictionary<int, int> consumableCounts)
        {
            consumableCounts = new Dictionary<int, int>(counts);
            // No capturar un parámetro out en la lambda del comparador (CS1628).
            // La referencia local representa el mismo diccionario de trabajo.
            Dictionary<int, int> countsForSort = consumableCounts;
            var toolIndices = new List<int>();
            for (int i = 0; i < rec.Ings.Length; i++)
                if (!IngredientConsumes(rec, i)) toolIndices.Add(i);

            // Las herramientas con menos variantes candidatas se reservan primero.
            toolIndices.Sort((a, b) => CountIngredientCandidates(rec, a, countsForSort)
                .CompareTo(CountIngredientCandidates(rec, b, countsForSort)));

            foreach (int i in toolIndices)
            {
                int need = Math.Max(1, rec.Qtys[i]);
                if (!rec.Wild[i])
                {
                    int id = rec.Ids[i];
                    consumableCounts.TryGetValue(id, out int have);
                    if (have < need) return false;
                    consumableCounts[id] = have - need;
                    continue;
                }

                var keys = new List<int>(consumableCounts.Keys);
                keys.Sort();
                for (int k = 0; k < keys.Count && need > 0; k++)
                {
                    int id = keys[k];
                    if (!consumableCounts.TryGetValue(id, out int available) || available <= 0) continue;
                    if (!WildMatches(rec.Ings[i], id)) continue;
                    int take = Math.Min(need, available);
                    consumableCounts[id] = available - take;
                    need -= take;
                }
                if (need > 0) return false;
            }
            return true;
        }

        static int CountIngredientCandidates(CompactRecipe rec, int index, Dictionary<int, int> counts)
        {
            if (index < 0 || index >= rec.Ings.Length) return int.MaxValue;
            if (!rec.Wild[index]) return 1;
            int count = 0;
            foreach (var entry in counts)
                if (entry.Value > 0 && WildMatches(rec.Ings[index], entry.Key)) count++;
            return count;
        }

        static bool IngredientConsumes(CompactRecipe rec, int index)
        {
            return rec?.Ings == null || index < 0 || index >= rec.Ings.Length
                || rec.Ings[index] == null || rec.Ings[index].Consume;
        }

        static int GetToolUseLimit(CompactRecipe rec)
        {
            int limit = int.MaxValue;
            bool foundTool = false;
            if (rec?.Ings == null) return limit;

            for (int i = 0; i < rec.Ings.Length; i++)
            {
                if (IngredientConsumes(rec, i)) continue;
                foundTool = true;
                CraftingRecipeIngredient ingredient = rec.Ings[i];
                int bestForIngredient = 0;
                foreach (var sampleList in Samples.Values)
                {
                    if (sampleList == null) continue;
                    for (int s = 0; s < sampleList.Count; s++)
                    {
                        ItemStack stack = sampleList[s];
                        if (stack?.Collectible == null) continue;
                        bool matches;
                        try { matches = ingredient.SatisfiesAsIngredient(stack, false); }
                        catch { matches = false; }
                        if (!matches) continue;

                        int uses = int.MaxValue;
                        try
                        {
                            int maxDurability = stack.Collectible.GetMaxDurability(stack);
                            int cost = Math.Max(0, ingredient.ToolDurabilityCost);
                            if (maxDurability > 0 && cost > 0)
                            {
                                int remaining = Math.Max(0, stack.Collectible.GetRemainingDurability(stack));
                                uses = remaining == 0 ? 0 : 1 + (remaining - 1) / cost;
                            }
                        }
                        catch { uses = int.MaxValue; }
                        if (uses > bestForIngredient) bestForIngredient = uses;
                    }
                }
                if (bestForIngredient <= 0) return 0;
                limit = Math.Min(limit, bestForIngredient);
            }
            return foundTool ? limit : int.MaxValue;
        }

        static bool ConsumeOnce(CompactRecipe rec, Dictionary<int, int> left)
        {
            for (int i = 0; i < rec.Ids.Length; i++)
            {
                if (!IngredientConsumes(rec, i) || rec.Wild[i]) continue;
                int id = rec.Ids[i];
                left.TryGetValue(id, out int have);
                if (have < rec.Qtys[i]) return false;
                left[id] = have - rec.Qtys[i];
            }
            return ConsumeWild(rec, left);
        }

        static bool ConsumeWild(CompactRecipe rec, Dictionary<int, int> left)
        {
            var wildIndices = new List<int>();
            for (int i = 0; i < rec.Wild.Length; i++)
                if (rec.Wild[i] && IngredientConsumes(rec, i)) wildIndices.Add(i);

            // Los ingredientes comodín consumibles con menos candidatos van primero.
            wildIndices.Sort((a, b) => CountWildCandidates(rec.Ings[a], left)
                .CompareTo(CountWildCandidates(rec.Ings[b], left)));

            foreach (int ingredientIndex in wildIndices)
            {
                int need = Math.Max(1, rec.Qtys[ingredientIndex]);
                var keys = new List<int>(left.Keys);
                keys.Sort();
                for (int k = 0; k < keys.Count && need > 0; k++)
                {
                    int id = keys[k];
                    if (!left.TryGetValue(id, out int available) || available <= 0) continue;
                    if (!WildMatches(rec.Ings[ingredientIndex], id)) continue;

                    int take = Math.Min(need, available);
                    left[id] = available - take;
                    need -= take;
                }
                if (need > 0) return false;
            }
            return true;
        }

        static int CountWildCandidates(CraftingRecipeIngredient ingredient, Dictionary<int, int> counts)
        {
            int count = 0;
            foreach (var entry in counts)
            {
                if (entry.Value <= 0) continue;
                if (WildMatches(ingredient, entry.Key)) count++;
            }
            return count;
        }

        static bool WildMatches(CraftingRecipeIngredient ing, int packedId)
        {
            if (ing == null || Samples == null) return false;
            if (!Samples.TryGetValue(packedId, out var samples) || samples == null) return false;
            for (int i = 0; i < samples.Count; i++)
            {
                ItemStack stack = samples[i];
                if (stack == null) continue;
                try { if (ing.SatisfiesAsIngredient(stack, false)) return true; }
                catch { /* probar otra variante del mismo collectible */ }
            }
            return false;
        }

        void EnsureUnavailableRecipeTexture()
        {
            if (unavailableRecipeTextureLoadAttempted) return;
            unavailableRecipeTextureLoadAttempted = true;
            try
            {
                unavailableRecipeTexture = capi.Gui.LoadSvg(
                    new AssetLocation("handbookopt:textures/gui/unavailable-recipe-gradient.svg"), 64, 64, 64, 64);
                if (unavailableRecipeTexture == null || unavailableRecipeTexture.TextureId <= 0)
                {
                    capi.Logger.Warning("[HandbookOpt] No se pudo cargar el degradado de recetas no disponibles; los iconos seguirán siendo seleccionables.");
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[HandbookOpt] Error al cargar el degradado de recetas no disponibles: {0}", e.Message);
            }
        }

        void ComposeAndOpen()
        {
            // No enfocar automáticamente el buscador al abrir la ventana (la tecla E
            // que abre el inventario podía acabar escrita aquí). Si el jugador ya hizo
            // clic en el buscador, conservamos el foco durante la recomposición causada
            // por la propia búsqueda para permitir escribir varios caracteres seguidos.
            bool preserveSearchFocus = false;
            try
            {
                preserveSearchFocus = IsOpened() && SingleComposer?.GetTextInput("search")?.HasFocus == true;
            }
            catch { /* el primer compositor todavía no existe */ }

            // El selector es una ayuda para la fabricación del inventario de supervivencia.
            // Evitamos que aparezca sobre la interfaz de Creative, incluso si se invoca
            // desde un enlace del Handbook mientras ese diálogo está activo.
            if (IsCreativeInventoryOpen())
            {
                hoverTooltip?.Clear();
                if (IsOpened()) TryClose();
                return;
            }

            hoverTooltip?.Clear();
            hoveredRecipeMatchIndex = -1;
            DisposeShown();

            var invDlg = FindInventoryDialog();
            double slot = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGrid.unscaledSlotPadding;
            double gridW = Cols * slot;
            double gridH = Rows * slot;
            // El selector de mod necesita más espacio que el antiguo campo numérico.
            // En singleplayer se añade una fila opcional para pausar el mundo al consultar.
            bool showPause = HandbookOptPauseController.CanPause(capi);
            double pauseRow = showPause ? 36 : 0;
            double dlgW = Math.Max(gridW + 28, 360);
            // Altura base del selector. La extensión adicional se aplica únicamente
            // al borde inferior: conserva la posición superior y deja más espacio debajo
            // de las instrucciones para que no queden pegadas ni recortadas por el marco.
            double baseDlgH = gridH + 178 + pauseRow;
            double dlgH = baseDlgH;
            double bottomExtension = 0;

            double x = 16;
            double y = 70;
            if (invDlg?.SingleComposer != null)
            {
                var b = invDlg.SingleComposer.Bounds;
                double scale = Math.Max(0.01, RuntimeEnv.GUIScale);
                double invLeft = b.renderX / scale;
                double invTop = b.renderY / scale;
                x = invLeft - dlgW - 12;
                y = invTop;
                if (x < 8) x = 8;
            }

            // Añade hasta 28 unidades hacia abajo, sin mover el borde superior.
            // Limitamos la extensión al espacio real disponible en pantalla para no
            // hacer que el selector quede inaccesible en resoluciones bajas o con UI scale alto.
            double guiScaleForHeight = Math.Max(0.01, RuntimeEnv.GUIScale);
            double screenHeight = capi.Render.FrameHeight / guiScaleForHeight;
            const double requestedBottomExtension = 28;
            double availableBelow = Math.Max(0, screenHeight - y - baseDlgH - 8);
            bottomExtension = Math.Min(requestedBottomExtension, availableBelow);
            dlgH = baseDlgH + bottomExtension;

            ElementBounds dialogBounds = ElementBounds.Fixed(x, y, dlgW, dlgH);
            ElementBounds bg = ElementBounds.Fill;
            ElementBounds searchBounds = ElementBounds.Fixed(10, 38, 132, 28);
            ElementBounds ingredientSearchButtonBounds = ElementBounds.Fixed(148, 38, 40, 28);
            // Recorte horizontal aplicado solo al desplegable de filtros/mods ("Todos los mods");
            // el tamaño del navegador y lector de guías no se modifica.
            ElementBounds modFilterBounds = ElementBounds.Fixed(197, 38, Math.Min(148, dlgW - 202), 28);
            TryRestoreIngredientPreference();
            EnsureModFilterOptions();
            ElementBounds gridBounds = ElementBounds.Fixed((dlgW - gridW) / 2, 74 + pauseRow, gridW, gridH);
            ElementBounds inset = gridBounds.FlatCopy().FixedGrow(4).WithFixedOffset(-2, -2);
            ElementBounds prevB = ElementBounds.Fixed(10, 78 + pauseRow + gridH, 36, 26);
            ElementBounds nextB = ElementBounds.Fixed(dlgW - 46, 78 + pauseRow + gridH, 36, 26);
            // El nombre se coloca entre las flechas y dispone de altura para envolverse
            // en dos líneas si un objeto tiene un nombre largo.
            ElementBounds infoB = ElementBounds.Fixed(50, 80 + pauseRow + gridH, dlgW - 100, 44);
            // Las instrucciones ocupan dos líneas independientes. La última queda algo
            // más abajo para reservar sitio al nombre largo del objeto.
            // Las instrucciones permanecen en su posición actual; el espacio extra queda
            // debajo de ellas, alargando el panel hacia abajo sin descolocar el texto.
            ElementBounds hintFirstB = ElementBounds.Fixed(10, dlgH - bottomExtension - 51, dlgW - 20, 18);
            ElementBounds hintLastB = ElementBounds.Fixed(10, dlgH - bottomExtension - 29, dlgW - 20, 20);

            int from = page * PageSize;
            shown = new List<SkillItem>();
            shownCraftable = new List<bool>();
            int take = Math.Min(PageSize, Math.Max(0, matches.Count - from));
            bool catalogMode = IsBrowsingRecipeCatalog();
            if (catalogMode) EnsureUnavailableRecipeTexture();
            bool anyVisibleVariants = false;
            for (int i = from; i < from + take && i < matchVariantGroups.Count; i++)
            {
                if (matchVariantGroups[i]?.RecipeIndexes.Count > 1) { anyVisibleVariants = true; break; }
            }
            if (anyVisibleVariants) EnsureRecipeVariantsBadgeTexture();
            // Solo calcular la disponibilidad de los 24 iconos visibles. Evaluar miles
            // de recetas a la vez ralentizaría cada cambio de filtro.
            Dictionary<int, int> ownedForHighlight = catalogMode && take > 0 && HandbookOptMod.IndexReady
                ? CountOwned()
                : null;

            for (int i = 0; i < take; i++)
            {
                int mi = from + i;
                var rec = CraftCatalog.Recipes[matches[mi]];
                RecipeVariantDisplayGroup displayGroup = mi < matchVariantGroups.Count
                    ? matchVariantGroups[mi]
                    : null;
                int n = times[mi];
                bool isCraftable = n > 0;
                if (catalogMode && ownedForHighlight != null)
                {
                    int possible = HowMany(rec, ownedForHighlight, false);
                    isCraftable = possible > 0 && MaxFit(rec, possible) > 0;
                }
                shownCraftable.Add(isCraftable);
                var dummy = new DummySlot(rec.Output.Clone());
                if (displayGroup != null)
                {
                    displayGroup.DisplaySlot = dummy;
                    displayGroup.VisibleSkill = null;
                    displayGroup.NextSlideAt = 0;
                    displayGroup.RemainingWhenPaused = 0;
                    displayGroup.IsPaused = false;
                }
                bool hasOutputVariants = displayGroup != null && displayGroup.RecipeIndexes.Count > 1;
                bool dimUnavailable = catalogMode && !isCraftable;
                var skill = new SkillItem
                {
                    Code = rec.Output.Collectible.Code,
                    // El nombre también anuncia la variante activa cuando el tooltip estándar aparece.
                    Name = hasOutputVariants
                        ? rec.Name + " · " + HandbookOptLang.Get("recipe-variants-info-short", displayGroup.ActiveVariantIndex + 1, displayGroup.RecipeIndexes.Count)
                        : rec.Name,
                    Data = isCraftable,
                    RenderHandler = (code, dt2, px, py) =>
                    {
                        float size = (float)GuiElement.scaled(32);
                        float tileSize = (float)GuiElement.scaled(GuiElementPassiveItemSlot.unscaledSlotSize);
                        DummySlot currentSlot = displayGroup?.DisplaySlot ?? dummy;
                        capi.Render.RenderItemstackToGui(currentSlot, px + size * 0.55, py + size * 0.55, 80, size, ColorUtil.WhiteArgb, true, false, false);

                        // En el catálogo de mods se oscurecen las recetas que no pueden
                        // fabricarse con el inventario actual. El degradado se dibuja
                        // por encima del icono para atenuar tanto el fondo como el objeto.
                        // La pestaña "Disponibles" conserva el aspecto nativo.
                        if (dimUnavailable && unavailableRecipeTexture != null && unavailableRecipeTexture.TextureId > 0)
                        {
                            capi.Render.Render2DTexturePremultipliedAlpha(
                                unavailableRecipeTexture.TextureId, px - 1, py - 1, tileSize, tileSize, 90f);
                        }

                        // Insignia visible: this one tile rotates between distinct output recipes.
                        if (hasOutputVariants && recipeVariantsBadgeTexture != null && recipeVariantsBadgeTexture.TextureId > 0)
                        {
                            // Tiny yellow corner triangle: marks variant tiles without
                            // covering the output icon or changing the carousel behavior.
                            float badgeW = (float)GuiElement.scaled(14);
                            float badgeH = (float)GuiElement.scaled(14);
                            capi.Render.Render2DTexturePremultipliedAlpha(
                                recipeVariantsBadgeTexture.TextureId,
                                px + tileSize - badgeW + 1,
                                py + 1,
                                badgeW, badgeH, 95f);
                        }
                    }
                };
                if (displayGroup != null) displayGroup.VisibleSkill = skill;
                shown.Add(skill);
            }

            string info = GetStatusInfoText();

            string hintText = IsBrowsingIngredientRecipes()
                ? HandbookOptLang.Get("ingredient-search-hint", ReverseSearchIngredientName())
                : catalogMode
                    ? HandbookOptLang.Get("recipe-catalog-hint")
                    : HandbookOptLang.Get("available-recipes-hint");
            string[] hintLines = (hintText ?? string.Empty).Split(new[] { '\n' }, 2);
            string hintFirstLine = hintLines.Length > 0 ? hintLines[0] : string.Empty;
            string hintLastLine = hintLines.Length > 1 ? hintLines[1] : string.Empty;

            SingleComposer?.Dispose();
            GuiComposer composer = capi.Gui.CreateCompo("handbookopt-picker", dialogBounds)
                .AddShadedDialogBG(bg, true)
                .AddDialogTitleBar(HandbookOptLang.Get("recipe-panel-title"), OnTitleClose)
                .AddTextInput(searchBounds, OnSearch, CairoFont.WhiteSmallText(), "search")
                .AddSmallButton(HandbookOptLang.Get("ingredient-search-button"), OnIngredientSearchButton, ingredientSearchButtonBounds)
                .AddDropDown(modFilterValues, modFilterNames, SelectedModFilterIndex(), OnModFilterChanged, modFilterBounds, "modfilter");
            if (showPause)
            {
                composer.AddStaticText(HandbookOptLang.Get("pause-game"), CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(10, 82, 88, 20), "pauseGameLabel");
                composer.AddSwitch(OnPauseGameToggled, ElementBounds.Fixed(100, 78, 28, 26), "pauseGame", 24, 2);
            }
            SingleComposer = composer
                .AddInset(inset, 2)
                .AddInteractiveElement(new ModifierAwareSkillItemGrid(capi, shown, Cols, Rows, OnPick, gridBounds, shift => shiftPressedForCurrentGridClick = shift) { HoverSuppressed = IsMouseCoveredByOtherDialog }, "recipes")
                .AddSmallButton("<", OnPrev, prevB)
                .AddSmallButton(">", OnNext, nextB)
                .AddDynamicText(info, CairoFont.WhiteDetailText(), infoB, "info")
                .AddStaticText(hintFirstLine, CairoFont.WhiteDetailText(), hintFirstB)
                .AddStaticText(hintLastLine, CairoFont.WhiteDetailText(), hintLastB)
                .Compose(preserveSearchFocus);

            try
            {
                var input = SingleComposer.GetTextInput("search");
                input.SetPlaceHolderText(HandbookOptLang.Get("recipe-search-placeholder"));
                if (search.Length > 0)
                {
                    settingSearch = true;
                    input.SetValue(search);
                    settingSearch = false;
                }
                var grid = SingleComposer.GetSkillItemGrid("recipes");
                grid.OnSlotOver = OnOver;
            }
            catch { /* la grilla vacía sigue usable */ }

            if (!IsOpened()) TryOpen();
            if (IsOpened()) HandbookOptPauseController.DialogOpened(this, capi);
            SyncPauseSwitch();
        }

        void OnOver(int index)
        {
            // Un compositor oculto puede seguir notificando hover cuando el popup del
            // filtro está abierto. No conservamos una receta activa en ese caso.
            if (ingredientPickArmed)
            {
                ClearHoveredRecipe();
                return;
            }

            if (IsMouseOverModFilterPopup() || IsMouseCoveredByOtherDialog())
            {
                ClearHoveredRecipe();
                return;
            }

            if (SingleComposer == null)
            {
                hoveredRecipeMatchIndex = -1;
                hoverTooltip?.Clear();
                return;
            }

            int abs = page * PageSize + index;
            if (index < 0 || abs < 0 || abs >= matches.Count || abs >= times.Count)
            {
                hoveredRecipeMatchIndex = -1;
                hoverTooltip?.Clear();
                try { SingleComposer.GetDynamicText("info")?.SetNewText(GetStatusInfoText()); } catch { }
                return;
            }

            hoveredRecipeMatchIndex = abs;
            var rec = CraftCatalog.Recipes[matches[abs]];
            RecipeVariantDisplayGroup group = abs < matchVariantGroups.Count ? matchVariantGroups[abs] : null;
            bool canCraft = index >= 0 && index < shownCraftable.Count && shownCraftable[index];
            Dictionary<int, int> ownedCounts = CountOwned();
            int possibleBatches = HowMany(rec, ownedCounts, true);
            if (possibleBatches > 0) possibleBatches = Math.Min(possibleBatches, MaxFit(rec, possibleBatches));
            hoverTooltip?.SetRecipe(rec, matches[abs], canCraft, ownedCounts, possibleBatches);
            try
            {
                // En una entrada agrupada la línea secundaria muestra qué salida exacta
                // está seleccionada; el clic utilizará precisamente este índice.
                SingleComposer.GetDynamicText("info")?.SetNewText(RecipeVariantInfoText(rec, group));
            }
            catch { }
        }

        string GetStatusInfoText()
        {
            if (ingredientPickArmed) return HandbookOptLang.Get("ingredient-search-pick-prompt");
            if (!HandbookOptMod.IndexReady)
            {
                int pct = HandbookOptMod.IndexTotal <= 0
                    ? 0
                    : (int)(100.0 * HandbookOptMod.IndexCursor / HandbookOptMod.IndexTotal);
                return HandbookOptLang.Get("indexing-recipes-percent", pct);
            }

            if (matches.Count == 0)
            {
                if (IsBrowsingRecentRecipes()) return HandbookOptLang.Get("filter-no-recent-recipes");
                if (IsBrowsingIngredientRecipes())
                    return HandbookOptLang.Get("filter-no-recipes-use-ingredient", ReverseSearchIngredientName());
                return IsBrowsingRecipeCatalog()
                    ? HandbookOptLang.Get("filter-no-grid-recipes")
                    : HandbookOptLang.Get("nothing-craftable");
            }

            int pages = Math.Max(1, (matches.Count + PageSize - 1) / PageSize);
            return matches.Count + "  ·  " + (page + 1) + "/" + pages;
        }

        /// <summary>
        /// Abre la entrada del Handbook vanilla para el resultado que está bajo el cursor.
        /// Sigue la misma ruta que el juego: respeta proveedores de página de mods y recurre
        /// a la página estándar del objeto si no existe una entrada específica para la variante.
        /// </summary>
        public bool OpenHoveredRecipeInVanillaHandbook()
        {
            if (!IsOpened() || capi?.World?.Player == null) return false;

            try
            {
                // No convertir una H escrita en el buscador en una acción de interfaz.
                if (SingleComposer?.GetTextInput("search")?.HasFocus == true) return false;
            }
            catch { }

            if (IsMouseOverModFilterPopup())
            {
                ClearHoveredRecipe();
                return false;
            }

            GuiElementSkillItemGrid grid;
            try { grid = SingleComposer?.GetSkillItemGrid("recipes"); }
            catch { grid = null; }

            if (grid == null || !grid.IsPositionInside(capi.Input.MouseX, capi.Input.MouseY))
            {
                hoveredRecipeMatchIndex = -1;
                return false;
            }

            int abs = hoveredRecipeMatchIndex;
            if (abs < 0 || abs >= matches.Count || abs >= times.Count) return false;

            CompactRecipe recipe = CraftCatalog.Recipes[matches[abs]];
            ItemStack stack = recipe?.Output;
            if (stack?.Collectible == null) return false;

            bool handbookHotkeyHandled = false;
            try
            {
                ModSystemSurvivalHandbook handbookSystem = capi.ModLoader.GetModSystem<ModSystemSurvivalHandbook>();
                if (handbookSystem == null) return false;

                // El diálogo vanilla no tiene propiedad pública en ModSystemSurvivalHandbook.
                // Se obtiene su campo privado existente, sin modificar ni parchear el Handbook.
                FieldInfo dialogField = typeof(ModSystemSurvivalHandbook).GetField(
                    "dialog", BindingFlags.Instance | BindingFlags.NonPublic);
                GuiDialogHandbook handbook = dialogField?.GetValue(handbookSystem) as GuiDialogHandbook;
                if (handbook == null) return false;

                // Si ya está abierto, dejar que el atajo nativo conserve su comportamiento
                // habitual de alternar/cerrar el Handbook en lugar de actuar sobre la ventana oculta.
                if (handbook.IsOpened()) return false;

                // A partir de aquí gestionamos la pulsación. Si algo falla después de abrir
                // el diálogo, no dejar que el handler vanilla vuelva a alternarlo y lo cierre.
                handbookHotkeyHandled = true;
                handbook.TryOpen();
                // Igual que el handler vanilla: evita que la pulsación que acaba de abrir
                // la ventana se interprete otra vez dentro del Handbook.
                handbook.ignoreNextKeyPress = true;

                string pageCode = stack.Collectible.GetCollectibleInterface<IHandBookPageCodeProvider>()
                    ?.HandbookPageCodeForStack(capi.World, stack)
                    ?? GuiHandbookItemStackPage.PageCodeForStack(stack);

                bool opened = !string.IsNullOrWhiteSpace(pageCode) && handbook.OpenDetailPageFor(pageCode);
                if (!opened)
                {
                    // Igual que el atajo nativo: probar la página base del collectible si
                    // no existe una entrada para esta variante concreta.
                    ItemStack baseStack = new ItemStack(stack.Collectible);
                    string basePageCode = GuiHandbookItemStackPage.PageCodeForStack(baseStack);
                    opened = !string.IsNullOrWhiteSpace(basePageCode) && handbook.OpenDetailPageFor(basePageCode);
                }

                if (opened) capi.Gui.PlaySound("tick");
                else capi.Logger.Debug("[HandbookOpt] El Handbook no encontró una entrada para {0}.", stack.Collectible.Code);

                // La tecla se ha usado sobre un resultado de la cuadrícula; no dejar que
                // el handler vanilla consulte por error el ItemSlot que hubiera detrás.
                return true;
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[HandbookOpt] No se pudo abrir la entrada del Handbook para {0}: {1}", stack.Collectible.Code, e.GetBaseException().Message);
                // Si falló antes de acceder al diálogo, permite que el atajo nativo lo
                // gestione. Si ya lo estábamos abriendo, evita que se cierre por segunda vez.
                return handbookHotkeyHandled;
            }
        }

        bool settingSearch;

        void OnSearch(string text)
        {
            if (settingSearch) return;

            string incoming = text ?? string.Empty;
            if (suppressHotkeyRUntilMs > 0)
            {
                long now = capi?.ElapsedMilliseconds ?? long.MaxValue;
                bool withinHotkeyWindow = now <= suppressHotkeyRUntilMs;
                string expectedText = suppressHotkeyRExpectedText ?? string.Empty;
                string normalizedIncoming = incoming.Trim().ToLowerInvariant();
                bool isResidualHotkeyR = withinHotkeyWindow &&
                    string.Equals(normalizedIncoming, expectedText + "r", StringComparison.Ordinal);

                // Consumir la protección al primer cambio real del campo. Así no queda
                // armada y no suprime una R que el usuario escriba intencionadamente más tarde.
                suppressHotkeyRUntilMs = 0;
                suppressHotkeyRExpectedText = string.Empty;

                if (isResidualHotkeyR)
                {
                    try
                    {
                        settingSearch = true;
                        SingleComposer?.GetTextInput("search")?.SetValue(expectedText);
                    }
                    finally
                    {
                        settingSearch = false;
                    }
                    return;
                }
            }

            search = incoming.Trim().ToLowerInvariant();
            page = 0;
            lastSig = "";
            if (HandbookOptMod.IndexReady) RebuildMatches();
        }

        bool OnPrev()
        {
            if (page > 0)
            {
                page--;
                lastSig = "";
                RebuildMatches();
                SavePickerPreferences();
            }
            return true;
        }

        bool OnNext()
        {
            int pages = Math.Max(1, (matches.Count + PageSize - 1) / PageSize);
            if (page < pages - 1)
            {
                page++;
                lastSig = "";
                RebuildMatches();
                SavePickerPreferences();
            }
            return true;
        }

        void OnPick(int index)
        {
            int abs = page * PageSize + index;
            if (abs < 0 || abs >= matches.Count) return;
            int selectedRecipeIndex = matches[abs];
            var rec = CraftCatalog.Recipes[selectedRecipeIndex];

            // Class-exclusive recipes should explain the required trait before moving
            // any items into the grid. Uses the same CharacterSystem trait check as
            // Vintage Story's recipe-matching events, including extraTraits.
            if (!CheckRequiredTrait(rec)) return;
            RememberRecentRecipe(selectedRecipeIndex);

            bool fillAll = shiftPressedForCurrentGridClick;
            if (fillAll)
            {
                try { capi?.Logger?.Debug("[HandbookOpt] Vanilla Shift+left-click detected for recipe {0}; attempting maximum additional batches.", rec.Output?.Collectible?.Code); }
                catch { }
            }

            var craft = GetInv("craftinggrid");
            bool hasCraftContent = HasCraftInputs(craft);
            ItemSlot currentOutputSlot = craft != null && craft.Count > 0 ? craft[craft.Count - 1] : null;
            ItemStack currentOutput = currentOutputSlot?.Itemstack;
            if (currentOutput != null) hasCraftContent = true; // salida generada/preview

            // Un clic sobre otra receta debe CAMBIAR la receta aunque algunos de sus
            // ingredientes coincidan con los actuales en las mismas posiciones. No
            // basta con CanAccumulate(), porque dos recetas distintas pueden solaparse.
            bool selectedDifferentRecipe = lastPanelRecipeIndex >= 0
                && lastPanelRecipeIndex != selectedRecipeIndex;
            bool outputIndicatesOtherRecipe = lastPanelRecipeIndex < 0
                && currentOutput != null
                && rec.Output != null
                && !StacksEquivalent(currentOutput, rec.Output);
            bool mustSwitch = hasCraftContent && (selectedDifferentRecipe || outputIndicatesOtherRecipe);

            if (!mustSwitch && !CanAccumulate(rec, craft, out string accumulateReason))
            {
                // Diagnóstico de la cuadrícula parcial: conserva la validación estricta
                // del ingrediente, pero deja evidencia si un recipiente con atributos
                // especiales ya no satisface el ingrediente resuelto de la receta.
                try
                {
                    if (craft != null && rec?.Slots != null && rec.Ings != null)
                    {
                        for (int i = 0; i < rec.Slots.Length && i < rec.Ings.Length; i++)
                        {
                            int dest = rec.Slots[i];
                            ItemStack current = dest >= 0 && dest < craft.Count - 1 ? craft[dest]?.Itemstack : null;
                            if (current == null || IngredientMatches(rec, i, current)) continue;
                            capi?.Logger?.Debug(
                                "[HandbookOpt][BulkFill] Ingredient mismatch in staged grid: slot={0}, current={1}, count={2}, maxStack={3}, recipeItem={4}, consume={5}, reason={6}",
                                dest,
                                current.Collectible?.Code?.ToString() ?? "?",
                                current.StackSize,
                                current.Collectible?.MaxStackSize ?? 0,
                                rec.Ings[i]?.ResolvedItemStack?.Collectible?.Code?.ToString() ?? "?",
                                IngredientConsumes(rec, i),
                                accumulateReason ?? "?");
                        }
                    }
                }
                catch { }
                mustSwitch = true;
            }

            if (mustSwitch)
            {
                if (!TrySwitchRecipeAndFill(rec, RequestedTandadasPerClick, fillAll, out int switchedLoaded, out string switchError))
                {
                    capi.TriggerIngameError(this, "recipeswitch", switchError ?? HandbookOptLang.Get("error-switch-recipe"));
                }
                else
                {
                    SelectLoadedRecipeVariant(rec);
                    lastPanelRecipeIndex = selectedRecipeIndex;
                    capi.Gui.PlaySound("tick");
                    if (!fillAll && switchedLoaded < RequestedTandadasPerClick)
                        capi.TriggerIngameError(this, "partialfill", HandbookOptLang.Get("error-partial-fill", switchedLoaded, RequestedTandadasPerClick));
                }
                MarkDirty();
                return;
            }

            // Si la cuadrícula tiene ingredientes consumibles colocados, pero aún no
            // tiene salida válida ni todos los ingredientes para esas tandas, completar
            // primero la cuadrícula parcial. No exigir que una pila esté en MaxStackSize.
            if (TryGetPartialGridFillTarget(rec, craft, out int stagedTarget))
            {
                if (!Fill(rec, stagedTarget, out int completedTimes, fillToTarget: true))
                {
                    capi.TriggerIngameError(this, "nofill", HandbookOptLang.Get("error-load-incomplete"));
                }
                else
                {
                    SelectLoadedRecipeVariant(rec);
                    lastPanelRecipeIndex = selectedRecipeIndex;
                    capi.Gui.PlaySound("tick");
                    if (completedTimes < stagedTarget)
                        capi.TriggerIngameError(this, "partialfill", HandbookOptLang.Get("error-partial-fill", completedTimes, stagedTarget));
                }
                MarkDirty();
                return;
            }

            // Si la cuadrícula fue preparada manualmente y la receta no coincide,
            // la rama de arriba intenta cambiarla. Si coincide, este clic acumula.
            var counts = CountAvailableForAdditional(rec, craft);
            int available = HowMany(rec, counts, true);
            if (available > 0) available = Math.Min(available, MaxFit(rec, available));
            if (available > 0) available = Math.Min(available, MaxAdditionalFit(rec, available, craft));
            if (available <= 0)
            {
                capi.TriggerIngameError(this, "nocraft", HandbookOptLang.Get("error-not-enough-additional-materials"));
                MarkDirty();
                return;
            }

            int requested = fillAll ? available : RequestedTandadasPerClick;
            int n = Math.Min(requested, available);
            if (n <= 0) return;

            if (!Fill(rec, n, out int loaded))
            {
                capi.TriggerIngameError(this, "nofill", HandbookOptLang.Get("error-load-incomplete"));
            }
            else
            {
                SelectLoadedRecipeVariant(rec);
                lastPanelRecipeIndex = selectedRecipeIndex;
                // PullInto usa el gestor de inventario del jugador y envía al servidor
                // los paquetes devueltos por TryTransferTo.
                capi.Gui.PlaySound("tick");
                if (loaded < requested)
                    capi.TriggerIngameError(this, "partialfill", HandbookOptLang.Get("error-partial-fill", loaded, requested));
            }
            MarkDirty();
        }

        void SelectLoadedRecipeVariant(CompactRecipe recipe)
        {
            if (recipe?.Recipe == null) return;
            // After ingredients have been transferred, set the actual native matching
            // recipe on the client and then notify the server. This is intentionally not
            // a fake output icon: CraftingGridPatch revalidates recipe.Matches before
            // using the game's normal FoundMatch path on each side.
            if (!CraftingGridPatch.ApplyClientRecipeSelection(recipe.Recipe))
            {
                capi?.Logger?.Debug("[HandbookOpt] La receta {0} aún no coincide con la cuadrícula completa; se conservará la selección nativa hasta que haya ingredientes suficientes.", recipe.Recipe.Name);
            }
        }

        bool CheckRequiredTrait(CompactRecipe recipe)
        {
            string requiredTrait = recipe?.RequiresTrait;
            if (string.IsNullOrWhiteSpace(requiredTrait)) return true;

            try
            {
                IPlayer player = capi?.World?.Player;
                if (player == null) return true;

                CharacterSystem characterSystem = capi.ModLoader.GetModSystem<CharacterSystem>();
                // If character selection is unavailable (for example, another game
                // configuration removed that system), do not block crafting on guesswork.
                if (characterSystem == null) return true;
                if (characterSystem.HasTrait(player, requiredTrait)) return true;

                string traitName = null;
                try { traitName = Lang.GetIfExists("trait-" + requiredTrait); }
                catch { }
                if (string.IsNullOrWhiteSpace(traitName)) traitName = requiredTrait;

                capi.TriggerIngameError(this, "class-required", HandbookOptLang.Get("error-requires-class-trait", traitName));
                return false;
            }
            catch (Exception e)
            {
                try { capi?.Logger?.Debug("[HandbookOpt] Could not validate recipe trait '{0}'; leaving the native recipe rules in control: {1}", requiredTrait, e.Message); }
                catch { }
                return true;
            }
        }

        static bool HasCraftInputs(IInventory craft)
        {
            if (craft == null || craft.Count <= 1) return false;
            for (int i = 0; i < craft.Count - 1; i++)
                if (craft[i]?.Itemstack != null) return true;
            return false;
        }

        bool TrySwitchRecipeAndFill(CompactRecipe rec, int requestedQuantity, bool fillAll, out int loadedTimes, out string reason)
        {
            loadedTimes = 0;
            reason = HandbookOptLang.Get("reason-switch-selected-recipe");
            var craft = GetInv("craftinggrid");
            if (craft == null || craft.Count < 1 || rec == null)
            {
                reason = HandbookOptLang.Get("reason-grid-or-recipe-not-found");
                return false;
            }

            // La ranura de salida es un resultado derivado de los ingredientes.
            // No la limpiamos ni regeneramos manualmente: al retirar/cargar ingredientes,
            // TryPutInto notifica las modificaciones y la cuadrícula nativa mantiene la salida.

            var bags = new List<IInventory>(2);
            var back = GetInv("backpack");
            var hot = GetInv("hotbar");
            if (back != null) bags.Add(back);
            if (hot != null && !ReferenceEquals(hot, back)) bags.Add(hot);
            if (bags.Count == 0)
            {
                reason = HandbookOptLang.Get("reason-no-inventory-for-current-ingredients");
                return false;
            }

            try
            {
                // No escribir en el slot de salida: los movimientos de ingredientes
                // debajo disparan las notificaciones nativas de la cuadrícula.

                int inputs = Math.Max(0, craft.Count - 1);
                for (int i = 0; i < inputs; i++)
                {
                    ItemSlot source = craft[i];
                    if (source?.Itemstack == null) continue;
                    if (!ReturnToBags(source, bags))
                    {
                        reason = HandbookOptLang.Get("reason-not-enough-room-to-return-ingredients");
                        return false;
                    }
                }

                // Las retiradas anteriores notifican a la cuadrícula nativa y deben
                // haber retirado ya el resultado derivado de la receta previa.

                if (!CanAccumulate(rec, craft, out string postClearConflict))
                {
                    reason = postClearConflict ?? HandbookOptLang.Get("reason-grid-not-ready-for-new-recipe");
                    return false;
                }

                // Contar de nuevo después de devolver los materiales anteriores:
                // pueden servir para la receta nueva, pero no deben contarse dos veces.
                var counts = CountAvailableForAdditional(rec, craft);
                int available = HowMany(rec, counts, true);
                if (available > 0) available = Math.Min(available, MaxFit(rec, available));
                if (available > 0) available = Math.Min(available, MaxAdditionalFit(rec, available, craft));
                if (available <= 0)
                {
                    reason = HandbookOptLang.Get("reason-insufficient-materials-for-new-recipe");
                    return false;
                }

                int requested = fillAll ? available : Math.Max(1, requestedQuantity);
                int target = Math.Min(requested, available);
                if (target <= 0 || !Fill(rec, target, out loadedTimes) || loadedTimes <= 0)
                {
                    loadedTimes = 0;
                    reason = HandbookOptLang.Get("reason-new-recipe-load-incomplete");
                    return false;
                }

                // Fill usa transferencias del gestor del jugador para sincronizar cada movimiento.
                reason = null;
                return true;
            }
            catch (Exception e)
            {
                loadedTimes = 0;
                reason = HandbookOptLang.Get("reason-switch-recipe-failed-details", e.Message);
                try { capi.Logger.Warning("[HandbookOpt] Falló el cambio de receta tras transferencias sincronizadas; revisar cuadrícula e inventario: {0}", e.Message); } catch { }
                return false;
            }
        }

        bool TryGetPartialGridFillTarget(CompactRecipe rec, IInventory craft, out int target)
        {
            target = 0;
            if (rec?.Ings == null || rec.Slots == null || rec.Qtys == null || craft == null
                || craft.Count < 2 || rec.Ings.Length == 0
                || rec.Ings.Length != rec.Slots.Length || rec.Ings.Length != rec.Qtys.Length)
                return false;

            // Solo intervenir cuando no existe todavía una salida válida. Una receta
            // ya lista debe conservar el comportamiento normal de añadir otra tanda.
            if (craft[craft.Count - 1]?.Itemstack != null) return false;
            if (!CanAccumulate(rec, craft, out _)) return false;

            int stagedBatches = 0;
            bool hasConsumableIngredient = false;

            // La cuadrícula puede contener una pila parcial aunque esa pila NO haya
            // alcanzado MaxStackSize. Su cantidad indica cuántas tandas pretendía
            // preparar el usuario; no exigir que la pila esté llena para detectarlo.
            for (int i = 0; i < rec.Ings.Length; i++)
            {
                if (!IngredientConsumes(rec, i)) continue;
                hasConsumableIngredient = true;

                int dest = rec.Slots[i];
                ItemStack current = craft[dest]?.Itemstack;
                if (current == null || current.StackSize <= 0 || !IngredientMatches(rec, i, current)) continue;

                int perBatch = Math.Max(1, rec.Qtys[i]);
                // Redondeo hacia arriba: si una tanda requiere 2 unidades y hay 1
                // colocada, sigue habiendo una tanda parcial que puede completarse.
                int possibleInThisStack = 1 + ((current.StackSize - 1) / perBatch);
                stagedBatches = Math.Max(stagedBatches, possibleInThisStack);
            }

            if (!hasConsumableIngredient || stagedBatches <= 0) return false;

            // Solo interceptar el clic si faltan ingredientes para terminar las tandas
            // ya representadas por alguna pila existente. Si ya está completa la
            // cuadrícula, el clic conserva la función habitual de añadir más tandas.
            for (int i = 0; i < rec.Ings.Length; i++)
            {
                if (!IngredientConsumes(rec, i)) continue;
                int dest = rec.Slots[i];
                ItemStack current = craft[dest]?.Itemstack;
                long required = (long)Math.Max(1, rec.Qtys[i]) * stagedBatches;
                int present = current != null && IngredientMatches(rec, i, current)
                    ? Math.Max(0, current.StackSize)
                    : 0;
                if (present < required)
                {
                    target = stagedBatches;
                    return true;
                }
            }

            return false;
        }

        static int MaxFit(CompactRecipe rec, int want)
        {
            if (rec == null || want <= 0) return 0;
            int cap = want;
            for (int i = 0; i < rec.Ings.Length; i++)
            {
                int maxStack = int.MaxValue;
                try
                {
                    if (!rec.Wild[i])
                    {
                        var collectible = rec.Ings[i].ResolvedItemStack?.Collectible;
                        maxStack = collectible != null ? collectible.MaxStackSize : 0;
                    }
                    else
                    {
                        // Para comodines, usar un límite conservador basado en las
                        // variantes que realmente tenemos en el inventario.
                        maxStack = 64;
                        bool found = false;
                        foreach (var entry in Samples)
                        {
                            if (!WildMatches(rec.Ings[i], entry.Key) || entry.Value == null) continue;
                            for (int sampleIndex = 0; sampleIndex < entry.Value.Count; sampleIndex++)
                            {
                                ItemStack sample = entry.Value[sampleIndex];
                                if (sample?.Collectible == null) continue;
                                try { if (!rec.Ings[i].SatisfiesAsIngredient(sample, false)) continue; }
                                catch { continue; }
                                maxStack = found
                                    ? Math.Max(maxStack, sample.Collectible.MaxStackSize)
                                    : sample.Collectible.MaxStackSize;
                                found = true;
                            }
                        }
                        if (!found) maxStack = 0;
                    }
                }
                catch { maxStack = 0; }

                int perBatch = Math.Max(1, rec.Qtys[i]);
                if (maxStack < perBatch) return 0;
                if (!IngredientConsumes(rec, i)) continue;
                int slotCapacity = maxStack / perBatch;
                if (slotCapacity <= 0) return 0;
                cap = Math.Min(cap, slotCapacity);
            }
            return cap;
        }

        bool CanAccumulate(CompactRecipe rec, IInventory craft, out string reason)
        {
            reason = HandbookOptLang.Get("reason-cannot-add-without-moving-incompatible-ingredients");
            if (craft == null || rec?.Ings == null || rec.Slots == null || rec.Qtys == null
                || rec.Ings.Length == 0 || rec.Ings.Length != rec.Slots.Length || rec.Ings.Length != rec.Qtys.Length)
            {
                reason = HandbookOptLang.Get("reason-invalid-ingredient-grid");
                return false;
            }

            int inputs = Math.Max(0, craft.Count - 1);
            var destinations = new HashSet<int>();
            for (int i = 0; i < rec.Slots.Length; i++)
            {
                int dest = rec.Slots[i];
                if (dest < 0 || dest >= inputs || !destinations.Add(dest))
                {
                    reason = HandbookOptLang.Get("reason-invalid-destination-slots");
                    return false;
                }
            }

            // No vaciar ni recolocar objetos manuales en casillas ajenas a la receta.
            for (int i = 0; i < inputs; i++)
            {
                ItemStack current = craft[i]?.Itemstack;
                if (current == null || destinations.Contains(i)) continue;
                reason = HandbookOptLang.Get("reason-ingredients-outside-recipe-slots");
                return false;
            }

            for (int i = 0; i < rec.Slots.Length; i++)
            {
                ItemStack current = craft[rec.Slots[i]]?.Itemstack;
                if (current != null && !IngredientMatches(rec, i, current))
                {
                    reason = HandbookOptLang.Get("reason-slot-has-another-ingredient");
                    return false;
                }
            }
            reason = null;
            return true;
        }

        int MaxAdditionalFit(CompactRecipe rec, int want, IInventory craft)
        {
            if (rec == null || craft == null || want <= 0) return 0;
            int cap = want;
            for (int i = 0; i < rec.Ings.Length; i++)
            {
                int dest = rec.Slots[i];
                if (dest < 0 || dest >= craft.Count - 1) return 0;
                ItemStack current = craft[dest]?.Itemstack;
                int perBatch = Math.Max(1, rec.Qtys[i]);

                if (IngredientConsumes(rec, i))
                {
                    int maxStack;
                    if (current != null)
                    {
                        if (!IngredientMatches(rec, i, current)) return 0;
                        maxStack = Math.Max(1, current.Collectible.MaxStackSize);
                        int free = maxStack - current.StackSize;
                        if (free < perBatch) return 0;
                        cap = Math.Min(cap, free / perBatch);
                    }
                    else
                    {
                        maxStack = GetAvailableMaxStack(rec, i);
                        if (maxStack < perBatch) return 0;
                        cap = Math.Min(cap, maxStack / perBatch);
                    }
                }
                else
                {
                    // Una herramienta reutilizable ocupa una casilla una sola vez.
                    ItemStack tool = current ?? FindBestToolSample(rec, i);
                    if (tool == null) return 0;
                    int uses = GetToolUseLimitForStack(rec.Ings[i], tool);
                    if (uses <= 0) return 0;
                    cap = Math.Min(cap, uses);
                }
            }
            return Math.Max(0, cap);
        }

        int GetAvailableMaxStack(CompactRecipe rec, int index)
        {
            if (rec == null || index < 0 || index >= rec.Ings.Length) return 0;
            if (!rec.Wild[index])
            {
                try { return rec.Ings[index].ResolvedItemStack?.Collectible?.MaxStackSize ?? 0; }
                catch { return 0; }
            }

            int maxStack = 0;
            foreach (var sampleList in Samples.Values)
            {
                if (sampleList == null) continue;
                for (int s = 0; s < sampleList.Count; s++)
                {
                    ItemStack sample = sampleList[s];
                    if (sample?.Collectible == null || !IngredientMatches(rec, index, sample)) continue;
                    maxStack = Math.Max(maxStack, sample.Collectible.MaxStackSize);
                }
            }
            return maxStack;
        }

        ItemStack FindBestToolSample(CompactRecipe rec, int index)
        {
            if (rec == null || index < 0 || index >= rec.Ings.Length) return null;
            ItemStack best = null;
            foreach (var sampleList in Samples.Values)
            {
                if (sampleList == null) continue;
                for (int s = 0; s < sampleList.Count; s++)
                {
                    ItemStack sample = sampleList[s];
                    if (sample?.Collectible == null || !IngredientMatches(rec, index, sample)) continue;
                    if (best == null || GetRemainingDurability(sample) > GetRemainingDurability(best)) best = sample;
                }
            }
            return best;
        }

        static int GetToolUseLimitForStack(CraftingRecipeIngredient ingredient, ItemStack stack)
        {
            if (ingredient == null || stack?.Collectible == null) return 0;
            try
            {
                int maxDurability = stack.Collectible.GetMaxDurability(stack);
                int cost = Math.Max(0, ingredient.ToolDurabilityCost);
                if (maxDurability <= 0 || cost <= 0) return int.MaxValue;
                int remaining = Math.Max(0, stack.Collectible.GetRemainingDurability(stack));
                return remaining <= 0 ? 0 : 1 + (remaining - 1) / cost;
            }
            catch { return int.MaxValue; }
        }

        bool Fill(CompactRecipe rec, int times, out int loadedTimes, bool fillToTarget = false)
        {
            loadedTimes = 0;
            var craft = GetInv("craftinggrid");
            if (craft == null || times <= 0) return false;
            if (!CanAccumulate(rec, craft, out _)) return false;

            var bags = new List<IInventory>(2);
            var back = GetInv("backpack");
            var hot = GetInv("hotbar");
            if (back != null) bags.Add(back);
            if (hot != null && !ReferenceEquals(hot, back)) bags.Add(hot);

            // Se conservan las pilas que ya están en la cuadrícula. El modo normal añade
            // tandas nuevas; fillToTarget solo completa las cantidades que faltan para
            // tandas parcialmente preparadas. Todos los movimientos siguen sincronizados.
            try
            {
                int low = 1;
                int high = times;
                int chosenTimes = 0;
                int[] sourceGroups = null;
                List<SourceGroup> groups = null;
                while (low <= high)
                {
                    int mid = low + (high - low) / 2;
                    if (TryCreateFillPlan(rec, mid, bags, craft, out var candidateGroups, out var candidatePlan, fillToTarget))
                    {
                        chosenTimes = mid;
                        groups = candidateGroups;
                        sourceGroups = candidatePlan;
                        low = mid + 1;
                    }
                    else high = mid - 1;
                }

                if (chosenTimes <= 0 || groups == null || sourceGroups == null)
                {
                    return false;
                }

                for (int i = 0; i < rec.Slots.Length; i++)
                {
                    int dest = rec.Slots[i];
                    ItemSlot destination = craft[dest];
                    int needed = AdditionalNeed(rec, i, chosenTimes, destination, fillToTarget);
                    if (needed <= 0) continue;
                    int groupIndex = sourceGroups[i];
                    if (groupIndex < 0 || groupIndex >= groups.Count)
                    {
                        return false;
                    }
                    if (!PullInto(destination, groups[groupIndex].Representative, needed, bags))
                    {
                        return false;
                    }
                }

                loadedTimes = chosenTimes;
                return true;
            }
            catch (Exception e)
            {
                try { capi.Logger.Warning("[HandbookOpt] Falló la carga acumulativa después de transferencias sincronizadas; revisar cuadrícula e inventario: {0}", e.Message); } catch { }
                loadedTimes = 0;
                return false;
            }
        }

        int AdditionalNeed(CompactRecipe rec, int ingredientIndex, int times, ItemSlot destination, bool fillToTarget = false)
        {
            if (rec == null || ingredientIndex < 0 || ingredientIndex >= rec.Qtys.Length) return int.MaxValue;
            int perBatch = Math.Max(1, rec.Qtys[ingredientIndex]);
            ItemStack current = destination?.Itemstack;
            int present = current != null && IngredientMatches(rec, ingredientIndex, current) ? Math.Max(0, current.StackSize) : 0;
            if (IngredientConsumes(rec, ingredientIndex))
            {
                long total = (long)perBatch * times;
                if (fillToTarget) total = Math.Max(0L, total - present);
                return total > int.MaxValue ? int.MaxValue : (int)total;
            }
            return Math.Max(0, perBatch - present);
        }

        // No existe una restauración local por snapshots: asignar Itemstack y llamar
        // MarkDirty() en el cliente no sincroniza la modificación al servidor.

        bool TryCreateFillPlan(
            CompactRecipe rec,
            int times,
            List<IInventory> bags,
            IInventory craft,
            out List<SourceGroup> groups,
            out int[] chosenGroupByIngredient,
            bool fillToTarget)
        {
            groups = CollectSourceGroups(bags);
            List<SourceGroup> sourceGroupsForSort = groups;
            chosenGroupByIngredient = null;
            int ingredients = rec.Ings.Length;
            if (ingredients == 0 || rec.Qtys.Length != ingredients || rec.Slots.Length != ingredients || craft == null)
                return false;

            var needs = new int[ingredients];
            var candidates = new List<int>[ingredients];
            var activeOrder = new List<int>(ingredients);
            for (int i = 0; i < ingredients; i++)
            {
                int dest = rec.Slots[i];
                if (dest < 0 || dest >= craft.Count - 1) return false;
                ItemStack current = craft[dest]?.Itemstack;
                if (current != null && !IngredientMatches(rec, i, current)) return false;

                int need = AdditionalNeed(rec, i, times, craft[dest], fillToTarget);
                if (need < 0 || need == int.MaxValue) return false;
                needs[i] = need;
                candidates[i] = new List<int>();
                if (need == 0) continue;

                if (current != null && (long)current.StackSize + need > Math.Max(1, current.Collectible.MaxStackSize))
                    return false;

                for (int g = 0; g < groups.Count; g++)
                {
                    SourceGroup group = groups[g];
                    if (group.Available < need) continue;
                    if (current != null)
                    {
                        // Si la pila ya está ocupada, añadir solo una variante idéntica.
                        if (!StacksEquivalent(current, group.Representative)) continue;
                        if ((long)current.StackSize + need > Math.Max(1, current.Collectible.MaxStackSize)) continue;
                    }
                    else if (group.MaxStackSize < need) continue;

                    if (!IngredientMatches(rec, i, group.Representative)) continue;
                    candidates[i].Add(g);
                }
                if (candidates[i].Count == 0) return false;
                activeOrder.Add(i);

                if (!IngredientConsumes(rec, i))
                {
                    candidates[i].Sort((a, b) =>
                    {
                        int da = GetRemainingDurability(sourceGroupsForSort[a].Representative);
                        int db = GetRemainingDurability(sourceGroupsForSort[b].Representative);
                        return db.CompareTo(da);
                    });
                }
            }

            int[] order = activeOrder.ToArray();
            Array.Sort(order, (a, b) => candidates[a].Count.CompareTo(candidates[b].Count));
            var remaining = new int[groups.Count];
            for (int i = 0; i < groups.Count; i++) remaining[i] = groups[i].Available;
            var chosen = new int[ingredients];
            for (int i = 0; i < chosen.Length; i++) chosen[i] = -1;
            int attempts = 0;
            if (!SearchFillPlan(0, order, candidates, needs, remaining, chosen, ref attempts)) return false;
            chosenGroupByIngredient = chosen;
            return true;
        }

        bool SearchFillPlan(
            int depth,
            int[] order,
            List<int>[] candidates,
            int[] needs,
            int[] remaining,
            int[] chosen,
            ref int attempts)
        {
            if (depth >= order.Length) return true;
            if (++attempts > 20000) return false;

            int ingredientIndex = order[depth];
            List<int> options = candidates[ingredientIndex];
            for (int i = 0; i < options.Count; i++)
            {
                int groupIndex = options[i];
                if (remaining[groupIndex] < needs[ingredientIndex]) continue;

                remaining[groupIndex] -= needs[ingredientIndex];
                chosen[ingredientIndex] = groupIndex;
                if (SearchFillPlan(depth + 1, order, candidates, needs, remaining, chosen, ref attempts))
                    return true;
                chosen[ingredientIndex] = -1;
                remaining[groupIndex] += needs[ingredientIndex];
            }
            return false;
        }

        List<SourceGroup> CollectSourceGroups(List<IInventory> bags)
        {
            var groups = new List<SourceGroup>(32);
            for (int b = 0; b < bags.Count; b++)
            {
                IInventory bag = bags[b];
                if (bag == null) continue;
                for (int s = 0; s < bag.Count; s++)
                {
                    ItemStack stack = bag[s]?.Itemstack;
                    if (stack?.Collectible == null || stack.StackSize <= 0) continue;

                    int groupIndex = -1;
                    for (int g = 0; g < groups.Count; g++)
                    {
                        if (StacksEquivalent(groups[g].Representative, stack))
                        {
                            groupIndex = g;
                            break;
                        }
                    }

                    if (groupIndex >= 0)
                    {
                        groups[groupIndex].Available += stack.StackSize;
                    }
                    else
                    {
                        groups.Add(new SourceGroup
                        {
                            Representative = stack.Clone(),
                            Available = stack.StackSize,
                            MaxStackSize = Math.Max(1, stack.Collectible.MaxStackSize)
                        });
                    }
                }
            }
            return groups;
        }

        static int GetRemainingDurability(ItemStack stack)
        {
            try
            {
                if (stack?.Collectible == null || stack.Collectible.GetMaxDurability(stack) <= 0) return int.MaxValue;
                return Math.Max(0, stack.Collectible.GetRemainingDurability(stack));
            }
            catch { return int.MaxValue; }
        }

        bool StacksEquivalent(ItemStack a, ItemStack b)
        {
            if (a?.Collectible == null || b?.Collectible == null) return false;
            if (a.Collectible.Id != b.Collectible.Id || (a.Collectible is Block) != (b.Collectible is Block)) return false;
            try
            {
                // Comparar identidad/atributos, no cantidad. Tras mover parte de una pila,
                // su StackSize cambia y no debe dejar de considerarse equivalente a la fuente.
                ItemStack left = a.Clone();
                ItemStack right = b.Clone();
                left.StackSize = 1;
                right.StackSize = 1;
                return left.Equals(capi.World, right);
            }
            catch
            {
                // Fallback por tipo e ID si una implementación de un mod no admite Equals.
                return a.Collectible.Id == b.Collectible.Id
                    && (a.Collectible is Block) == (b.Collectible is Block);
            }
        }

        /// <summary>
        /// Mueve objetos entre ranuras del inventario del jugador usando la API de
        /// transferencia del cliente. TryTransferTo devuelve el paquete de inventario;
        /// enviarlo es imprescindible para que el servidor reciba el mismo movimiento.
        /// No reemplazar por ItemSlot.TryPutInto ni por MarkDirty.
        /// </summary>
        int TryTransferSynced(ItemSlot source, ItemSlot target, int quantity)
        {
            if (source?.Itemstack == null || target == null || quantity <= 0) return 0;
            if (capi?.World?.Player?.InventoryManager == null) return 0;

            var manager = capi.World.Player.InventoryManager;
            var operation = new ItemStackMoveOperation(
                capi.World,
                EnumMouseButton.Left,
                0,
                EnumMergePriority.AutoMerge,
                quantity);

            object packet = manager.TryTransferTo(source, target, ref operation);
            int moved = Math.Max(0, operation.MovedQuantity);
            if (packet != null)
            {
                capi.Network.SendPacketClient(packet);
            }
            else if (moved > 0)
            {
                // El movimiento local ya ocurrió y no debe repetirse desde otra fuente.
                // Este caso inesperado queda visible para el log y para la prueba.
                capi.Logger.Error(
                    "[HandbookOpt] InventoryManager.TryTransferTo movió {0} objetos pero no devolvió paquete de sincronización. Fuente={1}; destino={2}. Se detiene la operación; revisa inventario y cuadrícula.",
                    moved,
                    source.Inventory?.GetType().Name ?? "?",
                    target.Inventory?.GetType().Name ?? "?");
                throw new InvalidOperationException("La API movió objetos sin devolver un paquete de sincronización.");
            }

            return moved;
        }

        bool ReturnToBags(ItemSlot from, List<IInventory> bags)
        {
            int guard = 0;
            while (from.Itemstack != null && from.StackSize > 0 && guard++ < 8)
            {
                int before = from.StackSize;
                for (int b = 0; b < bags.Count && from.Itemstack != null; b++)
                {
                    var bag = bags[b];
                    for (int s = 0; s < bag.Count && from.Itemstack != null; s++)
                    {
                        var sink = bag[s];
                        if (sink.Itemstack != null && !StacksEquivalent(sink.Itemstack, from.Itemstack)) continue;
                        TryTransferSynced(from, sink, from.StackSize);
                    }
                }
                if (from.Itemstack != null && from.StackSize == before) return false;
            }
            return from.Itemstack == null;
        }

        bool PullInto(ItemSlot dest, ItemStack prototype, int qty, List<IInventory> bags)
        {
            int left = qty;
            for (int b = 0; b < bags.Count && left > 0; b++)
            {
                var bag = bags[b];
                for (int s = 0; s < bag.Count && left > 0; s++)
                {
                    var src = bag[s];
                    var stack = src?.Itemstack;
                    if (stack == null || stack.StackSize <= 0) continue;
                    if (!StacksEquivalent(stack, prototype)) continue;
                    int moved = TryTransferSynced(src, dest, Math.Min(left, stack.StackSize));
                    left -= moved;
                }
            }
            return left <= 0;
        }

        static bool IngredientMatches(CompactRecipe rec, int index, ItemStack stack)
        {
            if (stack?.Collectible == null || index < 0 || index >= rec.Ings.Length) return false;
            try
            {
                return rec.Ings[index] != null && rec.Ings[index].SatisfiesAsIngredient(stack, false);
            }
            catch
            {
                // Respaldo solo si una implementación de ingrediente de un mod no
                // expone la comprobación normal: el caso exacto conserva su ID.
                return !rec.Wild[index] && CraftCatalog.Pack(stack.Collectible) == rec.Ids[index];
            }
        }

        GuiDialog FindInventoryDialog()
        {
            var guis = capi.Gui.OpenedGuis;
            for (int i = 0; i < guis.Count; i++)
            {
                var dlg = guis[i];
                if (dlg == null || ReferenceEquals(dlg, this)) continue;
                string key = dlg.ToggleKeyCombinationCode;
                if (string.Equals(key, "inventory", StringComparison.OrdinalIgnoreCase)) return dlg;
                string name = dlg.GetType().Name;
                if (string.Equals(name, "GuiDialogInventory", StringComparison.Ordinal)) return dlg;
            }
            return null;
        }

        void OnTitleClose()
        {
            userHid = true;
            if (IsOpened()) TryClose();
        }

        void DisposeShown()
        {
            if (shown == null) return;
            for (int i = 0; i < shown.Count; i++)
            {
                try { shown[i]?.Dispose(); } catch { }
            }
            shown.Clear();
            if (shownCraftable != null) shownCraftable.Clear();
        }

        public override void Dispose()
        {
            SavePickerPreferences();
            DetachInventoryLifecycleHooks();
            inventoryCloseSuppressedUntilOpen = false;
            if (watchListener != 0)
            {
                capi.Event.UnregisterGameTickListener(watchListener);
                watchListener = 0;
            }
            DisposeShown();
            try { hoverTooltip?.Dispose(); } catch { }
            hoverTooltip = null;
            try { unavailableRecipeTexture?.Dispose(); } catch { }
            unavailableRecipeTexture = null;
            try { recipeVariantsBadgeTexture?.Dispose(); } catch { }
            recipeVariantsBadgeTexture = null;
            base.Dispose();
        }
    }
}