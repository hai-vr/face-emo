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
        string[] _modeName;
        int _branchCount;
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
        int _overrideL = -1;      // Emote Select: fixed gestures, -1 = use the hands
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
            _modeCount = 0; _branchCount = 0; _defaultMode = -1; _groupCount = 0; _itemCount = 0; _targetCount = 0; _exprCount = 0;
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
            _modeName = new string[_modeCount];
            _branchBase = new int[_branchCount];
            _groupName = new string[_groupCount];
            _itemGroup = new int[_itemCount];
            _itemIsGroup = new byte[_itemCount];
            _itemIndex = new int[_itemCount];
            _tKind = new int[_targetCount];
            _tRef = new int[_targetCount];
            _tSub = new int[_targetCount];
            _tRest = new float[_targetCount * 4];
            _mark = new int[_targetCount];
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

        // Shows expression e (-1 = none). Targets the previous expression set and this one does not are put back to rest.
        void ShowExpression(int e)
        {
            if (e == _currentExpr) return;
            _epoch++;
            if (e >= 0)
            {
                int s = _exStart[e];
                for (int k = 0; k < _exCount[e]; k++) _mark[_exTarget[s + k]] = _epoch;
            }
            if (_currentExpr >= 0)
            {
                int s0 = _exStart[_currentExpr];
                for (int k = 0; k < _exCount[_currentExpr]; k++)
                {
                    int t = _exTarget[s0 + k];
                    if (_mark[t] != _epoch) WriteTarget(t, _tRest, t * 4);
                }
            }
            if (e >= 0)
            {
                int s = _exStart[e];
                for (int k = 0; k < _exCount[e]; k++) WriteTarget(_exTarget[s + k], _exVal, (s + k) * 4);
            }
            _currentExpr = e;
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
            ShowExpression(ResolveExpression(branch));
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

            // Emote Select: left gesture -> right gesture
            string[] names = GestureNames.Split(',');
            FreehandMenu es = new FreehandMenu();
            es.Layout = FreehandLayout.RadialClockwise;
            FreehandItem off = new FreehandItem("es_off", "faceemo.off", OnItemPressed);
            es.Items.Add(off);
            for (int l = 0; l < 8; l++)
            {
                FreehandItem left = new FreehandItem("esl" + l, OnItemPressed);
                left.LabelRaw = "L " + names[l];
                FreehandMenu sub = new FreehandMenu();
                sub.Layout = FreehandLayout.RadialClockwise;
                for (int r = 0; r < 8; r++)
                {
                    FreehandItem right = new FreehandItem("esr" + l + "_" + r, OnItemPressed);
                    right.LabelRaw = "R " + names[r];
                    sub.Items.Add(right);
                }
                left.Submenu = sub;
                es.Items.Add(left);
            }
            FreehandRootMenu esRoot = new FreehandRootMenu(PropUuid, "faceemo-emote", "faceemo.emote", es);
            if (!_freehand.AddRoot(esRoot)) Debug.Log("FaceEmo: Freehand refused the Emote Select menu");
            MarkSelected(-1, _mode);
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
                    FreehandItem gitem = new FreehandItem("g" + gi, OnItemPressed);
                    gitem.LabelRaw = _groupName[gi];
                    gitem.Submenu = BuildGroupMenu(gi);
                    menu.Items.Add(gitem);
                }
                else
                {
                    int mi = _itemIndex[i];
                    FreehandItem mitem = new FreehandItem("m" + mi, OnItemPressed);
                    mitem.LabelRaw = _modeName[mi];
                    menu.Items.Add(mitem);
                }
            }
            return menu;
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
                SetOverride(-1, -1);
            }
            else if (id.StartsWith("esr"))
            {
                string[] p = id.Substring(3).Split('_');
                SetOverride(I(p[0]), I(p[1]));
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

        void SetOverride(int l, int r)
        {
            _overrideL = l; _overrideR = r;
            _nextEval = 0f;
            SendState();
        }

        // ================= network =================
        // Message 1: [1, modeLo, modeHi, overrideL + 1, overrideR + 1]. Only the wearer sends; gestures need no sync because
        // every client reads them from the finger bones.

        byte[] StateMessage()
        {
            byte[] m = new byte[5];
            m[0] = 1;
            m[1] = (byte)(_mode & 255);
            m[2] = (byte)((_mode >> 8) & 255);
            m[3] = (byte)(_overrideL + 1);
            m[4] = (byte)(_overrideR + 1);
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
