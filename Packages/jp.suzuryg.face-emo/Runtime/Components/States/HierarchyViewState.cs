using UnityEngine;

namespace Suzuryg.FaceEmo.Components.States
{
    public class HierarchyViewState : ScriptableObject
    {
#if UNITY_EDITOR
#if UNITY_6000_2_OR_NEWER
        public UnityEditor.IMGUI.Controls.TreeViewState<int> TreeViewState;
#else
        public UnityEditor.IMGUI.Controls.TreeViewState TreeViewState;
#endif
#endif
    }
}
