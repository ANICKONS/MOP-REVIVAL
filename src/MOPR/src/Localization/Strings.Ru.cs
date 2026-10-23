// MOP - REVIVAL
// Copyright(C) 2026 ANICKON. Licensed under GPLv3 (see Loc.cs).
//
// Russian strings. A key missing here falls back to English (see Loc.T).
// Тексты настроек: подписи с заглавной буквы, под настройкой одна короткая понятная фраза (без внутренностей).
// Цвета добавляет MopMenu (MoprColors), здесь их нет.

using System.Collections.Generic;

namespace MOP.Localization
{
    internal static class StringsRu
    {
        internal static readonly Dictionary<string, string> Table = new Dictionary<string, string>
        {
            // === Настройки: информация ===
            // === Список модов (MSCLoader) ===
            // MSCLoader shows 4 lines in the mod list: keep it as short as the English one.
            { "mod.description", "Лучший проект оптимизации My Summer Car в 2026 году." },

            { "menu.header.info", "Информация" },
            { "menu.info.by", "автор {0}" },
            { "menu.info.based", "Основан на оригинальном MOP от Athlon - GPLv3" },
            { "menu.btn.github", "GitHub" },
            { "menu.btn.original_mop", "Оригинальный MOP" },

            // === Настройки: язык ===
            { "menu.header.language", "Language / Язык / Język" },

            // === Настройки: быстродействие ===
            { "menu.header.performance", "Быстродействие" },
            { "menu.sub.mode", "Режим производительности" },
            { "menu.mode.performance", "Производительность" },
            { "menu.mode.balanced", "Баланс" },
            { "menu.mode.quality", "Качество" },
            { "menu.mode.safe", "Безопасный" },
            { "menu.mode_hint", "Производительность — меньше видимость, больше FPS\n" +
                                "Баланс — рекомендуется\n" +
                                "Качество — дальше видимость, меньше FPS\n" +
                                "Безопасный — если что-то сломалось: MOPR управляет только зданиями и пейзажем." },
            { "menu.restart_note", "Дальность предметов и машин уже по новому режиму. Здания, комнаты дома и упрощённые " +
                                   "модели предметов — после следующей загрузки сохранения." },
            { "menu.restart_note_safe", "Безопасный режим включится или выключится после выхода в главное меню." },
            { "menu.sub.distance", "Дальность видимости" },
            { "menu.active_distance", "Радиус" },
            { "menu.dist.0", "Малый ×0.5" },
            { "menu.dist.1", "Меньше ×0.75" },
            { "menu.dist.2", "Обычный ×1" },
            { "menu.dist.3", "Больше ×2" },
            { "menu.dist.4", "Большой ×4" },
            { "menu.distance_hint", "Всё, что дальше этого радиуса, MOPR выключает и включает обратно, когда вы подходите. " +
                                    "Меньше радиус — больше FPS." },
            { "menu.sub.protection", "Защита сохранения" },
            { "menu.restart_guard", "Не сбрасывать детали и предметы" },
            { "menu.restart_guard_hint", "Когда MOPR включает объект обратно, игра иногда возвращает его краску, жидкости или двери " +
                                         "к последнему сохранению. Эта защита не даёт этому случиться." },

            // === Настройки: оптимизация отрисовки ===
            { "menu.header.render", "Оптимизация отрисовки" },
            { "menu.render_enable", "Включить оптимизацию отрисовки" },
            { "menu.render_hint", "Главный переключатель раздела: если выключить, всё ниже не работает (и скрывается)." },
            { "menu.render_quality", "Строгость" },
            { "menu.render_quality_hint", "Как рано мелкие и дальние вещи перестают рисоваться и отбрасывать тени." },
            { "menu.cull.0", "Больше FPS" },
            { "menu.cull.1", "Баланс" },
            { "menu.cull.2", "Лучше картинка" },
            { "menu.sub.objects", "Дальние объекты" },
            { "menu.render_culling", "Не рисовать крошечные дальние объекты" },
            { "menu.render_culling_hint", "То, что вдали заняло бы на экране пару пикселей, не рисуется." },
            { "menu.sub.shadows", "Тени" },
            { "menu.render_shadow_lod", "Без теней от мелочи вдали" },
            { "menu.render_shadow_lod_hint", "Мелкие предметы вдали не отбрасывают тень — её там всё равно не видно." },
            { "menu.render_lamps", "Без теней от дальних ламп" },
            { "menu.render_lamps_hint", "Дальние лампы светят как обычно, только без теней." },
            { "menu.sub.satsuma", "Satsuma" },
            { "menu.render_merge", "Быстрая отрисовка стоящей Satsuma" },
            { "menu.render_merge_hint", "Пока на машине никто не едет, её детали рисуются одним куском. Выглядит так же, " +
                                        "FPS рядом с машиной выше." },

            // === Настройки: движок игры ===
            { "menu.header.engine", "Движок игры" },
            { "menu.engine_fastpath", "Ускорить скрипты игры" },
            { "menu.engine_hint", "Пропускает работу скриптов, которая точно ничего не меняет. Около +10% FPS, игра ведёт себя так же." },
            { "menu.engine_mouse_ray", "Меньше лучей мыши" },
            { "menu.engine_mouse_ray_hint", "Детали машины и другие предметы каждый кадр спрашивают, наведена ли на них мышь. MOPR пускает " +
                                            "один луч за кадр на всех и отдаёт каждому тот же ответ, что дала бы игра. Около +5% FPS у дома." },
            { "menu.engine_mod_scripts", "Ускорить скрипты других модов" },
            { "menu.engine_mod_scripts_hint", "Better MSC: каждая лампочка в доме каждый кадр ищет компонент, которого не существует. " +
                                              "MOPR сразу отвечает «не найден», как ответил бы Unity. Около +19% FPS в доме." },
            { "menu.sub.diagnostics", "Для поиска багов" },
            { "menu.engine_verify", "Режим проверки" },
            { "menu.engine_verify_hint", "Ускорение не применяется: MOPR только сверяет результат с игрой и записывает расхождения " +
                                         "(консоль: mopr engine). Включайте, если вас об этом попросили." },

            // === Настройки: экспериментальное ===
            { "menu.header.experimental", "Экспериментальное" },
            { "menu.experimental_hint", "Всё выключено по умолчанию. Даёт больше FPS, но в редких случаях может вести себя чуть иначе, " +
                                        "чем игра без мода. Включайте по одному." },
            { "menu.exp_engine_bay", "Скрыть двигатель в режиме вождения" },
            { "menu.exp_engine_bay_hint", "Когда вы в машине и капот закрыт, двигатель под ним не рисуется. В зеркалах он виден." },
            { "menu.exp_mirrors", "Зеркала 60FPS" },
            { "menu.exp_mirrors_hint", "При FPS выше 60 зеркала машины обновляются не каждый кадр." },

            // === Настройки: графика ===
            { "menu.header.graphics", "Графика" },
            { "menu.sub.framerate", "Частота кадров" },
            { "menu.limit_fps", "Ограничить FPS" },
            { "menu.fps_limit", "Лимит" },
            { "menu.shadow_distance", "Дальность теней, м" },
            { "menu.shadow_distance_hint", "Как далеко видны тени. В игре — 200. Меньше — больше FPS." },
            { "menu.sub.other", "Прочее" },
            { "menu.dynamic_draw", "Динамическая дальность прорисовки" },
            { "menu.dynamic_draw_hint", "В помещениях дальний мир не рисуется, на улице дальность снова ваша." },
            { "menu.run_background", "Работать в фоне" },
            { "menu.run_background_hint", "Если выключить, игра встаёт на паузу, когда вы переключаетесь в другое окно." },
            { "menu.resolution", "Разрешение экрана" },

            // === Настройки: другое ===
            { "menu.header.other", "Другое" },
            { "menu.destroy_bottles", "Убирать пустую тару" },
            { "menu.destroy_bottles_hint", "Пустые бутылки, стаканы, чашки и пакеты после питья сразу исчезают, а не копятся." },
            { "menu.disable_empty", "Прятать опустевшие предметы" },
            { "menu.disable_empty_hint", "Опустевшие упаковки и банки скрываются, пока вы не держите их в руках." },
            { "menu.skidmarks", "Без следов шин" },
            { "menu.skidmarks_hint", "Следы шин не рисуются. На долгих поездках они понемногу съедают память." },
            { "menu.check_updates", "Проверять обновления" },
            { "menu.check_updates_hint", "При запуске игры MOPR узнаёт на GitHub свою последнюю версию и сообщает, если вышла новая." },

            // === Настройки: совместимость с модами (правила) ===
            { "menu.header.rules", "Совместимость с модами" },
            { "menu.rules_hint", "Правила говорят MOPR, какие объекты других модов не трогать. Для известных модов они уже " +
                                 "встроены в MOPR и включаются сами. Свои правила — JSON-файлы в папке правил." },
            { "menu.rules_status_none", "Правила ещё не прочитаны." },
            { "menu.rules_builtin", "Встроенные правила для: {0}" },
            { "menu.rules_builtin_none", "Встроенные правила для ваших модов не нужны." },
            { "menu.rules_sep", " — " },
            { "menu.rules_kind_user", "ваш файл" },
            { "menu.rules_kind_legacy", "старый формат" },
            { "menu.rules_count_one", "правил: {0}" },
            { "menu.rules_count_many", "правил: {0}" },
            { "menu.rules_problems_one", "ошибок: {0}" },
            { "menu.rules_problems_many", "ошибок: {0}" },
            { "menu.rules_unreadable", "не читается" },
            { "menu.rules_off", "правила выключены" },
            { "menu.rules_mod_missing", "мод {0} не установлен или выключен" },
            { "menu.rules_more", "…и ещё {0}" },
            { "menu.rules_see_console", "Что не так — в консоли: mopr rules" },
            { "menu.rules_next_load", "Изменения применятся при следующей загрузке игры." },
            { "menu.btn.reload_rules", "Перечитать правила" },
            { "menu.btn.open_rules", "Открыть папку правил" },
            { "menu.btn.new_rules", "Создать свой файл правил" },

            // === Настройки: логи и отчёты об ошибках ===
            { "menu.header.logging", "Логи и отчёты об ошибках" },
            { "menu.logging_hint", "Нашли баг? Нажмите Report bug в MSCLoader — логи приложатся сами." },
            { "menu.btn.open_logs", "Открыть папку логов" },
            { "menu.btn.report", "Создать отчёт мода" },
            { "menu.btn.delete_logs", "Удалить все логи" },

            // === Настройки: изменения / о моде ===
            { "menu.header.changelog", "Список изменений" },
            { "menu.header.about", "О моде" },
            { "menu.footer.test_build", " (тестовая сборка)" },
            { "menu.footer.original", "Оригинальный MOP: © Konrad Figura 2019-2023" },
            { "menu.footer.revival", "Revival: ANICKON 2026. Лицензия GPLv3." },

            // === Общие кнопки ===
            { "common.ok", "OK" },
            { "common.yes", "ДА" },
            { "common.no", "НЕТ" },

            // === Окна сообщений ===
            { "msg.title", "MOP - REVIVAL" },
            { "msg.title.warning", "MOP - REVIVAL - Предупреждение" },
            { "msg.welcome", "Добро пожаловать в MOP - REVIVAL {0}!\n\n" +
                             "Эта версия построена на оригинальном, проверенном временем коде MOP " +
                             "и добавляет оптимизатор рендера и оптимизации движка PlayMaker.\n\n" +
                             "<b>СДЕЛАЙТЕ БЭКАП СОХРАНЕНИЙ!</b>" },
            { "msg.btn.changelog", "ИЗМЕНЕНИЯ" },
            { "msg.changelog_title", "MOP - REVIVAL {0} - Список изменений" },
            { "msg.restarts", "Игра перезагружалась уже больше {0} раз. Это может вызвать проблемы с физикой игры." },
            { "msg.conflict", "MOP - REVIVAL не работает вместе с <color=yellow>{0}</color>. Сначала отключите этот мод." },
            { "msg.rules_next_load", "Правила перечитаны. Они применятся при следующей загрузке игры." },
            { "msg.open_link", "Будет открыта ссылка:\n<color=yellow>{0}</color>\n\nПродолжить?" },
            { "msg.safe_mode_on_later", "Безопасный режим включится после выхода в главное меню." },
            { "msg.safe_mode_off_later", "Безопасный режим выключится после выхода в главное меню." },
            { "msg.msceditor", "Блок двигателя сохранён как прикрученный, но часть его болтов не затянута совсем. " +
                               "Сама игра так не сохраняет; обычно это бывает после правки сейва (MSCEditor и подобные).\n\n" +
                               "На эту загрузку MOPR пропускает проверку, полностью ли загрузилась Satsuma. Играть можно дальше." },
            { "msg.satsuma_not_loaded", "Игра не загрузила Satsuma полностью!\n\n" +
                                        "Лучше перезапустите игру, чтобы избежать проблем." },
            { "msg.no_logs_folder", "Папки логов не существует." },
            { "msg.no_output_log", "Файл «output_log.txt» не найден." },
            { "msg.no_logs", "Логов нет." },
            { "msg.delete_logs_one", "Удалить логи (<color=yellow>{0}</color>)?" },
            { "msg.delete_logs_many", "Удалить логи (<color=yellow>{0}</color>)?" },
            { "msg.title.update", "MOP - REVIVAL - Обновление" },
            { "msg.update_available", "Вышла новая версия MOP - REVIVAL: <color=yellow>{0}</color>\nУ вас {1}.\n\n" +
                                      "Скачать её можно на Nexus Mods." },
            { "msg.btn.open_nexus", "ОТКРЫТЬ NEXUS MODS" },
            { "msg.btn.later", "ПОЗЖЕ" },

            // === Проверка целостности сохранения ===
            { "save.title", "MOP - REVIVAL - Проверка целостности сохранения" },
            { "save.check_title", "MOP - REVIVAL - Проверка сохранения" },
            { "save.found_one", "MOPR нашёл проблемы в вашем сохранении (<color=yellow>{0}</color>):" },
            { "save.found_many", "MOPR нашёл проблемы в вашем сохранении (<color=yellow>{0}</color>):" },
            { "save.more", "...и ещё <color=yellow>{0}</color>." },
            { "save.ask_one", "Попробовать исправить?" },
            { "save.ask_many", "Попробовать исправить?" },
            { "save.btn.report", "ПОКАЗАТЬ ОТЧЁТ" },
            { "save.fixed_one", "Исправлено проблем: <color=yellow>{0}</color>." },
            { "save.fixed_many", "Исправлено проблем: <color=yellow>{0}</color>." },
            { "save.failed_one", "Не удалось исправить: <color=red>{0}</color>." },
            { "save.failed_many", "Не удалось исправить: <color=red>{0}</color>." },
            { "save.backup_note", "Сохранение скопировано в <color=yellow>defaultES2File.txt.mopbackup</color> и <color=yellow>items.txt.mopbackup</color>." },
            { "save.bug.bucket_seats", "Ковшеобразные сиденья: куплено только одно из пары" },
            { "save.bug.flatbed", "Прицеп сохранён как прицепленный, но стоит далеко от трактора" },
            { "save.bug.fuel_line", "Неверная затяжка топливной магистрали.\n\nСейчас <b>{0}</b>, ожидается <b>{1}</b>." },

            // === Загрузочный экран ===
            { "load.waiting_mods", "Ждём остальные моды" },
            { "load.checking_satsuma", "Проверяем Satsuma" },
            { "load.restarting", "Перезагружаем сцену, чтобы догрузить Satsuma" },
            { "load.optimizing", "Оптимизация мира" },
            { "load.final_touches", "Последние штрихи" },
            { "load.easter", "ХОРОШЕГО ДНЯ :)" },
            { "load.tip_prefix", "СОВЕТ: " },
            { "load.tip.0", "MOPR отключает далёкие объекты и включает их обратно до того, как вы подойдёте." },
            { "load.tip.1", "Сохраняться безопасно: перед записью сейва MOPR включает все объекты обратно." },
            { "load.tip.2", "Нашли баг? Нажмите Report bug в MSCLoader — логи приложатся сами." },
            { "load.tip.3", "Введите «mopr help» в консоли, чтобы увидеть команды MOPR." },
            { "load.tip.4", "Есть проблемы? Режим «Безопасный» переключает только объекты мира и не трогает предметы и транспорт." },
            { "load.tip.5", "Оптимизации движка PlayMaker пропускают только ту работу, которая гарантированно ничего не меняет." },
            { "load.tip.6", "Свои правила для модов: кнопка «Создать свой файл правил» в настройках, раздел «Совместимость с модами»." },
            { "load.tip.7", "Язык можно сменить в настройках MOP - REVIVAL." },
            { "load.tip.8", "Следы шин понемногу съедают память. Их можно отключить в разделе «Другое» настроек." },
        };
    }
}
