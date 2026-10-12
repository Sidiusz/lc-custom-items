# CustomItems: каркас мода с кастомными предметами для Lethal Company

Стек: **BepInEx 5** (загрузчик модов) + **LethalLib 1.2.0** (регистрация предметов в игре) + **Unity AssetBundle** (ассеты Item и префабы предметов).

Схема обычная для модов на LethalLib: у каждого предмета есть ассет `Item` игры (название, стоимость, вес, хват, оффсеты) в `Assets/Sources/Data/Items/` и префаб в `Assets/Sources/Prefabs/` (модель, NetworkObject, коллайдер, скан-нода, скрипт поведения со звуками). Всё настраивается в инспекторе Unity. Мод берёт из бандла `ItemRegistry` (список предметов с редкостью и ценой) и регистрирует перечисленные там предметы.

Предметы: Егермейстер, энергетик Adrenaline Game Fuel, монитор 27", игрушечный пистолет, плюшевый Уинстон.

Образец, с которого удобно начинать новый предмет, — монитор: у него нет своего поведения (`Monitor27Item` пустой), всё задаётся в `Data/Items/Monitor27.asset` и `Prefabs/Monitor27.prefab`.

## Структура

Репозиторий одновременно Unity-проект (HDRP, Unity 2022.3.62f2) и C#-проект мода.

```
lc-custom-items/
├── Assets/
│   ├── Sources/                  всё наше в Unity
│   │   ├── Art/
│   │   │   ├── Models/<Предмет>/     fbx/obj (Tools/ пишет сюда)
│   │   │   ├── Textures/<Предмет>/   текстуры и иконки инвентаря
│   │   │   ├── Materials/<Предмет>/  материалы HDRP/Lit
│   │   │   └── Audio/<Предмет>/      звуки
│   │   ├── Prefabs/              префабы предметов (<Предмет>.prefab)
│   │   ├── Data/
│   │   │   ├── ItemRegistry.asset    какие предметы в моде: редкость, магазин, цена
│   │   │   └── Items/<Предмет>.asset ассеты Item: название, стоимость, вес, хват, оффсеты...
│   │   ├── Scenes/               тестовая сцена
│   │   └── Scripts/              Unity-скрипты (HeldItemPreview)
│   │       └── Editor/           меню Lethal Company: DLL, создание ассетов, сборка бандла
│   ├── Plugins/                  DLL игры и мода для редактора (создаётся сама, в .gitignore)
│   └── Settings/                 настройки HDRP (путь задан в ProjectSettings, не переносить)
├── Packages/, ProjectSettings/   Unity
├── Bundles/                      собранный AssetBundle `customitems` (коммитится)
├── CustomItems/                  C#-проект мода (вне Assets/, Unity его не видит)
│   ├── CustomItems.csproj        ссылки на BepInEx, LethalLib, библиотеки игры (NuGet)
│   ├── CustomItems.csproj        плагин BepInEx (CustomItems.dll)
│   ├── Plugin.cs                 грузит бандл и регистрирует предметы из ItemRegistry
│   ├── translations/             перевод для русификатора LethalRussian
│   └── Items/                    CustomItems.Items.dll: без BepInEx и LethalLib, её грузит и Unity
│       ├── ItemRegistry.cs       тип ассета ItemRegistry
│       ├── CustomGrabbable.cs    база: анимации/звуки из ванили, шейдеры HDRP
│       └── <Предмет>Item.cs      поведение предметов (вешается на префаб)
├── Tools/<Предмет>/              генераторы моделей и текстур (Blender/Python)
└── thunderstore/manifest.json    для публикации на Thunderstore
```

Код мода лежит вне `Assets/` и собирается `dotnet build`. Чтобы Unity знал скрипты предметов, готовая `CustomItems.Items.dll` копируется в `Assets/Plugins/CustomItems/` (сама, см. раздел 2), а в `Assets/Plugins/Game/` — `Assembly-CSharp.dll` и `Unity.Netcode.Runtime.dll` из папки игры (там лежат `Item`, `GrabbableObject`, `NetworkObject`) вместе со всем, на что они ссылаются. Без полного набора зависимостей Unity выгружает DLL («Unloading broken assembly»), и префабы сохраняются без скриптов. Поэтому скрипты предметов и вынесены в отдельную сборку без BepInEx и LethalLib. В бандле скрипты записаны по имени сборки и класса, поэтому в игре они находят те же классы мода и игры. Все скрипты самого Unity-проекта лежат в asmdef'ах, чтобы своя `Assembly-CSharp` не спорила с игровой.

## 1. Что нужно установить

- .NET SDK 8 (или новее) — для `dotnet build`.
- Unity 2022.3.62f2 (как у игры). Шаблон Lethal Company не нужен.
- r2modman или Gale с профилем Lethal Company, где стоят **BepInExPack** и **LethalLib**.

## 2. Unity: ассеты предметов и бандл

Порядок важен: сначала мод, потом Unity.

1. Соберите мод: `cd CustomItems && dotnet build -c Release` (раздел 3).
2. Откройте корень репозитория в Unity Hub (Add → Add project from disk).
3. При первом открытии Unity попросит папку игры (где лежит `Lethal Company.exe`) и скопирует из неё нужные DLL в `Assets/Plugins/Game/`. Если окно не появилось или папка поменялась: меню **Lethal Company → Setup → Import Game DLLs**. `CustomItems.Items.dll` подтягивается сама, когда вы возвращаетесь в Unity после `dotnet build` (или **Lethal Company → Setup → Update CustomItems.Items.dll**).
4. После этого Unity сам создаст недостающие ассеты: `Data/Items/<Предмет>.asset`, `Prefabs/<Предмет>.prefab` и `Data/ItemRegistry.asset`, и пометит их бандлом `customitems`. Вручную: **Lethal Company → Create Missing Item Assets**. Уже существующие ассеты не перезаписываются, так что правки в инспекторе не пропадут. Ассеты, сохранённые без скрипта (когда DLL не загрузились), пересоздаются.
5. Настраивайте в инспекторе:
   - **ассет Item** (`Data/Items`): Item Name, Is Scrap, Min/Max Value (игра умножает на 0.4), Weight (1.05 = 5 фунтов), Two Handed, Tool Tips, Item Icon, Position/Rotation Offset (в руке), Resting Rotation (на полу), Vertical Offset и всё остальное, что есть у ванильных предметов. Пустые анимации и звуки подбора/броска берутся у ванильного предмета из `Template Item Name` на префабе;
   - **префаб** (`Prefabs`): модель, коллайдер, скан-нода, на скрипте предмета звуки (`Voice Clip`, `Shot Clip`...) и `Template Item Name`;
   - **ItemRegistry** (`Data`): какие предметы попадают в мод, редкость скрапа, продажа в магазине и цена.
6. Меню **Lethal Company → Build AssetBundles**. Бандл окажется в `Bundles/customitems`, откуда его заберёт сборка мода.

Язык: в ассетах всё по-английски, перевод даёт русификатор **LethalRussian** по файлу `CustomItems/translations/Sidiusz-CustomItems.txt` (мод передаёт его через API LethalRussian, если тот установлен). Строки `@item English=Именительный|винительный|род` (род m/f/n/p) для названий, `English=Русский` для подсказок и сообщений. Меняете английское название или подсказку в Item — поправьте ключ и в этом файле.

Редкость, магазин и цена из ItemRegistry служат значениями по умолчанию для конфига BepInEx (`BepInEx/config/sidiusz.customitems.cfg`), так что их можно поменять и без пересборки. Если в конфиге уже есть строка, она важнее Unity: удалите её, чтобы взялось новое значение.

Если поменяли модель и хотите собрать префабы заново: **Lethal Company → Rebuild Item Prefabs** (ассеты Item не трогает, ручные правки префабов пропадут). Если поменяли код поведения (новые поля), пересоберите мод и бандл: поля префаба должны совпадать с DLL.

Если материал модели на другом шейдере (например, Standard из импортированной модели), код при появлении предмета переведёт его на HDRP/Lit с той же текстурой и цветом.

## 2.1. Как предмет держится в руке: превью в Unity

В игре предмет каждый кадр ставится в точку руки персонажа и сдвигается на Position / Rotation Offset из его ассета Item. Компонент `HeldItemPreview` (`Assets/Sources/Scripts/`) делает в редакторе то же самое, так что оффсеты можно подобрать на глаз.

1. Рипните игру AssetRipper'ом (Export → Unity project) и откройте экспорт отдельным проектом **или** скопируйте модель игрока с нужными мешами, анимациями и аватаром в `Assets/_Ripped/`. Эта папка в `.gitignore`: ассеты игры в репозиторий не кладём.
2. Поставьте в сцену префаб игрока. В иерархии найдите поиском `ItemHolder`: `LocalItemHolder` — рука в виде от первого лица (руки без тела), `ServerItemHolder` — рука на полной модели, как вас видят друзья. Проверьте оба.
3. Поставьте в сцену модель своего предмета (обычный экземпляр, не префаб-ассет).
4. Создайте пустой объект, повесьте на него `HeldItemPreview`, укажите `Holder`, `Item` и `Item Asset` (ассет из `Data/Items`). Кнопка **Взять из Item** подставит текущие оффсеты.
5. Чтобы увидеть позу руки, выделите модель игрока, откройте окно Animation и выберите клип удержания предмета. Анимацию, которую игра включит для вашего предмета, берём у ванильного шаблона из `Template Item Name` на префабе (по умолчанию ванильная Rubber Ducky). Её имя есть в поле `grabAnim` рипнутого ассета этого предмета.
6. Крутите `Position Offset` / `Rotation Offset` в инспекторе, затем нажмите **Записать в Item** и пересоберите бандл.

Для сравнения удобно открыть рипнутые ассеты Item ванильных предметов похожего размера: у них видно, какие оффсеты подобрали разработчики.

`Resting Rotation` — как предмет лежит на полу: игра берёт из него наклон по X и Z, а поворот по Y остаётся случайным. Обычно хватает подобрать, при каком повороте модель лежит естественно.

Финальную проверку всё равно делайте в игре: если что-то чуть съехало, значения можно поправить на лету через UnityExplorer (`itemProperties` предмета → `positionOffset` / `rotationOffset`), а потом перенести в ассет Item.

## 3. Сборка

```bash
cd CustomItems
dotnet build -c Release
```

Пакеты BepInEx и Unity берутся из фида `nuget.bepinex.dev`, он уже прописан в `.csproj`. Результат: `bin/Release/netstandard2.1/CustomItems.dll`, `CustomItems.Items.dll`, папка `translations` и `customitems`. Бандл берётся из `Bundles/`, поэтому после изменений в Unity сначала **Build AssetBundles**, потом `dotnet build`.

Чтобы после каждой сборки файлы сами копировались в профиль r2modman:

```bash
dotnet build -c Release -p:PluginDir="C:\Users\<вы>\AppData\Roaming\r2modmanPlus-local\LethalCompany\profiles\Dev\BepInEx\plugins"
```

(в r2modman путь профиля: Settings → Browse profile folder).

## 4. Проверка в игре

1. В папке профиля должно быть `BepInEx/plugins/CustomItems/`: `CustomItems.dll`, `CustomItems.Items.dll`, `customitems` и `translations/`.
2. В `BepInEx/config/BepInEx.cfg` включите консоль: `[Logging.Console] Enabled = true`.
3. Запустите игру через r2modman. В консоли должно быть `CustomItems 0.1.0 загружен` и никаких ошибок про AssetBundle или модель.
4. Быстрый тест без поиска по карте: поставьте `InShop = true` в `BepInEx/config/sidiusz.customitems.cfg` (секция с названием предмета, например `[Winston plush]`), купите его в терминале (`buy winston plush`, с LethalRussian и по-русски). Либо поставьте Imperium и заспавните предмет через его меню.
5. Для теста как скрапа поднимите `Rarity` (например, 200) и слетайте на любую луну.
6. Мультиплеер: проверьте с другом или двумя копиями игры (LAN-режим), что писк слышен второму игроку. У всех игроков мод должен быть одинаковой версии.

## 5. Как добавить новый предмет

1. Файлы в `Assets/Sources/Art/{Models,Textures,Materials,Audio}/<Предмет>/`. Генератор модели, если есть, в `Tools/<Предмет>/`.
2. Класс поведения `MyItem : CustomGrabbable` в `CustomItems/Items/`. Переопределяйте `ItemActivate` (ЛКМ), `Update`, `EquipItem`, `PocketItem`, `DiscardItem` и т.п. Если делать ничего не нужно, пустой класс. Звуки делайте публичными полями `AudioClip`: они появятся в инспекторе префаба. Соберите мод и вернитесь в Unity.
3. Ассеты, вариант А (руками, как в любом моде): Create → Item (ассет в `Data/Items`), префаб в `Prefabs` с моделью, `NetworkObject`, `BoxCollider`, `Rigidbody` (Is Kinematic), `AudioSource`, дочерним `ScanNode` (`BoxCollider` Is Trigger + `ScanNodeProperties`) и вашим `MyItem` (в Item Properties ваш Item). В Item укажите Spawn Prefab. Добавьте строку в `ItemRegistry`. Item и префаб пометьте бандлом `customitems`.
4. Вариант Б: добавьте описание в таблицу `Specs` в `Assets/Sources/Scripts/Editor/ItemAssets.cs` и нажмите **Lethal Company → Create Missing Item Assets**, он сделает всё из пункта 3.
5. **Build AssetBundles**, потом `dotnet build`.

Тег `PhysicsProp` и слои `Props` / `ScanNode` мод ставит сам при загрузке, в Unity их заводить не нужно. Хеш NetworkObject тоже задаётся модом из имени префаба, так что не переименовывайте префабы между версиями, на которых играете вместе.

Если предмету нужна своя сетевая логика (`[ServerRpc]` / `[ClientRpc]`), понадобится ещё **NetcodePatcher**, он патчит DLL после сборки. Для простых шуток (звук, анимация, шум по ЛКМ) хватает встроенной синхронизации (`Sync Use Function` в Item).

## 6. Публикация (когда будет готово)

Zip из `manifest.json`, `icon.png` 256×256, `README.md` и папки `BepInEx/plugins/CustomItems/` с обеими DLL, бандлом и `translations/`. Проверьте актуальные версии зависимостей в `manifest.json` на Thunderstore.
