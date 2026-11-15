
using Basis.Scripts.BasisSdk;
using UnityEngine;
using UnityEditor.Animations;
using UnityEditor;
using Suzuryg.FaceEmo.Domain;
using Suzuryg.FaceEmo.Components.Settings;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Suzuryg.FaceEmo.Detail.AV3
{
    public class AV3Utility
    {
        public static void SetLayerWeight(AnimatorController animatorController, string layerName, float weight)
        {
            var layers = animatorController.layers;
            var layerCount = layers.Where(x => x.name == layerName).Count();
            if (layerCount == 1)
            {
                var layerIndex = layers.ToList().FindIndex(x => x.name == layerName);
                layers[layerIndex].defaultWeight = weight;
                animatorController.layers = layers;
            }
            else
            {
                throw new FaceEmoException($"Num of layers which has name {layerName} was not 1: {layerCount}");
            }
        }

        public static void SetMotion(AnimatorController animatorController, string layerName, string stateName, Motion motion)
        {
            var layers = animatorController.layers;
            var layerCount = layers.Where(x => x.name == layerName).Count();
            if (layerCount == 1)
            {
                var layerIndex = layers.ToList().FindIndex(x => x.name == layerName);
                var states = layers[layerIndex].stateMachine.states;
                var stateCount = states.Where(x => x.state.name == stateName).Count();
                if (stateCount == 1)
                {
                    var stateIndex = layers[layerIndex].stateMachine.states.ToList().FindIndex(x => x.state.name == stateName);
                    layers[layerIndex].stateMachine.states[stateIndex].state.motion = motion;
                    animatorController.layers = layers;
                }
                else
                {
                    throw new FaceEmoException($"Num of states which has name {stateName} was not 1: {stateCount}");
                }
            }
            else
            {
                throw new FaceEmoException($"Num of layers which has name {layerName} was not 1: {layerCount}");
            }
        }

        public static void RemoveLayer(AnimatorController animatorController, string layerName)
        {
            var layers = animatorController.layers;
            var layerCount = layers.Where(x => x.name == layerName).Count();
            if (layerCount == 1)
            {
                var removed = layers.Where(x => x.name != layerName);
                animatorController.layers = removed.ToArray();
            }
            else
            {
                throw new FaceEmoException($"Num of layers which has name {layerName} was not 1: {layerCount}");
            }
        }

        public static void MoveLayer(AnimatorController animatorController, string layerName, int newIndex)
        {
            var layers = animatorController.layers;
            var layerCount = layers.Where(x => x.name == layerName).Count();
            if (layerCount == 1)
            {
                var target = layers.Where(x => x.name == layerName).First();
                var removed = layers.Where(x => x.name != layerName).ToList();
                removed.Insert(newIndex, target);
                animatorController.layers = removed.ToArray();
            }
            else
            {
                throw new FaceEmoException($"Num of layers which has name {layerName} was not 1: {layerCount}");
            }
        }

        public static (AnimationClip clip, string name) GetAnimationClipWithName(Domain.Animation animation)
        {
            if (animation is Domain.Animation && animation.GUID is string && AssetDatabase.GUIDToAssetPath(animation.GUID) is string path)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                var name = Path.GetFileName(path).Replace(".anim", "");
                return (clip, name);
            }
            else
            {
                return (null, null);
            }
        }

        public static int GetPreselectEmoteIndex(HandGesture left, HandGesture right)
        {
            // Left is row index, right is column index, return row-major index
            var numOfGesture = AV3Constants.EmoteSelectToGesture.Count;
            return (int)left * numOfGesture + (int)right;
        }

        public static SkinnedMeshRenderer GetFaceMesh(BasisAvatar avatar)
        {
            if (avatar == null) { return null; }

            // Basis stores the face meshes on the avatar itself.
            if (avatar.FaceVisemeMesh != null) { return avatar.FaceVisemeMesh; }
            if (avatar.FaceBlinkMesh != null) { return avatar.FaceBlinkMesh; }

            // Fall back to a child object named "Body".
            if (avatar.gameObject != null && avatar.gameObject.transform != null)
            {
                var avatarRoot = avatar.gameObject.transform;
                for (int i = 0; i < avatarRoot.childCount; i++)
                {
                    var child = avatarRoot.GetChild(i);
                    if (child != null && child.name == "Body")
                    {
                        var faceMesh = child.GetComponent<SkinnedMeshRenderer>();
                        if (faceMesh != null) { return faceMesh; }
                    }
                }
            }
            return null;
        }

        public static HashSet<BlendShape> GetBlendShapesToBeExcluded(BasisAvatar avatar, bool excludeBlink, bool excludeLipSync)
        {
            HashSet<BlendShape> toBeExcluded = new HashSet<BlendShape>();

            // Exclude shape keys that Basis drives for blinking
            if (excludeBlink)
            {
                toBeExcluded = new HashSet<BlendShape>(GetEyeLidsBlendShapes(avatar));
            }

            // Exclude shape keys that Basis drives for lip-sync
            if (excludeLipSync)
            {
                foreach (var blendShape in GetLipSyncBlendShapes(avatar))
                {
                    toBeExcluded.Add(blendShape);
                }
            }

            return toBeExcluded;
        }

        // First blink blend shape of the avatar (BasisAvatar.BlinkViseme[0] on BasisAvatar.FaceBlinkMesh).
        public static BlendShape GetBlinkBlendShape(BasisAvatar avatar)
        {
            var list = GetEyeLidsBlendShapes(avatar);
            return list.Count > 0 ? list[0] : null;
        }

        // Blend shapes that Basis uses for blinking (indices into the mesh of BasisAvatar.FaceBlinkMesh).
        public static List<BlendShape> GetEyeLidsBlendShapes(BasisAvatar avatar)
        {
            return GetBlendShapesFromIndices(avatar, avatar != null ? avatar.FaceBlinkMesh : null, avatar != null ? avatar.BlinkViseme : null);
        }

        // Blend shapes that Basis uses for visemes (indices into the mesh of BasisAvatar.FaceVisemeMesh).
        public static List<BlendShape> GetLipSyncBlendShapes(BasisAvatar avatar)
        {
            return GetBlendShapesFromIndices(avatar, avatar != null ? avatar.FaceVisemeMesh : null, avatar != null ? avatar.FaceVisemeMovement : null);
        }

        private static List<BlendShape> GetBlendShapesFromIndices(BasisAvatar avatar, SkinnedMeshRenderer skinnedMesh, int[] indices)
        {
            var ret = new List<BlendShape>();
            if (avatar == null || skinnedMesh == null || skinnedMesh.sharedMesh == null || indices == null) { return ret; }

            var transformPath = GetPathFromAvatarRoot(skinnedMesh.transform, avatar);
            if (transformPath == null) { return ret; }

            foreach (var index in indices)
            {
                if (0 <= index && index < skinnedMesh.sharedMesh.blendShapeCount)
                {
                    ret.Add(new BlendShape(path: transformPath, name: skinnedMesh.sharedMesh.GetBlendShapeName(index)));
                }
            }
            return ret;
        }

        public static string GetPathFromAvatarRoot(Transform transform, BasisAvatar avatar)
        {
            if (avatar == null || avatar.gameObject == null) { return null; }
            var animator = avatar.gameObject.GetComponent<Animator>();
            if (animator == null) { return null; }
            return AnimationUtility.CalculateTransformPath(transform, animator.transform);
        }

        public static SkinnedMeshRenderer GetMeshByPath(string pathFromAvatarRoot, BasisAvatar avatar)
        {
            if (avatar == null || avatar.gameObject == null) { return null; }

            var animator = avatar.gameObject.GetComponent<Animator>();
            if (animator == null) { return null; }

            var transform = animator.transform.Find(pathFromAvatarRoot);
            if (transform == null || transform.gameObject == null) { return null; }

            return transform.gameObject.GetComponent<SkinnedMeshRenderer>();
        }

        public static Dictionary<BlendShape, float> GetBlendShapeValues(SkinnedMeshRenderer skinnedMeshRenderer, BasisAvatar avatar, bool excludeBlink, bool excludeLipSync)
        {
            var values = new Dictionary<BlendShape, float>();

            if (skinnedMeshRenderer == null || skinnedMeshRenderer.sharedMesh == null) { return values; }
            var transformPath = GetPathFromAvatarRoot(skinnedMeshRenderer.transform, avatar);
            if (transformPath == null) { return values; }

            var toBeExcluded = GetBlendShapesToBeExcluded(avatar, excludeBlink, excludeLipSync);

            for (int i = 0; i < skinnedMeshRenderer.sharedMesh.blendShapeCount; i++)
            {
                var name = skinnedMeshRenderer.sharedMesh.GetBlendShapeName(i);
                var blendShape = new BlendShape(path: transformPath, name: name);

                var excluded = toBeExcluded.Contains(blendShape);
                if (excludeLipSync && IsLipSyncBlendShapeName(blendShape.Name)) { excluded = true; }

                if (!excluded)
                {
                    var weight = skinnedMeshRenderer.GetBlendShapeWeight(i);
                    values[blendShape] = weight;
                }
            }

            return values;
        }

        public static Dictionary<BlendShape, float> GetFaceMeshBlendShapeValues(BasisAvatar avatar, bool excludeBlink, bool excludeLipSync)
        {
            return GetBlendShapeValues(GetFaceMesh(avatar), avatar, excludeBlink, excludeLipSync);
        }

        public static string ConvertNameToSafePath(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c) ||
                    c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' ||
                    c == '%' || c == '#' || c == '{' || c == '}' || c == '[' || c == ']' || c == '`' || c == '^')
                {
                    sb.Append('_'); // Replace invalid characters with an underscore or any other suitable character
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // https://hacchi-man.hatenablog.com/entry/2020/08/23/220000
        public static void CreateFolderRecursively(string path)
        {
            // If it doesn't start with Assets, it can't be processed.
            if (!path.StartsWith("Assets/"))
            {
                return;
            }
         
            // AssetDatabase, so the delimiter is /.
            var dirs = path.Split('/');
            var combinePath = dirs[0];

            // Skip the Assets part.
            foreach (var dir in dirs.Skip(1))
            {
                // Check existence of directory
                if (!AssetDatabase.IsValidFolder(combinePath + '/' + dir))
                {
                    AssetDatabase.CreateFolder(combinePath, dir);
                }
                combinePath += '/' + dir;
            }
        }

        public static Animator GetAnimator(AV3Setting aV3Setting)
        {
            // Error occurs when returning from Play mode to Edit mode if Null conditional operator is used to determine Null
            if (aV3Setting == null || aV3Setting.TargetAvatar == null)
            {
                return null;
            }
            return aV3Setting.TargetAvatar.GetComponent<Animator>();
        }

        private static HumanPose _poseCache;
        private static AnimationClip _poseClipCache;
        public static AnimationClip GetAvatarPoseClip(BasisAvatar avatar)
        {
            if (avatar == null) { return null; }
            var clonedAvatar = UnityEngine.Object.Instantiate(avatar.gameObject);
            try
            {
                clonedAvatar.transform.localPosition = Vector3.zero;
                clonedAvatar.transform.localRotation = Quaternion.identity;
                // FIXME: Unable to support the case that avatar's body shape balance is tuned by root object's scale. (Is it necessary to assume this case...?)
                clonedAvatar.transform.localScale = Vector3.one;

                var animator = clonedAvatar.GetComponent<Animator>();
                if (animator == null || !animator.isHuman) { return null; }

                var humanPoseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
                var humanPose = new HumanPose();
                humanPoseHandler.GetHumanPose(ref humanPose);

                if (_poseClipCache == null ||
                    !ArePosesSimilar(humanPose, _poseCache, float.Epsilon, float.Epsilon, float.Epsilon))
                {
                    _poseCache = humanPose;
                    _poseClipCache = new AnimationClip() { legacy = false };
                    SetDefaultTransformToClip(_poseClipCache);
                    SetHumanPoseToClip(ref humanPose, _poseClipCache);
                }

                return _poseClipCache;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clonedAvatar);
            }
        }

        private static bool ArePosesSimilar(HumanPose pose1, HumanPose pose2, float positionThreshold, float rotationThreshold, float muscleThreshold)
        {
            // Compare body position
            if (Vector3.Distance(pose1.bodyPosition, pose2.bodyPosition) > positionThreshold)
            {
                return false;
            }

            // Compare body rotation
            if (Quaternion.Angle(pose1.bodyRotation, pose2.bodyRotation) > rotationThreshold)
            {
                return false;
            }

            // Compare muscle values
            for (int i = 0; i < pose1.muscles.Length; i++)
            {
                if (Mathf.Abs(pose1.muscles[i] - pose2.muscles[i]) > muscleThreshold)
                {
                    return false;
                }
            }

            // If none of the values are beyond the thresholds, the poses are considered similar
            return true;
        }

        private static void SetDefaultTransformToClip(AnimationClip clip)
        {
            // FIXME: Scale change is reflected correctly in AnimationWindow preview, but not in AnimationMode sampling, so avatar scale must be changed directly.
            // This scale change is useless because it is not reflected, but leave it for now.
            clip.SetCurve("", typeof(Transform), "m_LocalScale.x", new AnimationCurve(new Keyframe(time: 0, value: 1)));
            clip.SetCurve("", typeof(Transform), "m_LocalScale.y", new AnimationCurve(new Keyframe(time: 0, value: 1)));
            clip.SetCurve("", typeof(Transform), "m_LocalScale.z", new AnimationCurve(new Keyframe(time: 0, value: 1)));

            clip.SetCurve("", typeof(Animator), "MotionT.x", new AnimationCurve(new Keyframe(time: 0, value: 0)));
            clip.SetCurve("", typeof(Animator), "MotionT.y", new AnimationCurve(new Keyframe(time: 0, value: 0)));
            clip.SetCurve("", typeof(Animator), "MotionT.z", new AnimationCurve(new Keyframe(time: 0, value: 0)));
        }

        private static void SetHumanPoseToClip(ref HumanPose humanPose, AnimationClip clip)
        {
            // Set body position and rotation
#if UNITY_2022_1_OR_NEWER
            clip.SetCurve("", typeof(Animator), "RootT.x", new AnimationCurve(new Keyframe(0, humanPose.bodyPosition.x)));
            clip.SetCurve("", typeof(Animator), "RootT.y", new AnimationCurve(new Keyframe(0, humanPose.bodyPosition.y)));
            clip.SetCurve("", typeof(Animator), "RootT.z", new AnimationCurve(new Keyframe(0, humanPose.bodyPosition.z)));
#else
            clip.SetCurve("", typeof(Animator), "RootT.x", new AnimationCurve(new Keyframe(0, humanPose.bodyPosition.normalized.x)));
            clip.SetCurve("", typeof(Animator), "RootT.y", new AnimationCurve(new Keyframe(0, humanPose.bodyPosition.normalized.y)));
            clip.SetCurve("", typeof(Animator), "RootT.z", new AnimationCurve(new Keyframe(0, humanPose.bodyPosition.normalized.z)));
#endif

            clip.SetCurve("", typeof(Animator), "RootQ.x", new AnimationCurve(new Keyframe(0, humanPose.bodyRotation.x)));
            clip.SetCurve("", typeof(Animator), "RootQ.y", new AnimationCurve(new Keyframe(0, humanPose.bodyRotation.y)));
            clip.SetCurve("", typeof(Animator), "RootQ.z", new AnimationCurve(new Keyframe(0, humanPose.bodyRotation.z)));
            clip.SetCurve("", typeof(Animator), "RootQ.w", new AnimationCurve(new Keyframe(0, humanPose.bodyRotation.w)));

            // Set muscle values
            for (int i = 0; i < humanPose.muscles.Length; i++)
            {
                string muscleName = HumanTrait.MuscleName[i];
                float muscleValue = humanPose.muscles[i];
                clip.SetCurve("", typeof(Animator), muscleName, new AnimationCurve(new Keyframe(0, muscleValue)));
            }
        }

        public static AnimationClip SynthesizeClip(AnimationClip baseClip, AnimationClip additionalClip)
        {
            var synthesized = new AnimationClip();

            if (baseClip != null) { EditorUtility.CopySerialized(baseClip, synthesized); }

            if (additionalClip != null)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(additionalClip))
                {
                    var curve = AnimationUtility.GetEditorCurve(additionalClip, binding);
                    AnimationUtility.SetEditorCurve(synthesized, binding, curve);
                }
            }

            return synthesized;
        }

        public static AnimationClip SynthesizeAvatarPose(AnimationClip animationClip, BasisAvatar avatar) => SynthesizeClip(GetAvatarPoseClip(avatar), animationClip);

        public static void CombineExpressions(AnimationClip leftHand, AnimationClip rightHand, AnimationClip destination)
        {
            var leftTransforms = new Dictionary<string, List<(EditorCurveBinding binding, float value)>>();
            var rightTransforms = new Dictionary<string, List<(EditorCurveBinding binding, float value)>>();

            if (leftHand != null)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(leftHand))
                {
                    var leftCurve = AnimationUtility.GetEditorCurve(leftHand, binding);

                    var leftValue = leftCurve != null && leftCurve.keys.Length > 0 ? leftCurve.keys.Last().value : 0;

                    if (binding.type == typeof(Transform))
                    {
                        if (!leftTransforms.ContainsKey(binding.path)) { leftTransforms[binding.path] = new List<(EditorCurveBinding binding, float value)>(); }
                        leftTransforms[binding.path].Add((binding, leftValue));
                        continue;
                    }

                    var newCurve = new AnimationCurve(new Keyframe(time: 0, value: leftValue));
                    AnimationUtility.SetEditorCurve(destination, binding, newCurve);
                }
            }

            if (rightHand != null)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(rightHand)) 
                {
                    var rightCurve = AnimationUtility.GetEditorCurve(rightHand, binding);
                    var oldCurve = AnimationUtility.GetEditorCurve(destination, binding);

                    var rightValue = rightCurve != null && rightCurve.keys.Length > 0 ? rightCurve.keys.Last().value : 0;
                    var oldValue = oldCurve != null && oldCurve.keys.Length > 0 ? oldCurve.keys.Last().value : 0;

                    if (binding.type == typeof(Transform))
                    {
                        if (!rightTransforms.ContainsKey(binding.path)) { rightTransforms[binding.path] = new List<(EditorCurveBinding binding, float value)>(); }
                        rightTransforms[binding.path].Add((binding, rightValue));
                        continue;
                    }

                    var newCurve = new AnimationCurve(new Keyframe(time: 0, value: Math.Max(rightValue, oldValue)));
                    AnimationUtility.SetEditorCurve(destination, binding, newCurve);
                }
            }

            // TODO: Priority should be determined based on the difference from the default value.
            // For the time being, priority is given to the one selected later.
            // (When called from the gesture table, the right hand is the second argument, and in many cases, the right hand has priority for expression control.)
            var combinedTransforms = leftTransforms;
            foreach (var key in rightTransforms.Keys)
            {
                combinedTransforms[key] = rightTransforms[key];
            }

            foreach (var list in combinedTransforms.Values)
            {
                foreach (var pair in list)
                {
                    var newCurve = new AnimationCurve(new Keyframe(time: 0, value: pair.value));
                    AnimationUtility.SetEditorCurve(destination, pair.binding, newCurve);
                }
            }
        }

        public static List<ModeEx> FlattenMenuItemList(IMenuItemList menuItemList, ModeNameProvider modeNameProvider)
        {
            var ret = new List<ModeEx>();
            FlattenMenuItemListSub(menuItemList, ret, string.Empty, modeNameProvider);

            var branchCount = 0;
            foreach (var mode in ret)
            {
                mode.DefaultEmoteIndex = branchCount;
                branchCount += mode.Mode.Branches.Count + 1;
            }
            return ret;
        }

        private static void FlattenMenuItemListSub(IMenuItemList menuItemList, List<ModeEx> flattened, string pathToParent, ModeNameProvider modeNameProvider)
        {
            foreach (var id in menuItemList.Order)
            {
                if (menuItemList.GetType(id) == MenuItemType.Mode)
                {
                    var mode = menuItemList.GetMode(id);
                    flattened.Add(new ModeEx() { PathToMode = pathToParent + modeNameProvider.Provide(mode), Mode = new ModeExInner(mode) });
                }
                else
                {
                    var group = menuItemList.GetGroup(id);
                    FlattenMenuItemListSub(group, flattened, pathToParent + group.DisplayName + "/", modeNameProvider);
                }
            }
        }

        public static int? GetActualRegisteredListFreeSpace(IMenu menu, AV3Setting av3Setting)
        {
            var freeSpace = menu?.Registered?.FreeSpace;
            if (!freeSpace.HasValue) { return null; }

            if (av3Setting.AddConfig_EmoteSelect) { freeSpace--; }
            return freeSpace;
        }

        public static int GetActualRegisteredListCapacity(AV3Setting av3Setting)
        {
            var capacity = Domain.RegisteredMenuItemList.Capacity;
            if (av3Setting.AddConfig_EmoteSelect) { capacity--; }
            return capacity;
        }

        public static bool IsLipSyncBlendShapeName(string blendShapeName)
        {
            var lowerCase = blendShapeName.ToLower();

            return lowerCase.Contains("vrc.v_sil") ||
                lowerCase.Contains("vrc.v_pp") ||
                lowerCase.Contains("vrc.v_ff") ||
                lowerCase.Contains("vrc.v_th") ||
                lowerCase.Contains("vrc.v_dd") ||
                lowerCase.Contains("vrc.v_kk") ||
                lowerCase.Contains("vrc.v_ch") ||
                lowerCase.Contains("vrc.v_ss") ||
                lowerCase.Contains("vrc.v_nn") ||
                lowerCase.Contains("vrc.v_rr") ||
                lowerCase.Contains("vrc.v_aa") ||
                lowerCase.Contains("vrc.v_e") ||
                lowerCase.Contains("vrc.v_ih") ||
                lowerCase.Contains("vrc.v_oh") ||
                lowerCase.Contains("vrc.v_ou");
        }
    }
}
