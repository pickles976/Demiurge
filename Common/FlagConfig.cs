namespace Demiurge;

public static class FlagConfig
{
    public const int NeutralTeam = 0;

    /// <summary>
    /// Whose flag is actually on the pole: the owner if there is one, and the team taking it
    /// otherwise.
    ///
    /// That is exactly the two-phase capture seen from outside. An owned flag being drained still
    /// flies its owner's colours all the way down, and only once it belongs to nobody does the
    /// attacker's start going up. Here rather than in either of the two places that draw a flag —
    /// the model on the pole and the minimap icon — because two copies of this would disagree the
    /// first time somebody changed one.
    /// </summary>
    public static int FlyingTeam(int ownerTeam, int capturingTeam)
        => ownerTeam != NeutralTeam ? ownerTeam : capturingTeam;

    /// <summary>The cloth's pole height. A pristine neutral point flies white at the top while its
    /// capture progress still correctly begins at zero.</summary>
    public static float FlyingProgress(int ownerTeam, int capturingTeam, float captureProgress)
        => ownerTeam == NeutralTeam && capturingTeam == NeutralTeam
            ? 1f
            : captureProgress;
    public const float CaptureRadius = 4f;
    public const float SpawnRadius = 2.5f;
    public const float CaptureSeconds = 10f;
    public const int MaxCapturePlayers = 4;
    // Matches the temporary ring's segment count and bounds capture traffic to about ten updates
    // per second even at the four-player capture-rate cap.
    public const int ProgressReplicationBuckets = 24;
}
