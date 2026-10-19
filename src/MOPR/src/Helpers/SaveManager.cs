// Modern Optimization Plugin
// Copyright(C) 2019-2022 Athlon

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.If not, see<http://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MSCLoader;
using HutongGames.PlayMaker;

using MOP.Common;
using MOP.Localization;
using MOP.Rules;

namespace MOP.Helpers
{
    static class SaveManager
    {
        public static string SavePath => Path.Combine(Application.persistentDataPath, "defaultES2File.txt").Replace('\\', '/');
        public static string ItemsPath => Path.Combine(Application.persistentDataPath, "items.txt").Replace("\\", "/");
        public static string OptionsPath => Path.Combine(Application.persistentDataPath, "options.txt").Replace("\\", "/");
        static List<SaveBugs> saveBugs;

        readonly static ES2Settings setting = new ES2Settings();

        // MOP Save Files.
        static string mopSavePath => Path.Combine(Application.persistentDataPath, "MopSave.json");
        static bool SaveFileExists => File.Exists(mopSavePath);

        // Minimum number of parts that must be installed to the car,
        // in order to check for MSCEditor save tampering.
        const int MinimumNumberOfPartsToCheckMSCEditorTampering = 40;

        const int MaximumSaveBugsDisplayed = 4;

        private static SaveManagerBehaviour saveManagerBehaviour;

        /// <summary>
        /// For some reason, the save files get marked as read only files, not allowing MSC to save the game.
        /// This script is ran in PreSaveGame() script and removes ReadOnly attribute.
        /// </summary>
        public static void RemoveReadOnlyAttribute()
        {
            if (saveManagerBehaviour == null)
            {
                GameObject saveManagerObject = new GameObject("MOP_SaveAttributeRemoval");
                saveManagerBehaviour = saveManagerObject.AddComponent<SaveManagerBehaviour>();
            }

            try
            {
                saveManagerBehaviour.Run();
            }
            catch (UnauthorizedAccessException)
            {
                ModConsole.Log("[MOPR] " + MoprColors.Error("Could not access the save file at this moment."));
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, true, "ATTRIBUTES_REMOVAL_ERROR");
            }
        }

        static bool ReadBoolean(string tag)
        {
            string path = $"{SavePath}?tag={tag}";
            if (!ES2.Exists(path, setting))
            {
                throw new NullReferenceException($"Boolean '{tag}' is not present in save file.");
            }

            return ES2.Load<bool>(path, setting);
        }

        public static Transform ReadTransform(string tag)
        {
            string path = $"{SavePath}?tag={tag}";
            if (!ES2.Exists(path, setting))
            {
                throw new NullReferenceException($"Transform '{tag}' is not present in save file.");
            }

            return ES2.Load<Transform>(path, setting);
        }

        public static Transform ReadItemTranform(string tag)
        {
            string path = $"{ItemsPath}?tag={tag}";
            if (!ES2.Exists(path, setting))
            {
                throw new NullReferenceException($"Transform '{tag}' is not present in item save file.");
            }

            return ES2.Load<Transform>(path, setting);
        }

        static float ReadFloat(string tag)
        {
            string path = $"{SavePath}?tag={tag}";
            if (!ES2.Exists(path, setting))
            {
                throw new NullReferenceException($"Float '{tag}' is not present in save file.");
            }

            return ES2.Load<float>(path, setting);
        }

        static List<string> ReadStringList(string tag)
        {
            string path = $"{SavePath}?tag={tag}";
            if (!ES2.Exists(path, setting))
            {
                throw new NullReferenceException($"List<String> '{tag}' is not present in the save file.");
            }

            return ES2.LoadList<string>(path, setting);
        }

        static int ReadInt(string tag)
        {
            string path = $"{SavePath}?tag={tag}";
            if (!ES2.Exists(path, setting))
            {
                throw new NullReferenceException($"Int '{tag}' is not present in save file.");
            }

            return ES2.Load<int>(path, setting);
        }

        static int ReadItemInt(string tag)
        {
            string path = $"{ItemsPath}?tag={tag}";
            if (!ES2.Exists(path, setting))
            {
                throw new NullReferenceException($"Int '{tag}' is not present in item save file.");
            }

            return ES2.Load<int>(path, setting);
        }

        public static void WriteSaveTag<T>(string tag, T value)
        {
            ES2.Save(value, $"{SavePath}?tag={tag}", setting);
        }

        public static void WriteItemTag<T>(string tag, T value)
        {
            ES2.Save(value, $"{ItemsPath}?tag={tag}");
        }

        public static bool IsSaveTagPresent(string tag)
        {
            return ES2.Exists($"{SavePath}?tag={tag}");
        }

        public static bool IsItemTagPresent(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return false;
            }

            return ES2.Exists($"{ItemsPath}?tag={tag}");
        }

        public static void VerifySave()
        {
            if (!File.Exists(SavePath))
            {
                return;
            }

            if (IsSaveFileAfterPermadeath())
            {
                ModConsole.Log($"[MOPR] Save file wasn't checked, as it is post-permadeath.");
                return;
            }

            RemoveReadOnlyAttribute();

            // MOP - REVIVAL: with "satsuma_ignore" the Satsuma belongs to another mod, so none of the
            // Satsuma-specific save checks/fixes below are applied (they were written for the vanilla car).
            bool satsumaManaged = IsSatsumaManaged;

            // Passenger bucket seat.
            // Check if driver bucket seat is bought and check the same for passenger one.
            // If they do not match, fix it.
            saveBugs = new List<SaveBugs>();
            if (satsumaManaged)
            try
            {
                bool bucketPassengerSeat = ReadBoolean("bucket seat passenger(Clone)Purchased");
                bool bucketDriverSeat = ReadBoolean("bucket seat driver(Clone)Purchased");

                if (bucketDriverSeat != bucketPassengerSeat)
                {
                    saveBugs.Add(new SaveBugs(Loc.T("save.bug.bucket_seats"), () =>
                    {
                        WriteSaveTag("bucket seat passenger(Clone)Purchased", true);
                        WriteSaveTag("bucket seat driver(Clone)Purchased", true); 
                    }));
                }
            }
            catch (Exception e)
            {
                ExceptionManager.New(e, false, "VERIFY_SAVE_BUCKET_SEAT");
            }

            try
            {
                if (!IsSaveTagPresent("TractorTrailerAttached"))
                {
                    ModConsole.Log("[MOPR] Save Verification: TractorTrailerAttached hasn't been found. Transform fix will be skipped...");
                }
                else
                { 
                    bool tractorTrailerAttached = ReadBoolean("TractorTrailerAttached");
                    Transform flatbedTransform = ReadTransform("FlatbedTransform");
                    Transform kekmetTransform = ReadTransform("TractorTransform");

                    if (tractorTrailerAttached && Vector3.Distance(flatbedTransform.position, kekmetTransform.position) > 5.5f)
                    {
                        saveBugs.Add(new SaveBugs(Loc.T("save.bug.flatbed"), () =>
                        {
                            WriteSaveTag("TractorTrailerAttached", false);
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "VERIFY_SAVE_FLATBED");
            }

            // MOP - REVIVAL 5.1 (fix, original MOP bug): the bolt "check" against MopSave.json is removed. At every load
            // MOP wrote the rear bumper, both halfshafts and the battery minus terminal from the save into
            // MopSave.json; it was deleted on a save through MOP. If it was still there at the next start (the game
            // closed without saving through MOP, another save slot, a crash), the save was "fixed" back to those
            // load-time values: the rear bumper silently, the others after asking. That undid what the player had
            // done in the session (or wrote another slot's values), and after "Fix" the car would not start or its
            // wheels did not turn (about 30 reports). Comparing with a save the game has not written since gives
            // nothing to fix, so nothing is left of the check. A MopSave.json of an older version is deleted.
            ReleaseSave();

            if (satsumaManaged)
            try
            {
                // Fix fuel line tightness going below 0.
                List<string> fuelLineBolts = ReadStringList("FuelLineBolts");
                if (fuelLineBolts != null)
                {
                    float fuelLineTightness = ReadFloat("FuelLineTightness");

                    int boltOneValue = int.Parse(fuelLineBolts[0].Replace("int(", "").Replace(")", ""));

                    if (boltOneValue != fuelLineTightness)
                    {
                        saveBugs.Add(new SaveBugs(Loc.F("save.bug.fuel_line", fuelLineTightness, boltOneValue), () =>
                        {
                            WriteSaveTag("FuelLineTightness", (float)boltOneValue);
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "VERIFY_FUELLINE_TIGHTNESS");
            }

            // MOP - REVIVAL 5.1 (fix, original MOP bug): the item counter "fix" is removed. It counted the
            // "<item><n>Transform" tags in items.txt and offered to raise the game's item count to that number. But
            // the game keeps the tags of items that were eaten, drunk or thrown away, so the count went up and the
            // game spawned those items again at the next load (duplicate purchases at the store).

            if (saveBugs.Count > 0)
            {
                // MOP - REVIVAL: localized (5.1).
                int count = saveBugs.Count;
                string message = Loc.Plural("save.found", count) + "\n\n" +
                                 $"<color=yellow>{string.Join("\n", saveBugs.Select(f => f.BugName).Take(MaximumSaveBugsDisplayed).ToArray())}</color>\n";
                if (count > MaximumSaveBugsDisplayed)
                    message += Loc.F("save.more", count - MaximumSaveBugsDisplayed) + "\n";
                message += "\n" + Loc.T(count > 1 ? "save.ask_many" : "save.ask_one");

                ModUI.ShowCustomMessage(message, Loc.T("save.title"), new MsgBoxBtn[]
                {
                    ModUI.CreateMessageBoxBtn(Loc.T("common.yes"), FixAllProblems),
                    ModUI.CreateMessageBoxBtn(Loc.T("save.btn.report"), ShowReport, true),
                    ModUI.CreateMessageBoxBtn(Loc.T("common.no"))
                }, new MsgBoxBtn[] { });
            }
            else
            {
                ModConsole.Log("[MOPR] " + MoprColors.Success("Save check: no problems found :)"));
            }
        }

        static void FixAllProblems()
        {
            BackupSave();
            int success = 0, fail = 0;

            foreach (SaveBugs bug in saveBugs)
            {
                try
                {
                    bug.Fix.Invoke();
                    success++;
                }
                catch
                {
                    fail++;
                }
            }

            string msg = Loc.Plural("save.fixed", success);
            if (fail > 0)
            {
                msg += "\n" + Loc.Plural("save.failed", fail);
            }

            msg += "\n\n" + Loc.T("save.backup_note");

            ModUI.ShowMessage(msg, Loc.T("save.check_title"));
        }

        internal static Transform GetRadiatorHose3Transform()
        {
            return ES2.Load<Transform>(SavePath + "?tag=radiator hose3(xxxxx)", setting);
        }

        /// <summary>MOP - REVIVAL: false when the "satsuma_ignore" rule hands the Satsuma to another mod.</summary>
        static bool IsSatsumaManaged => !RulesManager.Book.SatsumaExclude;

        /// <summary>
        /// Deletes the MopSave.json that MOP 3.10 - 5.0 wrote at every load (see VerifySave). MOPR 5.1 writes none.
        /// </summary>
        internal static void ReleaseSave()
        {
            try
            {
                if (SaveFileExists)
                {
                    File.Delete(mopSavePath);
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "SAVE_RELEASE_ERROR");
            }
        }

        public static bool IsSatsumaLoadedCompletely()
        {
            // Check if cylinder head is supposed to be installed, but for some reason its disabled, or not a part of Satsuma at all.
            GameObject satsuma = GameObject.Find("SATSUMA(557kg, 248)");
            if (satsuma == null)
            {
                throw new NullReferenceException("Satsuma is missing");
            }

            GameObject cylinderHead = Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == "cylinder head(Clone)");
            if (cylinderHead == null)
            {
                throw new NullReferenceException("Cylinder head is missing");
            }

            GameObject block = Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == "block(Clone)");
            if (block == null)
            {
                throw new NullReferenceException("Block is missing");
            }

            bool isCylinderHeadInstalled = ReadBoolean("cylinder head(Clone)Installed");
            bool isEngineBlockInstalled = ReadBoolean("block(Clone)Installed");

            if (!isEngineBlockInstalled)
            {
                if (isCylinderHeadInstalled && !cylinderHead.transform.Path().Contains("block(Clone)"))
                {
                    throw new Exception("Cylinder head is not a part of the engine block.");
                }
            }
            else
            {
                if ((!cylinderHead.gameObject.activeSelf || cylinderHead.transform.root != satsuma.transform) && isCylinderHeadInstalled)
                {
                    throw new Exception("Cylinder head is not active, or is not a part of Satsuma.");
                }
            }

            Transform sparkPlug1Pivot = cylinderHead.transform.Find("pivot_sparkplug1");
            if (sparkPlug1Pivot == null)
            {
                throw new NullReferenceException("Unable to locate pivot_sparkplug1");
            }

            for (int i = 1; i < 1000; ++i)
            {
                if (ES2.Load<int?>($"{ItemsPath}?tag=spark plug{i}TriggerID") == null)
                {
                    break;
                }

                // Check if the spark plug is missing or not.
                if (ReadItemInt($"spark plug{i}TriggerID") == 1 && ES2.Load<bool>($"{ItemsPath}?tag=spark plug{i}Installed") && sparkPlug1Pivot.childCount == 0)
                {
                    throw new Exception($"Spark plug {i} is installed, but not a part of the engine.");
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true, if the save file is post permadeath.
        /// </summary>
        private static bool IsSaveFileAfterPermadeath()
        {
            return !ES2.Exists($"{SavePath}?tag=PlayerIsDead");
        }

        public static bool IsCarAssembledWithMSCEditor()
        {
            if (ModLoader.CurrentScene != CurrentScene.Game)
            {
                throw new AccessViolationException("Can only be executed in-game.");
            }

            int boltedCount = 0;
            int installedCount = 0;
            CountBoltedAndInstalled(GameObject.Find("Database/DatabaseBody"), ref boltedCount, ref installedCount);
            CountBoltedAndInstalled(GameObject.Find("Database/DatabaseMechanics"), ref boltedCount, ref installedCount);
            CountBoltedAndInstalled(GameObject.Find("Database/DatabaseMotor"), ref boltedCount, ref installedCount);

            // If the number of parts is too low for the game to check if car has been assembled with MSCEditor, 
            // don't bother.
            // It would be really weird, if someone installed a single part, not bolt it, then have that error appear.
            if (installedCount < MinimumNumberOfPartsToCheckMSCEditorTampering)
            {
                ModConsole.Log($"[MOPR] Only {installedCount} parts are installed.");
                return false;
            }

            double percentageOfPartsBolted = boltedCount / (double)installedCount;
            ModConsole.Log($"[MOPR] {((1d - percentageOfPartsBolted) * 100):0.00}% of Satsuma parts are installed, but not bolted.\n" +
                           $"{boltedCount}/{installedCount} parts are bolted and installed.");

            // MOP - REVIVAL 5.1 (fix, original MOP bug): "15% or fewer of the installed parts bolted" no longer counts
            // as MSCEditor. A car the player is still assembling (or taking apart) looks exactly like that: players
            // who never ran MSCEditor got the warning, and with it MOP skipped its "Satsuma loaded completely" check.
            // Only a state the game itself can't produce is left: the engine block saved as bolted (and not in the
            // hoist) while some of its bolts are not tightened at all.
            try
            {
                return IsEngineStupidlyAssembled();
            }
            catch (Exception ex)
            {
                ModConsole.Log("[MOPR] Engine block check skipped: " + ex.Message);
                return false;
            }
        }



        private static void CountBoltedAndInstalled(GameObject databaseObject, ref int boltedCount, ref int installedCount)
        {
            foreach (PlayMakerFSM fsm in databaseObject.GetComponentsInChildren<PlayMakerFSM>())
            {
                FsmBool installed = fsm.FsmVariables.FindFsmBool("Installed");
                FsmBool bolted = fsm.FsmVariables.FindFsmBool("Bolted");
                if (bolted != null && installed != null)
                {
                    if (installed.Value)
                    {
                        installedCount++;
                        if (bolted.Value)
                        {
                            boltedCount++;
                        }
                    }
                }
            }
        }

        private static void ShowReport()
        {
            if (!Paths.LogDirectoryExists)
            {
                Directory.CreateDirectory(Paths.LogFolder);
            }

            string path = Path.Combine(Paths.LogFolder, Paths.SaveFileBugsReport + ".txt");
            using (StreamWriter writer = new StreamWriter(path))
            {
                writer.WriteLine("=== Generated with MOP " + MOP.ModVersion + " ===\n\n");
                foreach (SaveBugs bug in saveBugs)
                {
                    writer.WriteLine($"{bug.BugName}");
                }
            }

            System.Diagnostics.Process.Start(path);
        }

        private static void BackupSave()
        {
            if (File.Exists(SavePath))
            {
                File.Copy(SavePath, SavePath + ".mopbackup", true);
            }

            if (File.Exists(ItemsPath))
            {
                // MOP - REVIVAL: the original copied defaultES2File.txt here, so items.txt was never backed up.
                File.Copy(ItemsPath, ItemsPath + ".mopbackup", true);
            }
        }

        /// <summary>
        /// Checks if engine has been assembled in using MSCEditor (aka: engine is installed, bolted, but the arrays are 0).
        /// </summary>
        /// <returns></returns>
        private static bool IsEngineStupidlyAssembled()
        {
            List<string> blockBolts = ReadStringList("BlockBolts");
            bool blockInstalled = ReadBoolean("block(Clone)Installed");
            bool blockBolted = ReadBoolean("block(Clone)Bolted");
            bool blockInHoist = ReadBoolean("block(Clone)InHoist");

            bool areAllbolted = true;
            foreach (string block in blockBolts)
            {
                if(int.Parse(block.Replace("int(", "").Replace(")", "")) == 0)
                { 
                    areAllbolted = false;
                }
            }

            return blockInstalled && blockBolted && !areAllbolted && !blockInHoist;
        }
    }
}
