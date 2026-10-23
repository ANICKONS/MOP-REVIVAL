// MOP - REVIVAL
// Copyright(C) 2026 ANICKON. Licensed under GPLv3 (see Loc.cs).
//
// Polish strings (new in 5.1). A key missing here falls back to English (see Loc.T).
// Same rules as the other languages: labels in sentence case, one short plain sentence under a setting.
// Counts use the "word: {0}" form, so no plural forms are needed. Colors are added by MopMenu (MoprColors).

using System.Collections.Generic;

namespace MOP.Localization
{
    internal static class StringsPl
    {
        internal static readonly Dictionary<string, string> Table = new Dictionary<string, string>
        {
            // === Lista modów (MSCLoader) ===
            // MSCLoader shows 4 lines in the mod list: keep it as short as the English one.
            { "mod.description", "Najlepszy projekt optymalizacji My Summer Car, odbudowany w 2026." },

            // === Ustawienia: informacje ===
            { "menu.header.info", "Informacje" },
            { "menu.info.by", "autor: {0}" },
            { "menu.info.based", "Oparty na oryginalnym MOP autorstwa Athlon - GPLv3" },
            { "menu.btn.github", "GitHub" },
            { "menu.btn.original_mop", "Oryginalny MOP" },

            // === Ustawienia: język ===
            { "menu.header.language", "Language / Язык / Język" },

            // === Ustawienia: wydajność ===
            { "menu.header.performance", "Wydajność" },
            { "menu.sub.mode", "Tryb wydajności" },
            { "menu.mode.performance", "Wydajność" },
            { "menu.mode.balanced", "Zrównoważony" },
            { "menu.mode.quality", "Jakość" },
            { "menu.mode.safe", "Bezpieczny" },
            { "menu.mode_hint", "Wydajność — mniejsza widoczność, więcej FPS\n" +
                                "Zrównoważony — zalecany\n" +
                                "Jakość — większa widoczność, mniej FPS\n" +
                                "Bezpieczny — jeśli coś się zepsuło: MOPR zarządza tylko budynkami i krajobrazem." },
            { "menu.restart_note", "Przedmioty i pojazdy już działają według nowego trybu. Budynki, pokoje domu i uproszczone " +
                                   "modele przedmiotów — po ponownym wczytaniu zapisu." },
            { "menu.restart_note_safe", "Tryb bezpieczny włączy się lub wyłączy po wyjściu do menu głównego." },
            { "menu.sub.distance", "Zasięg widoczności" },
            { "menu.active_distance", "Promień" },
            { "menu.dist.0", "Mały ×0.5" },
            { "menu.dist.1", "Mniejszy ×0.75" },
            { "menu.dist.2", "Normalny ×1" },
            { "menu.dist.3", "Większy ×2" },
            { "menu.dist.4", "Duży ×4" },
            { "menu.distance_hint", "Wszystko dalej niż ten promień MOPR wyłącza i włącza z powrotem, gdy się zbliżasz. " +
                                    "Mniejszy promień — więcej FPS." },
            { "menu.sub.protection", "Ochrona zapisu" },
            { "menu.restart_guard", "Nie resetuj części i przedmiotów" },
            { "menu.restart_guard_hint", "Gdy MOPR włącza obiekt z powrotem, gra czasem przywraca jego lakier, płyny lub drzwi " +
                                         "do stanu z ostatniego zapisu. Ta ochrona temu zapobiega." },

            // === Ustawienia: optymalizacja renderowania ===
            { "menu.header.render", "Optymalizacja renderowania" },
            { "menu.render_enable", "Włącz optymalizację renderowania" },
            { "menu.render_hint", "Główny przełącznik sekcji: po wyłączeniu nic poniżej nie działa (i jest ukryte)." },
            { "menu.render_quality", "Rygor" },
            { "menu.render_quality_hint", "Jak wcześnie małe i dalekie rzeczy przestają być rysowane i rzucać cienie." },
            { "menu.cull.0", "Więcej FPS" },
            { "menu.cull.1", "Zrównoważony" },
            { "menu.cull.2", "Lepszy obraz" },
            { "menu.sub.objects", "Dalekie obiekty" },
            { "menu.render_culling", "Nie rysuj malutkich dalekich obiektów" },
            { "menu.render_culling_hint", "To, co w oddali zajęłoby na ekranie kilka pikseli, nie jest rysowane." },
            { "menu.sub.shadows", "Cienie" },
            { "menu.render_shadow_lod", "Bez cieni małych rzeczy w oddali" },
            { "menu.render_shadow_lod_hint", "Małe rzeczy w oddali nie rzucają cienia — i tak nie byłoby go tam widać." },
            { "menu.render_lamps", "Bez cieni dalekich lamp" },
            { "menu.render_lamps_hint", "Dalekie lampy świecą jak zwykle, tylko bez cieni." },
            { "menu.sub.satsuma", "Satsuma" },
            { "menu.render_merge", "Szybsze rysowanie zaparkowanej Satsumy" },
            { "menu.render_merge_hint", "Gdy nikt nie jedzie samochodem, jego części są rysowane jako jedna całość. Wygląda tak samo, " +
                                        "więcej FPS przy samochodzie." },

            // === Ustawienia: silnik gry ===
            { "menu.header.engine", "Silnik gry" },
            { "menu.engine_fastpath", "Przyspiesz skrypty gry" },
            { "menu.engine_hint", "Pomija pracę skryptów, która na pewno niczego nie zmienia. Około +10% FPS, gra działa tak samo." },
            { "menu.engine_mouse_ray", "Mniej promieni myszy" },
            { "menu.engine_mouse_ray_hint", "Części samochodu i inne obiekty w każdej klatce sprawdzają, czy wskazuje je mysz. MOPR rzuca " +
                                            "jeden promień na klatkę dla wszystkich i daje każdemu tę samą odpowiedź, jaką dałaby gra. " +
                                            "Około +5% FPS w domu." },
            { "menu.engine_mod_scripts", "Przyspiesz skrypty innych modów" },
            { "menu.engine_mod_scripts_hint", "Better MSC: każda żarówka w domu w każdej klatce szuka komponentu, który nie istnieje. " +
                                              "MOPR od razu odpowiada „nie znaleziono”, tak jak Unity. Około +19% FPS w domu." },
            { "menu.sub.diagnostics", "Do szukania błędów" },
            { "menu.engine_verify", "Tryb sprawdzania" },
            { "menu.engine_verify_hint", "Bez przyspieszenia: MOPR tylko porównuje swój wynik z grą i zapisuje różnice " +
                                         "(konsola: mopr engine). Włącz, jeśli Cię o to poproszono." },

            // === Ustawienia: eksperymentalne ===
            { "menu.header.experimental", "Eksperymentalne" },
            { "menu.experimental_hint", "Domyślnie wszystko wyłączone. Więcej FPS, ale w rzadkich przypadkach może działać trochę inaczej " +
                                        "niż gra bez modów. Włączaj po jednym." },
            { "menu.exp_engine_bay", "Ukryj silnik podczas jazdy" },
            { "menu.exp_engine_bay_hint", "Gdy siedzisz w samochodzie z zamkniętą maską, silnik pod nią nie jest rysowany. W lusterkach nadal go widać." },
            { "menu.exp_mirrors", "Lusterka 60 FPS" },
            { "menu.exp_mirrors_hint", "Powyżej 60 FPS lusterka samochodu nie są odświeżane w każdej klatce." },

            // === Ustawienia: grafika ===
            { "menu.header.graphics", "Grafika" },
            { "menu.sub.framerate", "Liczba klatek" },
            { "menu.limit_fps", "Ogranicz FPS" },
            { "menu.fps_limit", "Limit" },
            { "menu.shadow_distance", "Zasięg cieni, m" },
            { "menu.shadow_distance_hint", "Jak daleko widać cienie. Gra używa 200. Mniej — więcej FPS." },
            { "menu.sub.other", "Inne" },
            { "menu.dynamic_draw", "Dynamiczny zasięg rysowania" },
            { "menu.dynamic_draw_hint", "W pomieszczeniach daleki świat nie jest rysowany; na zewnątrz wraca Twój zasięg rysowania." },
            { "menu.run_background", "Działaj w tle" },
            { "menu.run_background_hint", "Po wyłączeniu gra pauzuje, gdy przełączysz się do innego okna." },
            { "menu.resolution", "Rozdzielczość ekranu" },

            // === Ustawienia: pozostałe ===
            { "menu.header.other", "Pozostałe" },
            { "menu.destroy_bottles", "Usuwaj puste opakowania" },
            { "menu.destroy_bottles_hint", "Puste butelki, szklanki, kubki i opakowania znikają zaraz po wypiciu, zamiast się gromadzić." },
            { "menu.disable_empty", "Ukrywaj opróżnione przedmioty" },
            { "menu.disable_empty_hint", "Opróżnione opakowania i puszki są ukryte, dopóki ich nie trzymasz." },
            { "menu.skidmarks", "Bez śladów opon" },
            { "menu.skidmarks_hint", "Ślady opon nie są rysowane. Na długich trasach powoli zjadają pamięć." },
            { "menu.check_updates", "Sprawdzaj aktualizacje" },
            { "menu.check_updates_hint", "Przy starcie gry MOPR sprawdza na GitHubie swoją najnowszą wersję i informuje, gdy wyjdzie nowa." },

            // === Ustawienia: zgodność z modami (reguły) ===
            { "menu.header.rules", "Zgodność z modami" },
            { "menu.rules_hint", "Reguły mówią MOPR, których obiektów innych modów nie ruszać. Dla znanych modów są wbudowane " +
                                 "w MOPR i włączają się same. Twoje własne reguły to pliki JSON w folderze reguł." },
            { "menu.rules_status_none", "Reguły nie zostały jeszcze wczytane." },
            { "menu.rules_builtin", "Wbudowane reguły dla: {0}" },
            { "menu.rules_builtin_none", "Twoje mody nie potrzebują wbudowanych reguł." },
            { "menu.rules_sep", " — " },
            { "menu.rules_kind_user", "Twój plik" },
            { "menu.rules_kind_legacy", "stary format" },
            { "menu.rules_count_one", "reguł: {0}" },
            { "menu.rules_count_many", "reguł: {0}" },
            { "menu.rules_problems_one", "błędów: {0}" },
            { "menu.rules_problems_many", "błędów: {0}" },
            { "menu.rules_unreadable", "nie da się odczytać" },
            { "menu.rules_off", "reguły wyłączone" },
            { "menu.rules_mod_missing", "mod {0} nie jest zainstalowany lub jest wyłączony" },
            { "menu.rules_more", "…i jeszcze {0}" },
            { "menu.rules_see_console", "Co jest nie tak — w konsoli: mopr rules" },
            { "menu.rules_next_load", "Zmiany zadziałają przy następnym wczytaniu gry." },
            { "menu.btn.reload_rules", "Odśwież reguły" },
            { "menu.btn.open_rules", "Otwórz folder reguł" },
            { "menu.btn.new_rules", "Utwórz własny plik reguł" },

            // === Ustawienia: logi i zgłoszenia błędów ===
            { "menu.header.logging", "Logi i zgłoszenia błędów" },
            { "menu.logging_hint", "Znalazłeś błąd? Naciśnij Report bug w MSCLoader — logi zostaną dołączone same." },
            { "menu.btn.open_logs", "Otwórz folder logów" },
            { "menu.btn.report", "Utwórz raport moda" },
            { "menu.btn.delete_logs", "Usuń wszystkie logi" },

            // === Ustawienia: lista zmian / o modzie ===
            { "menu.header.changelog", "Lista zmian" },
            { "menu.header.about", "O modzie" },
            { "menu.footer.test_build", " (wersja testowa)" },
            { "menu.footer.original", "Oryginalny MOP: © Konrad Figura 2019-2023" },
            { "menu.footer.revival", "Revival: ANICKON 2026. Licencja GPLv3." },

            // === Wspólne przyciski ===
            { "common.ok", "OK" },
            { "common.yes", "TAK" },
            { "common.no", "NIE" },

            // === Okna komunikatów ===
            { "msg.title", "MOP - REVIVAL" },
            { "msg.title.warning", "MOP - REVIVAL - Ostrzeżenie" },
            { "msg.welcome", "Witaj w MOP - REVIVAL {0}!\n\n" +
                             "Ta wersja jest zbudowana na oryginalnym, sprawdzonym kodzie MOP " +
                             "i dodaje optymalizator renderowania oraz optymalizacje silnika PlayMaker.\n\n" +
                             "<b>ZRÓB KOPIĘ ZAPASOWĄ ZAPISÓW!</b>" },
            { "msg.btn.changelog", "ZMIANY" },
            { "msg.changelog_title", "MOP - REVIVAL {0} - Lista zmian" },
            { "msg.restarts", "Gra była ponownie wczytywana już ponad {0} razy. Może to powodować problemy z fizyką gry." },
            { "msg.conflict", "MOP - REVIVAL nie działa razem z <color=yellow>{0}</color>. Najpierw wyłącz ten mod." },
            { "msg.rules_next_load", "Reguły zostały wczytane ponownie. Zadziałają przy następnym wczytaniu gry." },
            { "msg.open_link", "Zostanie otwarty link:\n<color=yellow>{0}</color>\n\nKontynuować?" },
            { "msg.safe_mode_on_later", "Tryb bezpieczny włączy się po wyjściu do menu głównego." },
            { "msg.safe_mode_off_later", "Tryb bezpieczny wyłączy się po wyjściu do menu głównego." },
            { "msg.msceditor", "Blok silnika jest zapisany jako przykręcony, ale część jego śrub nie jest w ogóle dokręcona. " +
                               "Sama gra tak nie zapisuje; zwykle zdarza się to po edycji zapisu (MSCEditor lub podobne).\n\n" +
                               "Przy tym wczytaniu MOPR pomija sprawdzanie, czy Satsuma załadowała się w całości. Możesz grać dalej." },
            { "msg.satsuma_not_loaded", "Gra nie załadowała Satsumy w całości!\n\n" +
                                        "Lepiej uruchom grę ponownie, aby uniknąć problemów." },
            { "msg.no_logs_folder", "Folder logów nie istnieje." },
            { "msg.no_output_log", "Nie znaleziono pliku „output_log.txt”." },
            { "msg.no_logs", "Brak logów." },
            { "msg.delete_logs_one", "Usunąć logi (<color=yellow>{0}</color>)?" },
            { "msg.delete_logs_many", "Usunąć logi (<color=yellow>{0}</color>)?" },
            { "msg.title.update", "MOP - REVIVAL - Aktualizacja" },
            { "msg.update_available", "Wyszła nowa wersja MOP - REVIVAL: <color=yellow>{0}</color>\nMasz {1}.\n\n" +
                                      "Pobierz ją z Nexus Mods." },
            { "msg.btn.open_nexus", "OTWÓRZ NEXUS MODS" },
            { "msg.btn.later", "PÓŹNIEJ" },

            // === Weryfikacja integralności zapisu ===
            { "save.title", "MOP - REVIVAL - Weryfikacja integralności zapisu" },
            { "save.check_title", "MOP - REVIVAL - Sprawdzenie zapisu" },
            { "save.found_one", "MOPR znalazł problemy w Twoim zapisie (<color=yellow>{0}</color>):" },
            { "save.found_many", "MOPR znalazł problemy w Twoim zapisie (<color=yellow>{0}</color>):" },
            { "save.more", "...i jeszcze <color=yellow>{0}</color>." },
            { "save.ask_one", "Spróbować naprawić?" },
            { "save.ask_many", "Spróbować naprawić?" },
            { "save.btn.report", "POKAŻ RAPORT" },
            { "save.fixed_one", "Naprawiono problemów: <color=yellow>{0}</color>." },
            { "save.fixed_many", "Naprawiono problemów: <color=yellow>{0}</color>." },
            { "save.failed_one", "Nie udało się naprawić: <color=red>{0}</color>." },
            { "save.failed_many", "Nie udało się naprawić: <color=red>{0}</color>." },
            { "save.backup_note", "Zapis został skopiowany do <color=yellow>defaultES2File.txt.mopbackup</color> i <color=yellow>items.txt.mopbackup</color>." },
            { "save.bug.bucket_seats", "Fotele kubełkowe: kupiony tylko jeden z pary" },
            { "save.bug.flatbed", "Przyczepa jest zapisana jako podpięta, ale stoi daleko od traktora" },
            { "save.bug.fuel_line", "Nieprawidłowe dokręcenie przewodu paliwowego.\n\nTeraz <b>{0}</b>, oczekiwane <b>{1}</b>." },

            // === Ekran ładowania ===
            { "load.waiting_mods", "Czekamy na inne mody" },
            { "load.checking_satsuma", "Sprawdzamy Satsumę" },
            { "load.restarting", "Ponownie ładujemy scenę, aby doładować Satsumę" },
            { "load.optimizing", "Optymalizacja świata" },
            { "load.final_touches", "Ostatnie szlify" },
            { "load.easter", "MIŁEGO DNIA :)" },
            { "load.tip_prefix", "WSKAZÓWKA: " },
            { "load.tip.0", "MOPR wyłącza dalekie obiekty i włącza je z powrotem, zanim podejdziesz." },
            { "load.tip.1", "Zapisywanie jest bezpieczne: przed zapisem gry MOPR włącza wszystkie obiekty z powrotem." },
            { "load.tip.2", "Znalazłeś błąd? Naciśnij Report bug w MSCLoader — logi zostaną dołączone same." },
            { "load.tip.3", "Wpisz „mopr help” w konsoli, aby zobaczyć polecenia MOPR." },
            { "load.tip.4", "Masz problemy? Tryb „Bezpieczny” przełącza tylko obiekty świata i nie rusza przedmiotów ani pojazdów." },
            { "load.tip.5", "Optymalizacje silnika PlayMaker pomijają tylko pracę, która na pewno niczego nie zmienia." },
            { "load.tip.6", "Własne reguły dla modów: przycisk „Utwórz własny plik reguł” w ustawieniach, sekcja „Zgodność z modami”." },
            { "load.tip.7", "Język można zmienić w ustawieniach MOP - REVIVAL." },
            { "load.tip.8", "Ślady opon powoli zjadają pamięć. Można je wyłączyć w sekcji „Pozostałe” ustawień." },
        };
    }
}
