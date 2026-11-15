using System.Globalization;
using Basis.Scripts.BasisSdk;
using Cilbox;
using UnityEngine;

namespace Suzuryg.FaceEmo.Baked
{
    // The ONE fixed script every FaceEmo avatar carries. All per-avatar settings are DATA (FE1 text + the object arrays
    // that the text indexes), written by BasisBaker. See design_docs/BASIS_BAKED_FORMAT.md.
    // Written for Cilbox: no bool[], no generics, no big array initializers, numbers parsed with InvariantCulture.
    // Step 3: gestures only (finger bones -> HandGesture -> branch index). Applying expressions comes in step 4.
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

        // ---- gesture tuning (degrees of total finger curl; hysteresis between On and Off) ----
        public float CurlOn = 100f;
        public float CurlOff = 70f;
        public float ThumbCurlOn = 40f;
        public float ThumbCurlOff = 25f;
        public float EvaluateInterval = 0.1f;
        public bool LogChanges = true;

        // ---- parsed data ----
        int _modeCount;
        int[] _modeTable;     // modeCount * 64 : branch index or -1
        int _branchCount;
        int[] _branchBase;
        int _defaultMode;

        // ---- finger state ----
        BasisAvatar _avatar;
        Transform[] _leftBones;   // 15 = 5 fingers x (proximal, intermediate, distal)
        Transform[] _rightBones;
        Transform _leftHand;
        Transform _rightHand;
        byte[] _curled;           // 10 = hand * 5 + finger; 1 = curled
        float _nextEval;
        int _leftGesture;
        int _rightGesture;
        int _mode;
        int _branch = -2;
        byte _ready;

        void Start()
        {
            _avatar = BasisAvatar.GetGameObject(this).GetComponent<BasisAvatar>();
            _avatar.OnAvatarReady += OnReady;
        }

        void OnReady(bool isOwner)
        {
            Parse();
            Animator animator = _avatar.Animator;
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
            _curled = new byte[10];
            _mode = _defaultMode;
            _ready = 1;
        }

        void Parse()
        {
            _modeCount = 0; _branchCount = 0; _defaultMode = -1;
            if (Data == null) return;
            string[] lines = Data.text.Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("M|")) _modeCount++;
                else if (lines[i].StartsWith("B|")) _branchCount++;
            }
            _modeTable = new int[_modeCount * 64];
            _branchBase = new int[_branchCount];
            int m = 0; int b = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length < 2) continue;
                string[] f = line.Split('|');
                if (f[0] == "M")
                {
                    string[] cells = f[4].Split(',');
                    for (int c = 0; c < 64; c++) _modeTable[m * 64 + c] = int.Parse(cells[c], CultureInfo.InvariantCulture);
                    m++;
                }
                else if (f[0] == "B")
                {
                    _branchBase[b] = int.Parse(f[1], CultureInfo.InvariantCulture);
                    b++;
                }
                else if (f[0] == "D")
                {
                    _defaultMode = int.Parse(f[1], CultureInfo.InvariantCulture);
                }
            }
        }

        void Update()
        {
            if (_ready == 0) return;
            if (Time.time < _nextEval) return;
            _nextEval = Time.time + EvaluateInterval;

            int l = Classify(0, _leftHand, _leftBones);
            int r = Classify(1, _rightHand, _rightBones);
            int branch = -1;
            if (_mode >= 0 && _mode < _modeCount) branch = _modeTable[_mode * 64 + l * 8 + r];
            if (l != _leftGesture || r != _rightGesture || branch != _branch)
            {
                _leftGesture = l; _rightGesture = r; _branch = branch;
                if (LogChanges) Debug.Log("FaceEmo gestures L=" + l + " R=" + r + " mode=" + _mode + " branch=" + branch);
            }
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

        // Returns a FaceEmo HandGesture (0 Neutral, 1 Fist, 2 HandOpen, 3 Fingerpoint, 4 Victory, 5 RockNRoll, 6 HandGun, 7 ThumbsUp)
        int Classify(int hand, Transform wrist, Transform[] bones)
        {
            for (int f = 0; f < 5; f++)
            {
                float curl = Curl(wrist, bones[f * 3], bones[f * 3 + 1], bones[f * 3 + 2]);
                float on = f == 0 ? ThumbCurlOn : CurlOn;
                float off = f == 0 ? ThumbCurlOff : CurlOff;
                int i = hand * 5 + f;
                if (_curled[i] == 0 && curl > on) _curled[i] = 1;
                else if (_curled[i] == 1 && curl < off) _curled[i] = 0;
            }
            int s = hand * 5;
            int thumb = _curled[s];
            int idx = _curled[s + 1];
            int mid = _curled[s + 2];
            int ring = _curled[s + 3];
            int lit = _curled[s + 4];

            if (idx == 0 && mid == 0 && ring == 0 && lit == 0) return 2;
            if (idx == 1 && mid == 1 && ring == 1 && lit == 1) return thumb == 0 ? 7 : 1;
            if (idx == 0 && mid == 1 && ring == 1 && lit == 1) return thumb == 0 ? 6 : 3;
            if (idx == 0 && mid == 0 && ring == 1 && lit == 1) return 4;
            if (idx == 0 && mid == 1 && ring == 1 && lit == 0) return 5;
            return 0;
        }
    }
}
