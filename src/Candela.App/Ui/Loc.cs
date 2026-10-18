namespace Candela.App.Ui;

/// <summary>
/// The editor's UI strings in one resource class: English (the fallback) and Russian. <see cref="Get"/> is an array
/// lookup (no allocation), so labels can be fetched every frame. Strings with placeholders use
/// <see cref="string.Format(IFormatProvider, string, object[])"/> syntax (<c>{0}</c>).
/// </summary>
internal static class Loc
{
    /// <summary>Supported UI languages (code, native name).</summary>
    public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("ru", "Русский") };

    private static readonly string[] English;
    private static readonly string[] Russian;
    private static string[] s_current;

    static Loc()
    {
        int n = Enum.GetValues<Str>().Length;
        English = new string[n];
        Russian = new string[n];
        foreach (var (key, en, ru) in Table)
        {
            English[(int)key] = en;
            Russian[(int)key] = ru;
        }

        for (int i = 0; i < n; i++)
            English[i] ??= ((Str)i).ToString();
        s_current = English;
    }

    /// <summary>Raised after <see cref="Language"/> changed (panels rebuild cached titles).</summary>
    public static event Action? LanguageChanged;

    /// <summary>Current language code ("en", "ru"); unknown codes select English.</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>Selects the UI language.</summary>
    public static void SetLanguage(string? code)
    {
        string normalized = code?.Trim().ToLowerInvariant() == "ru" ? "ru" : "en";
        if (normalized == Language)
            return;
        Language = normalized;
        s_current = normalized == "ru" ? Russian : English;
        LanguageChanged?.Invoke();
    }

    /// <summary>The text of <paramref name="key"/> in the current language (English when no translation exists).</summary>
    public static string Get(Str key)
    {
        int i = (int)key;
        if ((uint)i >= (uint)s_current.Length)
            return key.ToString();
        return s_current[i] is { Length: > 0 } s ? s : English[i];
    }

    /// <summary>Formats the text of <paramref name="key"/> with arguments (culture-invariant numbers).</summary>
    public static string Format(Str key, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Get(key), args);

    /// <summary>English text (tests, logs).</summary>
    internal static string English_(Str key) => English[(int)key];

    /// <summary>Russian text or null when untranslated (tests).</summary>
    internal static string? Russian_(Str key) => Russian[(int)key];

    /// <summary>All entries (tests check completeness).</summary>
    internal static IReadOnlyList<(Str Key, string En, string Ru)> Entries => Table;

    private static readonly (Str Key, string En, string Ru)[] Table =
    {
        (Str.AppName, "Candela", "Candela"),
        (Str.Untitled, "Untitled", "Без названия"),

        (Str.MenuFile, "File", "Файл"),
        (Str.MenuEdit, "Edit", "Правка"),
        (Str.MenuView, "View", "Вид"),
        (Str.MenuTools, "Tools", "Инструменты"),
        (Str.MenuWindow, "Window", "Окно"),
        (Str.MenuHelp, "Help", "Справка"),

        (Str.FileNew, "New", "Создать"),
        (Str.FileOpen, "Open…", "Открыть…"),
        (Str.FileOpenRecent, "Open Recent", "Недавние файлы"),
        (Str.FileClearRecent, "Clear Recent Files", "Очистить список"),
        (Str.FileNoRecent, "No recent files", "Нет недавних файлов"),
        (Str.FileSave, "Save", "Сохранить"),
        (Str.FileSaveAs, "Save As…", "Сохранить как…"),
        (Str.FileImport, "Import", "Импорт"),
        (Str.FileImportProject, "Candela Project…", "Проект Candela…"),
        (Str.FileImportModel, "3D Model…", "3D-модель…"),
        (Str.FileImportMvr, "MVR Scene…", "Сцена MVR…"),
        (Str.FileImportImage, "Image…", "Изображение…"),
        (Str.FileExport, "Export", "Экспорт"),
        (Str.FileExportMvr, "MVR Scene…", "Сцена MVR…"),
        (Str.FileRender, "Render…", "Рендер…"),
        (Str.FileSettings, "Settings…", "Настройки…"),
        (Str.FileExit, "Exit", "Выход"),

        (Str.EditUndo, "Undo", "Отменить"),
        (Str.EditRedo, "Redo", "Повторить"),
        (Str.EditCut, "Cut", "Вырезать"),
        (Str.EditCopy, "Copy", "Копировать"),
        (Str.EditPaste, "Paste", "Вставить"),
        (Str.EditDuplicate, "Duplicate", "Дублировать"),
        (Str.EditDelete, "Delete", "Удалить"),
        (Str.EditSelectAll, "Select All", "Выделить всё"),
        (Str.EditDeselect, "Deselect", "Снять выделение"),

        (Str.ViewShading, "Shading", "Отображение"),
        (Str.ViewProjection, "Projection", "Проекция"),
        (Str.ViewCameraViews, "Camera Views", "Виды камеры"),
        (Str.ViewOverlay, "Overlay", "Оверлей"),
        (Str.ViewShowGrid, "Grid", "Сетка"),
        (Str.ViewShowStats, "Frame Statistics", "Статистика кадра"),
        (Str.ViewShowViewCube, "View Cube", "Куб вида"),
        (Str.ViewFrameSelection, "Frame Selection", "Показать выделенное"),
        (Str.ViewNewViewport, "New Viewport", "Новый вьюпорт"),
        (Str.ViewResetLayout, "Reset Layout", "Сбросить раскладку"),

        (Str.ToolSelect, "Select", "Выделение"),
        (Str.ToolMove, "Move", "Перемещение"),
        (Str.ToolRotate, "Rotate", "Поворот"),
        (Str.ToolWall, "Wall", "Стена"),
        (Str.ToolFloor, "Floor", "Пол"),
        (Str.ToolSnapping, "Snapping", "Привязка"),
        (Str.ToolSpaceLocal, "Local Axes", "Локальные оси"),
        (Str.ToolSpaceWorld, "World Axes", "Мировые оси"),

        (Str.WindowPanels, "Panels", "Панели"),
        (Str.WindowLayouts, "Layouts", "Раскладки"),
        (Str.WindowSaveLayoutAs, "Save Layout As…", "Сохранить раскладку как…"),
        (Str.WindowDeleteLayout, "Delete Layout", "Удалить раскладку"),
        (Str.WindowLayoutDefault, "Default", "По умолчанию"),
        (Str.WindowLayoutFourViews, "Four Viewports", "Четыре вьюпорта"),

        (Str.HelpShortcuts, "Keyboard Shortcuts", "Сочетания клавиш"),
        (Str.HelpDocs, "Open Documentation Folder", "Открыть папку документации"),
        (Str.HelpAbout, "About Candela", "О программе Candela"),

        (Str.PanelViewport, "Viewport", "Вьюпорт"),
        (Str.PanelLibrary, "Library", "Библиотека"),
        (Str.PanelProperties, "Properties", "Свойства"),
        (Str.PanelIssues, "Issues", "Проблемы"),
        (Str.PanelConsole, "Console", "Консоль"),
        (Str.PanelRender, "Render", "Рендер"),
        (Str.PanelPreview, "Preview", "Превью"),
        (Str.PanelSettings, "Settings", "Настройки"),

        (Str.TabStyle, "Tab style", "Стиль вкладок"),
        (Str.TabStyleTop, "Tabs on top: icon + name", "Вкладки сверху: значок и название"),
        (Str.TabStyleLeft, "Icon column on the left", "Значки слева"),
        (Str.TabStyleHint, "Applies to this panel group; remembered in the layout.", "Для этой группы панелей; сохраняется в раскладке."),
        (Str.DockPanelsInGroup, "Panels in this group", "Панели в группе"),
        (Str.DockClosePanel, "Close Panel", "Закрыть панель"),
        (Str.DockUndockPanel, "Undock Panel", "Открепить панель"),

        (Str.ShadingWireframe, "Wireframe", "Каркас"),
        (Str.ShadingColorless, "Colorless", "Без цвета"),
        (Str.ShadingFull, "Full", "Полный"),
        (Str.ProjectionPerspective, "Perspective", "Перспектива"),
        (Str.ProjectionOrthographic, "Orthographic", "Ортография"),
        (Str.OverlayNone, "No Overlay", "Без оверлея"),
        (Str.OverlayLux, "Illuminance (lux)", "Освещённость (лк)"),
        (Str.OverlayNoise, "Noise (dB)", "Шум (дБ)"),
        (Str.OverlayCables, "Cables", "Кабели"),
        (Str.ViewTop, "Top", "Сверху"),
        (Str.ViewBottom, "Bottom", "Снизу"),
        (Str.ViewFront, "Front", "Спереди"),
        (Str.ViewBack, "Back", "Сзади"),
        (Str.ViewLeft, "Left", "Слева"),
        (Str.ViewRight, "Right", "Справа"),
        (Str.ViewPerspectiveShort, "Persp", "Персп."),
        (Str.ViewOrthoShort, "Ortho", "Орто"),
        (Str.ViewportGrid, "Grid", "Сетка"),
        (Str.ViewportStats, "Statistics", "Статистика"),
        (Str.ViewportFps, "{0:0} fps", "{0:0} к/с"),
        (Str.ViewportFrameTime, "{0:0.0} ms", "{0:0.0} мс"),
        (Str.ViewportNoRenderer, "The viewport cannot render (GPU unavailable).", "Вьюпорт не может отрисовать сцену (GPU недоступен)."),
        (Str.ViewportPreviewNote, "Preview stage (renderer test scene)", "Демо-сцена (тестовая сцена рендерера)"),

        (Str.PropNothingSelected, "Nothing selected. Click an object in a viewport.", "Ничего не выделено. Щёлкните объект во вьюпорте."),
        (Str.PropSelectionCount, "{0} objects selected", "Выделено объектов: {0}"),
        (Str.PropName, "Name", "Имя"),
        (Str.PropTransform, "Transform", "Трансформация"),
        (Str.PropPosition, "Position", "Положение"),
        (Str.PropRotation, "Rotation", "Поворот"),
        (Str.PropScale, "Scale", "Масштаб"),
        (Str.PropComponents, "Components", "Компоненты"),
        (Str.PropNoComponents, "No components", "Нет компонентов"),
        (Str.PropVisible, "Visible", "Видимый"),
        (Str.PropLocked, "Locked", "Заблокирован"),
        (Str.PropMaterial, "Material", "Материал"),
        (Str.PropId, "Id", "Идентификатор"),
        (Str.PropLayer, "Layer", "Слой"),

        (Str.LibFixtures, "Fixtures", "Приборы"),
        (Str.LibModels, "Models", "Модели"),
        (Str.LibSearch, "Search fixtures…", "Поиск приборов…"),
        (Str.LibAll, "All", "Все"),
        (Str.LibEmpty, "No fixtures in this category.", "В этой категории нет приборов."),
        (Str.LibWaitingForEngine, "The fixture library is loaded by the stage engine, which arrives in the next build phase.",
            "Библиотеку приборов загружает движок сцены — он появится на следующем этапе."),

        (Str.IssuesSearch, "Search issues…", "Поиск проблем…"),
        (Str.IssuesAllCategories, "All categories", "Все категории"),
        (Str.IssuesNone, "No issues — everything looks fine.", "Проблем нет — всё в порядке."),
        (Str.IssueSeverityError, "Errors", "Ошибки"),
        (Str.IssueSeverityWarning, "Warnings", "Предупреждения"),
        (Str.IssueSeverityNote, "Notes", "Примечания"),
        (Str.IssuesCount, "{0} errors, {1} warnings, {2} notes", "Ошибок: {0}, предупреждений: {1}, примечаний: {2}"),
        (Str.IssuesClearMessages, "Clear program messages", "Очистить сообщения программы"),

        (Str.ConsoleClear, "Clear", "Очистить"),
        (Str.ConsoleFilter, "Filter…", "Фильтр…"),
        (Str.ConsoleAutoScroll, "Auto-scroll", "Автопрокрутка"),

        (Str.RenderOutput, "Output file", "Файл результата"),
        (Str.RenderResolution, "Resolution", "Разрешение"),
        (Str.RenderSamples, "Samples per pixel", "Сэмплов на пиксель"),
        (Str.RenderBounces, "Light bounces", "Отражений света"),
        (Str.RenderFormat, "Format", "Формат"),
        (Str.RenderStart, "Render", "Сделать рендер"),
        (Str.RenderNotAvailable, "The path tracer arrives in a later milestone; final renders are not available yet.",
            "Трассировщик путей появится в следующих этапах; финальный рендер пока недоступен."),
        (Str.RenderFrame, "Single frame", "Один кадр"),
        (Str.RenderRange, "Timecode range", "Диапазон таймкода"),
        (Str.RenderRangeStart, "From (s)", "С (с)"),
        (Str.RenderRangeEnd, "To (s)", "По (с)"),
        (Str.RenderFrameRate, "Frame rate", "Частота кадров"),
        (Str.PreviewQuality, "Real-time quality", "Качество реального времени"),
        (Str.PreviewShadows, "Shadows", "Тени"),
        (Str.PreviewVolumetrics, "Volumetric light", "Объёмный свет"),
        (Str.PreviewIndirect, "Indirect light", "Отражённый свет"),
        (Str.PreviewBloom, "Bloom", "Свечение"),
        (Str.PreviewToneMapper, "Tone mapper", "Тональная компрессия"),
        (Str.PreviewExposure, "Exposure compensation (EV)", "Поправка экспозиции (EV)"),
        (Str.PreviewAutoExposure, "Automatic exposure", "Автоэкспозиция"),
        (Str.PreviewResolutionScale, "Resolution scale", "Масштаб разрешения"),

        (Str.SettingsInterface, "Interface", "Интерфейс"),
        (Str.SettingsUiScale, "Interface scale", "Масштаб интерфейса"),
        (Str.SettingsFontSize, "Font size", "Размер шрифта"),
        (Str.SettingsLanguage, "Language", "Язык"),
        (Str.SettingsTheme, "Theme", "Тема"),
        (Str.SettingsThemeDark, "Dark", "Тёмная"),
        (Str.SettingsKeyboard, "Keyboard", "Клавиатура"),
        (Str.SettingsPreset, "Preset", "Пресет"),
        (Str.SettingsSaveAs, "Save as…", "Сохранить как…"),
        (Str.SettingsDeletePreset, "Delete preset", "Удалить пресет"),
        (Str.SettingsResetDefaults, "Reset to default", "Сбросить по умолчанию"),
        (Str.SettingsAction, "Action", "Действие"),
        (Str.SettingsShortcut, "Shortcut", "Сочетание"),
        (Str.SettingsPressKey, "Press a key… (Esc cancels)", "Нажмите клавишу… (Esc — отмена)"),
        (Str.SettingsConflict, "Also used by: {0}", "Также используется: {0}"),
        (Str.SettingsAddShortcut, "Add shortcut", "Добавить сочетание"),
        (Str.SettingsRemoveShortcut, "Remove shortcut", "Удалить сочетание"),
        (Str.SettingsViewport, "Viewport", "Вьюпорт"),
        (Str.SettingsNavigation, "Navigation style", "Стиль навигации"),
        (Str.SettingsZoomSpeed, "Zoom speed", "Скорость масштабирования"),
        (Str.SettingsUnits, "Display units", "Единицы отображения"),
        (Str.SettingsUnitsMetric, "Metric (m)", "Метрические (м)"),
        (Str.SettingsUnitsImperial, "Imperial (ft)", "Имперские (фут)"),
        (Str.SettingsLibrary, "Library", "Библиотека"),
        (Str.SettingsLibraryFolders, "Fixture library folders", "Папки библиотеки приборов"),
        (Str.SettingsAddFolder, "Add folder…", "Добавить папку…"),
        (Str.SettingsNetwork, "Network", "Сеть"),
        (Str.SettingsNetworkInterface, "Network interface for protocols", "Сетевой интерфейс для протоколов"),
        (Str.SettingsAllInterfaces, "All interfaces", "Все интерфейсы"),
        (Str.SettingsArtNetOffset, "Art-Net universe offset", "Смещение юниверсов Art-Net"),
        (Str.SettingsFiles, "Files", "Файлы"),
        (Str.SettingsAutosave, "Autosave interval (minutes)", "Автосохранение (минуты)"),
        (Str.SettingsAutosaveOff, "0 = off", "0 = выключено"),
        (Str.SettingsMaxRecent, "Recent files to remember", "Число недавних файлов"),
        (Str.SettingsGpu, "GPU", "Видеокарта"),
        (Str.SettingsGpuPreferred, "Preferred GPU", "Предпочитаемая видеокарта"),
        (Str.SettingsGpuAutomatic, "Automatic (most capable)", "Автоматически (самая мощная)"),
        (Str.SettingsGpuValidation, "Vulkan validation (debugging, slower)", "Проверка Vulkan (отладка, медленнее)"),
        (Str.SettingsRestartNote, "Takes effect after restarting the editor.", "Вступит в силу после перезапуска."),
        (Str.SettingsCurrentDevice, "In use: {0}", "Используется: {0}"),
        (Str.SettingsPresetNameHint, "Preset name", "Название пресета"),

        (Str.BrowserOpenTitle, "Open", "Открыть"),
        (Str.BrowserSaveTitle, "Save As", "Сохранить как"),
        (Str.BrowserImportTitle, "Import", "Импорт"),
        (Str.BrowserExportTitle, "Export", "Экспорт"),
        (Str.BrowserFolderTitle, "Select Folder", "Выбор папки"),
        (Str.BrowserPlaces, "Places", "Места"),
        (Str.BrowserRecentPlaces, "Recent", "Недавние"),
        (Str.BrowserHome, "Home", "Домашняя папка"),
        (Str.BrowserDocuments, "Documents", "Документы"),
        (Str.BrowserDesktop, "Desktop", "Рабочий стол"),
        (Str.BrowserWorkingFolder, "Working folder", "Рабочая папка"),
        (Str.BrowserName, "Name", "Имя"),
        (Str.BrowserSize, "Size", "Размер"),
        (Str.BrowserModified, "Modified", "Изменён"),
        (Str.BrowserFileName, "File name", "Имя файла"),
        (Str.BrowserFileType, "File type", "Тип файла"),
        (Str.BrowserOpen, "Open", "Открыть"),
        (Str.BrowserSave, "Save", "Сохранить"),
        (Str.BrowserSelectFolder, "Select Folder", "Выбрать папку"),
        (Str.BrowserCancel, "Cancel", "Отмена"),
        (Str.BrowserUp, "Parent folder", "Папка выше"),
        (Str.BrowserNewFolder, "New folder", "Новая папка"),
        (Str.BrowserShowHidden, "Show hidden files", "Показывать скрытые файлы"),
        (Str.BrowserOverwrite, "Replace File", "Заменить файл"),
        (Str.BrowserOverwriteQuestion, "'{0}' already exists. Replace it?", "Файл «{0}» уже существует. Заменить его?"),
        (Str.BrowserCannotRead, "Cannot read this folder: {0}", "Не удаётся прочитать папку: {0}"),
        (Str.BrowserAllFiles, "All files", "Все файлы"),
        (Str.BrowserCandelaProjects, "Candela projects", "Проекты Candela"),
        (Str.BrowserScenes, "Scenes (Candela, MVR)", "Сцены (Candela, MVR)"),
        (Str.BrowserModels, "3D models", "3D-модели"),
        (Str.BrowserImages, "Images", "Изображения"),
        (Str.BrowserMvr, "MVR scenes", "Сцены MVR"),

        (Str.MsgNotAvailableTitle, "Not Available Yet", "Пока недоступно"),
        (Str.MsgEngineMissing, "{0} needs the stage engine, which is not part of this build yet. Nothing was changed.",
            "Для операции «{0}» нужен движок сцены, которого ещё нет в этой сборке. Ничего не изменено."),
        (Str.MsgOk, "OK", "ОК"),
        (Str.MsgCancel, "Cancel", "Отмена"),
        (Str.MsgYes, "Yes", "Да"),
        (Str.MsgNo, "No", "Нет"),
        (Str.MsgError, "Error", "Ошибка"),
        (Str.MsgUnhandled, "An unexpected error occurred in {0}: {1}. The editor keeps running; details are in the console.",
            "Непредвиденная ошибка в «{0}»: {1}. Редактор продолжает работу; подробности в консоли."),
        (Str.MsgPanelFailed, "This panel stopped after an error: {0}", "Панель остановлена после ошибки: {0}"),
        (Str.MsgPanelRetry, "Retry", "Повторить"),
        (Str.MsgLayoutSaved, "Layout '{0}' saved.", "Раскладка «{0}» сохранена."),
        (Str.MsgLayoutLoadFailed, "Layout '{0}' could not be loaded: {1}", "Не удалось загрузить раскладку «{0}»: {1}"),
        (Str.MsgDeleteLayoutQuestion, "Delete the layout '{0}'?", "Удалить раскладку «{0}»?"),
        (Str.MsgLayoutNameHint, "Layout name", "Название раскладки"),
        (Str.MsgAboutText, "Physically based stage-lighting visualisation: GDTF fixtures, MVR scenes, volumetric light in haze, photometry in candela and lux.",
            "Физически корректная визуализация сценического света: приборы GDTF, сцены MVR, объёмный свет в дыму, фотометрия в канделах и люксах."),
        (Str.MsgAboutVersion, "Version {0}", "Версия {0}"),
        (Str.MsgNoVulkanTitle, "No Vulkan graphics device", "Нет графического устройства Vulkan"),
        (Str.MsgNoVulkanText, "Candela needs a GPU with a Vulkan 1.3 driver. Install or update the graphics driver (AMD, NVIDIA, Intel), or on Linux without a GPU install Mesa's software Vulkan driver (mesa-vulkan-drivers, lavapipe).",
            "Для работы Candela нужна видеокарта с драйвером Vulkan 1.3. Установите или обновите графический драйвер (AMD, NVIDIA, Intel), а в Linux без видеокарты — программный драйвер Mesa (mesa-vulkan-drivers, lavapipe)."),
        (Str.MsgNoVulkanReason, "Reason: {0}", "Причина: {0}"),
        (Str.MsgClose, "Close", "Закрыть"),
        (Str.MsgShortcutsTitle, "Keyboard Shortcuts", "Сочетания клавиш"),
        (Str.MsgDocsOpened, "Documentation: {0}", "Документация: {0}"),

        (Str.StatusReady, "Ready", "Готово"),
        (Str.StatusSelected, "{0} selected", "Выделено: {0}"),
        (Str.StatusUndo, "Undo: {0}", "Отмена: {0}"),
    };
}
