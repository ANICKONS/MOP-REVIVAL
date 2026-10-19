// Hand-written overlay for the UnityEngine compile-only stub (Unity 5.0.x, as shipped with My Summer Car).
// Declares inheritance, enums, delegates and members that the generated stub cannot infer from metadata.
// Only add APIs that exist in Unity 5.0. Values of enum members are informational (stubs are never run).
#pragma warning disable

// stub-skip: Camera.farClipPlane
// stub-skip: Renderer.shadowCastingMode
// stub-skip: Light.shadows

namespace UnityEngine
{
    public partial class Object
    {
        public int GetInstanceID() { throw null; }
    }
    public partial class Component : Object { }
    public partial class Behaviour : Component { }
    public partial class MonoBehaviour : Behaviour { }
    // Engine Lab (Next/Engine/RedundantWrites.cs). Both exist in Unity 5.0 with these exact signatures.
    public sealed partial class AudioListener : Behaviour
    {
        public static float volume { get { throw null; } set { } }
    }
    public sealed partial class PlayerPrefs
    {
        public static bool HasKey(string key) { throw null; }
    }
    public partial class ScriptableObject : Object { }
    public partial class GameObject : Object { }
    public partial class Transform : Component, System.Collections.IEnumerable { }
    public partial class RectTransform : Transform
    {
        public enum Axis { Horizontal, Vertical }
    }
    public partial class Rigidbody : Component { }
    public partial class Collider : Component { }
    public partial class BoxCollider : Collider { }
    public partial class MeshCollider : Collider { }
    public partial class SphereCollider : Collider { }
    public partial class CapsuleCollider : Collider { }
    public partial class WheelCollider : Collider { }
    public partial class CharacterController : Collider { }
    public partial class Renderer : Component { }
    public partial class MeshRenderer : Renderer { }
    public partial class SkinnedMeshRenderer : Renderer { }
    public partial class MeshFilter : Component { }
    public partial class Joint : Component { }
    public partial class HingeJoint : Joint { }
    public partial class FixedJoint : Joint { }
    public partial class ConfigurableJoint : Joint { }
    public partial class SpringJoint : Joint { }
    public partial class CharacterJoint : Joint { }
    public partial class Camera : Behaviour { }
    public partial class Light : Behaviour { }
    public partial class AudioSource : Behaviour { }
    public partial class AudioBehaviour : Behaviour { }
    public partial class Animation : Behaviour { }
    public partial class Animator : Behaviour { }
    public partial class Projector : Behaviour { }
    public partial class LODGroup : Component { }
    public partial class TextMesh : Component { }
    public partial class Material : Object { }
    public partial class Mesh : Object { }
    public partial class Shader : Object { }
    public partial class Texture : Object { }
    public partial class Texture2D : Texture { }
    public partial class Sprite : Object { }
    public partial class Font : Object { }
    public partial class AssetBundle : Object { }
    public partial class TextAsset : Object { }
    public partial class AudioClip : Object
    {
        public delegate void PCMReaderCallback(float[] data);
        public delegate void PCMSetPositionCallback(int position);
    }
    public partial class GUISkin : ScriptableObject { }
    public partial class YieldInstruction { }
    public partial class WaitForSeconds : YieldInstruction { }
    public partial class WaitForEndOfFrame : YieldInstruction { public WaitForEndOfFrame() { } }
    public partial class WaitForFixedUpdate : YieldInstruction { public WaitForFixedUpdate() { } }
    public partial class Coroutine : YieldInstruction { }
    public partial class AsyncOperation : YieldInstruction { }
    public partial class MissingReferenceException : System.Exception { }
    public partial class PropertyAttribute : System.Attribute { }
    public partial class HeaderAttribute : PropertyAttribute { }
    public partial class SerializeField : System.Attribute { }
    public partial class RequireComponent : System.Attribute { }

    // stub-skip: Application.targetFrameRate
    // stub-skip: Application.runInBackground
    public partial class Application
    {
        public delegate void LogCallback(string condition, string stackTrace, LogType type);
        public static int targetFrameRate { get { throw null; } set { } }
        public static bool runInBackground { get { throw null; } set { } }
        public static int loadedLevel { get { throw null; } }
    }

    // stub-skip: GameObject.layer
    public partial class GameObject
    {
        public int layer { get { throw null; } set { } }
    }

    public partial class Mathf
    {
        public const float PI = 3.14159274f;
        public const float Infinity = float.PositiveInfinity;
        public const float NegativeInfinity = float.NegativeInfinity;
        public const float Deg2Rad = 0.0174532924f;
        public const float Rad2Deg = 57.29578f;
        public const float Epsilon = 1.401298E-45f;
        public static float Clamp(float value, float min, float max) { throw null; }
        public static int Clamp(int value, int min, int max) { throw null; }
        public static float Clamp01(float value) { throw null; }
        public static float Max(float a, float b) { throw null; }
        public static int Max(int a, int b) { throw null; }
        public static float Min(float a, float b) { throw null; }
        public static float Tan(float f) { throw null; }
        public static float Sqrt(float f) { throw null; }
        public static bool Approximately(float a, float b) { throw null; }
        public static int RoundToInt(float f) { throw null; }
        public static float Lerp(float a, float b, float t) { throw null; }
    }

    public partial struct Vector3
    {
        public static Vector3 Scale(Vector3 a, Vector3 b) { throw null; }
    }

    public partial class Mesh
    {
        public Bounds bounds { get { throw null; } set { } }
    }

    public partial class Transform
    {
        public Vector3 lossyScale { get { throw null; } }
    }

    public partial class SkinnedMeshRenderer
    {
        public Mesh sharedMesh { get { throw null; } set { } }
        public Bounds localBounds { get { throw null; } set { } }
    }

    // ----- Rendering / culling (new MOPR render layer) -----
    public partial class Camera
    {
        public float fieldOfView { get { throw null; } set { } }
        public float farClipPlane { get { throw null; } set { } }
        public float nearClipPlane { get { throw null; } set { } }
        public float[] layerCullDistances { get { throw null; } set { } }
        public bool layerCullSpherical { get { throw null; } set { } }
        public int cullingMask { get { throw null; } set { } }
        public static Camera[] allCameras { get { throw null; } }
        public static Camera current { get { throw null; } }
    }

    public partial class Renderer
    {
        public UnityEngine.Rendering.ShadowCastingMode shadowCastingMode { get { throw null; } set { } }
        public bool receiveShadows { get { throw null; } set { } }
        public bool isVisible { get { throw null; } }
        public Material sharedMaterial { get { throw null; } set { } }
        public Material[] sharedMaterials { get { throw null; } set { } }
    }

    public partial class Light
    {
        public LightShadows shadows { get { throw null; } set { } }
        public LightType type { get { throw null; } set { } }
        public float range { get { throw null; } set { } }
        public float intensity { get { throw null; } set { } }
    }

    public partial struct LayerMask
    {
        public static string LayerToName(int layer) { throw null; }
    }

    public partial class QualitySettings
    {
        public static int pixelLightCount { get { throw null; } set { } }
        public static float lodBias { get { throw null; } set { } }
        public static int shadowCascades { get { throw null; } set { } }
        public static int GetQualityLevel() { throw null; }
    }

    public partial class Time
    {
        public static float realtimeSinceStartup { get { throw null; } }
        public static int frameCount { get { throw null; } }
        public static float unscaledDeltaTime { get { throw null; } }
        public static float smoothDeltaTime { get { throw null; } }
    }

    public partial struct Bounds
    {
        public Vector3 center { get { throw null; } set { } }
        public Vector3 extents { get { throw null; } set { } }
    }

    public partial struct Ray
    {
        public Ray(Vector3 origin, Vector3 direction) { throw null; }
        public Vector3 origin { get { throw null; } set { } }
        public Vector3 direction { get { throw null; } set { } }
        public Vector3 GetPoint(float distance) { throw null; }
    }

    public partial class GUILayout
    {
        public static void Label(string text, params GUILayoutOption[] options) { throw null; }
        public static void BeginArea(Rect screenRect) { throw null; }
        public static void EndArea() { throw null; }
    }
    public sealed class GUILayoutOption { }

    // ----- IMGUI loading screen (Common/LoadScreen.cs, 5.1). All exist in Unity 5.0. -----
    // stub-skip: GUI.skin
    public partial class GUI
    {
        public static GUISkin skin { get { throw null; } set { } }
        public static int depth { get { throw null; } set { } }
        public static Color color { get { throw null; } set { } }
        public static void Label(Rect position, string text, GUIStyle style) { throw null; }
        public static void DrawTexture(Rect position, Texture image) { throw null; }
        public static void DrawTexture(Rect position, Texture image, ScaleMode scaleMode) { throw null; }
    }
    public partial class GUISkin
    {
        public GUIStyle label { get { throw null; } set { } }
    }
    public sealed class GUIStyle
    {
        public GUIStyle() { throw null; }
        public GUIStyle(GUIStyle other) { throw null; }
        public TextAnchor alignment { get { throw null; } set { } }
        public int fontSize { get { throw null; } set { } }
        public FontStyle fontStyle { get { throw null; } set { } }
        public bool richText { get { throw null; } set { } }
        public bool wordWrap { get { throw null; } set { } }
        public GUIStyleState normal { get { throw null; } set { } }
    }
    public sealed class GUIStyleState
    {
        public Color textColor { get { throw null; } set { } }
    }
    public sealed class Event
    {
        public static Event current { get { throw null; } set { } }
        public EventType type { get { throw null; } set { } }
    }
    public enum EventType { MouseDown = 0, MouseUp = 1, MouseMove = 2, MouseDrag = 3, KeyDown = 4, KeyUp = 5, ScrollWheel = 6, Repaint = 7, Layout = 8 }
    public enum ScaleMode { StretchToFill = 0, ScaleAndCrop = 1, ScaleToFit = 2 }
    public enum FilterMode { Point = 0, Bilinear = 1, Trilinear = 2 }
    public enum TextureWrapMode { Repeat = 0, Clamp = 1 }
    public partial class Texture
    {
        public FilterMode filterMode { get { throw null; } set { } }
        public TextureWrapMode wrapMode { get { throw null; } set { } }
    }
    public partial class Texture2D
    {
        public void SetPixel(int x, int y, Color color) { throw null; }
    }
    public partial class Mathf
    {
        public static float Exp(float power) { throw null; }
    }

    // ----- Mesh merger (Next/MeshMerger.cs, 5.1). All exist in Unity 5.0. -----
    // stub-skip: Material.renderQueue
    // stub-skip: MeshFilter.sharedMesh
    public partial class Material
    {
        public int renderQueue { get { throw null; } set { } }
    }
    public partial class MeshFilter
    {
        public Mesh sharedMesh { get { throw null; } set { } }
    }
    public partial class Mesh
    {
        public void CombineMeshes(CombineInstance[] combine, bool mergeSubMeshes, bool useMatrices) { throw null; }
    }
    public partial class Transform
    {
        public int GetSiblingIndex() { throw null; }          // 5.1 settings layout
        public void SetSiblingIndex(int index) { throw null; }
        public bool hasChanged { get { throw null; } set { } }
        public Matrix4x4 localToWorldMatrix { get { throw null; } }
        public Matrix4x4 worldToLocalMatrix { get { throw null; } }
    }
    public partial class Renderer
    {
        public bool useLightProbes { get { throw null; } set { } }
    }
    public partial struct Matrix4x4
    {
        public float m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23, m30, m31, m32, m33;
        public static Matrix4x4 operator *(Matrix4x4 lhs, Matrix4x4 rhs) { throw null; }
    }
    public partial struct Matrix4x4
    {
        public static Matrix4x4 identity { get { throw null; } }
        public static Matrix4x4 TRS(Vector3 pos, Quaternion q, Vector3 s) { throw null; }
    }
    public partial struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity { get { throw null; } }
        public static float Angle(Quaternion a, Quaternion b) { throw null; }
    }
    public partial class RenderTexture : Texture { }
    // stub-skip: Camera.onPreCull
    // stub-skip: Camera.onPostRender
    // stub-skip: Camera.clearFlags
    public partial class Camera
    {
        public delegate void CameraCallback(Camera cam);
        public static CameraCallback onPreCull;
        public static CameraCallback onPostRender;
        public CameraClearFlags clearFlags { get { throw null; } set { } }
        public RenderTexture targetTexture { get { throw null; } set { } }
    }
    public enum CameraClearFlags { Skybox = 1, Color = 2, SolidColor = 2, Depth = 3, Nothing = 4 }
    // stub-skip: Collider.enabled
    public partial class Collider
    {
        public bool enabled { get { throw null; } set { } }
    }
    public partial struct RaycastHit
    {
        public float distance { get { throw null; } set { } }
    }
    public partial struct CombineInstance
    {
        public Mesh mesh { get { throw null; } set { } }
        public int subMeshIndex { get { throw null; } set { } }
        public Matrix4x4 transform { get { throw null; } set { } }
    }

    // ----- Roof lamp fix (Next/RoofLampFix.cs, 5.1). All exist in Unity 5.0. -----
    // stub-skip: Mesh.vertices
    // stub-skip: Mesh.triangles
    public partial class Mesh
    {
        public Vector3[] vertices { get { throw null; } set { } }
        public int[] triangles { get { throw null; } set { } }
    }
    public partial struct Matrix4x4
    {
        public Matrix4x4 inverse { get { throw null; } }
        public Vector3 MultiplyPoint3x4(Vector3 v) { throw null; }
    }
    public partial struct Vector3
    {
        public static Vector3 Cross(Vector3 lhs, Vector3 rhs) { throw null; }
        public string ToString(string format) { throw null; }
    }
    public partial class Transform
    {
        public Vector3 up { get { throw null; } set { } }
        public Vector3 InverseTransformPoint(Vector3 position) { throw null; }
    }

    // ----- Item toggle errors (5.1, Items/ItemBehaviour.cs): written to output_log.txt only. -----
    public sealed partial class Debug
    {
        public static void Log(object message) { throw null; }
    }

    // ----- Localization (5.1): first-start language detection. -----
    public partial class Application
    {
        public static SystemLanguage systemLanguage { get { throw null; } }
    }
    public enum SystemLanguage
    {
        Afrikaans = 0, Arabic = 1, Basque = 2, Belarusian = 3, Bulgarian = 4, Catalan = 5, Chinese = 6, Czech = 7,
        Danish = 8, Dutch = 9, English = 10, Estonian = 11, Faroese = 12, Finnish = 13, French = 14, German = 15,
        Greek = 16, Hebrew = 17, Icelandic = 19, Indonesian = 20, Italian = 21, Japanese = 22, Korean = 23,
        Latvian = 24, Lithuanian = 25, Norwegian = 26, Polish = 27, Portuguese = 28, Romanian = 29, Russian = 30,
        SerboCroatian = 31, Slovak = 32, Slovenian = 33, Spanish = 34, Swedish = 35, Thai = 36, Turkish = 37,
        Ukrainian = 38, Vietnamese = 39, Unknown = 42, Hungarian = 18
    }

    // ----- Enums (Unity 5.0) -----
    public enum LogType { Error = 0, Assert = 1, Warning = 2, Log = 3, Exception = 4 }
    public enum LightShadows { None = 0, Hard = 1, Soft = 2 }
    public enum LightType { Spot = 0, Directional = 1, Point = 2, Area = 3 }
    public enum CollisionDetectionMode { Discrete = 0, Continuous = 1, ContinuousDynamic = 2 }
    public enum ForceMode { Force = 0, Impulse = 1, VelocityChange = 2, Acceleration = 5 }
    public enum RigidbodyConstraints
    {
        None = 0, FreezePositionX = 2, FreezePositionY = 4, FreezePositionZ = 8,
        FreezeRotationX = 16, FreezeRotationY = 32, FreezeRotationZ = 64,
        FreezePosition = 14, FreezeRotation = 112, FreezeAll = 126
    }
    [System.Flags]
    public enum HideFlags
    {
        None = 0, HideInHierarchy = 1, HideInInspector = 2, DontSaveInEditor = 4, NotEditable = 8,
        DontSaveInBuild = 16, DontUnloadUnusedAsset = 32, DontSave = 52, HideAndDontSave = 61
    }
    public enum TextAnchor { UpperLeft, UpperCenter, UpperRight, MiddleLeft, MiddleCenter, MiddleRight, LowerLeft, LowerCenter, LowerRight }
    public enum TextAlignment { Left, Center, Right }
    public enum FontStyle { Normal, Bold, Italic, BoldAndItalic }
    public enum CursorLockMode { None, Locked, Confined }
    public enum CursorMode { Auto, ForceSoftware }
    public enum AudioClipLoadType { DecompressOnLoad, CompressedInMemory, Streaming }
    public enum AudioDataLoadState { Unloaded, Loading, Loaded, Failed }
    public enum TextureFormat { Alpha8 = 1, ARGB4444 = 2, RGB24 = 3, RGBA32 = 4, ARGB32 = 5, RGB565 = 7, DXT1 = 10, DXT5 = 12, RGBA4444 = 13, BGRA32 = 14 }
    public enum Space { World = 0, Self = 1 }
    public enum KeyCode
    {
        None = 0, Backspace = 8, Tab = 9, Return = 13, Escape = 27, Space = 32, Comma = 44, Minus = 45,
        Period = 46, Slash = 47, Alpha0 = 48, Alpha1 = 49, Alpha2 = 50, Alpha3 = 51, Alpha4 = 52, Alpha5 = 53,
        Alpha6 = 54, Alpha7 = 55, Alpha8 = 56, Alpha9 = 57, Equals = 61, BackQuote = 96,
        A = 97, B = 98, C = 99, D = 100, E = 101, F = 102, G = 103, H = 104, I = 105, J = 106, K = 107, L = 108,
        M = 109, N = 110, O = 111, P = 112, Q = 113, R = 114, S = 115, T = 116, U = 117, V = 118, W = 119,
        X = 120, Y = 121, Z = 122, Delete = 127,
        Keypad0 = 256, Keypad1 = 257, Keypad2 = 258, Keypad3 = 259, Keypad4 = 260, Keypad5 = 261, Keypad6 = 262,
        Keypad7 = 263, Keypad8 = 264, Keypad9 = 265, KeypadMinus = 269, KeypadPlus = 270, KeypadEnter = 271,
        UpArrow = 273, DownArrow = 274, RightArrow = 275, LeftArrow = 276, Insert = 277, Home = 278, End = 279,
        PageUp = 280, PageDown = 281, F1 = 282, F2 = 283, F3 = 284, F4 = 285, F5 = 286, F6 = 287, F7 = 288,
        F8 = 289, F9 = 290, F10 = 291, F11 = 292, F12 = 293, RightShift = 303, LeftShift = 304,
        RightControl = 305, LeftControl = 306, RightAlt = 307, LeftAlt = 308, Mouse0 = 323, Mouse1 = 324, Mouse2 = 325
    }
}

namespace UnityEngine.Rendering
{
    public enum ShadowCastingMode { Off = 0, On = 1, TwoSided = 2, ShadowsOnly = 3 }
}

namespace UnityEngine.Events
{
    public partial class UnityEventBase { }
    public partial class UnityEvent : UnityEventBase { }
    public partial class UnityEvent<T0> : UnityEventBase { }
}
