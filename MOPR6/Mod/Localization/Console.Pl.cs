// MOPR6 - teksty konsoli, polski. Słowa poleceń i linie techniczne (stany systemów, liczniki) nie są tłumaczone.

using System.Collections.Generic;

namespace MOPR6.Localization
{
    internal static class ConsolePl
    {
        internal static readonly Dictionary<string, string> Table = new Dictionary<string, string>
        {
            { "con.loaded", "MOPR6 {0}{1} - tylko dokładne optymalizacje." },
            { "con.preview_mark", " PREVIEW" },
            { "con.conflict", "Zainstalowany i włączony jest MOPR 5.x. Używaj tylko jednego z nich - w tej sesji MOPR6 nic nie robi." },
            { "con.other_mods", "Znaleziono inne mody: {0} ({1}). Zgodność z innymi modami nie jest jeszcze obsługiwana: MOPR6 dotyka tylko znanych mu obiektów gry." },
            { "con.tripped", "{0} kilka razy zawiodła i została wyłączona; jej zmiany przywrócono. Szczegóły: {1}" },
            { "con.language", "Język: {0}" },
            { "con.language_restart", "Ta wersja MSCLoader nie potrafi zmieniać nazw ustawień na bieżąco: nowy język zadziała po ponownym uruchomieniu gry." },
            { "con.help",
                "mopr status - stan wszystkich optymalizacji\n" +
                "mopr on | off - główny wyłącznik\n" +
                "mopr on | off | verify ID | all - jedna optymalizacja, na tę sesję\n" +
                "mopr verify - różnice znalezione przez tryb weryfikacji\n" +
                "mopr restore - przywróć teraz wszystkie zmiany\n" +
                "mopr report - plik do zgłoszenia błędu (po angielsku, bez danych osobowych)\n" +
                "mopr import5 - przenieś ustawienia MOPR 5.x\n" +
                "mopr catalog - katalog gry bez modów i wersja gry\n" +
                "mopr update - sprawdź teraz aktualizacje\n" +
                "mopr errors | ledger | barrier - diagnostyka" },
            { "con.unknown", "Nieznane polecenie „{0}”.\n{1}" },
            { "con.inactive", "nieaktywny: załadowany jest MOPR 5.x" },
            { "con.main_state", "główny wyłącznik: {0}" },
            { "con.word.on", "wł." },
            { "con.word.off", "WYŁ." },
            { "con.ledger_line", "dziennik: trzymanych zmian {0}; bariera zapisu: {1}" },
            { "con.main_off", "MOPR6 wyłączony: przywrócono zmian {0}, nie udało się przywrócić {1}; wszystkie optymalizacje zatrzymane." },
            { "con.main_on", "MOPR6 włączony." },
            { "con.main_is_off", "MOPR6 jest wyłączony. Najpierw go włącz: mopr on" },
            { "con.no_system", "Nie ma systemu „{0}”. Systemy: {1}" },
            { "con.session_only", "(na tę sesję; strona ustawień się nie zmienia)" },
            { "con.not_ready", "Optymalizacje startują przy pierwszym wczytaniu gry." },
            { "con.restored", "Przywrócono zmian {0}, nie udało się przywrócić {1}." },
            { "con.ledger_empty", "Teraz nic nie jest zmienione." },
            { "con.no_errors", "Brak błędów." },
            { "con.mismatches", "Różnice trybu weryfikacji: {0}" },
            { "con.report_saved", "Raport zapisany: {0}" },
            { "con.report_failed", "Nie udało się zapisać raportu: {0}" },
            { "con.import.no_file", "Nie znaleziono ustawień MOPR 5.x: {0}" },
            { "con.import.done", "Przeniesiono z MOPR 5.x: {0}. Ustawienia 5.x nie zostały zmienione." },
            { "con.import.nothing", "Ustawienia MOPR 5.x nie mają jeszcze niczego, czego używa MOPR6." },
            { "con.import.failed", "Nie udało się odczytać ustawień MOPR 5.x: {0}" },
            { "con.import.not_saved", "Przeniesiono, ale MSCLoader nie zapisał ustawień: otwórz i zamknij stronę ustawień, aby je zachować." },
            { "con.update.running", "Sprawdzanie aktualizacji już trwa." },
            { "con.update.failed", "Sprawdzanie aktualizacji nie powiodło się: {0}" },
            { "con.update.latest", "{0} to najnowsza wersja." },
            { "con.update.ahead", "Ta wersja ({0}) jest nowsza niż najnowsza opublikowana ({1})." },
            { "con.update.none", "Nie opublikowano jeszcze żadnej wersji MOPR6 dla tej kompilacji." },
            { "con.update.available", "Dostępna jest nowa wersja: {0} (masz {1}). Pobierz: {2}" },
            { "con.lang_check", "{0}: brakujących kluczy: {1}{2}" },
        };
    }
}
