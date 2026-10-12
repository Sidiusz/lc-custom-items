# CustomItems: каркас мода с кастомными предметами для Lethal Company

Стек: **BepInEx 5** (загрузчик модов) + **LethalLib 1.2.0** (регистрация предметов в игре) + **Unity AssetBundle** (только модель и звуки, всё остальное собирается в коде).
В проекте один пример: резиновая уточка (скрап, по ЛКМ пищит и привлекает монстров). Опционально продаётся в магазине через конфиг.

## Структура

Репозиторий одновременно Unity-проект (HDRP, Unity 2022.3.62f2) и C#-проект мода.

```
lc-custom-items/
├── Assets/                       Unity: модели, звуки, иконки предметов
│   └── Editor/BuildBundles.cs    меню Lethal Company → Build AssetBundles
├── Packages/, ProjectSettings/   Unity
├── Bundles/                      собранный AssetBundle `customitems` (коммитится)
├── CustomItems/                  C#-проект мода (вне Assets/, Unity его не видит)
│   ├── CustomItems.csproj        ссылки на BepInEx, LethalLib, библиотеки игры (NuGet)
│   ├── Plugin.cs                 список предметов и их регистрация
│   └── Items/
│       ├── CustomItemDef.cs      описание предмета (цена, вес, редкость, оффсеты...)
│       ├── ItemFactory.cs        собирает из голой модели сетевой предмет
│       ├── CustomGrabbable.cs    база: анимации/звуки из ванили, шейдеры HDRP
│       └── RubberDuckItem.cs     пример поведения
└── thunderstore/manifest.json    для публикации на Thunderstore
```

## 1. Что нужно установить

- .NET SDK 8 (или новее) — для `dotnet build`.
- Unity 2022.3.62f2 (как у игры). Шаблон Lethal Company не нужен.
- r2modman или Gale с профилем Lethal Company, где стоят **BepInExPack** и **LethalLib**.

## 2. Unity: только модель и звуки

Всё, что обычно настраивают в Unity (ассет Item, NetworkObject, коллайдер, Rigidbody, скан-нода, скрипт), мод создаёт сам в коде. В Unity нужен только бандл с ассетами.

1. Откройте корень репозитория в Unity Hub (Add → Add project from disk).
2. Импортируйте модель (fbx/obj/blend), сделайте из неё префаб `RubberDuck`. Масштаб: 1 единица = 1 метр, уточка ~0.2–0.3. Центр модели примерно в её середине. Материалы на шейдере HDRP/Lit, как в игре.
3. Импортируйте звук писка и назовите его `Squeak`.
4. По желанию: картинку-иконку (Texture Type = Sprite) для инвентаря. Без неё берётся иконка ванильного предмета.
5. Выделите префаб и звук, внизу инспектора в поле AssetBundle впишите `customitems`.
6. Меню **Lethal Company → Build AssetBundles**. Бандл окажется в `Bundles/customitems`, откуда его заберёт сборка мода.

Код ищет ассеты по имени (`ModelAsset`, `Squeak` в `Plugin.cs`), так что имена в Unity и в коде должны совпадать.

Если материал всё-таки на другом шейдере (например, Standard из импортированной модели), код при появлении предмета переведёт его на HDRP/Lit с той же текстурой и цветом.

## 3. Сборка

```bash
cd CustomItems
dotnet build -c Release
```

Пакеты BepInEx и Unity берутся из фида `nuget.bepinex.dev`, он уже прописан в `.csproj`. Результат: `bin/Release/netstandard2.1/CustomItems.dll` и рядом `customitems`.

Чтобы после каждой сборки файлы сами копировались в профиль r2modman:

```bash
dotnet build -c Release -p:PluginDir="C:\Users\<вы>\AppData\Roaming\r2modmanPlus-local\LethalCompany\profiles\Dev\BepInEx\plugins"
```

(в r2modman путь профиля: Settings → Browse profile folder).

## 4. Проверка в игре

1. В папке профиля должно быть `BepInEx/plugins/CustomItems/CustomItems.dll` и `.../CustomItems/customitems`.
2. В `BepInEx/config/BepInEx.cfg` включите консоль: `[Logging.Console] Enabled = true`.
3. Запустите игру через r2modman. В консоли должно быть `CustomItems 0.1.0 загружен` и никаких ошибок про AssetBundle или модель.
4. Быстрый тест без поиска по карте: поставьте `InShop = true` в `BepInEx/config/sidiusz.customitems.cfg` (секция `[Rubber duck]`), купите уточку в терминале (`buy rubber duck`). Либо поставьте Imperium и заспавните предмет через его меню.
5. Для теста как скрапа поднимите `Rarity` (например, 200) и слетайте на любую луну.
6. Мультиплеер: проверьте с другом или двумя копиями игры (LAN-режим), что писк слышен второму игроку. У всех игроков мод должен быть одинаковой версии.

## 5. Как добавить новый предмет

1. В Unity: префаб с моделью (+ звуки), тот же бандл `customitems`, пересобрать.
2. Класс поведения `MyItem : CustomGrabbable`. Переопределяйте `ItemActivate` (ЛКМ), `Update`, `EquipItem`, `PocketItem`, `DiscardItem` и т.п. Если делать ничего не нужно, пустой класс.
3. В `Plugin.Awake` одна запись:

```csharp
Register<MyItem>(bundle, new CustomItemDef
{
    Name = "Cursed sock",
    ModelAsset = "CursedSock",
    MinValue = 20, MaxValue = 60,
    DefaultRarity = 15,
    Weight = 1.02f,
});
```

Для магазинного предмета: `IsScrap = false, DefaultInShop = true, DefaultPrice = 40`. Редкость и цену потом можно менять в конфиге без пересборки.

Если предмет держится в руке криво, подгоните `PositionOffset` / `RotationOffset` (в руке) и `RestingRotation` (на полу).

Если предмету нужна своя сетевая логика (`[ServerRpc]` / `[ClientRpc]`), понадобится ещё **NetcodePatcher**, он патчит DLL после сборки. Для простых шуток (звук, анимация, шум по ЛКМ) хватает встроенной синхронизации (`SyncUse = true`).

## 6. Публикация (когда будет готово)

Zip из `manifest.json`, `icon.png` 256×256, `README.md` и папки `BepInEx/plugins/CustomItems/` с DLL и бандлом. Проверьте актуальные версии зависимостей в `manifest.json` на Thunderstore.
