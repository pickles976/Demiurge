namespace Demiurge.Net
{
    /// <summary>
    /// A value that can write itself to, and read itself from, a <see cref="Message"/>.
    /// </summary>
    /// <remarks>
    /// Field order IS the protocol. Append fields, never insert or reorder them, or a client built
    /// against the old order silently reads garbage rather than failing.
    /// <para>
    /// Every implementation of this interface is automatically covered by the round-trip conformance
    /// test, which enumerates implementations by reflection rather than listing them. That is
    /// deliberate: a wire type added through the RECIPES.md checklist gets coverage without anyone
    /// remembering to write a test for it.
    /// </para>
    /// </remarks>
    public interface IMessageSerializable
    {
        void Serialize(Message message);
        void Deserialize(Message message);
    }
}
