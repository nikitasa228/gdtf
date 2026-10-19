using System.Globalization;
using Candela.Electrical;

namespace Candela.App.Ui;

/// <summary>
/// Keys of the user-visible strings of cabling (requirement "Кабели"): the Devices section of the library, the cable
/// tool and its port picker, cable nodes, the cable link window, consoles and power sources in the properties panel,
/// and the duplicate re-patch option. The texts are in <see cref="CableLoc"/> (English and Russian) and follow
/// <see cref="Loc.Language"/>.
/// </summary>
internal enum CableStr
{
    // Library: devices
    LibDevices,
    GroupConsoles,
    GroupData,
    GroupMains,
    GroupDistros,
    GroupDimmers,
    DevicesCount,
    DevicesNoMatches,
    DevicesFromLibrary,
    DevicesPorts,
    DevicesDragHint,
    DevicePlaced,
    DeviceUnresolved,

    // Entity names
    NameConsole,
    NameMains,
    NameDistro,
    NameNode,
    NameSplitter,
    NameDimmer,
    NameCableDmx,
    NameCableEthernet,
    NameCablePower,
    NameCableMulticore,

    // Cable tool
    ToolCableType,
    CableGroupData,
    CableGroupPower,
    CableGroupMulticore,
    ToolCableHint,
    ToolCableDrawingHint,
    ToolCableLength,
    ToolCableTarget,
    ToolCableBranchHere,
    ToolCableContinueHere,
    ToolCableJoinHere,
    ToolCableNoPort,
    ToolCableAdded,
    ToolCableBranched,
    ToolCableConnected,
    ToolCableNeedsEngine,
    ToolCableTooShort,
    ToolCableOwnBranch,

    // Port picker
    PickerTitle,
    PickerHint,
    PickerConnected,
    PickerAdapter,
    PickerIncompatible,
    PickerNoFreePort,
    PickerInput,
    PickerOutput,
    PickerBidirectional,
    PickerOutputToOutput,
    PickerInputToInput,

    // Cable nodes
    NodeHint,
    NodeSelected,
    NodeMoved,
    NodeDeleted,
    NodePosition,
    NodeAnchor,
    NodeAnchorFree,
    NodePlugged,

    // Cable link window
    PanelCableLinks,
    LinksEmpty,
    LinksSource,
    LinksUnfed,
    LinksDevices,
    LinksClickHint,
    LinksCableIndex,
    LinksFreeEnds,
    LinksInPort,
    LinksSourceData,
    LinksSourcePower,
    LinksCableGone,
    LinksOverlay,

    // Properties: cable, console, power source, data/power rows
    PropsCable,
    PropsCableType,
    PropsCableLabel,
    PropsCableLength,
    PropsCableWeight,
    PropsCableSlack,
    PropsCableEnds,
    PropsCableShowLinks,
    PropsConsole,
    PropsConsoleModel,
    PropsConsoleProtocol,
    PropsConsoleProtocolNone,
    PropsConsoleOutputs,
    PropsConsoleAddOutput,
    PropsConsoleRemoveOutput,
    PropsConsoleInputOn,
    PropsConsoleInputOff,
    PropsConsoleEnableInput,
    PropsConsoleDriven,
    PropsPowerSource,
    PropsPowerVoltage,
    PropsPowerFrequency,
    PropsPowerPhases,
    PropsPowerOutlets,
    PropsPowerAddOutlet,
    PropsPowerRemoveOutlet,
    PropsPowerBreaker,
    PropsPowerPhase,
    PropsPowerConnector,
    PropsPowerLoad,
    PropsPowerTotal,
    PropsPowerUnfed,
    PatchData,
    PatchDataNone,
    PatchDataLine,
    PatchPower,
    PatchPowerNone,
    PatchPowerLine,
    LockConsoleCable,

    // Legend
    LegendInScene,
    LegendOthers,

    // Duplicate re-patch
    SettingsEditing,
    DuplicatePatch,
    DuplicatePatchKeep,
    DuplicatePatchNext,
    DuplicatePatchAsk,
    DuplicatePatchHint,
    DuplicateQuestionTitle,
    DuplicateQuestion,
    DuplicateRepatch,
    DuplicateKeep,
    DuplicateRepatched,
    DuplicateRemember,
}

/// <summary>English and Russian texts of <see cref="CableStr"/>, of the cable types and of the device presets.</summary>
internal static class CableLoc
{
    private static readonly string[] English;
    private static readonly string[] Russian;
    private static readonly Dictionary<(string Icon, CableStr Key), string> s_labels = new();
    private static string s_labelsLanguage = "";

    /// <summary>Russian names of the cable types of <see cref="CableCatalog.Default"/>, by id.</summary>
    private static readonly Dictionary<string, string> RussianCables = new(StringComparer.OrdinalIgnoreCase)
    {
        [CableCatalog.Ids.DmxXlr3] = "DMX-кабель XLR 3-pin",
        [CableCatalog.Ids.DmxXlr5] = "DMX-кабель XLR 5-pin",
        [CableCatalog.Ids.Ethernet] = "Ethernet etherCON Cat6",
        [CableCatalog.Ids.Powercon] = "Удлинитель powerCON (серый → синий) 20 А",
        [CableCatalog.Ids.PowerconTrue1] = "powerCON TRUE1 16 А",
        [CableCatalog.Ids.PowerconTrue1Top] = "powerCON TRUE1 TOP 16 А",
        [CableCatalog.Ids.Schuko] = "Удлинитель Schuko 16 А",
        [CableCatalog.Ids.Iec] = "Сетевой шнур IEC (Schuko → C13) 10 А",
        [CableCatalog.Ids.Cee16] = "CEE 16 А однофазный",
        [CableCatalog.Ids.Cee32] = "CEE 32 А однофазный",
        [CableCatalog.Ids.Cee16ThreePhase] = "CEE 16 А трёхфазный",
        [CableCatalog.Ids.Cee32ThreePhase] = "CEE 32 А трёхфазный",
        [CableCatalog.Ids.Cee63ThreePhase] = "CEE 63 А трёхфазный",
        [CableCatalog.Ids.Cee125ThreePhase] = "CEE 125 А трёхфазный",
        [CableCatalog.Ids.Powerlock] = "Фидер Powerlock 400 А (5 × 120 мм²)",
        [CableCatalog.Ids.Socapex] = "Многожильный Socapex 19-pin (6 × 16 А)",
        [CableCatalog.Ids.Harting16] = "Многожильный Harting 16-pin (6 × 16 А)",
    };

    /// <summary>Russian names and descriptions of the built-in device presets, by id.</summary>
    private static readonly Dictionary<string, (string Name, string Description)> RussianDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["console-dmx-2"] = ("DMX-пульт, 2 юниверса",
            "Небольшой DMX-пульт с двумя выходами XLR 5-pin (юниверсы 1–2), управляемый по Art-Net из окна внешней связи."),
        ["console-dmx-4"] = ("DMX-пульт, 4 юниверса",
            "DMX-пульт с четырьмя выходами XLR 5-pin (юниверсы 1–4), управляемый по Art-Net из окна внешней связи."),
        ["console-dmx-8"] = ("DMX-пульт, 8 юниверсов",
            "Большой DMX-пульт с восемью выходами XLR 5-pin (юниверсы 1–8), управляемый по Art-Net из окна внешней связи."),
        ["console-artnet-16"] = ("Сетевой пульт (Art-Net)",
            "Пульт, порт etherCON которого выдаёт 16 юниверсов по Art-Net; узлы Art-Net за ним превращают их в линии DMX."),
        ["console-sacn-16"] = ("Сетевой пульт (sACN)",
            "Пульт, порт etherCON которого выдаёт 16 юниверсов по sACN (E1.31); узлы за ним превращают их в линии DMX."),
        ["dmx-splitter-6"] = ("DMX-сплиттер 1 → 6",
            "Рэковый (19\", 1U) оптоизолированный DMX-сплиттер/бустер: вход DMX с проходом и шесть развязанных выходов XLR 5-pin, каждый начинает новую линию DMX."),
        ["dmx-splitter-4-xlr3"] = ("DMX-сплиттер 1 → 4 (XLR 3-pin)",
            "Переносной DMX-сплиттер/оптобустер для фермы или пола: вход XLR 3-pin с проходом, четыре развязанных выхода XLR 3-pin, вход и проход powerCON TRUE1."),
        ["artnet-node-2"] = ("Узел Art-Net / sACN, 2 × DMX",
            "Преобразователь Ethernet → DMX: первые два юниверса со входа etherCON выходят на DMX A и DMX B (XLR 5-pin); второй etherCON передаёт сеть дальше."),
        ["artnet-node-8"] = ("Узел Art-Net / sACN, 8 × DMX",
            "Рэковый (19\", 1U) преобразователь Ethernet → DMX: первые восемь юниверсов со входа etherCON выходят на DMX A … H (XLR 5-pin)."),
        ["mains-schuko-16"] = ("Розетка Schuko 16 А", "Настенная розетка 230 В на автомате 16 А."),
        ["mains-cee16"] = ("Сеть CEE 16 А (230 В)", "Синяя розетка CEE 16 А: 230 В, одна фаза, автомат 16 А."),
        ["mains-cee32"] = ("Сеть CEE 32 А (230 В)", "Синяя розетка CEE 32 А: 230 В, одна фаза, автомат 32 А."),
        ["mains-cee32-3p"] = ("Сеть CEE 32 А, три фазы", "Красная розетка CEE 32 А: 400 В, три фазы (230 В относительно нейтрали), автомат 32 А."),
        ["mains-cee63-3p"] = ("Сеть CEE 63 А, три фазы", "Красная розетка CEE 63 А: 400 В, три фазы, автомат 63 А."),
        ["mains-cee125-3p"] = ("Сеть CEE 125 А, три фазы", "Красная розетка CEE 125 А: 400 В, три фазы, автомат 125 А."),
        ["mains-powerlock-400"] = ("Ввод Powerlock 400 А", "Трёхфазный ввод 400 А или генератор на однополюсных разъёмах Powerlock."),
        ["distro-16-true1"] = ("Дистрибьютор 16 А → 4 × TRUE1",
            "Однофазный дистрибьютор на ферму: вход 16 А, четыре выхода powerCON TRUE1 на автоматах 10 А."),
        ["distro-32-schuko"] = ("Дистрибьютор 32 А 3~ → 6 × Schuko",
            "Трёхфазный дистрибьютор: вход CEE 32 А, шесть розеток Schuko на автоматах 16 А, разнесённых по L1–L3."),
        ["distro-32-true1"] = ("Дистрибьютор 32 А 3~ → 6 × TRUE1",
            "Трёхфазный дистрибьютор: вход CEE 32 А, шесть выходов powerCON TRUE1 на автоматах 16 А, разнесённых по L1–L3."),
        ["distro-63-socapex"] = ("Дистрибьютор 63 А 3~ → 2 × Socapex + 3 × CEE 16 А",
            "Трёхфазный дистрибьютор для диммерной: вход CEE 63 А, два выхода Socapex (по 6 цепей 16 А) и три розетки CEE 16 А."),
        ["distro-125"] = ("Дистрибьютор 125 А 3~ → 2 × CEE 63 А + 2 × CEE 32 А",
            "Главный дистрибьютор: вход CEE 125 А, два выхода CEE 63 А и два CEE 32 А (три фазы) для следующих дистрибьюторов."),
        ["distro-powerlock-400"] = ("Дистрибьютор 400 А → 2 × CEE 125 А + 2 × CEE 63 А",
            "Генераторный дистрибьютор: вход Powerlock 400 А, два выхода CEE 125 А и два CEE 63 А (три фазы)."),
        ["dimmer-4x10"] = ("Диммерный блок 4 × 10 А",
            "Четырёхканальный диммер с фазовой отсечкой на однофазном вводе CEE 32 А: четыре розетки Schuko по 10 А (2,3 кВт), всего 7,36 кВт, один канал DMX на выход."),
    };

    private static readonly (CableStr Key, string En, string Ru)[] Table =
    {
        (CableStr.LibDevices, "Devices", "Устройства"),
        (CableStr.GroupConsoles, "Consoles", "Пульты"),
        (CableStr.GroupData, "Nodes & splitters", "Узлы и сплиттеры"),
        (CableStr.GroupMains, "Mains", "Ввод питания"),
        (CableStr.GroupDistros, "Distros", "Дистрибьюторы"),
        (CableStr.GroupDimmers, "Dimmers & PSUs", "Диммеры и БП"),
        (CableStr.DevicesCount, "{0} devices", "Устройств: {0}"),
        (CableStr.DevicesNoMatches, "No device matches the search.", "Нет устройств по запросу."),
        (CableStr.DevicesFromLibrary, "From the fixture library", "Из библиотеки приборов"),
        (CableStr.DevicesPorts, "Connectors", "Разъёмы"),
        (CableStr.DevicesDragHint, "Drag into a viewport to place it, then connect it with the Cable tool (C).",
            "Перетащите во вьюпорт, затем подключите инструментом «Кабель» (C)."),
        (CableStr.DevicePlaced, "Placed {0}.", "Добавлено: {0}."),
        (CableStr.DeviceUnresolved, "The device's GDTF file is not in the fixture library.", "GDTF-файла устройства нет в библиотеке приборов."),

        (CableStr.NameConsole, "Console", "Пульт"),
        (CableStr.NameMains, "Mains", "Ввод"),
        (CableStr.NameDistro, "Distro", "Дистрибьютор"),
        (CableStr.NameNode, "Node", "Узел"),
        (CableStr.NameSplitter, "Splitter", "Сплиттер"),
        (CableStr.NameDimmer, "Dimmer", "Диммер"),
        (CableStr.NameCableDmx, "DMX", "DMX"),
        (CableStr.NameCableEthernet, "Ethernet", "Сеть"),
        (CableStr.NameCablePower, "Power", "Питание"),
        (CableStr.NameCableMulticore, "Multicore", "Многожильный"),

        (CableStr.ToolCableType, "Cable type", "Тип кабеля"),
        (CableStr.CableGroupData, "Data", "Данные"),
        (CableStr.CableGroupPower, "Power", "Питание"),
        (CableStr.CableGroupMulticore, "Multicore", "Многожильные"),
        (CableStr.ToolCableHint,
            "Cable ({0}): click a device to plug in, click walls, trusses or the floor to fix the cable there, click the middle of a cable to branch it. Right-click or Esc finishes.",
            "Кабель ({0}): щелчок по устройству — подключить, по стене, ферме или полу — закрепить кабель, по середине кабеля — разветвить. Правая кнопка или Esc — закончить."),
        (CableStr.ToolCableDrawingHint,
            "Click to add a point, a device to connect, a cable to join it. Backspace removes the last point; right-click or Esc finishes.",
            "Щелчок — точка, по устройству — подключение, по кабелю — соединение. Backspace убирает последнюю точку; правая кнопка или Esc — закончить."),
        (CableStr.ToolCableLength, "{0} m · {1} kg", "{0} м · {1} кг"),
        (CableStr.ToolCableTarget, "{0}: {1} free", "{0}: свободно {1}"),
        (CableStr.ToolCableBranchHere, "Branch from here", "Ответвление отсюда"),
        (CableStr.ToolCableContinueHere, "Continue this cable", "Продолжить этот кабель"),
        (CableStr.ToolCableJoinHere, "Join this cable here", "Присоединить к этому кабелю"),
        (CableStr.ToolCableNoPort, "'{0}' has no free port for a {1}: {2}", "У «{0}» нет свободного разъёма для кабеля «{1}»: {2}"),
        (CableStr.ToolCableAdded, "Cable '{0}' added: {1} m, {2} kg.", "Кабель «{0}» добавлен: {1} м, {2} кг."),
        (CableStr.ToolCableBranched, "Branch added to cable '{0}' ({1} m in total).", "К кабелю «{0}» добавлено ответвление (всего {1} м)."),
        (CableStr.ToolCableConnected, "'{0}' {1} connected.", "«{0}»: {1} подключён."),
        (CableStr.ToolCableNeedsEngine, "Cables need an open stage.", "Для кабелей нужна открытая сцена."),
        (CableStr.ToolCableTooShort, "The cable needs at least two points; nothing was added.", "Кабелю нужны хотя бы две точки; ничего не добавлено."),
        (CableStr.ToolCableOwnBranch, "A cable cannot join itself.", "Кабель нельзя соединить сам с собой."),

        (CableStr.PickerTitle, "Plug the {0} into '{1}'", "Подключить «{0}» к «{1}»"),
        (CableStr.PickerHint, "1–9 choose · Esc cancels", "1–9 — выбор · Esc — отмена"),
        (CableStr.PickerConnected, "in use: {0}", "занят: {0}"),
        (CableStr.PickerAdapter, "adapter: {0}", "нужен переходник: {0}"),
        (CableStr.PickerIncompatible, "does not fit: {0}", "не подходит: {0}"),
        (CableStr.PickerNoFreePort, "No free port for this cable.", "Свободного разъёма для этого кабеля нет."),
        (CableStr.PickerInput, "input", "вход"),
        (CableStr.PickerOutput, "output", "выход"),
        (CableStr.PickerBidirectional, "in/out", "вход/выход"),
        (CableStr.PickerOutputToOutput, "the cable's other end is in an output too", "другой конец кабеля тоже в выходе"),
        (CableStr.PickerInputToInput, "the cable's other end is in an input too", "другой конец кабеля тоже во входе"),

        (CableStr.NodeHint, "Cable nodes: click one to select it, drag it or use the Move gizmo; Delete removes it.",
            "Точки кабеля: щелчок выделяет точку, её можно тянуть мышью или гизмо перемещения; Delete удаляет её."),
        (CableStr.NodeSelected, "Point {0} of '{1}'", "Точка {0} кабеля «{1}»"),
        (CableStr.NodeMoved, "Move cable point", "Перемещение точки кабеля"),
        (CableStr.NodeDeleted, "Delete cable point", "Удаление точки кабеля"),
        (CableStr.NodePosition, "Position", "Положение"),
        (CableStr.NodeAnchor, "Fixed to", "Закреплена на"),
        (CableStr.NodeAnchorFree, "free (cable space)", "свободная (в пространстве кабеля)"),
        (CableStr.NodePlugged, "plugged into '{0}' {1}", "подключена к «{0}» {1}"),

        (CableStr.PanelCableLinks, "Cable Links", "Связи кабеля"),
        (CableStr.LinksEmpty,
            "Click a cable in a viewport, or the cable next to a connector in the Properties panel, to see the devices it links.",
            "Щёлкните по кабелю во вьюпорте или по кабелю у разъёма в окне свойств, чтобы увидеть, какие устройства он связывает."),
        (CableStr.LinksSource, "Source", "Источник"),
        (CableStr.LinksUnfed, "This cable is not connected to a console or a power source: the strip shows the devices plugged into it.",
            "Кабель не подключён к пульту или источнику питания: показаны устройства, подключённые к нему."),
        (CableStr.LinksDevices, "{0} devices on the line", "Устройств на линии: {0}"),
        (CableStr.LinksClickHint, "Click a device to select it and show its properties; click a cable to follow it.",
            "Щелчок по устройству выделяет его и показывает свойства; щелчок по кабелю переходит к нему."),
        (CableStr.LinksCableIndex, "{0} · cable {1}", "{0} · кабель {1}"),
        (CableStr.LinksFreeEnds, "{0} free end(s)", "Свободных концов: {0}"),
        (CableStr.LinksInPort, "in: {0}", "вход: {0}"),
        (CableStr.LinksSourceData, "'{0}' {1} · universe {2}", "«{0}» {1} · юниверс {2}"),
        (CableStr.LinksSourcePower, "'{0}' {1} · L{2} · {3} V", "«{0}» {1} · L{2} · {3} В"),
        (CableStr.LinksCableGone, "The cable is no longer in the scene.", "Кабеля больше нет в сцене."),
        (CableStr.LinksOverlay, "Show the cable overlay", "Показать оверлей кабелей"),

        (CableStr.PropsCable, "Cable", "Кабель"),
        (CableStr.PropsCableType, "Type", "Тип"),
        (CableStr.PropsCableLabel, "Label", "Метка"),
        (CableStr.PropsCableLength, "Length", "Длина"),
        (CableStr.PropsCableWeight, "Weight", "Вес"),
        (CableStr.PropsCableSlack, "Slack", "Запас"),
        (CableStr.PropsCableEnds, "Plugged into", "Подключён к"),
        (CableStr.PropsCableShowLinks, "Show cable links", "Показать связи кабеля"),
        (CableStr.PropsConsole, "Console", "Пульт"),
        (CableStr.PropsConsoleModel, "Model", "Модель"),
        (CableStr.PropsConsoleProtocol, "Input", "Вход"),
        (CableStr.PropsConsoleProtocolNone, "None (offline)", "Нет (офлайн)"),
        (CableStr.PropsConsoleOutputs, "Outputs", "Выходы"),
        (CableStr.PropsConsoleAddOutput, "Add output", "Добавить выход"),
        (CableStr.PropsConsoleRemoveOutput, "Remove this output", "Удалить этот выход"),
        (CableStr.PropsConsoleInputOn, "{0} input is on: the fixtures cabled to this console follow it.",
            "Вход {0} включён: приборы, подключённые к пульту кабелями, следуют ему."),
        (CableStr.PropsConsoleInputOff, "{0} input is off: the fixtures cabled to this console keep their last DMX.",
            "Вход {0} выключен: приборы, подключённые к пульту, сохраняют последний DMX."),
        (CableStr.PropsConsoleEnableInput, "Turn {0} on", "Включить {0}"),
        (CableStr.PropsConsoleDriven, "{0} fixtures are cabled to this console.", "К пульту подключено приборов: {0}."),
        (CableStr.PropsPowerSource, "Power source", "Источник питания"),
        (CableStr.PropsPowerVoltage, "Voltage", "Напряжение"),
        (CableStr.PropsPowerFrequency, "Frequency", "Частота"),
        (CableStr.PropsPowerPhases, "Phases", "Фазы"),
        (CableStr.PropsPowerOutlets, "Outlets", "Розетки"),
        (CableStr.PropsPowerAddOutlet, "Add outlet", "Добавить розетку"),
        (CableStr.PropsPowerRemoveOutlet, "Remove this outlet", "Удалить эту розетку"),
        (CableStr.PropsPowerBreaker, "Breaker, A", "Автомат, А"),
        (CableStr.PropsPowerPhase, "Phase", "Фаза"),
        (CableStr.PropsPowerConnector, "Connector", "Разъём"),
        (CableStr.PropsPowerLoad, "{0} W · {1} A ({2}% of {3} A)", "{0} Вт · {1} А ({2}% от {3} А)"),
        (CableStr.PropsPowerTotal, "Total {0} W · L1 {1} A · L2 {2} A · L3 {3} A", "Всего {0} Вт · L1 {1} А · L2 {2} А · L3 {3} А"),
        (CableStr.PropsPowerUnfed, "Its mains input has no power.", "На вводе нет питания."),
        (CableStr.PatchData, "Data", "Данные"),
        (CableStr.PatchDataNone, "not cabled to a console", "не подключён к пульту"),
        (CableStr.PatchDataLine, "'{0}' {1} · cable {2} · U{3}", "«{0}» {1} · кабель {2} · U{3}"),
        (CableStr.PatchPower, "Power", "Питание"),
        (CableStr.PatchPowerNone, "not cabled to power", "не подключён к питанию"),
        (CableStr.PatchPowerLine, "'{0}' {1} · L{2} · {3} V", "«{0}» {1} · L{2} · {3} В"),
        (CableStr.LockConsoleCable, "Locked: driven by console '{0}' ({1} · cable {2}, universe {3}).",
            "Заблокировано: прибором управляет пульт «{0}» ({1} · кабель {2}, юниверс {3})."),

        (CableStr.LegendInScene, "{0} pcs · {1} m · {2} kg", "{0} шт. · {1} м · {2} кг"),
        (CableStr.LegendOthers, "Other cable types", "Другие типы кабелей"),

        (CableStr.SettingsEditing, "Editing", "Правка"),
        (CableStr.DuplicatePatch, "Duplicated fixtures", "Дубликаты приборов"),
        (CableStr.DuplicatePatchKeep, "Keep their DMX addresses", "Оставлять их DMX-адреса"),
        (CableStr.DuplicatePatchNext, "Patch them to the next free addresses", "Патчить на следующие свободные адреса"),
        (CableStr.DuplicatePatchAsk, "Ask each time", "Спрашивать каждый раз"),
        (CableStr.DuplicatePatchHint,
            "Duplicate (Ctrl+D) and Paste copy fixtures with their DMX addresses, so copies overlap their originals. The editor can patch the copies to the next free addresses instead.",
            "Дублирование (Ctrl+D) и вставка копируют приборы вместе с DMX-адресами, и копии перекрывают оригиналы. Редактор может сразу перепатчить копии на следующие свободные адреса."),
        (CableStr.DuplicateQuestionTitle, "Duplicated fixtures", "Дубликаты приборов"),
        (CableStr.DuplicateQuestion, "{0} copied fixtures use the same DMX addresses as their originals. Patch them to the next free addresses?",
            "{0} скопированных приборов занимают те же DMX-адреса, что и оригиналы. Перепатчить их на следующие свободные адреса?"),
        (CableStr.DuplicateRepatch, "Patch to free addresses", "Перепатчить"),
        (CableStr.DuplicateKeep, "Keep addresses", "Оставить адреса"),
        (CableStr.DuplicateRepatched, "{0} copied fixtures patched to the next free addresses.", "Скопированные приборы перепатчены на свободные адреса: {0}."),
        (CableStr.DuplicateRemember, "Remember my choice", "Запомнить выбор"),
    };

    static CableLoc()
    {
        int n = Enum.GetValues<CableStr>().Length;
        English = new string[n];
        Russian = new string[n];
        foreach (var (key, en, ru) in Table)
        {
            English[(int)key] = en;
            Russian[(int)key] = ru;
        }

        for (int i = 0; i < n; i++)
            English[i] ??= ((CableStr)i).ToString();
    }

    /// <summary>The text of <paramref name="key"/> in the current UI language (English when untranslated).</summary>
    public static string Get(CableStr key)
    {
        int i = (int)key;
        if ((uint)i >= (uint)English.Length)
            return key.ToString();
        return Loc.Language == "ru" && Russian[i] is { Length: > 0 } ru ? ru : English[i];
    }

    /// <summary>The English text of <paramref name="key"/> (undo step names, logs).</summary>
    public static string English_(CableStr key) => (uint)key < (uint)English.Length ? English[(int)key] : key.ToString();

    /// <summary>Formats the text of <paramref name="key"/> (culture-invariant numbers).</summary>
    public static string Format(CableStr key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, Get(key), args);

    /// <summary>"icon  text" for headers, tabs and buttons, built once per language.</summary>
    public static string With(string icon, CableStr key)
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

    /// <summary>The name of a cable type in the UI language (its catalogue name for unknown types and in English).</summary>
    public static string CableName(CableType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Loc.Language == "ru" && RussianCables.TryGetValue(type.Id, out var ru) ? ru : type.DisplayName;
    }

    /// <summary>
    /// The name of a connector in the UI language: the generic terminations ("Loose end", "Terminal block", "Fixed
    /// cable") are translated; connector models keep their catalogue name.
    /// </summary>
    public static string ConnectorName(ConnectorInfo connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        return Loc.Language == "ru" && RussianConnectors.TryGetValue(connector.Id, out var ru) ? ru : connector.DisplayName;
    }

    private static readonly Dictionary<string, string> RussianConnectors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Loose End"] = "Свободный конец",
        ["Terminal Block"] = "Клеммная колодка",
        ["Fixed Cable"] = "Несъёмный кабель",
    };

    /// <summary>The name of a device preset in the UI language.</summary>
    public static string DeviceName(DevicePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return Loc.Language == "ru" && RussianDevices.TryGetValue(preset.Id, out var ru) ? ru.Name : preset.Name;
    }

    /// <summary>The description of a device preset in the UI language.</summary>
    public static string DeviceDescription(DevicePreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return Loc.Language == "ru" && RussianDevices.TryGetValue(preset.Id, out var ru) ? ru.Description : preset.Description;
    }

    /// <summary>The name of a library section.</summary>
    public static string GroupName(DeviceGroup group) => Get(group switch
    {
        DeviceGroup.Console => CableStr.GroupConsoles,
        DeviceGroup.DataDistribution => CableStr.GroupData,
        DeviceGroup.Mains => CableStr.GroupMains,
        DeviceGroup.Distro => CableStr.GroupDistros,
        _ => CableStr.GroupDimmers,
    });

    /// <summary>All entries (tests check completeness and placeholders).</summary>
    internal static IEnumerable<(CableStr Key, string En, string Ru)> Entries => Table;

    /// <summary>Every cable id and device id with a Russian translation (tests check the catalogues are covered).</summary>
    internal static IEnumerable<string> TranslatedCableIds => RussianCables.Keys;

    /// <inheritdoc cref="TranslatedCableIds"/>
    internal static IEnumerable<string> TranslatedDeviceIds => RussianDevices.Keys;
}
