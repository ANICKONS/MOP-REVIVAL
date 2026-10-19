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

namespace HutongGames.PlayMaker.Actions
{
    public partial class FloatCompare : HutongGames.PlayMaker.FsmStateAction { }
    public partial class Wait : HutongGames.PlayMaker.FsmStateAction { }
    public partial class ActivateGameObject : HutongGames.PlayMaker.FsmStateAction { }
}
