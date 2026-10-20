using System.Globalization;

namespace Candela.App.Ui;

/// <summary>
/// Keys of the user-visible strings of an installed editor's first experience: the fixture library setup window shown
/// at the first start, the GDTF files copied into the library folder, and the messages the operating system shows when
/// the editor cannot start (no Vulkan driver) or stopped because of an error (crash report). The texts are in
/// <see cref="InstallLoc"/> (English and Russian) and follow <see cref="Loc.Language"/>.
/// </summary>
internal enum InstallStr
{
    // Library setup window
    SetupTitle,
    SetupWelcome,
    SetupIntro,
    SetupWhere,
    SetupFolderLabel,
    SetupNoFolder,
    SetupChoose,
    SetupUseDefault,
    SetupOpenFolder,
    SetupRescan,
    SetupRescanTip,
    SetupCounting,
    SetupFound,
    SetupFoundNone,
    SetupFoundSubfolders,
    SetupMissing,
    SetupHowToGet,
    SetupWithout,
    SetupShowAtStart,
    SetupOpenSample,
    SetupStart,
    SetupContinueWithout,
    SetupAdded,
    SetupFolderFailed,
    SetupMenu,
    SetupBrowserTitle,

    // GDTF import into the library folder
    ImportCopied,
    ImportCopyFailed,

    // Settings → Library
    SettingsAppliedNow,

    // Native message boxes: no Vulkan
    NoVulkanTitle,
    NoVulkanIntro,
    NoVulkanLoaderMissing,
    NoVulkanDevice,
    NoVulkanDrivers,
    NoVulkanVendorNvidia,
    NoVulkanVendorAmd,
    NoVulkanVendorIntel,
    NoVulkanVendorUnknown,
    NoVulkanLog,
    NoVulkanOpenPage,

    // Native message box: the Microsoft Visual C++ runtime is missing
    VcRuntimeTitle,
    VcRuntimeText,
    VcRuntimeOpenPage,

    // Native message boxes: crash report
    CrashTitle,
    CrashText,
    CrashOpenFolder,
    StartFailedTitle,
    StartFailedText,
}

/// <summary>English and Russian texts of <see cref="InstallStr"/>.</summary>
internal static class InstallLoc
{
    private static readonly string[] English;
    private static readonly string[] Russian;

    private static readonly (InstallStr Key, string En, string Ru)[] Table =
    {
        (InstallStr.SetupTitle, "Fixture Library", "Библиотека приборов"),
        (InstallStr.SetupWelcome, "Welcome to Candela", "Добро пожаловать в Candela"),
        (InstallStr.SetupIntro,
            "Candela draws fixtures from their GDTF files (.gdtf): the manufacturer's description of the fixture with its 3D model, DMX modes, beam, colours and gobos. The fixture library is a folder on your computer that holds these files; the Library panel lists every fixture in it, and scenes use them by name.",
            "Candela строит приборы по их файлам GDTF (.gdtf): это описание прибора от производителя — 3D-модель, DMX-режимы, луч, цвета и гобо. Библиотека приборов — это папка на вашем компьютере с такими файлами; панель «Библиотека» показывает все приборы из неё, а сцены находят их по имени."),
        (InstallStr.SetupWhere,
            "Choose the folder with your .gdtf files, or create the default folder and copy files into it later. You can change the folders at any time in File → Settings → Library.",
            "Выберите папку с вашими файлами .gdtf или создайте папку по умолчанию и скопируйте файлы в неё позже. Папки можно изменить в любой момент: Файл → Настройки → Библиотека."),
        (InstallStr.SetupFolderLabel, "Library folder", "Папка библиотеки"),
        (InstallStr.SetupNoFolder, "No folder chosen yet.", "Папка ещё не выбрана."),
        (InstallStr.SetupChoose, "Choose Folder…", "Выбрать папку…"),
        (InstallStr.SetupUseDefault, "Use Documents\\Candela\\Fixture Library", "Использовать Документы\\Candela\\Fixture Library"),
        (InstallStr.SetupOpenFolder, "Show in File Manager", "Показать в проводнике"),
        (InstallStr.SetupRescan, "Rescan", "Обновить"),
        (InstallStr.SetupRescanTip, "Read the folder again: GDTF files copied into it since are added to the library.",
            "Прочитать папку снова: файлы GDTF, скопированные в неё после выбора, добавятся в библиотеку."),
        (InstallStr.SetupCounting, "Looking for fixture files…", "Поиск файлов приборов…"),
        (InstallStr.SetupFound, "{0} fixture files (.gdtf) found — they are in the Library panel now.",
            "Найдено файлов приборов (.gdtf): {0} — они уже в панели «Библиотека»."),
        (InstallStr.SetupFoundNone,
            "This folder has no .gdtf files yet. Copy your GDTF files into it (Show in File Manager), then click Rescan — or use File → Import → GDTF Fixture.",
            "В этой папке пока нет файлов .gdtf. Скопируйте в неё ваши файлы GDTF («Показать в проводнике»), затем нажмите «Обновить» — или используйте Файл → Импорт → Прибор GDTF."),
        (InstallStr.SetupFoundSubfolders,
            "{0} more .gdtf files are in subfolders. The library reads only the files directly in the folder: choose the subfolder that holds them, or move them up.",
            "Ещё файлов .gdtf во вложенных папках: {0}. Библиотека читает только файлы, лежащие прямо в папке: выберите вложенную папку с ними или перенесите их выше."),
        (InstallStr.SetupMissing, "The folder does not exist.", "Папка не существует."),
        (InstallStr.SetupHowToGet,
            "GDTF files are published by the manufacturers on gdtf-share.com and on their own websites.",
            "Файлы GDTF публикуют производители — на gdtf-share.com и на своих сайтах."),
        (InstallStr.SetupWithout,
            "You can also continue without a library: MVR scenes that contain their GDTF files, and the sample scene, still open with their fixtures.",
            "Можно продолжить и без библиотеки: сцены MVR, содержащие свои файлы GDTF, и пример сцены всё равно откроются с приборами."),
        (InstallStr.SetupShowAtStart, "Show this window at start-up while the library has no fixtures",
            "Показывать это окно при запуске, пока в библиотеке нет приборов"),
        (InstallStr.SetupOpenSample, "Open Sample Scene", "Открыть пример сцены"),
        (InstallStr.SetupStart, "Start", "Начать"),
        (InstallStr.SetupContinueWithout, "Continue Without a Library", "Продолжить без библиотеки"),
        (InstallStr.SetupAdded, "Fixture library: {0}", "Библиотека приборов: {0}"),
        (InstallStr.SetupFolderFailed, "The folder '{0}' cannot be used: {1}", "Папку «{0}» нельзя использовать: {1}"),
        (InstallStr.SetupMenu, "Fixture Library Setup…", "Настройка библиотеки приборов…"),
        (InstallStr.SetupBrowserTitle, "Fixture Library Folder", "Папка библиотеки приборов"),

        (InstallStr.ImportCopied, "{0} was copied into the fixture library folder '{1}'.",
            "{0} скопирован в папку библиотеки приборов «{1}»."),
        (InstallStr.ImportCopyFailed,
            "{0} could not be copied into the fixture library folder ({1}); it is used from where it is for this session.",
            "{0} не удалось скопировать в папку библиотеки приборов ({1}); в этом сеансе он используется с исходного места."),

        (InstallStr.SettingsAppliedNow, "A folder that is added is read at once; removing one takes effect after restarting the editor.",
            "Добавленная папка читается сразу; удаление папки вступит в силу после перезапуска."),

        (InstallStr.NoVulkanTitle, "Candela cannot start", "Candela не может запуститься"),
        (InstallStr.NoVulkanIntro, "Candela needs a graphics card with a Vulkan 1.3 driver.", "Для Candela нужна видеокарта с драйвером Vulkan 1.3."),
        (InstallStr.NoVulkanLoaderMissing,
            "The Vulkan runtime (vulkan-1.dll) was not found: the graphics driver is missing, too old, or the generic Windows display driver is in use.",
            "Не найдена библиотека Vulkan (vulkan-1.dll): драйвер видеокарты не установлен, устарел или используется стандартный видеодрайвер Windows."),
        (InstallStr.NoVulkanDevice, "No graphics card with Vulkan 1.3 was found ({0}).", "Не найдена видеокарта с поддержкой Vulkan 1.3 ({0})."),
        (InstallStr.NoVulkanDrivers,
            "Please install the latest driver from the manufacturer of your graphics card and start Candela again:\n  NVIDIA: https://www.nvidia.com/Download/index.aspx\n  AMD: https://www.amd.com/en/support/download/drivers.html\n  Intel: https://www.intel.com/content/www/us/en/support/detect.html",
            "Установите последнюю версию драйвера с сайта производителя видеокарты и запустите Candela снова:\n  NVIDIA: https://www.nvidia.com/Download/index.aspx\n  AMD: https://www.amd.com/ru/support/download/drivers.html\n  Intel: https://www.intel.com/content/www/us/en/support/detect.html"),
        (InstallStr.NoVulkanVendorNvidia, "Your graphics card: {0} (NVIDIA).", "Ваша видеокарта: {0} (NVIDIA)."),
        (InstallStr.NoVulkanVendorAmd, "Your graphics card: {0} (AMD).", "Ваша видеокарта: {0} (AMD)."),
        (InstallStr.NoVulkanVendorIntel, "Your graphics card: {0} (Intel).", "Ваша видеокарта: {0} (Intel)."),
        (InstallStr.NoVulkanVendorUnknown, "Your graphics adapter: {0}.", "Ваш видеоадаптер: {0}."),
        (InstallStr.NoVulkanLog, "Details are in the log: {0}", "Подробности — в журнале: {0}"),
        (InstallStr.NoVulkanOpenPage, "Open the driver download page now?", "Открыть страницу загрузки драйвера?"),

        (InstallStr.VcRuntimeTitle, "Candela cannot start", "Candela не может запуститься"),
        (InstallStr.VcRuntimeText,
            "The Microsoft Visual C++ 2015-2022 runtime (x64) is missing ({0}). Candela's user interface libraries need it; Setup installs it, but here it was not installed (cancelled, no administrator rights, or a copied program folder).\n\nPlease install it from Microsoft:\n  {1}\nand start Candela again.\n\nDetails are in the log: {2}",
            "Не найдена среда выполнения Microsoft Visual C++ 2015-2022 (x64) ({0}). Она нужна библиотекам интерфейса Candela; программа установки ставит её, но здесь она не установлена (установка отменена, нет прав администратора или папка программы скопирована вручную).\n\nУстановите её с сайта Microsoft:\n  {1}\nи запустите Candela снова.\n\nПодробности — в журнале: {2}"),
        (InstallStr.VcRuntimeOpenPage, "Download it now?", "Скачать её сейчас?"),

        (InstallStr.CrashTitle, "Candela stopped because of an error", "Candela остановлена из-за ошибки"),
        (InstallStr.CrashText,
            "Sorry — Candela ran into an error it could not recover from. Unsaved work kept by the autosave is offered for recovery at the next start.\n\nA report was written to:\n{0}\n\nPlease attach this file when you report the problem.",
            "Извините — в Candela произошла ошибка, после которой работа невозможна. Несохранённые изменения, сохранённые автосохранением, будут предложены к восстановлению при следующем запуске.\n\nОтчёт об ошибке записан в файл:\n{0}\n\nПриложите этот файл, когда будете сообщать о проблеме."),
        (InstallStr.CrashOpenFolder, "Open the folder with the report?", "Открыть папку с отчётом?"),
        (InstallStr.StartFailedTitle, "Candela could not start", "Candela не удалось запустить"),
        (InstallStr.StartFailedText,
            "Candela could not start on this computer.\n\nA report was written to:\n{0}\n\nPlease attach this file when you report the problem.",
            "Candela не удалось запустить на этом компьютере.\n\nОтчёт записан в файл:\n{0}\n\nПриложите этот файл, когда будете сообщать о проблеме."),
    };

    static InstallLoc()
    {
        int n = Enum.GetValues<InstallStr>().Length;
        English = new string[n];
        Russian = new string[n];
        foreach (var (key, en, ru) in Table)
        {
            English[(int)key] = en;
            Russian[(int)key] = ru;
        }

        for (int i = 0; i < n; i++)
            English[i] ??= ((InstallStr)i).ToString();
    }

    /// <summary>The text of <paramref name="key"/> in the current UI language (English when untranslated).</summary>
    public static string Get(InstallStr key)
    {
        int i = (int)key;
        if ((uint)i >= (uint)English.Length)
            return key.ToString();
        return Loc.Language == "ru" && Russian[i] is { Length: > 0 } ru ? ru : English[i];
    }

    /// <summary>The English text of <paramref name="key"/> (scripted menu paths, logs).</summary>
    public static string English_(InstallStr key) => (uint)key < (uint)English.Length ? English[(int)key] : key.ToString();

    /// <summary>Formats the text of <paramref name="key"/> (culture-invariant numbers).</summary>
    public static string Format(InstallStr key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, Get(key), args);

    /// <summary>"icon  text" for buttons and menu items, built once per language (no allocation per frame).</summary>
    public static string With(string icon, InstallStr key)
    {
        if (!string.Equals(s_labelsLanguage, Loc.Language, StringComparison.Ordinal))
        {
            s_labels.Clear();
            s_labelsLanguage = Loc.Language;
        }

        if (!s_labels.TryGetValue((icon, key), out var label))
        {
            label = icon.Length > 0 ? icon + "  " + Get(key) : Get(key);
            s_labels[(icon, key)] = label;
        }

        return label;
    }

    private static readonly Dictionary<(string Icon, InstallStr Key), string> s_labels = new();
    private static string s_labelsLanguage = "";

    /// <summary>All entries (tests check completeness and placeholders).</summary>
    internal static IEnumerable<(InstallStr Key, string En, string Ru)> Entries => Table;
}
