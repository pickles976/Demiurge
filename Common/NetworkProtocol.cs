namespace Demiurge
{
    public static class NetworkConfig
    {
        public const ushort Port = 7777;
    }

    // One enum per direction. The ushort value IS the wire protocol —
    // if client and server disagree on these numbers, handlers silently never fire.
    public enum ServerToClientId : ushort
    {
        Welcome = 1,
    }

    public enum ClientToServerId : ushort
    {
        // empty until Phase 3 (Input)
    }
}