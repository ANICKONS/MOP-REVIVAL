// Hand-written overlay for the UnityEngine.UI compile-only stub (Unity 5.0.x).
// stub-depends: UnityEngine
#pragma warning disable

namespace UnityEngine.EventSystems
{
    public partial class UIBehaviour : UnityEngine.MonoBehaviour { }
    public partial class BaseEventData { }
    public partial class PointerEventData : BaseEventData { }
    public partial interface IEventSystemHandler { }
    public partial interface IDragHandler : IEventSystemHandler { }
}

namespace UnityEngine.UI
{
    public partial class Graphic : UnityEngine.EventSystems.UIBehaviour { }
    public partial class MaskableGraphic : Graphic { }
    public partial class Text : MaskableGraphic { }
    public partial class Image : MaskableGraphic { }
    public partial class RawImage : MaskableGraphic { }
    public partial class Selectable : UnityEngine.EventSystems.UIBehaviour
    {
        public enum Transition { None, ColorTint, SpriteSwap, Animation }
    }
    public partial class Button : Selectable
    {
        public partial class ButtonClickedEvent : UnityEngine.Events.UnityEvent { }
    }
    public partial class Toggle : Selectable
    {
        public partial class ToggleEvent : UnityEngine.Events.UnityEvent<bool> { }
    }
    public partial class Slider : Selectable
    {
        public partial class SliderEvent : UnityEngine.Events.UnityEvent<float> { }
    }
    public partial class InputField : Selectable
    {
        public enum ContentType { Standard, Autocorrected, IntegerNumber, DecimalNumber, Alphanumeric, Name, EmailAddress, Password, Pin, Custom }
        public partial class OnChangeEvent : UnityEngine.Events.UnityEvent<string> { }
    }
    public partial class ScrollRect : UnityEngine.EventSystems.UIBehaviour { }
    public partial class LayoutElement : UnityEngine.EventSystems.UIBehaviour
    {
        public float minHeight { get { throw null; } set { } }   // 5.1 settings layout (Common/SettingsUiPatcher.cs)
    }
    // Settings layout (5.1): two buttons on one row. All exist in Unity 5.0 UI.
    public abstract partial class LayoutGroup : UnityEngine.EventSystems.UIBehaviour { }
    public abstract partial class HorizontalOrVerticalLayoutGroup : LayoutGroup
    {
        public float spacing { get { throw null; } set { } }
        public bool childForceExpandWidth { get { throw null; } set { } }
        public bool childForceExpandHeight { get { throw null; } set { } }
    }
    public partial class HorizontalLayoutGroup : HorizontalOrVerticalLayoutGroup { }
    public partial class ToggleGroup : UnityEngine.EventSystems.UIBehaviour { }
}
