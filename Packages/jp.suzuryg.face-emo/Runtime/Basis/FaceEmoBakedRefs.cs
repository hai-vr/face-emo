using UnityEngine;

namespace Suzuryg.FaceEmo.Baked
{
    // Written by BasisBaker onto the avatar. Holds the FE1 text and the scene objects the text indexes
    // (see design_docs/BASIS_BAKED_FORMAT.md). The fixed Cilbox script reads these.
    [DisallowMultipleComponent]
    public class FaceEmoBakedRefs : MonoBehaviour
    {
        public TextAsset Data;
        public string PropUuid;
        public SkinnedMeshRenderer[] SkinnedMeshes = new SkinnedMeshRenderer[0];
        public Transform[] Transforms = new Transform[0];
        public GameObject[] GameObjects = new GameObject[0];
        public Behaviour[] Behaviours = new Behaviour[0];
        public Renderer[] Renderers = new Renderer[0];
    }
}
