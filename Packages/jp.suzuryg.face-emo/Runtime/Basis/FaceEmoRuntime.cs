using System;
using System.Collections.Generic;
using System.Globalization;
using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Networking.NetworkedAvatar;
using Cilbox;
#if FACEEMO_FREEHAND
using HVR.Freehand;
#endif
using UnityEngine;

namespace Suzuryg.FaceEmo.Baked
{
    // The ONE fixed script every FaceEmo avatar carries. All per-avatar settings are DATA (FE1 text + the object arrays
    // that the text indexes), written by BasisBaker. See design_docs/BASIS_BAKED_FORMAT.md.
    // Written for Cilbox: no bool[], no big array initializers, numbers parsed with InvariantCulture.
    //
    //  * every client: finger bones -> hand gestures -> branch -> expression, applied as plain transform / blendshape /
    //    SetActive / enabled writes (no Animator).
    //  * the wearer: a Freehand radial menu (modes and groups, plus Emote Select); the choice is sent to the others.
    // Not done yet: blink and mouth-morph canceler flags (parsed, no Basis hook to suppress blink / lip sync from a script),
    // analog trigger animations (no input access without a new shim).
    [Cilboxable]
    public class FaceEmoRuntime : MonoBehaviour
    {
        // ---- baked by BasisBaker ----
        public TextAsset Data;
        public string PropUuid;
        public SkinnedMeshRenderer[] SkinnedMeshes;
        public Transform[] Transforms;
        public GameObject[] GameObjects;
        public Behaviour[] Behaviours;
        public Renderer[] Renderers;
        public Texture2D[] Icons;      // menu pictures (face thumbnails) referenced by the K records

        // ---- tuning ----
        public float CurlOn = 100f;      // degrees of total finger curl; hysteresis between On and Off
        public float CurlOff = 70f;
        public float ThumbCurlOn = 40f;
        public float ThumbCurlOff = 25f;
        public float EvaluateInterval = 0.1f;
        public bool LogChanges = true;
        public bool BuildMenu = true;    // tests switch the Freehand menu and the network off

        const string GestureNames = "Neutral,Fist,HandOpen,Fingerpoint,Victory,RockNRoll,HandGun,ThumbsUp";
        const int MaxSubmenus = 32;      // Freehand limit per root

        // ---- parsed data ----
        int _modeCount;
        int[] _modeTable;      // modeCount * 64 : branch index or -1
        int[] _modeAnim;       // expression index or -1
        int[] _modeFlags;
        string[] _modeName;
        int _branchCount;
        int[] _modeIcon;
        int[] _branchIcon;
        int[] _branchBase;     // expression index or -1
        int _defaultMode;
        int _groupCount;
        string[] _groupName;
        int _itemCount;
        int[] _itemGroup;      // -1 = root
        byte[] _itemIsGroup;
        int[] _itemIndex;
        int _targetCount;
        int[] _tKind;
        int[] _tRef;
        int[] _tSub;
        float[] _tRest;        // 4 per target
        int _exprCount;
        int[] _exStart;        // first entry of each expression
        int[] _exCount;
        int[] _exTarget;       // entries
        float[] _exVal;        // 4 per entry
        int[] _mark;           // per target, epoch of the last expression that set it
        int _epoch;
        int _currentExpr = -1;
        float _transition;     // seconds, from the S record
        float[] _cur;          // 4 per target: value last written
        float[] _from;         // 4 per target: value when the current blend started
        float[] _to;           // 4 per target: goal of the current blend
        int[] _act;            // targets still blending
        byte[] _isAct;
        int _actCount;
        float _blendElapsed;

        // ---- finger state ----
        BasisAvatar _avatar;
        Transform[] _leftBones;   // 15 = 5 fingers x (proximal, intermediate, distal)
        Transform[] _rightBones;
        Transform _leftHand;
        Transform _rightHand;
        byte[] _curled;           // 10 = hand * 5 + finger; 1 = curled
        byte[] _present;          // 10: 1 if the finger has all three bones
        byte[] _hasFingers;       // 2: 1 if the hand has at least one of index..little
        float _nextEval;
        int _leftGesture;
        int _rightGesture;
        int _mode;
        int _branch = -2;
        int _emoteExpr = -1;      // Emote Select: expression shown instead of the hands' one, -1 = use the hands
        int _overrideL = -1;      // fixed gestures, -1 = use the hands (API only)
        int _overrideR = -1;
        byte _ready;
        byte _isOwner;

        // ---- menu + network ----
#if FACEEMO_FREEHAND
        BasisFreehandMenuShim _freehand;
#endif
        BasisNetworkShim _net;
        byte _netSendPending;

        void Start()
        {
            _avatar = BasisAvatar.GetGameObject(this).GetComponent<BasisAvatar>();
            _avatar.OnAvatarReady += OnReady;
        }

        void OnDestroy()
        {
            if (_net != null)
            {
                _net.NetworkMessageReceived -= OnNetworkMessage;
                _net.PlayerJoined -= OnPlayerJoined;
                _net.NetworkReady -= OnNetworkReady;
            }
#if FACEEMO_FREEHAND
            if (_freehand != null) _freehand.Stop();
#endif
        }

        void OnReady(bool isOwner)
        {
            _isOwner = isOwner ? (byte)1 : (byte)0;
            Parse();
            // NOT _avatar.Animator: the shim exposes it as a property but the compiled script reads a field, and Cilbox fails
            // the build with "Could not find field ... BasisAvatarShim.Animator". GetComponent works.
            Animator animator = BasisAvatar.GetGameObject(this).GetComponent<Animator>();
            if (animator == null) return;
            _leftHand = animator.GetBoneTransform((HumanBodyBones)17);
            _rightHand = animator.GetBoneTransform((HumanBodyBones)18);
            _leftBones = new Transform[15];
            _rightBones = new Transform[15];
            for (int i = 0; i < 15; i++)
            {
                _leftBones[i] = animator.GetBoneTransform((HumanBodyBones)(24 + i));
                _rightBones[i] = animator.GetBoneTransform((HumanBodyBones)(39 + i));
            }
            SetupFingers();
            _mode = _defaultMode;
            _ready = 1;
            if (BuildMenu)
            {
                _net = GetComponent<BasisNetworkShim>();
                if (_net != null)
                {
                    _net.NetworkMessageReceived += OnNetworkMessage;
                    _net.PlayerJoined += OnPlayerJoined;
                    _net.NetworkReady += OnNetworkReady;
                }
#if FACEEMO_FREEHAND
                if (isOwner) BuildFreehand();
#endif
            }
        }

        // Which fingers the avatar has. A missing finger is ignored when matching a sign (see Classify).
        void SetupFingers()
        {
            _curled = new byte[10];
            _present = new byte[10];
            _hasFingers = new byte[2];
            for (int h = 0; h < 2; h++)
            {
                Transform[] bones = h == 0 ? _leftBones : _rightBones;
                Transform wrist = h == 0 ? _leftHand : _rightHand;
                for (int f = 0; f < 5; f++)
                {
                    if (wrist != null && bones[f * 3] != null && bones[f * 3 + 1] != null && bones[f * 3 + 2] != null)
                    {
                        _present[h * 5 + f] = 1;
                        if (f > 0) _hasFingers[h] = 1;
                    }
                }
            }
        }

        // ================= data =================

        static string Unesc(string s)
        {
            return s.Replace("%0A", "\n").Replace("%2C", ",").Replace("%3A", ":").Replace("%3B", ";").Replace("%7C", "|").Replace("%25", "%");
        }

        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
        static int I(string s) { return int.Parse(s, CultureInfo.InvariantCulture); }

        void Parse()
        {
            _modeCount = 0; _branchCount = 0; _defaultMode = -1; _groupCount = 0; _itemCount = 0; _targetCount = 0; _exprCount = 0; _transition = 0f; _actCount = 0;
            if (Data == null) return;
            string[] lines = Data.text.Split('\n');
            int entries = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.StartsWith("M|")) _modeCount++;
                else if (ln.StartsWith("B|")) _branchCount++;
                else if (ln.StartsWith("G|")) _groupCount++;
                else if (ln.StartsWith("I|")) _itemCount++;
                else if (ln.StartsWith("T|")) _targetCount++;
                else if (ln.StartsWith("X|"))
                {
                    _exprCount++;
                    string[] xf = ln.Split('|');
                    if (xf.Length > 2 && xf[2].Length > 0) entries += xf[2].Split(';').Length;
                }
            }
            _modeTable = new int[_modeCount * 64];
            _modeAnim = new int[_modeCount];
            _modeFlags = new int[_modeCount];
            _modeName = new string[_modeCount];
            _branchBase = new int[_branchCount];
            _branchIcon = new int[_branchCount];
            _modeIcon = new int[_modeCount];
            for (int i = 0; i < _branchCount; i++) _branchIcon[i] = -1;
            for (int i = 0; i < _modeCount; i++) _modeIcon[i] = -1;
            _groupName = new string[_groupCount];
            _itemGroup = new int[_itemCount];
            _itemIsGroup = new byte[_itemCount];
            _itemIndex = new int[_itemCount];
            _tKind = new int[_targetCount];
            _tRef = new int[_targetCount];
            _tSub = new int[_targetCount];
            _tRest = new float[_targetCount * 4];
            _mark = new int[_targetCount];
            _cur = new float[_targetCount * 4];
            _from = new float[_targetCount * 4];
            _to = new float[_targetCount * 4];
            _act = new int[_targetCount];
            _isAct = new byte[_targetCount];
            _exStart = new int[_exprCount];
            _exCount = new int[_exprCount];
            _exTarget = new int[entries];
            _exVal = new float[entries * 4];

            int m = 0; int b = 0; int g = 0; int it = 0; int t = 0; int x = 0; int e = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length < 2) continue;
                string[] f = line.Split('|');
                if (f[0] == "T")
                {
                    _tKind[t] = I(f[1]); _tRef[t] = I(f[2]); _tSub[t] = I(f[3]);
                    if (f[4].Length > 0)
                    {
                        string[] rv = f[4].Split(',');
                        for (int k = 0; k < rv.Length && k < 4; k++) _tRest[t * 4 + k] = F(rv[k]);
                    }
                    for (int k = 0; k < 4; k++) _cur[t * 4 + k] = _tRest[t * 4 + k];
                    t++;
                }
                else if (f[0] == "X")
                {
                    _exStart[x] = e;
                    if (f[2].Length > 0)
                    {
                        string[] pairs = f[2].Split(';');
                        for (int p = 0; p < pairs.Length; p++)
                        {
                            string[] kv = pairs[p].Split(':');
                            _exTarget[e] = I(kv[0]);
                            string[] vv = kv[1].Split(',');
                            for (int k = 0; k < vv.Length && k < 4; k++) _exVal[e * 4 + k] = F(vv[k]);
                            e++;
                        }
                    }
                    _exCount[x] = e - _exStart[x];
                    x++;
                }
                else if (f[0] == "B")
                {
                    _branchBase[b] = I(f[1]);
                    b++;
                }
                else if (f[0] == "M")
                {
                    _modeName[m] = Unesc(f[1]);
                    _modeAnim[m] = I(f[2]);
                    _modeFlags[m] = I(f[3]);
                    string[] cells = f[4].Split(',');
                    for (int c = 0; c < 64; c++) _modeTable[m * 64 + c] = I(cells[c]);
                    m++;
                }
                else if (f[0] == "G")
                {
                    _groupName[g] = Unesc(f[1]);
                    g++;
                }
                else if (f[0] == "I")
                {
                    _itemGroup[it] = I(f[1]);
                    _itemIsGroup[it] = f[2] == "G" ? (byte)1 : (byte)0;
                    _itemIndex[it] = I(f[3]);
                    it++;
                }
                else if (f[0] == "D")
                {
                    _defaultMode = I(f[1]);
                }
                else if (f[0] == "S")
                {
                    _transition = F(f[1]);
                }
                else if (f[0] == "K")
                {
                    int ki = I(f[2]);
                    if (f[1] == "M") { if (ki >= 0 && ki < _modeCount) _modeIcon[ki] = I(f[3]); }
                    else { if (ki >= 0 && ki < _branchCount) _branchIcon[ki] = I(f[3]); }
                }
            }
        }

        // ================= expressions =================

        void WriteTarget(int t, float[] src, int off)
        {
            int r = _tRef[t];
            switch (_tKind[t])
            {
                case 0: SkinnedMeshes[r].SetBlendShapeWeight(_tSub[t], src[off]); break;
                case 1: Transforms[r].localPosition = new Vector3(src[off], src[off + 1], src[off + 2]); break;
                case 2: Transforms[r].localRotation = new Quaternion(src[off], src[off + 1], src[off + 2], src[off + 3]); break;
                case 3: Transforms[r].localScale = new Vector3(src[off], src[off + 1], src[off + 2]); break;
                case 4: GameObjects[r].SetActive(src[off] > 0.5f); break;
                case 5: Behaviours[r].enabled = src[off] > 0.5f; break;
                case 6: Renderers[r].enabled = src[off] > 0.5f; break;
            }
        }

        // Sets the goal of target t to src[off..]. Continuous targets lerp there over the transition time; on/off targets,
        // and everything when the transition time is 0, switch at once.
        void SetGoal(int t, float[] src, int off)
        {
            int o = t * 4;
            if (_tKind[t] >= 4 || _transition <= 0f)
            {
                for (int k = 0; k < 4; k++) { _cur[o + k] = src[off + k]; _from[o + k] = src[off + k]; _to[o + k] = src[off + k]; }
                WriteTarget(t, _cur, o);
                return;
            }
            for (int k = 0; k < 4; k++) { _from[o + k] = _cur[o + k]; _to[o + k] = src[off + k]; }
            if (_isAct[t] == 0) { _isAct[t] = 1; _act[_actCount] = t; _actCount++; }
        }

        // Shows expression e (-1 = none). Targets the previous expression set (or that are still blending) and this one does not go back to rest.
        void ShowExpression(int e)
        {
            if (e == _currentExpr) return;
            _epoch++;
            if (e >= 0)
            {
                int s = _exStart[e];
                for (int k = 0; k < _exCount[e]; k++) _mark[_exTarget[s + k]] = _epoch;
            }
            for (int i = 0; i < _actCount; i++)
            {
                int t = _act[i];
                if (_mark[t] != _epoch) SetGoal(t, _tRest, t * 4);
            }
            if (_currentExpr >= 0)
            {
                int s0 = _exStart[_currentExpr];
                for (int k = 0; k < _exCount[_currentExpr]; k++)
                {
                    int t = _exTarget[s0 + k];
                    if (_mark[t] != _epoch) SetGoal(t, _tRest, t * 4);
                }
            }
            if (e >= 0)
            {
                int s = _exStart[e];
                for (int k = 0; k < _exCount[e]; k++) SetGoal(_exTarget[s + k], _exVal, (s + k) * 4);
            }
            _blendElapsed = 0f;
            _currentExpr = e;
        }

        // Moves every blending target along; called every frame.
        void StepBlend()
        {
            if (_actCount == 0) return;
            _blendElapsed += Time.deltaTime;
            float a = _transition > 0f ? _blendElapsed / _transition : 1f;
            if (a > 1f) a = 1f;
            int w = 0;
            for (int i = 0; i < _actCount; i++)
            {
                int t = _act[i];
                int o = t * 4;
                if (_tKind[t] == 2)
                {
                    Quaternion q = Quaternion.Slerp(new Quaternion(_from[o], _from[o + 1], _from[o + 2], _from[o + 3]), new Quaternion(_to[o], _to[o + 1], _to[o + 2], _to[o + 3]), a);
                    _cur[o] = q.x; _cur[o + 1] = q.y; _cur[o + 2] = q.z; _cur[o + 3] = q.w;
                }
                else
                {
                    for (int k = 0; k < 4; k++) _cur[o + k] = _from[o + k] + (_to[o + k] - _from[o + k]) * a;
                }
                WriteTarget(t, _cur, o);
                if (a < 1f) { _act[w] = t; w++; }
                else _isAct[t] = 0;
            }
            _actCount = w;
        }

        // Branch for the current mode and gestures; the expression that goes with it.
        int ResolveBranch(int l, int r)
        {
            if (_mode < 0 || _mode >= _modeCount) return -1;
            return _modeTable[_mode * 64 + l * 8 + r];
        }

        int ResolveExpression(int branch)
        {
            if (_mode < 0 || _mode >= _modeCount) return -1;
            if (branch >= 0 && _branchBase[branch] >= 0) return _branchBase[branch];
            return _modeAnim[_mode];
        }

        // ================= update =================

        void Update()
        {
            if (_ready == 0) return;
#if FACEEMO_FREEHAND
            if (_freehand != null) _freehand.PumpEvents();
#endif
            StepBlend();
            if (Time.time < _nextEval) return;
            _nextEval = Time.time + EvaluateInterval;

            int l = Classify(0, _leftHand, _leftBones);
            int r = Classify(1, _rightHand, _rightBones);
            if (_overrideL >= 0) l = _overrideL;
            if (_overrideR >= 0) r = _overrideR;
            int branch = ResolveBranch(l, r);
            if (l != _leftGesture || r != _rightGesture || branch != _branch)
            {
                _leftGesture = l; _rightGesture = r; _branch = branch;
                if (LogChanges) Debug.Log("FaceEmo gestures L=" + l + " R=" + r + " mode=" + _mode + " branch=" + branch);
            }
            ShowExpression(_emoteExpr >= 0 ? _emoteExpr : ResolveExpression(branch));
        }

        // ================= Freehand menu =================
        // Needs a Cilbox-callable Freehand shim (HVR.Freehand is not whitelisted in the avatar box yet). Define FACEEMO_FREEHAND
        // and reference the HVR.Freehand assembly from jp.suzuryg.face-emo.basis.Runtime.asmdef once it is.
#if FACEEMO_FREEHAND

        void BuildFreehand()
        {
            _freehand = new BasisFreehandMenuShim(this);
            _freehand.SetFallbackLanguage("en");
            Dictionary<string, string> en = new Dictionary<string, string>();
            en.Add("faceemo.title", "FaceEmo");
            en.Add("faceemo.emote", "Emote Select");
            en.Add("faceemo.off", "Off (use hands)");
            _freehand.AddLocalization("en", en);

            if (_groupCount > MaxSubmenus) Debug.Log("FaceEmo: " + _groupCount + " groups, Freehand allows " + MaxSubmenus + " sub-menus per root; the menu is not built");
            else
            {
                FreehandRootMenu root = new FreehandRootMenu(PropUuid, "faceemo", "faceemo.title", BuildGroupMenu(-1));
                if (!_freehand.AddRoot(root)) Debug.Log("FaceEmo: Freehand refused the mode menu");
            }

            // Emote Select, laid out like FaceEmo's: the groups, then a folder per mode with its default face and its branches.
            FreehandMenu es = new FreehandMenu();
            es.Layout = FreehandLayout.RadialClockwise;
            FreehandItem off = new FreehandItem("es_off", "faceemo.off", OnItemPressed);
            off.Icon = FreehandIconRef.FromName("back_hand");
            es.Items.Add(off);
            AddEmoteItems(es, -1);
            if (es.Items.Count > 1)
            {
                FreehandRootMenu esRoot = new FreehandRootMenu(PropUuid, "faceemo-emote", "faceemo.emote", es);
                if (!_freehand.AddRoot(esRoot)) Debug.Log("FaceEmo: Freehand refused the Emote Select menu");
            }
            _freehand.AddLocalization("en", _titles);
            _freehand.OnIconRequested += OnIconRequested;
            MarkSelected(-1, _mode);
        }

        // A sub-menu is titled by the localization key of its item (the item id when none is given, which showed "em0"):
        // the label of these items is a raw name, so register the name as the text of a key of its own.
        Dictionary<string, string> _titles = new Dictionary<string, string>();
        string TitleKey(string itemId, string name)
        {
            string key = "faceemo.t." + itemId;
            _titles[key] = name;
            return key;
        }

        FreehandMenu BuildGroupMenu(int group)
        {
            FreehandMenu menu = new FreehandMenu();
            menu.Layout = FreehandLayout.RadialClockwise;
            for (int i = 0; i < _itemCount; i++)
            {
                if (_itemGroup[i] != group) continue;
                if (_itemIsGroup[i] == 1)
                {
                    int gi = _itemIndex[i];
                    FreehandItem gitem = new FreehandItem("g" + gi, TitleKey("g" + gi, _groupName[gi]), OnItemPressed);
                    gitem.LabelRaw = _groupName[gi];
                    gitem.Icon = FreehandIconRef.FromName("folder");
                    gitem.Submenu = BuildGroupMenu(gi);
                    menu.Items.Add(gitem);
                }
                else
                {
                    int mi = _itemIndex[i];
                    FreehandItem mitem = new FreehandItem("m" + mi, OnItemPressed);
                    mitem.LabelRaw = _modeName[mi];
                    if (HasIcon(_modeIcon[mi])) mitem.CustomIcon = "m" + mi;
                    else mitem.Icon = FreehandIconRef.FromName("mood");
                    menu.Items.Add(mitem);
                }
            }
            return menu;
        }

        bool HasIcon(int ic)
        {
            return ic >= 0 && Icons != null && ic < Icons.Length && Icons[ic] != null;
        }

        // The pictures the menu asks for, by the names given to CustomIcon: m<mode> and b<branch>.
        void OnIconRequested(string name)
        {
            int ic = -1;
            if (name.StartsWith("m")) { int i = I(name.Substring(1)); if (i >= 0 && i < _modeCount) ic = _modeIcon[i]; }
            else if (name.StartsWith("b")) { int i = I(name.Substring(1)); if (i >= 0 && i < _branchCount) ic = _branchIcon[i]; }
            if (HasIcon(ic)) _freehand.SendIconTexture(name, Icons[ic]);
            else _freehand.SkipIcon(name);
        }

        // Items of the Emote Select menu for the items of one group (-1 = root).
        void AddEmoteItems(FreehandMenu menu, int group)
        {
            for (int i = 0; i < _itemCount; i++)
            {
                if (_itemGroup[i] != group) continue;
                if (_itemIsGroup[i] == 1)
                {
                    int gi = _itemIndex[i];
                    FreehandMenu sub = new FreehandMenu();
                    sub.Layout = FreehandLayout.RadialClockwise;
                    AddEmoteItems(sub, gi);
                    if (sub.Items.Count == 0) continue;
                    FreehandItem gitem = new FreehandItem("eg" + gi, TitleKey("eg" + gi, _groupName[gi]), OnItemPressed);
                    gitem.LabelRaw = _groupName[gi];
                    gitem.Icon = FreehandIconRef.FromName("folder");
                    gitem.Submenu = sub;
                    menu.Items.Add(gitem);
                }
                else
                {
                    int mi = _itemIndex[i];
                    FreehandMenu sub = new FreehandMenu();
                    sub.Layout = FreehandLayout.RadialClockwise;
                    if ((_modeFlags[mi] & 64) != 0 && _modeAnim[mi] >= 0)
                    {
                        FreehandItem d = new FreehandItem("ed" + mi, OnItemPressed);
                        d.LabelRaw = _modeName[mi];
                        if (HasIcon(_modeIcon[mi])) d.CustomIcon = "m" + mi; else d.Icon = FreehandIconRef.FromName("mood");
                        sub.Items.Add(d);
                    }
                    int n = 0;
                    for (int c = 0; c < 64; c++)
                    {
                        int bi = _modeTable[mi * 64 + c];
                        if (bi < 0 || _branchBase[bi] < 0) continue;
                        bool seen = false;
                        for (int c2 = 0; c2 < c; c2++) if (_modeTable[mi * 64 + c2] == bi) seen = true;
                        if (seen) continue;
                        n++;
                        FreehandItem e = new FreehandItem("eb" + bi, OnItemPressed);
                        e.LabelRaw = "Emote " + n;
                        if (HasIcon(_branchIcon[bi])) e.CustomIcon = "b" + bi; else e.Icon = FreehandIconRef.FromName("mood");
                        sub.Items.Add(e);
                    }
                    if (sub.Items.Count == 0) continue;
                    FreehandItem mitem = new FreehandItem("em" + mi, TitleKey("em" + mi, _modeName[mi]), OnItemPressed);
                    mitem.LabelRaw = _modeName[mi];
                    mitem.Icon = FreehandIconRef.FromName("folder");
                    mitem.Submenu = sub;
                    menu.Items.Add(mitem);
                }
            }
        }

        string MenuIdOfMode(int mode)
        {
            for (int i = 0; i < _itemCount; i++)
                if (_itemIsGroup[i] == 0 && _itemIndex[i] == mode) return _itemGroup[i] < 0 ? "" : "g" + _itemGroup[i];
            return "";
        }

        // A dot in front of the selected mode.
        void MarkSelected(int oldMode, int newMode)
        {
            if (_freehand == null) return;
            if (oldMode >= 0 && oldMode < _modeCount) _freehand.SetLabelRaw(MenuIdOfMode(oldMode), "m" + oldMode, _modeName[oldMode]);
            if (newMode >= 0 && newMode < _modeCount) _freehand.SetLabelRaw(MenuIdOfMode(newMode), "m" + newMode, "\u25CF " + _modeName[newMode]);
        }

        void OnItemPressed(FreehandInteractionEvent e)
        {
            string id = e.Id;
            if (id.StartsWith("m"))
            {
                int mode = I(id.Substring(1));
                if (mode >= 0 && mode < _modeCount) SelectMode(mode);
            }
            else if (id == "es_off")
            {
                SelectEmote(-1);
            }
            else if (id.StartsWith("ed"))
            {
                int mode = I(id.Substring(2));
                if (mode >= 0 && mode < _modeCount) SelectEmote(_modeAnim[mode]);
            }
            else if (id.StartsWith("eb"))
            {
                int branch = I(id.Substring(2));
                if (branch >= 0 && branch < _branchCount) SelectEmote(_branchBase[branch]);
            }
        }
#else
        void MarkSelected(int oldMode, int newMode) { }
#endif

        // Mode and Emote Select entry points: used by the Freehand menu, callable by anything else.
        void SelectMode(int mode)
        {
            if (mode == _mode) return;
            MarkSelected(_mode, mode);
            _mode = mode;
            _nextEval = 0f;
            SendState();
        }

        void SelectEmote(int expr)
        {
            if (expr >= _exprCount) expr = -1;
            _emoteExpr = expr;
            _nextEval = 0f;
            SendState();
        }

        void SetOverride(int l, int r)
        {
            _overrideL = l; _overrideR = r;
            _nextEval = 0f;
            SendState();
        }

        // ================= network =================
        // Message 1: [1, modeLo, modeHi, overrideL + 1, overrideR + 1, emoteLo, emoteHi] (emote = expression + 1, 0 = none). Only the wearer sends; gestures need no sync because
        // every client reads them from the finger bones.

        byte[] StateMessage()
        {
            byte[] m = new byte[7];
            m[0] = 1;
            m[1] = (byte)(_mode & 255);
            m[2] = (byte)((_mode >> 8) & 255);
            m[3] = (byte)(_overrideL + 1);
            m[4] = (byte)(_overrideR + 1);
            m[5] = (byte)((_emoteExpr + 1) & 255);
            m[6] = (byte)(((_emoteExpr + 1) >> 8) & 255);
            return m;
        }

        void SendState()
        {
            if (_net == null || _isOwner == 0) return;
            if (!_net.HasNetworkID) { _netSendPending = 1; return; }
            _net.SendCustomNetworkEvent(StateMessage(), DeliveryMethod.ReliableOrdered);
        }

        void OnNetworkReady()
        {
            if (_netSendPending == 1) { _netSendPending = 0; SendState(); }
        }

        void OnPlayerJoined(BasisNetworkPlayer player)
        {
            if (_net == null || _isOwner == 0 || !_net.HasNetworkID) return;
            _net.SendCustomNetworkEvent(StateMessage(), DeliveryMethod.ReliableOrdered, new ushort[] { player.playerId });
        }

        void OnNetworkMessage(ushort sender, byte[] buffer, DeliveryMethod method)
        {
            if (_isOwner == 1 || buffer == null || buffer.Length < 5 || buffer[0] != 1) return;
            int mode = buffer[1] + buffer[2] * 256;
            if (mode < _modeCount) _mode = mode;
            _overrideL = buffer[3] - 1;
            _overrideR = buffer[4] - 1;
            if (_overrideL > 7 || _overrideR > 7) { _overrideL = -1; _overrideR = -1; }
            _emoteExpr = -1;
            if (buffer.Length >= 7)
            {
                int emote = buffer[5] + buffer[6] * 256 - 1;
                if (emote >= 0 && emote < _exprCount) _emoteExpr = emote;
            }
            _nextEval = 0f;
        }

        // Total curl of one finger in degrees: knuckle bend + middle joint bend. Independent of the rest pose.
        float Curl(Transform hand, Transform prox, Transform mid, Transform dist)
        {
            if (hand == null || prox == null || mid == null || dist == null) return 0f;
            Vector3 a = prox.position - hand.position;
            Vector3 b = mid.position - prox.position;
            Vector3 c = dist.position - mid.position;
            return Vector3.Angle(a, b) + Vector3.Angle(b, c);
        }

        // A sign constrains index, middle, ring and little. A finger the avatar does not have constrains nothing: only the
        // fingers that exist decide. If that makes two signs identical, the first one in the list below wins.
        bool Ok(int s, int role, int want)
        {
            return _present[s + role] == 0 || _curled[s + role] == want;
        }

        // Returns a FaceEmo HandGesture (0 Neutral, 1 Fist, 2 HandOpen, 3 Fingerpoint, 4 Victory, 5 RockNRoll, 6 HandGun, 7 ThumbsUp)
        int Classify(int hand, Transform wrist, Transform[] bones)
        {
            if (_hasFingers[hand] == 0) return 0;
            int s = hand * 5;
            for (int f = 0; f < 5; f++)
            {
                if (_present[s + f] == 0) continue;
                float curl = Curl(wrist, bones[f * 3], bones[f * 3 + 1], bones[f * 3 + 2]);
                float on = f == 0 ? ThumbCurlOn : CurlOn;
                float off = f == 0 ? ThumbCurlOff : CurlOff;
                if (_curled[s + f] == 0 && curl > on) _curled[s + f] = 1;
                else if (_curled[s + f] == 1 && curl < off) _curled[s + f] = 0;
            }
            int thumb = _present[s] == 1 ? _curled[s] : 1; // no thumb counts as curled, so Fist / Fingerpoint / Victory still work

            if (Ok(s, 1, 0) && Ok(s, 2, 0) && Ok(s, 3, 0) && Ok(s, 4, 0)) return 2;
            if (Ok(s, 1, 1) && Ok(s, 2, 1) && Ok(s, 3, 1) && Ok(s, 4, 1)) return thumb == 0 ? 7 : 1;
            if (Ok(s, 1, 0) && Ok(s, 2, 1) && Ok(s, 3, 1) && Ok(s, 4, 1)) return thumb == 0 ? 6 : 3;
            if (Ok(s, 1, 0) && Ok(s, 2, 0) && Ok(s, 3, 1) && Ok(s, 4, 1)) return 4;
            if (Ok(s, 1, 0) && Ok(s, 2, 1) && Ok(s, 3, 1) && Ok(s, 4, 0)) return 5;
            return 0;
        }
    }
}
