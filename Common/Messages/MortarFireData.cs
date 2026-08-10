using System.Numerics;
using Demiurge.Net;

namespace Demiurge;

/// <summary>
/// "Drop one there." A mortar is laid on a place rather than pointed along a line, so the request
/// carries the target point and nothing else — no origin, no direction. The server knows which
/// mortar the man is on (he cannot move while operating it), so it needs only the where.
///
/// The point is still validated: MortarBallistics.IsLegalTarget re-derives the sector and range
/// from the emplacement the server has, so a fabricated request cannot reach outside them.
/// </summary>
public struct MortarFireData : IMessageSerializable
{
    public Vector3 Target;

    public void Serialize(Message message) => message.AddVector3(Target);

    public void Deserialize(Message message) => Target = message.GetVector3();
}
