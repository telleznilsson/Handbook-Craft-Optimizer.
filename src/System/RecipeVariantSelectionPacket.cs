using ProtoBuf;

namespace HandbookOpt
{
    /// <summary>
    /// Synchronizes the recipe currently displayed by the crafting-result carousel.
    /// The server validates the key against the player's live crafting grid before use.
    /// </summary>
    [ProtoContract]
    public sealed class RecipeVariantSelectionPacket
    {
        [ProtoMember(1)]
        public string RecipeKey;
    }
}
