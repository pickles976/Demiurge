namespace Demiurge;

public static class FlagConfig
{
    public const int NeutralTeam = 0;
    public const float CaptureRadius = 4f;
    public const float SpawnRadius = 2.5f;
    public const float CaptureSeconds = 10f;
    public const int MaxCapturePlayers = 4;
    // Matches the temporary ring's segment count and bounds capture traffic to about ten updates
    // per second even at the four-player capture-rate cap.
    public const int ProgressReplicationBuckets = 24;
}
