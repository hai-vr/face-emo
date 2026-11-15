![GitHub release (latest by date)](https://img.shields.io/github/v/release/suzuryg/face-emo?label=release)

<img alt="Logo" src="docs/static/img/root_page/title.png">

FaceEmo is a facial expression creation and configuration tool, converted for use in the [Basis Framework](http://basisvr.org/). It runs using the *Cilbox VM* instead of animator controllers.  
It allows for easy configuration of facial expression changes using hand gestures and the Freehand menu!

✅ ported · ⚠️ ported with limits · ➖ not needed in Basis · ❌ not ported

| | FaceEmo feature | Notes |
|---|---|---|
| ✅ | Menu, groups, modes and branches (data model) | Domain and UseCase layers kept as they were. Baked to FE1 data. |
| ✅ | Editor window for editing modes, groups and branches | SDK-free. "Apply to Avatar" runs the baker. |
| ✅ | Gesture conditions (left, right, either, both, one side, equals / not equal) | Precomputed at bake time into a lookup table. The runtime only does a lookup. |
| ✅ | Hand signs | Classified from finger curls in the fixed Cilbox script. Tested on real hands. |
| ✅ | Expression animations (base / left / right / both) | Clips are baked to blendshape tables, plus transform and on/off targets. No Animator. |
| ✅ | Fixed Cilbox script (one for every avatar) | `FaceEmoRuntime`, with the data baked in. |
| ✅ | Transition duration | Continuous targets lerp, rotations slerp, on/off targets switch at once. Interrupted blends continue from the current value. |
| ✅ | Default mode and gesture override | The default mode is selected on load. `SelectMode` is public. `SetOverride` forces the left and right gestures (API only, synced to other players). |
| ✅ | Network sync | A few bytes (mode, branch, emote selection), replacing VRChat synced parameters. |
| ✅ | Freehand radial menu replacing VRCExpressionsMenu | Groups, modes and an Emote Select sub-menu, behind `FACEEMO_FREEHAND`. Tested in a client. |
| ⚠️ | Menu icons | Folder and smile icons, plus face thumbnails rendered by the baker. Thumbnail items show no text, and the "selected" dot is invisible on them. |
| ⚠️ | Emote Select | Pick any expression from the menu (mode, then branch) to show it regardless of hand gestures; "Off" returns to hand control. Synced to other players. Tested in a client. Thumbnail items show no text label. |
| ⚠️ | Import of FaceEmo-prepared prefabs | Works for the avatar tested so far. Missing VRChat scripts on other prefabs may still need cleanup. |
| ➖ | Tests that needed VRChat avatars | Replaced by editor tests of the baked format and the runtime. |
| ➖ | Generated FX animator and layers (`FxGenerator`) | Replaced by design: the baker writes data and the Cilbox script reads it. |
| ➖ | Modular Avatar and NDMF build plugins | VRChat build-time tools that merged the generated animator and menu into the avatar. Nothing to merge in Basis. |
| ➖ | AnimatorAsCode and ComboGestureIntegrator | Libraries for generating animator layers and integrating with a gesture-controller tool. There is no Animator. |
| ➖ | Import buttons for existing FX or controllers | There are no VRChat FX controllers to import. |
| ❌ | Root menu logo | A Freehand root takes only a title. |
| ❌ | Eye and mouth tracking control (VRChat tracking behaviours) | Would need a Basis eye or viseme override. Undecided. |
| ❌ | Blink and mouth-morph canceler | Flags are parsed, but Basis has no hook for them. |
| ❌ | Analog trigger animations (fist-trigger winks) | Not started. The trigger value is already available in Basis. |
| ❌ | AFK face | Deferred. |
| ❌ | Dance gimmick | Deferred. |
| ❌ | Emote lock | Deferred. |
| ❌ | Voice wait | Deferred. |
| ❌ | Contact receivers (nadenade lock) | Dropped for v1. |

[![video (en)](docs/static/img/readme/video_en.png)](https://www.youtube.com/embed/hZIHdWUONHM?rel=0)

For more information, check out the [documentation](https://suzuryg.github.io/face-emo/).

---

FaceEmoは[Basis Framework](http://basisvr.org/)で使えるように変換された、表情の作成・設定ツールです。アニメーターコントローラーの代わりに *Cilbox VM* で動作します。  
ハンドジェスチャーとFreehandメニューを組み合わせた表情切り替えを簡単に設定できます！

[![video (jp)](docs/static/img/readme/video_jp.png)](https://www.youtube.com/embed/Y3taqFjOjZ8?rel=0)

詳細については[ドキュメント](https://suzuryg.github.io/face-emo/ja/)をご参照ください。

---

This repository uses symbolic links. To clone and use this project correctly on Windows, you must perform the following two steps before cloning:

#### 1. Enable Symlink Support in Git
By default, Git on Windows may check out symlinks as plain text files. To prevent this, open your terminal and run:
```bash
git config --global core.symlinks true

```

#### 2. Enable Developer Mode in Windows

Windows restricts the creation of symlinks for standard users, which will cause a `Permission denied` error during setup. To fix this permanently:

1. Open Windows **Settings**.
2. Go to **Privacy & security** > **For developers**.
3. Turn on the **Developer Mode** toggle.
