// Hand-written overlay for the PlayMaker compile-only stub (PlayMaker 1.6.0.0, as shipped with My Summer Car).
// IMPORTANT: in PlayMaker 1.6 `Fsm.RestartOnEnable` is a public FIELD (proven by the original MOP.dll metadata).
// Anything not proven by a compiled binary should be accessed through reflection at runtime.
// stub-depends: UnityEngine
#pragma warning disable

public partial class PlayMakerFSM : UnityEngine.MonoBehaviour { }
public partial class PlayMakerGlobals : UnityEngine.ScriptableObject { }

// Verified against the decompiled PlayMaker 1.6 shipped with MSC: Fsm.StartState (get/set),
// FsmTransition.EventName (get), FsmTransition.ToState (get/set).
// stub-skip: FsmTransition.ToState
//
// Engine Lab (src/MOPR/src/Next/Engine), verified against the decompiled PlayMaker 1.6 (_reference/decomp/PlayMaker):
//   Fsm.Finished {get; private set}, Fsm.Owner {get; set}, Fsm.DelayedEvents {get}, Fsm.States {get; set},
//   static Fsm.HitBreakpoint {get; set}, FsmState.loopCount/StateTime {get; private set}, FsmState.Active {get},
//   FsmStateAction.State/Owner {get; set}, static class FsmExecutionStack (StackCount, MaxStackCount {get}),
//   static FsmVariables.GlobalVariablesSynced {get; set}, static class FsmTime (RealtimeBugFix()), class DelayedEvent.
// stub-skip: Fsm.States
//
// FsmProperty fast path (Next/Engine/FsmPropertyFastPath.cs), same source: FsmProperty public fields and
// GetValue/SetValue/CheckForReinitialize, NamedVariable.UseVariable/IsNone, FsmObject.ObjectType/Value,
// the Value properties of FsmVector2/FsmVector3/FsmRect/FsmQuaternion/FsmColor/FsmInt and the "new Value"
// of FsmMaterial (Material) and FsmTexture (Texture).
// stub-skip: FsmInt.Value

namespace HutongGames.PlayMaker
{
    public partial class Fsm
    {
        public string StartState { get { throw null; } set { } }
        public bool Finished { get { throw null; } }
        public global::UnityEngine.MonoBehaviour Owner { get { throw null; } set { } }
        public global::System.Collections.Generic.List<DelayedEvent> DelayedEvents { get { throw null; } }
        public FsmState[] States { get { throw null; } set { } }
        public static bool HitBreakpoint { get { throw null; } set { } }
    }
    public partial class FsmState
    {
        public int loopCount { get { throw null; } }
        public float StateTime { get { throw null; } }
        public bool Active { get { throw null; } }
    }
    public partial class FsmStateAction
    {
        public FsmState State { get { throw null; } set { } }
        public global::UnityEngine.GameObject Owner { get { throw null; } set { } }
    }
    public static class FsmExecutionStack
    {
        public static int StackCount { get { throw null; } }
        public static int MaxStackCount { get { throw null; } }
    }
    public static class FsmTime
    {
        public static void RealtimeBugFix() { throw null; }
    }
    public class DelayedEvent { }
    public partial class FsmEvent { }
    public partial class FsmTransition
    {
        public string EventName { get { throw null; } }
        public string ToState { get { throw null; } set { } }
    }
    public partial class FsmVariables
    {
        public static bool GlobalVariablesSynced { get { throw null; } set { } }
    }
    // Evaluated actions (Next/Engine/FsmFastPath.cs, 5.1), same source: FsmOwnerDefault.OwnerOption {get; set},
    // enum OwnerDefaultOption, Fsm.RaycastHitInfo {get; set}, ActionHelpers.LayerArrayToLayerMask(FsmInt[], bool).
    public partial class FsmOwnerDefault
    {
        public OwnerDefaultOption OwnerOption { get { throw null; } set { } }
    }
    public enum OwnerDefaultOption
    {
        UseOwner,
        SpecifyGameObject
    }
    public partial class Fsm
    {
        public global::UnityEngine.RaycastHit RaycastHitInfo { get { throw null; } set { } }
    }

    // Shared mouse ray (Next/Engine/SharedMouseRay.cs), verified against the decompiled PlayMaker 1.6
    // (_reference/decomp/PlayMaker/HutongGames.PlayMaker/ActionHelpers.cs).
    public static partial class ActionHelpers
    {
        public static global::UnityEngine.RaycastHit mousePickInfo;
        public static bool IsMouseOver(global::UnityEngine.GameObject gameObject, float distance, int layerMask) { throw null; }
        public static global::UnityEngine.RaycastHit MousePick(float distance, int layerMask) { throw null; }
        public static global::UnityEngine.GameObject MouseOver(float distance, int layerMask) { throw null; }
        public static int LayerArrayToLayerMask(FsmInt[] layers, bool invert) { throw null; }
    }

    public partial class NamedVariable
    {
        public bool UseVariable { get { throw null; } set { } }
        public bool IsNone { get { throw null; } set { } }
    }
    public partial class FsmFloat : NamedVariable { }
    public partial class FsmInt : NamedVariable
    {
        public int Value { get { throw null; } set { } }
    }
    public partial class FsmBool : NamedVariable { }
    public partial class FsmString : NamedVariable { }
    public partial class FsmGameObject : NamedVariable { }
    public partial class FsmColor : NamedVariable
    {
        public global::UnityEngine.Color Value { get { throw null; } set { } }
    }
    public partial class FsmVector2 : NamedVariable
    {
        public global::UnityEngine.Vector2 Value { get { throw null; } set { } }
    }
    public partial class FsmVector3 : NamedVariable
    {
        public global::UnityEngine.Vector3 Value { get { throw null; } set { } }
    }
    public partial class FsmRect : NamedVariable
    {
        public global::UnityEngine.Rect Value { get { throw null; } set { } }
    }
    public partial class FsmQuaternion : NamedVariable
    {
        public global::UnityEngine.Quaternion Value { get { throw null; } set { } }
    }
    public partial class FsmObject : NamedVariable
    {
        public global::System.Type ObjectType { get { throw null; } set { } }
        public global::UnityEngine.Object Value { get { throw null; } set { } }
    }
    public partial class FsmMaterial : FsmObject
    {
        public new global::UnityEngine.Material Value { get { throw null; } set { } }
    }
    public partial class FsmTexture : FsmObject
    {
        public new global::UnityEngine.Texture Value { get { throw null; } set { } }
    }
    public class FsmProperty
    {
        public FsmObject TargetObject;
        public string TargetTypeName;
        public global::System.Type TargetType;
        public string PropertyName;
        public global::System.Type PropertyType;
        public FsmBool BoolParameter;
        public FsmFloat FloatParameter;
        public FsmInt IntParameter;
        public FsmGameObject GameObjectParameter;
        public FsmString StringParameter;
        public FsmVector2 Vector2Parameter;
        public FsmVector3 Vector3Parameter;
        public FsmRect RectParamater;
        public FsmQuaternion QuaternionParameter;
        public FsmObject ObjectParameter;
        public FsmMaterial MaterialParameter;
        public FsmTexture TextureParameter;
        public FsmColor ColorParameter;
        public bool setProperty;
        public void GetValue() { throw null; }
        public void SetValue() { throw null; }
        public void CheckForReinitialize() { throw null; }
    }
}
