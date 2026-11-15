using Suzuryg.FaceEmo.Components.Settings;
using Suzuryg.FaceEmo.Domain;
using Suzuryg.FaceEmo.UseCase;
using System.Collections.Generic;

namespace Suzuryg.FaceEmo.Detail.AV3
{
    // Replaces the VRChat FxGenerator. Instead of building an FX AnimatorController and an expression menu,
    // the baker turns the FaceEmo menu into data for the fixed Cilbox runtime script (phase 2, not written yet).
    public class BasisBaker : IFxGenerator
    {
        private AV3Setting _aV3Setting;

        public BasisBaker(AV3Setting aV3Setting)
        {
            _aV3Setting = aV3Setting;
        }

        public List<string> GetParentPrefabPathOfMARootObjects() => new List<string>();

        public void Generate(IMenu menu, IEnumerable<string> editablePrefabPaths)
        {
            throw new FaceEmoException("Baking for Basis is not implemented yet.");
        }
    }
}
