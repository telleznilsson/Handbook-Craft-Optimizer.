using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace HandbookOpt
{
    /// <summary>
    /// Incremental, client-thread search index for the vanilla Handbook pages.
    /// It is deliberately isolated from CraftCatalog and all crafting-grid indexes.
    /// </summary>
    internal static class VanillaHandbookSearchIndex
    {
        private const int TickIntervalMs = 33;
        private const double TickBudgetMs = 2.0;
        private const int MaxPagesPerTick = 48;
        // Very long/modded pages stay in the fallback set so building an index can never
        // spend a long uninterrupted period processing a single text body.
        private const int MaxIndexedTextCharsPerPage = 6000;

        private static readonly FieldInfo LoadingField = AccessTools.Field(typeof(GuiDialogHandbook), "loadingPagesAsync");
        private static readonly FieldInfo AllPagesField = AccessTools.Field(typeof(GuiDialogHandbook), "allHandbookPages");
        private static HashSet<ulong> ScratchGrams = new HashSet<ulong>();

        private static ICoreClientAPI api;
        private static GuiDialogHandbook dialog;
        private static long tickListener;
        private static List<GuiHandbookPage> sourceList;
        private static GuiHandbookPage[] sourceSnapshot;
        private static List<IndexedPage> entries;
        private static List<int> fallbackEntryIndices;
        private static Dictionary<ulong, int> trigramFrequency;
        private static Dictionary<ulong, List<int>> trigramPostings;
        private static int cursor;
        private static int commonTrigramLimit;
        private static bool ready;
        private static bool failed;
        private static int pagesWithTextErrors;
        private static BuildStage stage;

        private enum BuildStage
        {
            None,
            CaptureText,
            CountTrigrams,
            BuildPostings
        }

        internal sealed class IndexedPage
        {
            public GuiHandbookPage Page;
            public string CategoryCode;
            public string Title;
            public string Body;
            public bool TextCaptured;
        }

        /// <summary>Start incremental indexing once vanilla has begun filtering pages.</summary>
        public static void EnsurePolling(ICoreClientAPI clientApi, GuiDialogHandbook handbook, List<GuiHandbookPage> allPages)
        {
            if (clientApi == null || handbook == null) return;

            if (!ReferenceEquals(api, clientApi))
            {
                Reset();
                api = clientApi;
                try { api.Event.LeaveWorld += OnLeaveWorld; }
                catch { }
            }

            dialog = handbook;
            if (allPages != null && IsSameReadySource(allPages)) return;
            if (allPages != null && failed && ReferenceEquals(sourceList, allPages) && sourceSnapshot != null && sourceSnapshot.Length == allPages.Count)
                return;

            EnsureTickListener();
        }

        public static bool TryGetEntries(List<GuiHandbookPage> allPages, out List<IndexedPage> indexedEntries)
        {
            indexedEntries = null;
            if (!ready || sourceSnapshot == null || entries == null || allPages == null) return false;
            if (!ReferenceEquals(sourceList, allPages) || sourceSnapshot.Length != allPages.Count) return false;
            indexedEntries = entries;
            return true;
        }

        /// <summary>
        /// Returns the rarest posting list needed to verify an ASCII substring query.
        /// If no selective trigram exists, the caller scans the cached text; if a query
        /// trigram never occurs in indexed pages, only unindexed fallback pages need checking.
        /// </summary>
        public static List<int> GetCandidateIndices(string needle, out bool scanAll, out bool fallbackOnly)
        {
            scanAll = true;
            fallbackOnly = false;
            if (!ready || string.IsNullOrEmpty(needle) || needle.Length < 3 || !IsSafeAsciiQuery(needle)) return null;

            List<int> best = null;
            for (int i = 0; i <= needle.Length - 3; i++)
            {
                ulong gram = PackTrigram(needle[i], needle[i + 1], needle[i + 2]);
                if (!trigramFrequency.ContainsKey(gram))
                {
                    scanAll = false;
                    fallbackOnly = true;
                    return null;
                }

                if (trigramPostings.TryGetValue(gram, out List<int> posting) && (best == null || posting.Count < best.Count))
                    best = posting;
            }

            if (best != null)
            {
                scanAll = false;
                fallbackOnly = false;
                return best;
            }

            // Every query trigram exists but all are common, so a cached-text scan is cheaper
            // and safer than building a large candidate union.
            scanAll = true;
            fallbackOnly = false;
            return null;
        }

        public static List<int> FallbackEntryIndices => fallbackEntryIndices;

        private static bool IsSameReadySource(List<GuiHandbookPage> allPages)
        {
            return ready && sourceSnapshot != null && ReferenceEquals(sourceList, allPages) && sourceSnapshot.Length == allPages.Count;
        }

        private static void EnsureTickListener()
        {
            if (tickListener != 0 || api == null) return;
            try { tickListener = api.Event.RegisterGameTickListener(OnTick, TickIntervalMs); }
            catch (Exception e)
            {
                tickListener = 0;
                api?.Logger?.Debug("[HandbookOpt][VanillaSearchIndex] No se pudo iniciar el indexado progresivo: {0}", e.Message);
            }
        }

        private static void OnTick(float dt)
        {
            try
            {
                if (api == null || dialog == null || LoadingField == null || AllPagesField == null)
                {
                    StopTickListener();
                    return;
                }

                if (LoadingField.GetValue(dialog) is bool loading && loading) return;
                var allPages = AllPagesField.GetValue(dialog) as List<GuiHandbookPage>;
                if (allPages == null) return;

                if (IsSameReadySource(allPages))
                {
                    StopTickListener();
                    return;
                }

                if (failed && ReferenceEquals(sourceList, allPages) && sourceSnapshot != null && sourceSnapshot.Length == allPages.Count)
                {
                    StopTickListener();
                    return;
                }

                // If the source list was replaced or changed size, take a new main-thread snapshot.
                if (sourceSnapshot == null || !ReferenceEquals(sourceList, allPages) || sourceSnapshot.Length != allPages.Count)
                    BeginBuild(allPages);

                if (sourceSnapshot == null || entries == null) return;

                long started = Stopwatch.GetTimestamp();
                int processed = 0;
                while (processed < MaxPagesPerTick)
                {
                    if (stage == BuildStage.CaptureText)
                    {
                        if (cursor >= sourceSnapshot.Length)
                        {
                            cursor = 0;
                            stage = BuildStage.CountTrigrams;
                            continue;
                        }

                        GuiHandbookPage page = sourceSnapshot[cursor++];
                        processed++;
                        if (page != null && !page.IsDuplicate)
                        {
                            var entry = new IndexedPage
                            {
                                Page = page,
                                CategoryCode = page.CategoryCode,
                                Title = string.Empty,
                                Body = string.Empty,
                                TextCaptured = false
                            };

                            try
                            {
                                var pageText = page.GetPageText();
                                entry.Title = pageText.Title ?? string.Empty;
                                entry.Body = pageText.Text ?? string.Empty;
                                entry.TextCaptured = true;
                            }
                            catch
                            {
                                // A bad mod page falls back to GetPageText during a query;
                                // it must not disable the optimized path for every other page.
                                pagesWithTextErrors++;
                            }

                            entries.Add(entry);
                        }
                    }
                    else if (stage == BuildStage.CountTrigrams)
                    {
                        if (cursor >= entries.Count)
                        {
                            commonTrigramLimit = Math.Max(8, Math.Min(256, entries.Count / 5));
                            trigramPostings = new Dictionary<ulong, List<int>>();
                            cursor = 0;
                            stage = BuildStage.BuildPostings;
                            continue;
                        }

                        int entryIndex = cursor++;
                        processed++;
                        IndexedPage entry = entries[entryIndex];
                        if (!CanIndexText(entry))
                        {
                            fallbackEntryIndices.Add(entryIndex);
                        }
                        else
                        {
                            CollectPageTrigrams(entry);
                            foreach (ulong gram in ScratchGrams)
                            {
                                trigramFrequency.TryGetValue(gram, out int count);
                                trigramFrequency[gram] = count + 1;
                            }
                        }
                    }
                    else if (stage == BuildStage.BuildPostings)
                    {
                        if (cursor >= entries.Count)
                        {
                            CompleteBuild();
                            return;
                        }

                        int entryIndex = cursor++;
                        processed++;
                        IndexedPage entry = entries[entryIndex];
                        if (CanIndexText(entry))
                        {
                            CollectPageTrigrams(entry);
                            foreach (ulong gram in ScratchGrams)
                            {
                                if (!trigramFrequency.TryGetValue(gram, out int frequency) || frequency > commonTrigramLimit) continue;
                                if (!trigramPostings.TryGetValue(gram, out List<int> posting))
                                {
                                    posting = new List<int>(frequency);
                                    trigramPostings.Add(gram, posting);
                                }
                                posting.Add(entryIndex);
                            }
                        }
                    }
                    else
                    {
                        StopTickListener();
                        return;
                    }

                    double elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    if (processed > 0 && elapsedMs >= TickBudgetMs) break;
                }
            }
            catch (Exception e)
            {
                // All advanced filtering remains optional: on an index-build failure, the
                // existing Handbook filter continues to run without this acceleration.
                failed = true;
                ready = false;
                entries = null;
                trigramFrequency = null;
                trigramPostings = null;
                fallbackEntryIndices = null;
                stage = BuildStage.None;
                api?.Logger?.Warning("[HandbookOpt][VanillaSearchIndex] Indexado desactivado; se conserva el filtro de respaldo: {0}", e.GetBaseException().Message);
                StopTickListener();
            }
        }

        private static void BeginBuild(List<GuiHandbookPage> allPages)
        {
            sourceList = allPages;
            sourceSnapshot = allPages.ToArray();
            entries = new List<IndexedPage>(sourceSnapshot.Length);
            fallbackEntryIndices = new List<int>();
            trigramFrequency = new Dictionary<ulong, int>();
            trigramPostings = null;
            cursor = 0;
            commonTrigramLimit = 0;
            ready = false;
            failed = false;
            pagesWithTextErrors = 0;
            stage = BuildStage.CaptureText;
        }

        private static bool CanIndexText(IndexedPage entry)
        {
            if (entry == null || !entry.TextCaptured) return false;
            return (entry.Title?.Length ?? 0) + (entry.Body?.Length ?? 0) <= MaxIndexedTextCharsPerPage;
        }

        private static void CollectPageTrigrams(IndexedPage entry)
        {
            ScratchGrams.Clear();
            AddTrigrams(entry.Title);
            AddTrigrams(entry.Body);
        }

        private static void AddTrigrams(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 3) return;
            for (int i = 0; i <= text.Length - 3; i++)
                ScratchGrams.Add(PackTrigram(text[i], text[i + 1], text[i + 2]));
        }

        private static ulong PackTrigram(char a, char b, char c)
        {
            // Ordinal-ignore-case style folding, culture independent. The candidate index
            // is used only for ASCII search needles; non-ASCII queries take the full cached scan.
            a = char.ToUpperInvariant(a);
            b = char.ToUpperInvariant(b);
            c = char.ToUpperInvariant(c);
            return ((ulong)a << 32) | ((ulong)b << 16) | c;
        }

        private static bool IsSafeAsciiQuery(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 32 || c > 126) return false;
            }
            return true;
        }

        private static void CompleteBuild()
        {
            var latestPages = AllPagesField?.GetValue(dialog) as List<GuiHandbookPage>;
            if (!ReferenceEquals(sourceList, latestPages) || latestPages == null || latestPages.Count != sourceSnapshot.Length)
            {
                sourceSnapshot = null;
                entries = null;
                trigramFrequency = null;
                trigramPostings = null;
                fallbackEntryIndices = null;
                ready = false;
                failed = false;
                cursor = 0;
                stage = BuildStage.None;
                EnsureTickListener();
                return;
            }

            ready = true;
            failed = false;
            stage = BuildStage.None;
            StopTickListener();
            api?.Logger?.Notification(
                "[HandbookOpt][VanillaSearchIndex] Índice listo: páginas={0}, candidatas indexadas={1}, trigramas únicos={2}, páginas de respaldo={3}, errores al leer texto={4}.",
                sourceSnapshot.Length, entries.Count - fallbackEntryIndices.Count, trigramFrequency.Count, fallbackEntryIndices.Count, pagesWithTextErrors);
        }

        private static void StopTickListener()
        {
            if (tickListener == 0) return;
            try { api?.Event?.UnregisterGameTickListener(tickListener); }
            catch { }
            tickListener = 0;
        }

        private static void OnLeaveWorld()
        {
            Reset();
        }

        internal static void Reset()
        {
            StopTickListener();
            try { if (api != null) api.Event.LeaveWorld -= OnLeaveWorld; }
            catch { }
            api = null;
            dialog = null;
            sourceList = null;
            sourceSnapshot = null;
            entries = null;
            fallbackEntryIndices = null;
            trigramFrequency = null;
            trigramPostings = null;
            cursor = 0;
            commonTrigramLimit = 0;
            ready = false;
            failed = false;
            pagesWithTextErrors = 0;
            stage = BuildStage.None;
            ScratchGrams = new HashSet<ulong>();
        }
    }
}
