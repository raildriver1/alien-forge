# alien-forge

Извлечение ассетов Alien: Isolation — модели, текстуры, анимации, уровни и интерфейс.

---

## 🚀 HOW TO USE

> ⚠️ **Релизы пока не делались.** Скачивайте ветку целиком через **Code → Download ZIP** на GitHub, а не ищите файл в Releases — его там нет.

### 1. Скачать проект

1. Откройте страницу репозитория на GitHub
2. Нажмите зелёную кнопку **`<> Code`**
3. Выберите **`Download ZIP`**
4. Распакуйте архив в любую удобную папку

### 2. Собрать проект

Понадобится **.NET 9 SDK**.

```bash
dotnet build -c Release src/AlienForge.App
```

(при необходимости также `src/AlienForge.Cli` для консольной версии)

### 3. Запустить

Готовый `.exe` после сборки лежит по пути:

```
..\alien-forge\src\AlienForge.App\bin\Release\net9.0-windows\AlienForge.exe
```

Просто запустите его — дальше всё управляется через интерфейс программы.

### 4. Установить JPEXS Free Flash Decompiler (опционально, но желательно)

Нужен для просмотра и экспорта UI-элементов игры (`.GFX` / Scaleform).

- Скачайте JPEXS Free Flash Decompiler с официального сайта
- Установите в `Program Files\FFDec` **или** положите на рабочий стол
- Либо укажите путь вручную через переменную окружения `FFDEC_HOME`

Без JPEXS всё остальное (модели, анимации, уровни) работает без ограничений — decompiler нужен только для раздела **«Интерфейс»**.

---

## Что умеет

- **Модели** (.cs2 → .glb) с текстурами и скином. Роль текстуры берётся из
  привязки сэмплера убершейдера (DIFFUSE_MAP, NORMAL_MAP, SPECULAR_MAP…), а не
  из буквы в имени файла — поэтому цвет подхватывается у ~95 % материалов.
  Учитываются DIFFUSE_TINT, DIFFUSE_UV_MULT, прозрачность/вырез по флагам шейдера.
- **Анимации**: реальные имена клипов из ANIM_STRING_DB (вместо `ANIM_356982…`),
  риг подбирается сам — по пути модели, по имени (RIPLEY_FP → FEMALEFP,
  HEAD_ADRIANA → ADRIANA, ANDROID → MALE) и проверяется по числу костей скина.
  Статические позы (одноключевые каналы) экспортируются как константные каналы.
  Геометрия, скелет и клипы зеркалятся по Z (левосторонняя система игры →
  правосторонний glTF), как и в экспорте уровней; консольный `export` теперь
  тоже пишет настоящие имена клипов.
- **Уровень целиком** (меню «Экспорт → Уровень целиком в .glb»): иерархия
  композитов из COMMANDS, позиции алиасов/прокси, MaterialMappings, без
  окклюдеров, LOD, частиц и служебных мешей — как показывает игра/OpenCAGE.
  Оси и обмотка треугольников уже под Blender.
- **Материалы 1:1 в Blender**: в extras каждого материала .glb — убершейдер,
  роль каждого канала RGBA каждой карты (по дизассемблированным пиксельным
  шейдерам игры, см. `ChannelSemantics.cs`), фичи и параметры (DIFFUSE_TINT,
  SPECULAR_POWER, UV_MULT…); в файл кладутся все карты, не только диффуз, и
  цвет вершин COLOR_0. `tools/alienforge_blender.py` после импорта .glb
  пересобирает деревья нод по этим данным (Run Script или аддон,
  F3 → «AlienForge: Rebuild materials»).
- **Уровень для Godot** (флажки в экспорте уровня): коллизия по суффиксам
  `-col`, родные окклюдеры игры `-occonly` (OccluderInstance3D), динамические
  пропсы `-rigid`, свет KHR_lights_punctual (Omni/Spot), зоны стриминга
  `ZONE_*`, частицы и туман через sidecar `<файл>.glb.alienforge.json`.
  Скрипты в `tools/godot/`: `alienforge_level_import.gd` (Import Script для
  .glb — собирает GPUParticles3D, FogVolume с шейдером «стелющегося» тумана,
  WorldEnvironment с volumetric fog, настраивает свет) и
  `alienforge_zone_streaming.gd` (рендерятся только зоны рядом с камерой).
  Экспортёр исполняет скрипт уровня «на момент загрузки» (`LevelExporter.Logic`):
  шаблоны (`is_template`, кроме тех, что AssetSpawner ставит при загрузке),
  удалённые (`deleted` ← LogicNot/And/Or ← переменные композита, Delete*-функции
  Door_Mechanism по DOOR_MECHANISM), скрытые (`show_on_reset`, `disable_display`,
  Master) в файл не попадают — на дверях не висит по три механизма, нет
  повреждённых дублей ламп и т.п. Проекторы CA_DECAL (кровь, потёртости)
  идут отдельными узлами `DECAL_*`, в Godot они становятся `Decal`.
- **Интерфейс** (меню «Интерфейс»): предпросмотр прямо в окне — DDS сразу, у
  .GFX кадры ролика и спрайты рендерит JPEXS в кэш, потом проигрываются с
  нужным fps. Список UI.PAK, открытие .GFX (Scaleform) в
  JPEXS Free Flash Decompiler, выгрузка спрайтов PNG-последовательностями через
  `ffdec-cli`. JPEXS ищется в `Program Files\FFDec`, на рабочем столе или по
  переменной `FFDEC_HOME`.
- **Просмотр**: камера как в Blender — ЛКМ/СКМ вращение без ограничения по
  вертикали (360°), ПКМ/Shift+СКМ сдвиг, колесо/Ctrl+СКМ зум, Numpad 1/3/7 виды
  (Ctrl — с обратной стороны), 4/6/8/2 шаг 15°, 9 разворот, F/Home вписать.

---

## Консоль

```
alienforge export ENG_ALIEN_NEST CHARACTERS\ALIEN\model0.cs2 out\alien.glb 5
alienforge level SCI_HOSPITALLOWER out\hospital.glb [--no-textures] [--unlit] [--gray] [--no-decals]
                                                [--no-collision] [--no-occluders] [--no-lights] [--no-particles] [--no-fog] [--no-rigid] [--no-zones]
alienforge funcs SCI_HOSPITALLOWER [фильтр]     # какие функции/композиты есть на уровне и с какими параметрами
alienforge comp SCI_HOSPITALLOWER Door_Mechanism --links   # дамп композита: сущности, параметры, связи, кто его инстанцирует
alienforge paramstats SCI_HOSPITALLOWER AssetSpawner       # частоты параметров у сущностей типа
alienforge enum DOOR_MECHANISM                  # значения перечисления Cathode
alienforge rig SCI_ANDROIDLAB CHARACTERS        # какой риг подобран каждой модели
alienforge rigs [фильтр]                        # все риги архива анимаций
alienforge texstats SCI_ANDROIDLAB              # статистика привязки текстур
alienforge shaderdump SCI_ANDROIDLAB out\shaders # байткод шейдеров + сэмплеры/параметры (для D3DDisassemble)
alienforge ui list [фильтр]
alienforge ui open MOTIONTRACKER                # в JPEXS
alienforge ui sprites MOTIONTRACKER out\tracker # спрайты → PNG
alienforge guitest <уровень> <модель> <out.glb> # тот же путь, что и кнопки GUI
```

GUI-автотест: `AlienForge.exe --autotest <уровень> <фрагмент модели> <out.glb> [лог]`.

---

## Сборка

- **.NET 9** — `dotnet build -c Release src/AlienForge.App` (и `src/AlienForge.Cli`)
- Havok-анимации распаковывает `hkdump.exe`, который должен лежать рядом с программой
