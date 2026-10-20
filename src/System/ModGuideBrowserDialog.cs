using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace HandbookOpt
{
    /// <summary>
    /// Navegador de guías de mods dentro de HandbookOpt. Lee las páginas estándar
    /// de assets/*/config/handbook y las páginas de texto añadidas por código mediante
    /// OnInitCustomPages. No reemplaza ni modifica los objetos de receta del juego.
    /// </summary>
    public sealed class ModGuideBrowserDialog : GuiDialog
    {
        sealed class GuideEntry
        {
            public GuiHandbookPage Page;
            public string Code;
            public string Title;
            public string ModName;
            public string Body;
            public string RenderBody;
            public string SearchText;
        }

        // Retenemos un número muy pequeño de lectores ya compuestos. GuiElementRichtext
        // conserva su textura y sus componentes hasta que el compositor se libera, así
        // volver a una guía reciente no obliga a ejecutar otra vez Richtextify y Cairo.
        sealed class ReaderView
        {
            public GuideEntry Entry;
            public GuiComposer Composer;
            public ElementBounds ViewportBounds;
            public ElementBounds ContentBounds;
            public double ContentBaseY;
            public double ContentHeight;
            public double ViewportHeight;
            public float ScrollOffset;
        }

        const int ListRowHeight = 29;
        // Dos compositores de lectura como máximo (actual + reciente) para limitar el
        // consumo de texturas de páginas largas. La lista se mantiene aparte.
        const int MaxCachedReaderViews = 2;
        readonly List<GuideEntry> guides = new List<GuideEntry>();
        readonly Dictionary<string, GuideEntry> guidesByCode = new Dictionary<string, GuideEntry>(StringComparer.OrdinalIgnoreCase);
        readonly List<GuiHandbookPage> ownedPages = new List<GuiHandbookPage>();
        readonly HashSet<GuiHandbookPage> ownedPageSet = new HashSet<GuiHandbookPage>();
        readonly Dictionary<string, string> modNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, ReaderView> readerViews = new Dictionary<string, ReaderView>(StringComparer.OrdinalIgnoreCase);
        readonly LinkedList<string> readerLru = new LinkedList<string>();
        List<GuideEntry> filtered = new List<GuideEntry>();

        GuiComposer listComposer;
        ReaderView activeReaderView;
        long nextReaderComposerId;
        bool catalogLoaded;
        bool catalogNeedsListRebuild;
        bool settingSearch;
        string search = string.Empty;
        string pendingSearchValue = string.Empty;
        long pendingSearchCallback;
        int page;
        GuideEntry selected;
        int visibleRows = 10;

        public override string ToggleKeyCombinationCode => null;
        public override double DrawOrder => 0.23;
        public override bool PrefersUngrabbedMouse => true;
        public override bool CaptureAllInputs() => false;

        public ModGuideBrowserDialog(ICoreClientAPI capi) : base(capi)
        {
        }

        public void RegisterLinkProtocols()
        {
            // Los enlaces entre guías se redirigen aquí para que se puedan seguir sin
            // abrir el Handbook vanilla. Los enlaces a objetos se envían al catálogo
            // de recetas de HandbookOpt.
            capi.RegisterLinkProtocol("handbookoptguide", OnGuideLink);
            capi.RegisterLinkProtocol("handbookoptcraft", OnCraftLink);
        }

        public void OpenBrowser()
        {
            CancelPendingSearch();
            EnsureCatalog();
            selected = null;
            activeReaderView = null;
            RebuildFiltered();
            ShowList(rebuild: catalogNeedsListRebuild || listComposer == null);
        }

        public void ResetCatalog()
        {
            CancelPendingSearch();
            try { if (IsOpened()) TryClose(); } catch { }

            // GuiDialog.SingleComposer does not accept null safely in this game build.
            // Keep a valid composer assigned while releasing any retained reader views.
            // The list is kept temporarily and rebuilt on the next OpenBrowser call.
            if (listComposer != null && !ReferenceEquals(SingleComposer, listComposer))
            {
                try { SingleComposer = listComposer; } catch { }
            }
            activeReaderView = null;
            DisposeReaderViews();
            DisposeOwnedPages();

            guides.Clear();
            guidesByCode.Clear();
            ownedPages.Clear();
            ownedPageSet.Clear();
            modNames.Clear();
            filtered.Clear();
            catalogLoaded = false;
            catalogNeedsListRebuild = true;
            selected = null;
            search = string.Empty;
            pendingSearchValue = string.Empty;
            page = 0;
            visibleRows = 10;
        }

        void EnsureCatalog()
        {
            if (catalogLoaded || capi?.World == null) return;
            catalogLoaded = true;
            BuildModNameMap();

            var pageList = new List<GuiHandbookPage>();
            try
            {
                var assets = capi.Assets.GetMany<GuiHandbookTextPage>(capi.Logger, "config/handbook");
                foreach (var pair in assets.OrderBy(pair => pair.Key.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    GuiHandbookTextPage textPage = pair.Value;
                    if (textPage == null) continue;
                    try
                    {
                        // Reproduce el paso nativo: localizar título/texto y preparar la página
                        // antes de que los mods puedan añadir páginas mediante el evento estándar.
                        textPage.Init(capi);
                        pageList.Add(textPage);
                        TrackOwnedPage(textPage);
                    }
                    catch (Exception e)
                    {
                        capi.Logger.Warning("[HandbookOpt] No se pudo preparar una guía de {0}: {1}", pair.Key, e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[HandbookOpt] No se pudieron leer assets config/handbook: {0}", e.Message);
            }

            // La API pública permite suscribirse a OnInitCustomPages, pero el disparador
            // está dentro de ModSystemSurvivalHandbook. Se invoca el mismo método interno
            // que usa el juego, con la lista de guías JSON ya inicializada, para incluir
            // también guías creadas por código. Si la versión del juego lo cambia, la lista
            // JSON sigue disponible y el resto de HandbookOpt continúa funcionando.
            try
            {
                ModSystemSurvivalHandbook handbookSystem = capi.ModLoader.GetModSystem<ModSystemSurvivalHandbook>();
                MethodInfo trigger = AccessTools.Method(typeof(ModSystemSurvivalHandbook), "TriggerOnInitCustomPages");
                if (handbookSystem != null && trigger != null)
                {
                    int priorCount = pageList.Count;
                    trigger.Invoke(handbookSystem, new object[] { pageList });
                    for (int i = priorCount; i < pageList.Count; i++) TrackOwnedPage(pageList[i]);
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[HandbookOpt] No se pudieron consultar algunas guías añadidas por código; se conservan las guías JSON: {0}", e.GetBaseException().Message);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pageList.Count; i++)
            {
                GuiHandbookPage guidePage = pageList[i];
                if (guidePage == null) continue;

                string code;
                try
                {
                    if (!guidePage.Visible || guidePage.IsDuplicate) continue;
                    code = guidePage.PageCode;
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(code) || !seen.Add(code)) continue;

                PageText pageText;
                try { pageText = guidePage.GetPageText(); }
                catch { continue; }

                string title;
                try
                {
                    // Las páginas estándar exponen la clave Title; las páginas creadas
                    // por código suelen entregar el título ya preparado en GetPageText().
                    if (guidePage is GuiHandbookTextPage textPage)
                        title = Lang.Get(textPage.Title ?? code);
                    else
                        title = pageText.Title ?? code;
                }
                catch { title = pageText.Title ?? code; }
                if (string.IsNullOrWhiteSpace(title)) title = code;

                string domain = GetPageDomain(code);
                string modName = domain;
                if (string.Equals(domain, "game", StringComparison.OrdinalIgnoreCase))
                    modName = "Vintage Story";
                else if (modNames.TryGetValue(domain, out string resolvedModName))
                    modName = resolvedModName;

                string body = guidePage is GuiHandbookTextPage standardPage
                    ? (standardPage.Text ?? pageText.Text ?? string.Empty)
                    : (pageText.Text ?? string.Empty);
                var entry = new GuideEntry
                {
                    Page = guidePage,
                    Code = code,
                    Title = title,
                    ModName = string.IsNullOrWhiteSpace(modName) ? domain : modName,
                    Body = body,
                    SearchText = (title + " " + modName + " " + code + " " + body).ToLowerInvariant()
                };
                guides.Add(entry);
                guidesByCode[code] = entry;
            }

            guides.Sort((a, b) =>
            {
                int byTitle = string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
                if (byTitle != 0) return byTitle;
                int byMod = string.Compare(a.ModName, b.ModName, StringComparison.CurrentCultureIgnoreCase);
                return byMod != 0 ? byMod : string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase);
            });

            // Rewrite internal URLs once per catalog instead of running a regular
            // expression over the body every time the same guide is opened.
            for (int i = 0; i < guides.Count; i++)
                guides[i].RenderBody = RedirectGuideLinks(guides[i].Body ?? string.Empty);
        }

        void TrackOwnedPage(GuiHandbookPage page)
        {
            if (page != null && ownedPageSet.Add(page)) ownedPages.Add(page);
        }

        void BuildModNameMap()
        {
            modNames.Clear();
            try
            {
                foreach (var mod in capi.ModLoader.Mods)
                {
                    var info = mod?.Info;
                    string id = info?.ModID?.Trim();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    string name = string.IsNullOrWhiteSpace(info.Name) ? id : info.Name.Trim();
                    modNames[id] = name;
                }
            }
            catch { /* si ModInfo no está disponible, mostramos el dominio */ }
        }

        static string GetPageDomain(string pageCode)
        {
            if (string.IsNullOrWhiteSpace(pageCode)) return "";
            int colon = pageCode.IndexOf(':');
            return (colon > 0 ? pageCode.Substring(0, colon) : pageCode).Trim().ToLowerInvariant();
        }

        void RebuildFiltered()
        {
            filtered.Clear();
            string needle = (search ?? string.Empty).Trim().ToLowerInvariant();
            for (int i = 0; i < guides.Count; i++)
            {
                if (needle.Length == 0 || guides[i].SearchText.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    filtered.Add(guides[i]);
            }

            int pages = Math.Max(1, (filtered.Count + visibleRows - 1) / visibleRows);
            if (page >= pages) page = pages - 1;
            if (page < 0) page = 0;
        }

        void BuildListComposer(bool restoreSearchFocus = false)
        {
            try { listComposer?.Dispose(); } catch { }
            listComposer = null;

            double scale = Math.Max(0.01, RuntimeEnv.GUIScale);
            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;
            if (screenW <= 0) screenW = 1280;
            if (screenH <= 0) screenH = 720;

            bool showPause = HandbookOptPauseController.CanPause(capi);
            // Un poco más de espacio para que el control de pausa no comprima la zona
            // de búsqueda. Se mantienen al menos 8 unidades de margen por lado.
            double dlgW = Math.Max(440, Math.Min(800, screenW - 16));
            double dlgH = Math.Max(380, Math.Min(680, screenH - 16));
            double x = Math.Max(8, (screenW - dlgW) / 2);
            double y = Math.Max(8, (screenH - dlgH) / 2);
            ElementBounds dialogBounds = ElementBounds.Fixed(x, y, dlgW, dlgH);
            ElementBounds bg = ElementBounds.Fill;

            GuiComposer composer = capi.Gui.CreateCompo("handbookopt-guide-list", dialogBounds)
                .AddShadedDialogBG(bg, true)
                .AddDialogTitleBar(HandbookOptLang.Get("guides-window-title"), OnTitleClose);

            // El buscador termina en Y=66. Dejamos un espacio visible antes del
            // control de pausa y otro entre el control y la lista.
            ElementBounds searchBounds = ElementBounds.Fixed(12, 38, dlgW - 24, 28);
            composer.AddTextInput(searchBounds, OnSearch, CairoFont.WhiteSmallText(), "guideSearch");

            if (showPause)
            {
                composer.AddStaticText(HandbookOptLang.Get("pause-game"), CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(dlgW - 140, 86, 88, 20), "pauseGameLabel");
                composer.AddSwitch(OnPauseGameToggled, ElementBounds.Fixed(dlgW - 44, 82, 28, 28), "pauseGame", 24, 2);
            }

            double listY = showPause ? 122 : 74;
            double bottomY = dlgH - 48;
            visibleRows = Math.Max(4, (int)((bottomY - listY - 10) / ListRowHeight));
            int from = page * visibleRows;
            int take = Math.Min(visibleRows, Math.Max(0, filtered.Count - from));
            for (int i = 0; i < take; i++)
            {
                GuideEntry entry = filtered[from + i];
                string label = entry.Title + "   ·   " + entry.ModName;
                if (label.Length > 105) label = label.Substring(0, 102) + "…";
                ElementBounds rowBounds = ElementBounds.Fixed(12, listY + i * ListRowHeight, dlgW - 24, 26);
                composer.AddSmallButton(label, () => { OpenGuide(entry); return true; }, rowBounds, key: "guideRow" + i);
            }

            int pages = Math.Max(1, (filtered.Count + visibleRows - 1) / visibleRows);
            string info = filtered.Count == 0
                ? HandbookOptLang.Get("guides-none-found")
                : HandbookOptLang.Get("guides-page-count", filtered.Count, page + 1, pages);
            composer.AddSmallButton("<", OnPrev, ElementBounds.Fixed(12, dlgH - 44, 36, 26), key: "guidePrev");
            composer.AddSmallButton(">", OnNext, ElementBounds.Fixed(dlgW - 48, dlgH - 44, 36, 26), key: "guideNext");
            composer.AddDynamicText(info, CairoFont.WhiteDetailText(), ElementBounds.Fixed(54, dlgH - 40, dlgW - 108, 20), "guideInfo");

            listComposer = composer.Compose();
            try
            {
                var input = listComposer.GetTextInput("guideSearch");
                input.SetPlaceHolderText(HandbookOptLang.Get("guides-search-placeholder"));
                if (!string.IsNullOrEmpty(search))
                {
                    settingSearch = true;
                    input.SetValue(search);
                    settingSearch = false;
                }
                if (restoreSearchFocus) listComposer.FocusElement(input.TabIndex);
            }
            catch { }
            SyncPauseSwitch();
        }

        ReaderView BuildReaderView(GuideEntry entry)
        {
            double scale = Math.Max(0.01, RuntimeEnv.GUIScale);
            double screenW = capi.Render.FrameWidth / scale;
            double screenH = capi.Render.FrameHeight / scale;
            if (screenW <= 0) screenW = 1280;
            if (screenH <= 0) screenH = 720;

            bool showPause = HandbookOptPauseController.CanPause(capi);
            // El lector comparte la altura ligeramente mayor del navegador para dar
            // más espacio al contenido de guías largas.
            double dlgW = Math.Max(440, Math.Min(800, screenW - 16));
            double dlgH = Math.Max(380, Math.Min(680, screenH - 16));
            double x = Math.Max(8, (screenW - dlgW) / 2);
            double y = Math.Max(8, (screenH - dlgH) / 2);
            ElementBounds dialogBounds = ElementBounds.Fixed(x, y, dlgW, dlgH);
            ElementBounds bg = ElementBounds.Fill;

            string windowTitle = HandbookOptLang.Get("guide-window-title", entry.Title);
            if (windowTitle.Length > 68) windowTitle = windowTitle.Substring(0, 65) + "…";

            var view = new ReaderView { Entry = entry };
            view.ViewportBounds = ElementBounds.Fixed(12, 76, dlgW - 38, dlgH - 100);
            view.ContentBaseY = view.ViewportBounds.fixedY;
            view.ViewportHeight = view.ViewportBounds.fixedHeight;
            view.ContentHeight = view.ViewportHeight;
            view.ContentBounds = ElementBounds.Fixed(12, view.ContentBaseY, dlgW - 42, view.ViewportHeight);

            string composerId = "handbookopt-guide-reader-" + (++nextReaderComposerId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            GuiComposer composer = capi.Gui.CreateCompo(composerId, dialogBounds)
                .AddShadedDialogBG(bg, true)
                .AddDialogTitleBar(windowTitle, OnTitleClose)
                .AddSmallButton(HandbookOptLang.Get("back-to-guides"), OnBack, ElementBounds.Fixed(12, 38, 104, 26), key: "backToGuides")
                .AddStaticText(entry.ModName + "  ·  " + entry.Code,
                    CairoFont.WhiteDetailText(), ElementBounds.Fixed(102, 42, showPause ? dlgW - 250 : dlgW - 114, 22), "guideMetadata");

            composer.BeginClip(view.ViewportBounds);
            string vtml = entry.RenderBody ?? entry.Body ?? string.Empty;
            RichTextComponentBase[] richText = VtmlUtil.Richtextify(
                capi, vtml, CairoFont.WhiteSmallText().WithLineHeightMultiplier(1.2));
            composer.AddRichtext(richText, view.ContentBounds, "guideText");
            composer.EndClip();
            composer.AddVerticalScrollbar(value => OnGuideScroll(view, value),
                ElementBounds.Fixed(dlgW - 21, 76, 14, dlgH - 100), "guideScroll");

            if (showPause)
            {
                composer.AddStaticText(HandbookOptLang.Get("pause-game"), CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(dlgW - 140, 42, 88, 20), "pauseGameLabel");
                composer.AddSwitch(OnPauseGameToggled, ElementBounds.Fixed(dlgW - 44, 38, 28, 28), "pauseGame", 24, 2);
            }

            view.Composer = composer.Compose();
            try
            {
                var rich = view.Composer.GetRichtext("guideText");
                view.ContentHeight = Math.Max(view.ViewportBounds.fixedHeight, rich?.Bounds?.fixedHeight ?? view.ContentBounds.fixedHeight);
                view.ContentBounds.fixedHeight = view.ContentHeight;
                view.ViewportHeight = view.ViewportBounds.fixedHeight;
                view.ContentBounds.CalcWorldBounds();
                rich?.Bounds?.CalcWorldBounds();
                view.Composer.GetScrollbar("guideScroll")?.SetHeights((float)view.ViewportHeight, (float)view.ContentHeight);
                ApplyGuideScroll(view, view.ScrollOffset);
            }
            catch { }
            return view;
        }

        void ShowList(bool rebuild = false, bool restoreSearchFocus = false)
        {
            selected = null;
            activeReaderView = null;
            if (rebuild || listComposer == null)
            {
                BuildListComposer(restoreSearchFocus);
                catalogNeedsListRebuild = false;
            }
            SingleComposer = listComposer;
            if (!IsOpened()) TryOpen();
            if (IsOpened()) HandbookOptPauseController.DialogOpened(this, capi);
            SyncPauseSwitch();
        }

        ReaderView GetReaderView(GuideEntry entry)
        {
            if (readerViews.TryGetValue(entry.Code, out ReaderView existing))
            {
                TouchReader(entry.Code);
                return existing;
            }

            ReaderView created = BuildReaderView(entry);
            readerViews[entry.Code] = created;
            TouchReader(entry.Code);
            return created;
        }

        void ShowReader(ReaderView view)
        {
            if (view == null) return;
            selected = view.Entry;
            activeReaderView = view;
            // Cada vista conserva su propia posición y su barra, incluso al volver a ella.
            ApplyGuideScroll(view, view.ScrollOffset);
            SingleComposer = view.Composer;
            if (!IsOpened()) TryOpen();
            if (IsOpened()) HandbookOptPauseController.DialogOpened(this, capi);
            SyncPauseSwitch();
            TrimReaderCache();
        }

        void TouchReader(string code)
        {
            LinkedListNode<string> node = readerLru.First;
            while (node != null)
            {
                LinkedListNode<string> next = node.Next;
                if (string.Equals(node.Value, code, StringComparison.OrdinalIgnoreCase)) readerLru.Remove(node);
                node = next;
            }
            readerLru.AddLast(code);
        }

        void TrimReaderCache()
        {
            while (readerViews.Count > MaxCachedReaderViews && readerLru.First != null)
            {
                string candidate = readerLru.First.Value;
                readerLru.RemoveFirst();
                if (activeReaderView != null && string.Equals(candidate, activeReaderView.Entry.Code, StringComparison.OrdinalIgnoreCase))
                {
                    readerLru.AddLast(candidate);
                    continue;
                }
                if (readerViews.TryGetValue(candidate, out ReaderView oldView))
                {
                    readerViews.Remove(candidate);
                    try { oldView.Composer?.Dispose(); } catch { }
                }
            }
        }

        void DisposeReaderViews()
        {
            foreach (ReaderView view in readerViews.Values)
            {
                try { view.Composer?.Dispose(); } catch { }
            }
            readerViews.Clear();
            readerLru.Clear();
        }

        void OnPauseGameToggled(bool value)
        {
            HandbookOptPauseController.SetEnabled(capi, value);
        }

        public void SyncPauseSwitch()
        {
            if (!HandbookOptPauseController.CanPause(capi)) return;
            bool enabled = HandbookOptPauseController.Enabled;
            try { listComposer?.GetSwitch("pauseGame")?.SetValue(enabled); } catch { }
            foreach (ReaderView view in readerViews.Values)
            {
                try { view.Composer?.GetSwitch("pauseGame")?.SetValue(enabled); } catch { }
            }
        }

        string RedirectGuideLinks(string vtml)
        {
            if (string.IsNullOrEmpty(vtml) || guidesByCode.Count == 0) return vtml ?? string.Empty;
            return Regex.Replace(vtml, @"(?i)(href\s*=\s*[""'])handbook://([^""']+)([""'])", match =>
            {
                string target = match.Groups[2].Value;
                if (guidesByCode.ContainsKey(target))
                    return match.Groups[1].Value + "handbookoptguide://" + target + match.Groups[3].Value;
                if (target.StartsWith("item-", StringComparison.OrdinalIgnoreCase) || target.StartsWith("block-", StringComparison.OrdinalIgnoreCase))
                    return match.Groups[1].Value + "handbookoptcraft://" + target + match.Groups[3].Value;
                return match.Value;
            });
        }

        void OnGuideLink(LinkTextComponent component)
        {
            if (component == null) return;
            const string prefix = "handbookoptguide://";
            string href = component.Href ?? string.Empty;
            string target = href.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? href.Substring(prefix.Length) : href;
            if (guidesByCode.TryGetValue(target, out GuideEntry entry)) OpenGuide(entry);
        }

        void OnCraftLink(LinkTextComponent component)
        {
            if (component == null) return;
            string href = component.Href ?? string.Empty;
            const string prefix = "handbookoptcraft://";
            string target = href.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? href.Substring(prefix.Length) : href;
            string code = target;
            if (code.StartsWith("item-", StringComparison.OrdinalIgnoreCase)) code = code.Substring(5);
            else if (code.StartsWith("block-", StringComparison.OrdinalIgnoreCase)) code = code.Substring(6);

            try { TryClose(); } catch { }
            HandbookOptMod.Picker?.BrowseOutputCode(code);
        }

        void OpenGuide(GuideEntry entry)
        {
            if (entry == null) return;
            bool hadPendingSearch = pendingSearchCallback != 0;
            CommitPendingSearch();
            // Si se abrió una guía antes de vencer el debounce, dejamos lista la lista
            // filtrada mientras el usuario lee; el botón Atrás podrá reutilizarla.
            if (hadPendingSearch) BuildListComposer();

            ReaderView view = GetReaderView(entry);
            // Si ya estaba en caché, se conserva también su posición de lectura y la
            // posición asociada de la barra. Las guías nuevas empiezan desde arriba.
            ShowReader(view);
        }

        bool OnBack()
        {
            ShowList();
            return true;
        }

        void OnSearch(string value)
        {
            if (settingSearch) return;
            pendingSearchValue = value ?? string.Empty;
            if (pendingSearchCallback != 0)
            {
                try { capi.Event.UnregisterCallback(pendingSearchCallback); } catch { }
                pendingSearchCallback = 0;
            }

            // The previous implementation rebuilt every row and recreated the dialog
            // for every keystroke. Apply the latest search after a short quiet period.
            // This callback is explicitly allowed while paused so local pause remains usable.
            pendingSearchCallback = capi.Event.RegisterCallback(_ =>
            {
                pendingSearchCallback = 0;
                if (!IsOpened() || selected != null || capi?.World == null) return;
                ApplyPendingSearch();
                BuildListComposer(true);
                ShowList();
            }, 160, true);
        }

        void ApplyPendingSearch()
        {
            search = (pendingSearchValue ?? string.Empty).Trim();
            page = 0;
            RebuildFiltered();
        }

        void CommitPendingSearch()
        {
            if (pendingSearchCallback == 0) return;
            try { capi.Event.UnregisterCallback(pendingSearchCallback); } catch { }
            pendingSearchCallback = 0;
            if (!string.Equals(search, (pendingSearchValue ?? string.Empty).Trim(), StringComparison.Ordinal))
                ApplyPendingSearch();
        }

        void CancelPendingSearch()
        {
            if (pendingSearchCallback != 0)
            {
                try { capi?.Event?.UnregisterCallback(pendingSearchCallback); } catch { }
                pendingSearchCallback = 0;
            }
        }

        bool OnPrev()
        {
            CommitPendingSearch();
            if (page > 0)
            {
                page--;
                BuildListComposer();
                ShowList();
            }
            return true;
        }

        bool OnNext()
        {
            CommitPendingSearch();
            int pages = Math.Max(1, (filtered.Count + visibleRows - 1) / visibleRows);
            if (page < pages - 1)
            {
                page++;
                BuildListComposer();
                ShowList();
            }
            return true;
        }

        void OnGuideScroll(ReaderView view, float value)
        {
            if (view?.ContentBounds == null || view.ViewportBounds == null) return;
            double maxOffset = Math.Max(0, view.ContentHeight - view.ViewportHeight);
            // GuiElementScrollbar entrega CurrentYPosition como desplazamiento absoluto
            // (0..contenido-visible), no como fracción normalizada 0..1.
            view.ScrollOffset = (float)Math.Max(0, Math.Min(maxOffset, value));
            ApplyGuideScroll(view, view.ScrollOffset);
        }

        void ApplyGuideScroll(ReaderView view, float offset)
        {
            if (view?.ContentBounds == null || view.ViewportBounds == null) return;
            double maxOffset = Math.Max(0, view.ContentHeight - view.ViewportHeight);
            double clampedOffset = Math.Max(0, Math.Min(maxOffset, offset));
            double newY = view.ContentBaseY - clampedOffset;

            try
            {
                // Ajustamos el Bounds real del texto de ESTA página. Como los lectores
                // recientes se conservan, cada uno mantiene correctamente su posición.
                var rich = view.Composer?.GetRichtext("guideText");
                ElementBounds bounds = rich?.Bounds ?? view.ContentBounds;
                bounds.fixedY = newY;
                bounds.CalcWorldBounds();
                if (!ReferenceEquals(bounds, view.ContentBounds)) view.ContentBounds = bounds;
            }
            catch
            {
                view.ContentBounds.fixedY = newY;
                view.ContentBounds.CalcWorldBounds();
            }
        }

        void OnTitleClose() => TryClose();

        public override void OnGuiClosed()
        {
            CancelPendingSearch();
            base.OnGuiClosed();
            HandbookOptPauseController.DialogClosed(this, capi);

            // Al cerrar el navegador, libera las texturas grandes retenidas por el
            // pequeño caché de lectura. Solo se conservan mientras se navega.
            if (listComposer != null) SingleComposer = listComposer;
            activeReaderView = null;
            selected = null;
            DisposeReaderViews();
        }

        void DisposeOwnedPages()
        {
            for (int i = 0; i < ownedPages.Count; i++)
            {
                GuiHandbookPage page = ownedPages[i];
                if (page == null) continue;
                try
                {
                    // Las páginas de texto generan componentes VTML en Init(). El visor usa
                    // su propio texto enriquecido, así que liberamos también ese caché aparte
                    // de la textura de título de cada página.
                    FieldInfo componentsField = AccessTools.Field(page.GetType(), "comps")
                        ?? AccessTools.Field(typeof(GuiHandbookTextPage), "comps");
                    if (componentsField?.GetValue(page) is RichTextComponentBase[] components)
                    {
                        for (int j = 0; j < components.Length; j++)
                        {
                            try { components[j]?.Dispose(); } catch { }
                        }
                    }
                }
                catch { }
                try { page.Dispose(); } catch { }
            }
        }

        public override void Dispose()
        {
            CancelPendingSearch();
            try { if (IsOpened()) TryClose(); } catch { }

            // Do not call ResetCatalog here: during GuiManager.Dispose the dialog is
            // already being torn down, and assigning SingleComposer = null throws in
            // Vintage Story 1.22.7. Keep the live list composer assigned so base.Dispose
            // can release it normally; reader composers and owned source pages are ours.
            if (listComposer != null && !ReferenceEquals(SingleComposer, listComposer))
            {
                try { SingleComposer = listComposer; } catch { }
            }

            activeReaderView = null;
            DisposeReaderViews();
            DisposeOwnedPages();
            guides.Clear();
            guidesByCode.Clear();
            ownedPages.Clear();
            ownedPageSet.Clear();
            modNames.Clear();
            filtered.Clear();
            selected = null;

            // If the list composer is the dialog's current composer, base.Dispose owns
            // its final disposal. Dispose it ourselves only if it is not the current one.
            if (listComposer != null && !ReferenceEquals(SingleComposer, listComposer))
            {
                try { listComposer.Dispose(); } catch { }
            }
            listComposer = null;
            base.Dispose();
        }
    }
}
