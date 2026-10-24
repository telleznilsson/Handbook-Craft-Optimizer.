using ProtoBuf;

namespace HandbookOpt
{
    /// <summary>
    /// Synchronizes the explicit recipe selection. ProtocolVersion guards the full asset-name
    /// selector plus validated identity fallbacks.
    /// </summary>
    [ProtoContract]
    public sealed class RecipeVariantSelectionPacket
    {
        [ProtoMember(1)]
        public string RecipeKey;

        [ProtoMember(2)]
        public int ProtocolVersion;

        // Secondary fingerprint is conservative and excludes tag conditions entirely.
        [ProtoMember(3)]
        public string FallbackRecipeKey;

        // Full domain-qualified GridRecipe asset name; server validates it against its live grid.
        [ProtoMember(4)]
        public string RecipeAssetKey;
    }
}
