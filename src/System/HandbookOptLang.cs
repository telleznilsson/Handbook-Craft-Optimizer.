using Vintagestory.API.Config;

namespace HandbookOpt
{
    /// <summary>
    /// Centralized localization lookup for HandbookOpt UI and player-facing messages.
    /// Keys are stored under assets/handbookopt/lang/<language>.json.
    /// </summary>
    internal static class HandbookOptLang
    {
        public static string Get(string key, params object[] args)
        {
            return Lang.Get("handbookopt:" + key, args);
        }
    }
}
