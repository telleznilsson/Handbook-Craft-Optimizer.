namespace HandbookOpt
{
    /// <summary>
    /// Local client preferences for the recipe picker. This stores only UI state;
    /// recipe catalogs and indexes remain runtime-only.
    /// </summary>
    public sealed class CraftPickerPreferences
    {
        public int Version = 1;
        public string Search = string.Empty;
        public string SelectedFilter = "__available";
        public int Page;

        // The selected ingredient is serialized by ItemStack.ToBytes so its stack
        // attributes are retained for mods whose ingredient matcher depends on them.
        public string ReverseIngredientData = string.Empty;
        public string ReverseIngredientCode = string.Empty;
    }
}
