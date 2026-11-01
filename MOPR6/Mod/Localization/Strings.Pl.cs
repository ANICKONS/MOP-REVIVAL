// MOPR6 - teksty interfejsu, polski. Klucze jak w Strings.En.cs; brakujące są brane z angielskiego.

using System.Collections.Generic;

namespace MOPR6.Localization
{
    internal static class StringsPl
    {
        internal static readonly Dictionary<string, string> Table = new Dictionary<string, string>
        {
            { "mod.description", "Mod wydajności dla gry bez modów, napisany od nowa (preview). Nie używaj razem z MOPR 5.x." },

            { "menu.header.info", "Informacje" },
            { "menu.info", "MOPR6 {0}: MOP - REVIVAL napisany od nowa dla gry bez modów. Tylko optymalizacje, które dowodliwie niczego nie zmieniają w grze." },
            { "menu.preview", "PREVIEW: wersja testowa. Znalazłeś problem? Naciśnij „Utwórz raport błędu” i dołącz plik." },
            { "menu.conflict", "Zainstalowany i włączony jest MOPR 5.x. Używaj tylko jednego z nich: w tej sesji MOPR6 nic nie robi." },
            { "menu.btn.github", "GitHub" },
            { "menu.btn.report", "Utwórz raport błędu" },

            { "menu.header.language", "Język" },

            { "menu.header.main", "Główny wyłącznik" },
            { "menu.enabled", "MOPR6 włączony" },
            { "menu.enabled.hint", "Wyłączenie od razu przywraca wszystko, co zmienił MOPR6, i zatrzymuje wszystkie optymalizacje. Działa także w grze." },

            { "menu.header.exact", "Dokładne optymalizacje" },
            { "menu.exact.hint", "Nie zmieniają niczego w grze: te same klatki, ten sam zapis. Każde ustawienie działa od razu." },
            { "menu.sys.engine.fsm", "Skrypty PlayMaker: pomijanie pustych klatek" },
            { "menu.sys.engine.fsm.hint", "Skrypty gry, które na coś czekają, nie są uruchamiane w każdej klatce na próżno." },
            { "menu.sys.engine.property", "Właściwości PlayMaker: bezpośrednie wywołania" },
            { "menu.sys.engine.property.hint", "Skrypty czytają i zapisują właściwości komponentów bez wolnej refleksji: mniej śmieci i mniej przycięć GC." },
            { "menu.sys.engine.writes", "Pomijanie zbędnych zapisów" },
            { "menu.sys.engine.writes.hint", "Ustawienia sterowania i głośność nie są zapisywane ponownie, gdy nic się nie zmieniło." },
            { "menu.sys.engine.ik", "IK: pomijanie niezmienionych obliczeń" },
            { "menu.sys.engine.ik.hint", "Wahacze zawieszenia, podnośniki i siłowniki nie są przeliczane, dopóki nie poruszyło się nic, od czego zależą." },
            { "menu.sys.engine.mouseray", "Wspólny promień myszy" },
            { "menu.sys.engine.mouseray.hint", "Części auta, które pytają, co jest pod kursorem, dzielą jeden promień na klatkę zamiast dziesiątek." },
            { "menu.verify", "Tryb weryfikacji (wolniejszy)" },
            { "menu.verify.hint", "Każda optymalizacja dodatkowo wykonuje oryginalny kod i sprawdza, że nic by się nie zmieniło. Tylko do sprawdzania i zgłaszania błędów: obniża FPS." },

            { "menu.header.sleep", "Uśpienie skryptów" },
            { "menu.sleep.hint", "Skrypty gry, które czekają na zdarzenie, są wyjmowane z aktualizacji w każdej klatce i budzone, zanim zdarzenie do nich dotrze. Każdy przypadek sprawdzono z kodem samej gry." },
            { "menu.sys.sleep.idle", "Uśpienie bezczynnych skryptów" },
            { "menu.sys.sleep.idle.hint", "Skrypt, którego bieżący stan się zakończył i który czeka tylko na zdarzenia, śpi, dopóki nie dotrze do niego zdarzenie, wyszukiwanie lub jego obiekt." },

            { "menu.header.world", "Świat" },
            { "menu.world.hint", "Części świata z dala od gracza przestają być symulowane, bez wyłączania czegokolwiek. To nie są dokładne optymalizacje: każdą sprawdzono na stanowisku testowym względem gry bez modów, a zapis, główny przełącznik i \"mopr restore\" przywracają wszystko." },
            { "menu.sys.world.vehicles", "Zaparkowane pojazdy" },
            { "menu.sys.world.vehicles.hint", "Pojazd stojący ze zgaszonym silnikiem dalej niż 15 m od gracza zamiera: fizyka nadwozia i kół nie jest liczona. Ożywa, gdy gracz podejdzie, wsiądzie, zaczepi linkę holowniczą lub obok przejedzie inny samochód." },
            { "menu.sys.world.deactivate", "Wyłączanie odległych pojazdów (test)" },
            { "menu.sys.world.deactivate.hint", "Zaparkowany pojazd dalej niż 125 m jest wyłączany, a w jego miejscu rysowana jest nieruchoma kopia - tylko gdy przez 10 s nic się w nim nie zmienia i nie reaguje na sterowanie gracza. Wszystko, po co sięga gra, jest najpierw włączane z powrotem. Wciąż w testach." },

            { "menu.header.render", "Renderowanie" },
            { "menu.render.hint", "Zmieniają tylko sposób rysowania sceny: to, co jest za małe, by je zobaczyć, nie jest rysowane, dalekie cienie są pomijane. Logika gry, fizyka i zapis się nie zmieniają. Domyślnie włączone jest tylko łączenie Satsumy (do 0,4 ms na klatkę): pozostałe w naszych pomiarach prawie nic nie oszczędzały (poniżej 0,2 ms)." },
            { "menu.sys.render.layers", "Nie rysuj malutkich dalekich obiektów" },
            { "menu.sys.render.layers.hint", "Według warstw: obiekty mniejsze niż 2 piksele na ekranie nie są rysowane (nigdy bliżej niż 30 m)." },
            { "menu.sys.render.shadows", "Bez cieni małych dalekich obiektów" },
            { "menu.sys.render.shadows.hint", "Mały obiekt przestaje rzucać cień tam, gdzie cień miałby mniej niż 6 pikseli (nigdy bliżej niż 10 m)." },
            { "menu.sys.render.lamps", "Bez cieni dalekich lamp" },
            { "menu.sys.render.lamps.hint", "Lampa świeci jak dotąd, ale nie rzuca cieni, gdy jesteś dalej niż jej zasięg plus 35 m." },

            { "menu.sys.render.merge", "Łączenie stojącej Satsumy" },
            { "menu.sys.render.merge.hint", "Gdy auto stoi zamrożone (zob. \"Zaparkowane pojazdy\"), jest rysowane kilkudziesięcioma siatkami zamiast setek części. Każda zmiana części przywraca części w tej samej klatce." },

            { "menu.header.experimental", "Eksperymentalne" },
            { "menu.experimental.hint", "Zmierzone, ale MOPR6 nie może udowodnić dla każdej klatki, że obraz pozostaje ten sam. Domyślnie wyłączone." },
            { "menu.sys.experimental.mirrors", "Lusterka auta najwyżej 60 FPS" },
            { "menu.sys.experimental.mirrors.hint", "Powyżej 60 FPS lusterko pomija rysowanie i pokazuje poprzedni obraz (nie starszy niż 1/60 s)." },
            { "menu.sys.experimental.enginebay", "Nie rysuj silnika z miejsca kierowcy" },
            { "menu.sys.experimental.enginebay.hint", "Gdy siedzisz w aucie z zamkniętą maską, twój widok nie rysuje silnika. Lusterka i inne kamery rysują go jak dotąd." },

            { "menu.header.other", "Inne" },
            { "menu.check_updates", "Sprawdzaj aktualizacje przy starcie" },
            { "menu.check_updates.hint", "Raz na uruchomienie gry, w menu głównym (GitHub)." },
            { "menu.btn.import5", "Importuj ustawienia MOPR 5.x" },
            { "menu.import5.hint", "Przenosi to, co MOPR6 już ma: optymalizacje silnika, tryb weryfikacji, sprawdzanie aktualizacji, język. Pliki 5.x są tylko odczytywane." },

            { "msg.title", "MOPR6" },
            { "msg.title.update", "Aktualizacja MOPR6" },
            { "msg.update_available", "Dostępna jest nowa wersja: {0}\nMasz {1}." },
            { "msg.btn.open_page", "Otwórz stronę pobierania" },
            { "msg.btn.later", "Później" },
            { "msg.report_saved", "Raport zapisany:\n{0}\n\nJest po angielsku i bez danych osobowych. Dołącz go do zgłoszenia błędu." },
            { "msg.btn.open_report", "Otwórz plik" },
            { "common.ok", "OK" },
        };
    }
}
