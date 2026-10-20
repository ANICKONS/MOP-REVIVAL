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
                    ModConsole.Debug("Removed attributes from save file.");
                    currentRoutine = null;
                    yield break;
                }
                catch
                {
                    ModConsole.Debug($"Retrying removing attributes ({retries + 1}/5)");
                }
                retries++;
                yield return new WaitForSeconds(0.5f);
            }
            ModConsole.Err("con.save.attributes_failed");

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
