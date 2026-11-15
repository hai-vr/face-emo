# FaceEmo Basis baked data format v1 (`FE1`)

One TextAsset per avatar, parsed by the single fixed Cilbox script. Line oriented,
`|` separates fields, `;` separates list items, `,` separates numbers, `:` pairs target:value.
Text fields escape `%`->`%25`, `|`->`%7C`, `;`->`%3B`, `:`->`%3A`, `,`->`%2C`, newline->`%0A`.
Numbers: invariant culture, `R` round-trip.

```
FE1
T|kind|ref|sub|rest          target (index = order of T lines)
X|name|t:v,v;t:v             expression: values for targets; absent targets revert to rest
B|base|left|right|both|flags branch (global index = order); expression index or -1
M|name|anim|flags|table      mode; anim = mode default expression or -1;
                             table = 64 branch indices, cell = left*8+right, -1 = none
G|name|parent                group; parent = group index or -1 (root)
I|group|type|index           ordered menu item in group (-1 = root); type M or G
D|mode                       default mode index or -1
```
Target kinds (`ref` indexes the Cilbox script's typed array of that kind; `sub` as noted; `rest` = value count):
| kind | array | sub | values |
|---|---|---|---|
| 0 BlendShape | SkinnedMeshRenderer[] | blendshape index | 1 (weight) |
| 1 Position | Transform[] | - | 3 local |
| 2 Rotation | Transform[] | - | 4 local quaternion xyzw |
| 3 Scale | Transform[] | - | 3 local |
| 4 Active | GameObject[] | - | 1 (0/1) |
| 5 BehaviourEnabled | Behaviour[] | - | 1 |
| 6 RendererEnabled | Renderer[] | - | 1 |

Flags (bit set): 1 leftTrigger, 2 rightTrigger, 4 blink, 8 mouthMorphCanceler, 16 eyeAnimation, 32 mouthAnimation, 64 changeDefaultFace (modes).
Gestures 0-7 = FaceEmo HandGesture; Hand semantics are resolved by the baker into the 64-cell table, so runtime only indexes it.
Hierarchy: groups max 8 items, so menus fit Freehand's 16 items/menu; sub-menu count may exceed 32 (runtime splits).
