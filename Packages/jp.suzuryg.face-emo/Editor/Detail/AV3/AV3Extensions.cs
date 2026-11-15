using Basis.Scripts.BasisSdk;
using UnityEngine;

namespace Suzuryg.FaceEmo.Detail.AV3
{
    internal static class AV3Extensions
    {
        // Eye position in the avatar's local space, divided by the root scale.
        // BasisAvatar.AvatarEyePosition = (x: height above the animator root, y: forward offset).
        public static Vector3 GetScaledViewPosition(this BasisAvatar avatar)
        {
            if (avatar == null || avatar.gameObject == null || avatar.gameObject.transform == null)
            {
                return Vector3.zero;
            }

            var viewPosition = new Vector3(0, avatar.AvatarEyePosition.x, avatar.AvatarEyePosition.y);
            var scale = avatar.gameObject.transform.localScale;

            if (Mathf.Approximately(scale.x, 0) || Mathf.Approximately(scale.y, 0) || Mathf.Approximately(scale.z, 0))
            {
                return viewPosition;
            }

            return new Vector3(viewPosition.x / scale.x, viewPosition.y / scale.y, viewPosition.z / scale.z);
        }
    }
}
