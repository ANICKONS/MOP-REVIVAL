// MOPR6 - console texts, English (also the language of the bug report and of the TestBot's copy of an answer).
// The command words and the technical lines (system states, counters) are never translated.

using System.Collections.Generic;

namespace MOPR6.Localization
{
    internal static class ConsoleEn
    {
        internal static readonly Dictionary<string, string> Table = new Dictionary<string, string>
        {
            { "con.loaded", "MOPR6 {0}{1} - exact optimizations only." },
            { "con.preview_mark", " PREVIEW" },
            { "con.conflict", "MOPR 5.x is installed and enabled. Use only one of them - MOPR6 does nothing in this session." },
            { "con.other_mods", "Other mods found: {0} ({1}). Compatibility with other mods is not supported yet: MOPR6 only touches vanilla objects it knows." },
            { "con.tripped", "{0} failed repeatedly and was switched off; its changes were returned. Details: {1}" },
            { "con.language", "Language: {0}" },
            { "con.language_restart", "This MSCLoader cannot relabel the settings page live: the new language applies after a game restart." },
            { "con.help",
                "mopr status - state of every optimization\n" +
                "mopr on | off - the main switch\n" +
                "mopr on | off | verify ID | all - one optimization, for this session\n" +
                "mopr verify - mismatches found by the verify mode\n" +
                "mopr restore - return every change now\n" +
                "mopr report - bug report file (English, no personal data)\n" +
                "mopr import5 - take over the MOPR 5.x settings\n" +
                "mopr catalog - vanilla catalog and game version\n" +
                "mopr update - check for updates now\n" +
                "mopr errors | ledger | barrier - diagnostics" },
            { "con.unknown", "Unknown command \"{0}\".\n{1}" },
            { "con.inactive", "inactive: MOPR 5.x is loaded" },
            { "con.main_state", "main switch: {0}" },
            { "con.word.on", "on" },
            { "con.word.off", "OFF" },
            { "con.ledger_line", "ledger: {0} changes held; save barrier: {1}" },
            { "con.main_off", "MOPR6 is off: {0} changes returned, {1} could not be; every optimization stopped." },
            { "con.main_on", "MOPR6 is on." },
            { "con.main_is_off", "MOPR6 is switched off. Switch it on first: mopr on" },
            { "con.no_system", "No system \"{0}\". Systems: {1}" },
            { "con.session_only", "(for this session; the settings page is unchanged)" },
            { "con.not_ready", "The optimizations start with the first game load." },
            { "con.restored", "Returned {0} changes, could not return {1}." },
            { "con.ledger_empty", "Nothing is changed right now." },
            { "con.no_errors", "No errors." },
            { "con.mismatches", "Verify mismatches: {0}" },
            { "con.report_saved", "Bug report saved: {0}" },
            { "con.report_failed", "Could not write the bug report: {0}" },
            { "con.import.no_file", "No MOPR 5.x settings found: {0}" },
            { "con.import.done", "Imported from MOPR 5.x: {0}. The 5.x settings were not changed." },
            { "con.import.nothing", "The MOPR 5.x settings have nothing MOPR6 uses yet." },
            { "con.import.failed", "Could not read the MOPR 5.x settings: {0}" },
            { "con.import.not_saved", "Imported, but MSCLoader did not save the settings: open and close the settings page to keep them." },
            { "con.update.running", "The update check is already running." },
            { "con.update.failed", "Update check failed: {0}" },
            { "con.update.latest", "{0} is the newest version." },
            { "con.update.ahead", "This build ({0}) is newer than the newest published one ({1})." },
            { "con.update.none", "No published MOPR6 version for this build yet." },
            { "con.update.available", "A new version is available: {0} (you have {1}). Download: {2}" },
            { "con.lang_check", "{0}: {1} missing keys{2}" },
        };
    }
}
