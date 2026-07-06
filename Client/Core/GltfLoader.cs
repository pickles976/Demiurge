

using Stride.Engine;
using Stride.Rendering;

namespace Demiurge.GameClient
{

    public class GLTFLoader {
        public static Model LoadModel(Game game, string gltfPath)
        {
            var contentPath = Path.ChangeExtension(Path.GetRelativePath("assets", gltfPath), null);
            return game.Content.Load<Model>(contentPath);
        }
    }

}