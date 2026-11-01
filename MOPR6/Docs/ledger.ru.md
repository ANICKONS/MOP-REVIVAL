# Журнал изменений: известные писатели игры

Задача 20 (Т5.4, design §3, механизм 2). Список составлен по декомпиляции `Assembly-CSharp.dll`
(`_reference/decomp/Assembly-CSharp`, 820 действий PlayMaker) и по скану ванили операцией TestBot `writers`
(сценарий `inventory-dump`: 6 815 FSM, 51 760 состояний, все FSM сцены, включая выключенные).

## Как работает

Перед записью игры префикс сообщает журналу (`ChangeLedger.NoteExternalWrite`). Если мы держим это свойство,
запись становится `External`, арбитр снимает причины и отпускает её без записи: остаётся значение игры. Это
закрывает случай «игра пишет то же значение, что и мы» (скрыла уже скрытый нами рендерер): без хука возврат бы его
показал. Пока журнал пуст, каждый префикс выходит сразу (одно чтение статического поля).

Код: `MOPR6/Mod/Ledger/WriterHooks.cs`, ставится вместе со сценой (`MoprMod.OnPostLoad`).

## Действия PlayMaker

| Действие | В игре | Вид в журнале | Хук |
|---|---|---|---|
| `ActivateGameObject` | 12 750 (рекурсивно 2 845, сброс при выходе 1 515, каждый кадр 14) | `GoActive` (рекурсивно — всё поддерево) | `OnEnter`, `OnUpdate`, `OnExit` |
| `EnableFSM` | 2 180 | `FsmEnabled` | `OnEnter`, `OnExit` (сброс) |
| `SetParent` | 1 879 | `Parent` | `OnEnter` |
| `SetIsKinematic` | 458 | `RbKinematic` | `OnEnter` |
| `SetLayer` | 54 | `Layer` | `OnEnter` |
| `EnableBehaviour` | 40 (CarDynamics, Drivetrain, ForceFeedback, Wheel) | `BehaviourEnabled` | `OnEnter`, `OnExit` (сброс) |
| `Blink` | 10 (только рендерер) | `RendererEnabled` / `GoActive` | `OnEnter`, `OnUpdate` |
| `WakeUp` | 2 | `RbSleep` | `OnEnter` |
| `SetProperty` | 6 940 | по члену, см. ниже | `OnEnter`, `OnUpdate` |

`SetProperty` пишет через отражение. Члены, которые держит журнал (из 6 940 действий):

| Член | Действий | Вид |
|---|---|---|
| `SphereCollider.enabled` | 1 171 | `ColliderEnabled` |
| `BoxCollider.enabled` | 213 | `ColliderEnabled` |
| `MeshCollider.enabled` | 106 | `ColliderEnabled` |
| `CapsuleCollider.enabled` | 71 | `ColliderEnabled` |
| `Rigidbody.isKinematic` | 43 | `RbKinematic` |
| `Light.shadows` | 34 | `LightShadows` |
| `MeshRenderer.enabled` | 11 (8 каждый кадр) | `RendererEnabled` |
| `Light.enabled` | 9 | `LightEnabled` |
| `*.enabled` у Behaviour | AudioSource 27, Deformable 36, ES2Auto 25, CarDynamics 15, MouseLook 15, эффекты камеры… | `BehaviourEnabled` |

Остальное (`TextMesh.text`, `Material.mainTextureOffset`, параметры `Drivetrain`, `Wheel`, суставов…) журнал не
держит.

Нет в игре (0 действий): `SetVisibility`, `UseGravity`, `Sleep`, `WakeAllRigidBodies`, `Flicker`,
`ControllerSettings`, `SendMessage`, `InvokeMethod`, `CallStaticMethod`. `CallMethod` — одно
(`SetRainClip.SetClipValue`).

Уничтожение (`DestroyObject` 791, `DestroySelf` 247, `DestroyComponent` 834 — например, rigidbody детали при
установке) хуков не требует: при возврате журнал видит, что цели нет (`TargetGone`).

## Скрипты игры

Пишут те же свойства из кода (`.enabled`, `isKinematic`, `useGravity`, `layer`, `parent`): `CarDynamics`,
`UnityCar`, `Wheel`, `FuelTank`, `StartGame`, `SettingsMenu`, `iTween`, эффекты камеры, `LODcam` и `MainCamera`
(`layerCullDistances`). Их ловит фоновая сверка (механизм 3): найденный дрейф — `External` и строка «неизвестный
писатель» в логе; такие случаи разбираются и при необходимости получают свой хук.

## Что проверено

- Сборка и модульные тесты: внешнее изменение, «то же значение от игры», отпускание без записи (`LedgerTests`).
- В игре: пока нет. Проверка «взять замороженный предмет» — с заморозкой физики (этап 4); до этого — тестовой
  системой, когда появится сценарий на `SetIsKinematic`.
