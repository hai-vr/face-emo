using UnityEngine;

namespace Suzuryg.FaceEmo.Detail
{
    // Object.GetInstanceID() was removed in Unity 6000.2+ in favour of EntityId.
    public static class ObjectIdUtility
    {
        public static int GetId(Object obj)
        {
#if UNITY_6000_2_OR_NEWER
            return obj.GetEntityId().GetHashCode();
#else
            return obj.GetInstanceID();
#endif
        }
    }
}
