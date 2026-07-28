using Stride.Engine;
using Stride.Games;

namespace Demiurge;

public enum ClientSessionKind
{
    Runtime,
    Editor,
}

public interface IClientSession : IDisposable
{
    ClientSessionKind Kind { get; }
    void Start(Scene scene);
    void Update(GameTime time);
}
