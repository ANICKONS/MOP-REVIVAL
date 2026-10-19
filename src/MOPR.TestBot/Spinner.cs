// Test helper: turns the player (standing, not seated) around the vertical axis at a fixed rate, in this
// component's Update. The camera then moves every frame at a point between the game's own Update calls, which
// is exactly the case the shared mouse ray's camera check has to handle.

using UnityEngine;

namespace MOPR.TestBot
{
    class Spinner : MonoBehaviour
    {
        static Spinner instance;
        Transform player;
        float degreesPerSecond;

        public static string Start(float degreesPerSecond)
        {
            Stop();
            GameObject p = GameObject.Find("PLAYER");
            if (p == null) return "no player";
            if (p.transform.parent != null) return "the player is attached to " + p.transform.parent.name;
            GameObject host = new GameObject("MOPR_TestBot_Spinner");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<Spinner>();
            instance.player = p.transform;
            instance.degreesPerSecond = degreesPerSecond;
            return null;
        }

        public static void Stop()
        {
            if (instance != null) Destroy(instance.gameObject);
            instance = null;
        }

        void Update()
        {
            if (player == null) { Stop(); return; }
            player.Rotate(0f, degreesPerSecond * Time.deltaTime, 0f, Space.World);
        }
    }
}
