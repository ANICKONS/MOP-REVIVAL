// Hand-written overlay for the Assembly-CSharp compile-only stub (My Summer Car game scripts).
// stub-depends: UnityEngine
// stub-depends: PlayMaker
#pragma warning disable

public partial class CarDynamics : UnityEngine.MonoBehaviour { }
public partial class Axles : UnityEngine.MonoBehaviour { }
public partial class Drivetrain : UnityEngine.MonoBehaviour { }
public partial class Wheel : UnityEngine.MonoBehaviour { }
public partial class CarController : UnityEngine.MonoBehaviour { }
public partial class AxisCarController : CarController { }
public partial class EventSounds : UnityEngine.MonoBehaviour { }
public partial class MasterAudio : UnityEngine.MonoBehaviour { }
public partial class PlayMakerCollectionProxy : UnityEngine.MonoBehaviour { }
public partial class PlayMakerArrayListProxy : PlayMakerCollectionProxy { }
public partial class Deformable : UnityEngine.MonoBehaviour { }   // body panels that dent (5.1 mesh merger skips them)

// The game's CCD IK (suspension, jacks, tractor and flatbed cylinders). Public fields as decompiled
// (_reference/decomp/actions/SimpleIKSolver.cs); used by src/MOPR/src/Next/Engine/IkFastPath.cs (5.1).
public partial class SimpleIKSolver : UnityEngine.MonoBehaviour
{
    public bool IsActive;
    public UnityEngine.Transform Target;
    public JointEntity[] JointEntities;
    public bool IsDamping;
    public float DampingMax;
    public partial class JointEntity
    {
        public UnityEngine.Transform Joint;
        public AngleRestriction AngleRestrictionRange;
    }
    public partial class AngleRestriction
    {
        public bool xAxis;
        public float xMin;
        public float xMax;
        public bool yAxis;
        public float yMin;
        public float yMax;
        public bool zAxis;
        public float zMin;
        public float zMax;
    }
}

// Evaluated actions (src/MOPR/src/Next/Engine/FsmFastPath.cs, 5.1): public fields as in the decompiled
// actions (_reference/decomp/actions/MousePickEvent.cs, BoolTest.cs, FloatCompare.cs). FloatCompare.float2 is generated.
namespace HutongGames.PlayMaker.Actions
{
    public partial class FloatCompare : HutongGames.PlayMaker.FsmStateAction
    {
        public HutongGames.PlayMaker.FsmFloat float1;
        public HutongGames.PlayMaker.FsmFloat tolerance;
        public HutongGames.PlayMaker.FsmEvent equal;
        public HutongGames.PlayMaker.FsmEvent lessThan;
        public HutongGames.PlayMaker.FsmEvent greaterThan;
        public bool everyFrame;
    }
    public partial class MousePickEvent : HutongGames.PlayMaker.FsmStateAction
    {
        public HutongGames.PlayMaker.FsmOwnerDefault GameObject;
        public HutongGames.PlayMaker.FsmFloat rayDistance;
        public HutongGames.PlayMaker.FsmEvent mouseOver;
        public HutongGames.PlayMaker.FsmEvent mouseDown;
        public HutongGames.PlayMaker.FsmEvent mouseUp;
        public HutongGames.PlayMaker.FsmEvent mouseOff;
        public HutongGames.PlayMaker.FsmInt[] layerMask;
        public HutongGames.PlayMaker.FsmBool invertMask;
        public bool everyFrame;
    }
    public partial class BoolTest : HutongGames.PlayMaker.FsmStateAction
    {
        public HutongGames.PlayMaker.FsmBool boolVariable;
        public HutongGames.PlayMaker.FsmEvent isTrue;
        public HutongGames.PlayMaker.FsmEvent isFalse;
        public bool everyFrame;
    }
    public partial class Wait : HutongGames.PlayMaker.FsmStateAction { }
    public partial class ActivateGameObject : HutongGames.PlayMaker.FsmStateAction { }
}
