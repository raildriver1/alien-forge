using System.ComponentModel;

namespace AlienForge.App;

public enum Lang
{
    Russian,
    English,
}

/// <summary>
/// String table with live language switching.
/// </summary>
/// <remarks>
/// Bound from XAML through the indexer, e.g.
/// <c>{Binding [MENU_FILE], Source={x:Static local:Loc.Current}}</c>. Changing the
/// language raises a change notification for the indexer itself, which makes every
/// bound string re-read at once, so no window reload is needed.
/// </remarks>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Current { get; } = new();

    private Lang _language = Lang.Russian;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Lang Language
    {
        get => _language;
        set
        {
            if (_language == value)
                return;
            _language = value;
            // "Item[]" is the name WPF listens for to refresh indexer bindings.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRussian)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
        }
    }

    public bool IsRussian => _language == Lang.Russian;
    public bool IsEnglish => _language == Lang.English;

    public string this[string key] => Get(key);

    public static string T(string key) => Current.Get(key);

    private string Get(string key)
    {
        if (!Table.TryGetValue(key, out var pair))
            return key;
        return _language == Lang.Russian ? pair.Ru : pair.En;
    }

    /// <summary>Formats a localised string that contains placeholders.</summary>
    public static string F(string key, params object?[] args)
        => string.Format(Current.Get(key), args);

    private readonly record struct Pair(string Ru, string En);

    private static readonly Dictionary<string, Pair> Table = new()
    {
        // ---------------------------------------------------------------- shell
        ["APP_TITLE"] = new("AlienForge — браузер ассетов Alien: Isolation",
                            "AlienForge — Alien: Isolation asset browser"),

        ["MENU_FILE"] = new("Файл", "File"),
        ["MENU_OPEN_GAME"] = new("Выбрать папку игры...", "Choose game folder..."),
        ["MENU_DETECT"] = new("Найти игру автоматически", "Detect game automatically"),
        ["MENU_RELOAD"] = new("Перезагрузить уровень", "Reload level"),
        ["MENU_EXIT"] = new("Выход", "Exit"),

        ["MENU_EXPORT"] = new("Экспорт", "Export"),
        ["MENU_EXPORT_MODEL"] = new("Модель в .glb...", "Model to .glb..."),
        ["MENU_EXPORT_TEXTURES"] = new("Текстуры модели в PNG...", "Model textures to PNG..."),
        ["MENU_EXPORT_TEXTURE"] = new("Текущую текстуру в PNG...", "Current texture to PNG..."),
        ["MENU_EXPORT_LEVEL"] = new("Все модели уровня...", "All models in level..."),

        ["MENU_VIEW"] = new("Вид", "View"),
        ["MENU_SHOW_COLLISION"] = new("Показывать коллизии", "Show collision hulls"),
        ["MENU_WIREFRAME"] = new("Каркас", "Wireframe"),
        ["MENU_RESET_CAMERA"] = new("Сбросить камеру", "Reset camera"),

        ["MENU_LANGUAGE"] = new("Язык", "Language"),
        ["MENU_RUSSIAN"] = new("Русский", "Russian"),
        ["MENU_ENGLISH"] = new("English", "English"),

        ["MENU_HELP"] = new("Справка", "Help"),
        ["MENU_ABOUT"] = new("О программе", "About"),

        // -------------------------------------------------------------- toolbar
        ["GAME_PATH"] = new("Папка игры", "Game folder"),
        ["BROWSE"] = new("Обзор", "Browse"),
        ["LEVEL"] = new("Уровень", "Level"),
        ["FILTER"] = new("Фильтр", "Filter"),
        ["FILTER_HINT"] = new("часть имени, например alien", "part of a name, e.g. alien"),
        ["LOAD"] = new("Загрузить", "Load"),

        // ---------------------------------------------------------------- panes
        ["PANE_ARCHIVE"] = new("Содержимое архива", "Archive contents"),
        ["PANE_DEPENDENCIES"] = new("Зависимости", "Dependencies"),
        ["PANE_PREVIEW"] = new("Просмотр", "Preview"),

        ["TAB_MAP"] = new("Карта", "Map"),
        ["MAP_HINT"] = new("Колесо — масштаб, перетаскивание — сдвиг, клик по узлу — открыть",
                           "Wheel zooms, drag pans, click a node to open it"),
        ["MAP_PICK"] = new("Выберите модель, чтобы увидеть карту её зависимостей",
                           "Pick a model to see its dependency map"),
        ["HUB_MATERIALS"] = new("Материалы", "Materials"),
        ["HUB_TEXTURES"] = new("Текстуры", "Textures"),
        ["HUB_ANIMATIONS"] = new("Анимации", "Animations"),
        ["HUB_ANIM_HINT"] = new("нажмите «Найти анимации модели»", "press Find model animations"),
        ["TAB_MODEL"] = new("Модель", "Model"),
        ["TAB_TEXTURE"] = new("Текстура", "Texture"),
        ["TAB_ANIMATION"] = new("Анимации", "Animations"),
        ["TAB_INFO"] = new("Сведения", "Details"),

        // ------------------------------------------------------------ model tab
        ["MODEL_PARTS"] = new("Частей", "Parts"),
        ["MODEL_VERTICES"] = new("Вершин", "Vertices"),
        ["MODEL_TRIANGLES"] = new("Треугольников", "Triangles"),
        ["MODEL_MATERIALS"] = new("Материалов", "Materials"),
        ["MODEL_TEXTURES"] = new("Текстур", "Textures"),
        ["MODEL_TEXTURED_PARTS"] = new("С текстурой", "Textured"),
        ["MODEL_SKINNED"] = new("Со скином", "Skinned"),
        ["MODEL_HINT"] = new("Левая кнопка — поворот, колесо — приближение, правая — сдвиг",
                             "Left button orbits, wheel zooms, right button pans"),
        ["MODEL_PICK"] = new("Выберите модель в дереве слева",
                             "Pick a model in the tree on the left"),

        ["PARTS_TITLE"] = new("Части модели", "Model parts"),
        ["PARTS_ISOLATE"] = new("Только эта", "Isolate"),
        ["PARTS_SHOW_ALL"] = new("Показать все", "Show all"),

        ["LIGHT_TITLE"] = new("Освещение", "Lighting"),
        ["LIGHT_AZIMUTH"] = new("Поворот вокруг модели", "Rotation around model"),
        ["LIGHT_ELEVATION"] = new("Высота источника", "Light height"),
        ["LIGHT_INTENSITY"] = new("Яркость", "Brightness"),
        ["LIGHT_AMBIENT"] = new("Заполняющий свет", "Ambient light"),
        ["LIGHT_FOLLOW"] = new("Свет от камеры", "Light from camera"),
        ["LIGHT_RESET"] = new("Сброс", "Reset"),
        ["LIGHT_FROM_CAMERA"] = new("от камеры", "from camera"),

        ["CAM_TITLE"] = new("Камера", "Camera"),
        ["CAM_FOLLOW_MODEL"] = new("Следить за моделью в анимации",
                                   "Follow the model during playback"),
        ["CAM_HELP"] = new(
            "Левая кнопка — поворот, средняя или правая — сдвиг, колесо — приближение. " +
            "W A S D — движение, Q и E — вверх и вниз, Shift — быстрее, F — вписать в кадр.",
            "Left button orbits, middle or right pans, wheel zooms. " +
            "W A S D moves, Q and E go up and down, Shift is faster, F frames everything."),

        // ---------------------------------------------------------- texture tab
        ["TEX_FORMAT"] = new("Формат", "Format"),
        ["TEX_SIZE"] = new("Размер", "Size"),
        ["TEX_MIPS"] = new("Мип-уровней", "Mip levels"),
        ["TEX_SOURCE"] = new("Источник", "Source"),
        ["TEX_ALPHA"] = new("Альфа-канал", "Alpha channel"),
        ["TEX_PICK"] = new("Выберите текстуру в дереве",
                           "Pick a texture in the tree"),
        ["TEX_ALPHA_YES"] = new("есть", "yes"),
        ["TEX_ALPHA_NO"] = new("нет", "no"),
        ["TEX_SHOW_ALPHA"] = new("Показать альфу", "Show alpha"),

        // -------------------------------------------------------- animation tab
        ["ANIM_SKELETON"] = new("Скелет", "Skeleton"),
        ["ANIM_CLIPS"] = new("Клипов", "Clips"),
        ["ANIM_EXPORT"] = new("Сохранить .glb", "Save .glb"),
        ["ANIM_EXPORT_ALL"] = new("Все клипы в .glb", "All clips to .glb"),
        ["ANIM_LOAD"] = new("Найти анимации модели", "Find model animations"),
        ["ANIM_NONE"] = new("Для этой модели анимации не найдены",
                            "No animations found for this model"),
        ["ANIM_PICK_MODEL"] = new(
            "Сначала выберите модель на вкладке «Модели», затем нажмите «Найти анимации модели».",
            "Pick a model on the Models tab first, then press Find model animations."),
        ["ANIM_PLAY"] = new("Проиграть", "Play"),
        ["ANIM_STOP"] = new("Стоп", "Stop"),
        ["ANIM_FRAME"] = new("Кадр", "Frame"),
        ["ANIM_DECODING"] = new("Распаковка {0}...", "Unpacking {0}..."),
        ["ANIM_EMPTY"] = new("В клипе нет ни скелета, ни треков",
                             "The clip has neither a skeleton nor tracks"),
        ["ANIM_NO_TOOL"] = new(
            "Не найден {0} — без него анимации Havok не распаковать. Положите его рядом " +
            "с AlienForge.exe или на рабочий стол в _havoklib\\build\\hkdump_bin.",
            "{0} not found — Havok animations cannot be unpacked without it. Put it next " +
            "to AlienForge.exe or on the desktop under _havoklib\\build\\hkdump_bin."),
        ["ANIM_ABOUT"] = new(
            "Клип можно проиграть здесь же и сохранить в .glb для Blender вместе с моделью " +
            "и скелетом. В игре 14975 названных анимаций у 362 персонажей. Если скелет " +
            "модели определить не удалось, выберите его в списке справа: у андроидов, " +
            "например, человеческий риг, а не свой собственный.",
            "A clip plays here and saves to .glb for Blender together with the model and its " +
            "rig. The game holds 14975 named animations across 362 characters. When a model's " +
            "rig cannot be worked out from its path, pick it from the list on the right: " +
            "androids, for one, use a human rig rather than one of their own."),

        // ------------------------------------------------------------ browsing
        ["LIST_MODELS"] = new("Модели", "Models"),
        ["LIST_TEXTURES"] = new("Текстуры", "Textures"),
        ["LIST_ANIMS"] = new("Анимации", "Animations"),
        ["SEARCH"] = new("Поиск", "Search"),
        ["SEARCH_HINT"] = new("начните печатать...", "start typing..."),
        ["SHOWN"] = new("показано {0} из {1}", "showing {0} of {1}"),
        ["PICK_ANY"] = new("Выберите что-нибудь в списке слева",
                           "Select something in the list on the left"),
        ["DEPENDENCIES_OF"] = new("Из чего состоит", "What it is made of"),
        ["DLG_HKX_FILTER"] = new("Анимация Havok (*.hkx)|*.hkx", "Havok animation (*.hkx)|*.hkx"),
        ["ANIM_NO_SKELETON"] = new("Скелет для этой модели не найден",
                                   "No skeleton found for this model"),
        ["ANIM_EXPORT_OK"] = new("Записано анимаций: {0}, костей: {1}",
                                 "Wrote {0} animations, {1} bones"),
        ["ANIM_EXPORT_PARTIAL"] = new("Записано анимаций: {0}, не распаковалось секций: {1}",
                                      "Wrote {0} animations, {1} sections failed"),
        ["ANIM_EXPORT_RAW"] = new("Сырой .hkx", "Raw .hkx"),
        ["ANIM_SKELETON_PICK"] = new("Скелет:", "Skeleton:"),
        ["ANIM_RIG_MISMATCH"] = new(
            "выбран не родной скелет модели — список и экспорт работают, поза будет неверной",
            "not this model's own rig — listing and export work, the pose will be wrong"),
        ["ANIM_RIG_UNKNOWN"] = new(
            "по пути модели скелет не опознан — выберите его в списке",
            "the model path does not name a rig — pick one from the list"),
        ["EXPORT_VALID"] = new("проверка пройдена", "validated"),
        ["SAVED_N"] = new("Сохранено {0} файлов в {1}", "Saved {0} files to {1}"),

        // --------------------------------------------------------------- status
        ["STATUS_READY"] = new("Готово", "Ready"),
        ["STATUS_NO_GAME"] = new("Игра не найдена — укажите папку вручную",
                                 "Game not found — choose the folder manually"),
        ["STATUS_LOADING"] = new("Загрузка уровня {0}...", "Loading level {0}..."),
        ["STATUS_STAGE"] = new("{0}: {1}", "{0}: {1}"),
        ["STAGE_GLOBALTEXTURES"] = new("общие текстуры (88 МБ, один раз)",
                                       "shared textures (88 MB, once)"),
        ["STAGE_LEVELTEXTURES"] = new("текстуры уровня", "level textures"),
        ["STAGE_SHADERS"] = new("шейдеры", "shaders"),
        ["STAGE_COLLISIONS"] = new("коллизии и морф-таргеты", "collisions and morph targets"),
        ["STAGE_MATERIALS"] = new("материалы", "materials"),
        ["STAGE_MODELS"] = new("модели", "models"),
        ["STAGE_DONE"] = new("построение дерева", "building the tree"),
        ["STATUS_LOADED"] = new("{0}: моделей {1}, материалов {2}, текстур {3} — за {4:F1} с",
                                "{0}: {1} models, {2} materials, {3} textures — in {4:F1} s"),
        ["STATUS_DECODING"] = new("Разбор {0}...", "Decoding {0}..."),
        ["STATUS_EXPORTED"] = new("Записан {0} ({1:F2} МБ)", "Wrote {0} ({1:F2} MB)"),
        ["STATUS_EXPORT_FAILED"] = new("Экспорт не удался: {0}", "Export failed: {0}"),
        ["STATUS_VALID"] = new("Проверка glTF: {0}", "glTF check: {0}"),
        ["STATUS_BUSY"] = new("Занято, дождитесь завершения", "Busy, wait for the current task"),

        // --------------------------------------------------------------- dialogs
        ["DLG_PICK_GAME"] = new("Укажите папку установленной Alien: Isolation",
                                "Select the installed Alien: Isolation folder"),
        ["DLG_PICK_OUT"] = new("Куда сохранить", "Where to save"),
        ["DLG_GLB_FILTER"] = new("Модель glTF (*.glb)|*.glb", "glTF model (*.glb)|*.glb"),
        ["DLG_PNG_FILTER"] = new("Изображение PNG (*.png)|*.png", "PNG image (*.png)|*.png"),
        ["ABOUT_TEXT"] = new(
            "AlienForge — извлечение моделей, текстур и анимаций из Alien: Isolation.\n\n" +
            "Чтение форматов: CathodeLib. Декодеры вершин, BC1/BC3/BC5(DXN)/BC7 и запись " +
            "glTF — свои.\nЭкспорт .glb открывается в Blender напрямую.",
            "AlienForge extracts models, textures and animations from Alien: Isolation.\n\n" +
            "Format reading: CathodeLib. Vertex, BC1/BC3/BC5(DXN)/BC7 decoders and the " +
            "glTF writer are our own.\nThe .glb export opens directly in Blender."),

        ["NONE"] = new("нет", "none"),
        ["YES"] = new("да", "yes"),
        ["NO"] = new("нет", "no"),
    };
}
