using System.Collections;
using System.IO;
using UnityEngine;

namespace MOP.Helpers
{
    internal class SaveManagerBehaviour : MonoBehaviour
    {
        public void Run()
        {
            if (currentRoutine != null)
            {
                StopCoroutine(currentRoutine);
            }
            currentRoutine = RemoveReadOnlyAttributesRoutine();
            StartCoroutine(currentRoutine);
        }

        private IEnumerator currentRoutine;
        private IEnumerator RemoveReadOnlyAttributesRoutine()
        {
            int retries = 0;
            while (retries < 5)
            {
                try
                {
                    RemoveAttribute(SaveManager.SavePath);
                    RemoveAttribute(SaveManager.ItemsPath);
                    ModConsole.Log("[MOPR] Removed attributes from save file.");
                    currentRoutine = null;
                    yield break;
                }
                catch
                {
                    ModConsole.Log($"[MOPR] Retrying removing attributes ({retries + 1}/5)");
                }
                retries++;
                yield return new WaitForSeconds(0.5f);
            }
            ModConsole.Error("[MOPR] Failed to remove attributes from the save file. Try removing it manually form defaultSaveES2.txt, and items.txt.");

            currentRoutine = null;
        }

        private void RemoveAttribute(string filename)
        {
            if (File.Exists(filename))
            {
                FileInfo fi = new FileInfo(filename);
                fi.IsReadOnly = false;
            }
        }
    }
}
