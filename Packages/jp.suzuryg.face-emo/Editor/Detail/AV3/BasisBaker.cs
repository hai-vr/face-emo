using Suzuryg.FaceEmo.Baked;
using Suzuryg.FaceEmo.Components.Settings;
using Suzuryg.FaceEmo.Domain;
using Suzuryg.FaceEmo.UseCase;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Basis.Scripts.BasisSdk;

namespace Suzuryg.FaceEmo.Detail.AV3
{
    // Replaces the VRChat FxGenerator. Turns the FaceEmo menu into FE1 data (design_docs/BASIS_BAKED_FORMAT.md)
    // plus a FaceEmoBakedRefs component on the avatar, for the fixed Cilbox runtime script.
    public class BasisBaker : IFxGenerator
    {
        public const int Pos = 1, Rot = 2, Scl = 3;
        private AV3Setting _aV3Setting;

        public BasisBaker(AV3Setting aV3Setting)
        {
            _aV3Setting = aV3Setting;
        }

        public List<string> GetParentPrefabPathOfMARootObjects() => new List<string>();

        public void Generate(IMenu menu, IEnumerable<string> editablePrefabPaths)
        {
            var avatar = _aV3Setting.TargetAvatar as BasisAvatar;
            if (avatar == null) throw new FaceEmoException("Target avatar is not set.");
            var ctx = new Context(avatar.transform);
            var data = Bake(menu, ctx);

            var dir = $"Assets/FaceEmo/{avatar.gameObject.name}";
            Directory.CreateDirectory(dir);
            var path = $"{dir}/FaceEmoBaked.txt";
            File.WriteAllText(path, data.Write());
            AssetDatabase.ImportAsset(path);
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(path);

            var refs = avatar.GetComponent<FaceEmoBakedRefs>();
            if (refs == null) refs = Undo.AddComponent<FaceEmoBakedRefs>(avatar.gameObject);
            Undo.RecordObject(refs, "FaceEmo bake");
            refs.Data = text;
            if (string.IsNullOrEmpty(refs.PropUuid)) refs.PropUuid = Guid.NewGuid().ToString();
            refs.SkinnedMeshes = ctx.Smrs.ToArray();
            refs.Transforms = ctx.Transforms.ToArray();
            refs.GameObjects = ctx.GameObjects.ToArray();
            refs.Behaviours = ctx.Behaviours.ToArray();
            refs.Renderers = ctx.Renderers.ToArray();
            EditorUtility.SetDirty(refs);
            if (ctx.Skipped.Count > 0)
                Debug.LogWarning($"FaceEmo bake: skipped {ctx.Skipped.Count} unsupported curve kinds: {string.Join(", ", ctx.Skipped)}");
            Debug.Log($"FaceEmo bake: {data.Modes.Count} modes, {data.Branches.Count} branches, {data.Expressions.Count} expressions, {data.Targets.Count} targets -> {path}");
        }

        // ----- bake -----

        public static BakedFaceData Bake(IMenu menu, Context ctx)
        {
            var d = ctx.Data;
            var exprByGuid = new Dictionary<string, int>();
            var branchIdx = new Dictionary<IBranch, int>();
            var modeIdx = new Dictionary<string, int>();
            var groupIdx = new Dictionary<string, int>();

            int Expr(Domain.Animation a)
            {
                if (a == null || string.IsNullOrEmpty(a.GUID)) return -1;
                if (exprByGuid.TryGetValue(a.GUID, out var e)) return e;
                var (clip, name) = AV3Utility.GetAnimationClipWithName(a);
                if (clip == null) return -1;
                var x = ctx.BakeClip(clip, name);
                d.Expressions.Add(x);
                return exprByGuid[a.GUID] = d.Expressions.Count - 1;
            }

            int Flags(bool lt, bool rt, bool blink, bool mouth, EyeTrackingControl eye, MouthTrackingControl mt, bool cdf)
                => (lt ? 1 : 0) | (rt ? 2 : 0) | (blink ? 4 : 0) | (mouth ? 8 : 0)
                 | (eye == EyeTrackingControl.Animation ? 16 : 0) | (mt == MouthTrackingControl.Animation ? 32 : 0) | (cdf ? 64 : 0);

            void Walk(IMenuItemList list, int groupId)
            {
                foreach (var id in list.Order)
                {
                    if (list.GetType(id) == MenuItemType.Mode)
                    {
                        var mode = list.GetMode(id);
                        var m = new BakedFaceData.Mode
                        {
                            Name = mode.DisplayName ?? "",
                            Anim = Expr(mode.Animation),
                            Flags = Flags(false, false, mode.BlinkEnabled, mode.MouthMorphCancelerEnabled, mode.EyeTrackingControl, mode.MouthTrackingControl, mode.ChangeDefaultFace),
                        };
                        if (mode.UseAnimationNameAsDisplayName && mode.Animation != null)
                        {
                            var (_, n) = AV3Utility.GetAnimationClipWithName(mode.Animation);
                            if (!string.IsNullOrEmpty(n)) m.Name = n;
                        }
                        foreach (var b in mode.Branches)
                        {
                            d.Branches.Add(new BakedFaceData.Branch
                            {
                                Base = Expr(b.BaseAnimation), Left = Expr(b.LeftHandAnimation), Right = Expr(b.RightHandAnimation), Both = Expr(b.BothHandsAnimation),
                                Flags = Flags(b.IsLeftTriggerUsed, b.IsRightTriggerUsed, b.BlinkEnabled, b.MouthMorphCancelerEnabled, b.EyeTrackingControl, b.MouthTrackingControl, false),
                            });
                            branchIdx[b] = d.Branches.Count - 1;
                        }
                        for (int l = 0; l < 8; l++)
                            for (int r = 0; r < 8; r++)
                            {
                                var cell = mode.GetGestureCell((HandGesture)l, (HandGesture)r);
                                m.Table[l * 8 + r] = cell != null && branchIdx.TryGetValue(cell, out var bi) ? bi : -1;
                            }
                        d.Modes.Add(m);
                        modeIdx[mode.GetId()] = d.Modes.Count - 1;
                        d.Items.Add(new BakedFaceData.Item { Group = groupId, Type = 'M', Index = d.Modes.Count - 1 });
                    }
                    else
                    {
                        var g = list.GetGroup(id);
                        d.Groups.Add(new BakedFaceData.Group { Name = g.DisplayName ?? "", Parent = groupId });
                        int gi = d.Groups.Count - 1;
                        groupIdx[g.GetId()] = gi;
                        d.Items.Add(new BakedFaceData.Item { Group = groupId, Type = 'G', Index = gi });
                        Walk(g, gi);
                    }
                }
            }

            Walk(menu.Registered, -1);
            d.DefaultMode = menu.DefaultSelection != null && modeIdx.TryGetValue(menu.DefaultSelection, out var dm) ? dm : -1;
            return d;
        }

        // ----- clip sampling -----

        public class Context
        {
            public readonly Transform Root;
            public readonly BakedFaceData Data = new BakedFaceData();
            public readonly List<SkinnedMeshRenderer> Smrs = new List<SkinnedMeshRenderer>();
            public readonly List<Transform> Transforms = new List<Transform>();
            public readonly List<GameObject> GameObjects = new List<GameObject>();
            public readonly List<Behaviour> Behaviours = new List<Behaviour>();
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly HashSet<string> Skipped = new HashSet<string>();
            private readonly Dictionary<(int kind, UnityEngine.Object obj, int sub), int> _targets = new Dictionary<(int, UnityEngine.Object, int), int>();

            public Context(Transform root) { Root = root; }

            static int IndexOf<T>(List<T> l, T o) where T : UnityEngine.Object { int i = l.IndexOf(o); if (i < 0) { l.Add(o); i = l.Count - 1; } return i; }

            int Target(int kind, UnityEngine.Object obj, int sub, Func<float[]> rest)
            {
                if (_targets.TryGetValue((kind, obj, sub), out var t)) return t;
                int r;
                switch (kind)
                {
                    case 0: r = IndexOf(Smrs, (SkinnedMeshRenderer)obj); break;
                    case 1: case 2: case 3: r = IndexOf(Transforms, (Transform)obj); break;
                    case 4: r = IndexOf(GameObjects, (GameObject)obj); break;
                    case 5: r = IndexOf(Behaviours, (Behaviour)obj); break;
                    default: r = IndexOf(Renderers, (Renderer)obj); break;
                }
                Data.Targets.Add(new BakedFaceData.Target { Kind = kind, Ref = r, Sub = sub, Rest = rest() });
                return _targets[(kind, obj, sub)] = Data.Targets.Count - 1;
            }

            static float Last(AnimationCurve c) => c != null && c.length > 0 ? c.keys[c.length - 1].value : 0f;

            public BakedFaceData.Expression BakeClip(AnimationClip clip, string name)
            {
                var x = new BakedFaceData.Expression { Name = name ?? clip.name };
                var vals = new Dictionary<int, float[]>();
                var euler = new Dictionary<int, Vector3>();
                var eulerSet = new Dictionary<int, bool[]>();

                float[] Vals(int t) { if (!vals.TryGetValue(t, out var v)) vals[t] = v = (float[])Data.Targets[t].Rest.Clone(); return v; }

                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    var value = Last(AnimationUtility.GetEditorCurve(clip, b));
                    var tr = string.IsNullOrEmpty(b.path) ? Root : Root.Find(b.path);
                    if (tr == null) { Skipped.Add("missing path " + b.path); continue; }
                    var p = b.propertyName;

                    if (b.type == typeof(SkinnedMeshRenderer) && p.StartsWith("blendShape."))
                    {
                        var smr = tr.GetComponent<SkinnedMeshRenderer>();
                        int bs = smr != null && smr.sharedMesh != null ? smr.sharedMesh.GetBlendShapeIndex(p.Substring(11)) : -1;
                        if (bs < 0) { Skipped.Add("blendShape missing"); continue; }
                        Vals(Target(0, smr, bs, () => new[] { smr.GetBlendShapeWeight(bs) }))[0] = value;
                    }
                    else if (b.type == typeof(Transform))
                    {
                        var dot = p.LastIndexOf('.');
                        var comp = dot >= 0 ? p.Substring(dot + 1) : "";
                        int ax = comp == "x" ? 0 : comp == "y" ? 1 : comp == "z" ? 2 : comp == "w" ? 3 : -1;
                        var head = dot >= 0 ? p.Substring(0, dot) : p;
                        if (ax < 0) { Skipped.Add(p); continue; }
                        if (head == "m_LocalPosition") Vals(Target(Pos, tr, 0, () => new[] { tr.localPosition.x, tr.localPosition.y, tr.localPosition.z }))[ax] = value;
                        else if (head == "m_LocalScale") Vals(Target(Scl, tr, 0, () => new[] { tr.localScale.x, tr.localScale.y, tr.localScale.z }))[ax] = value;
                        else if (head == "m_LocalRotation" || head == "localEulerAnglesRaw")
                        {
                            int t = Target(Rot, tr, 0, () => new[] { tr.localRotation.x, tr.localRotation.y, tr.localRotation.z, tr.localRotation.w });
                            if (head == "m_LocalRotation") Vals(t)[ax] = value;
                            else
                            {
                                if (!euler.ContainsKey(t)) { euler[t] = tr.localRotation.eulerAngles; eulerSet[t] = new bool[3]; }
                                var e = euler[t]; e[ax] = value; euler[t] = e; eulerSet[t][ax] = true;
                            }
                        }
                        else Skipped.Add(p);
                    }
                    else if (b.type == typeof(GameObject) && p == "m_IsActive")
                        Vals(Target(4, tr.gameObject, 0, () => new[] { tr.gameObject.activeSelf ? 1f : 0f }))[0] = value;
                    else if (p == "m_Enabled" && typeof(Renderer).IsAssignableFrom(b.type) && tr.GetComponent(b.type) is Renderer rd)
                        Vals(Target(6, rd, 0, () => new[] { rd.enabled ? 1f : 0f }))[0] = value;
                    else if (p == "m_Enabled" && typeof(Behaviour).IsAssignableFrom(b.type) && tr.GetComponent(b.type) is Behaviour bh)
                        Vals(Target(5, bh, 0, () => new[] { bh.enabled ? 1f : 0f }))[0] = value;
                    else Skipped.Add(b.type.Name + "." + p);
                }

                foreach (var kv in euler)
                {
                    var q = Quaternion.Euler(kv.Value);
                    vals[kv.Key] = new[] { q.x, q.y, q.z, q.w };
                }

                var keys = new List<int>(vals.Keys); keys.Sort();
                foreach (var t in keys)
                {
                    var rest = Data.Targets[t].Rest; var v = vals[t];
                    bool same = true; for (int i = 0; i < v.Length; i++) if (v[i] != rest[i]) same = false;
                    if (same) continue; // identical to rest: omit
                    x.Targets.Add(t); x.Values.Add(v);
                }
                return x;
            }
        }
    }
}
