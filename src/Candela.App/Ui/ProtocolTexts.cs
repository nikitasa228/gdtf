using System.Globalization;
using Candela.Protocols;

namespace Candela.App.Ui;

/// <summary>
/// The protocols' status texts in the UI language. A <see cref="ProtocolStatus"/> carries its message, error and warnings
/// as <see cref="ProtocolText"/> — a stable key of <see cref="ProtocolMessages"/> with language-neutral arguments (ports,
/// addresses, counts, names, system messages) and the ready English text. In English that text is shown as it is; in
/// Russian the key's template here is filled with the same arguments. Every key of <see cref="ProtocolMessages.All"/>
/// has a Russian template with the same placeholders (a test checks it), and every <see cref="ProtocolErrorCode"/> a
/// hint that says what to do. A template that does not fit its arguments falls back to the English text, never throws.
/// </summary>
internal static class ProtocolTexts
{
    private static readonly Dictionary<string, string> Russian = new(StringComparer.Ordinal)
    {
        [ProtocolMessages.Literal] = "{0}",
        [ProtocolMessages.Stopped] = "Остановлен",
        [ProtocolMessages.Unavailable] = "{0} недоступен: {1}",
        [ProtocolMessages.StartFailed] = "Не удалось запустить {0}: {1}",
        [ProtocolMessages.StopFailed] = "{0} остановлен с ошибкой: {1}",
        [ProtocolMessages.PortConflict] = "{0} и {1} настроены на один и тот же UDP-порт {2}; выберите разные порты.",
        [ProtocolMessages.InvalidPort] = "{0}: порт {1} не является допустимым UDP-портом (0–65535).",
        [ProtocolMessages.PortInUse] = "{0}: UDP-порт {1} уже занят другой программой на этом компьютере (другим визуализатором, " +
                                       "офлайн-версией пульта или ПО узла). Закройте её или выберите конкретный сетевой интерфейс.",
        [ProtocolMessages.AddressNotAvailable] = "{0}: адрес {1} не назначен ни одному сетевому интерфейсу этого компьютера.",
        [ProtocolMessages.BindFailed] = "{0}: не удаётся открыть UDP {1}:{2} ({3}: {4}).",
        [ProtocolMessages.SocketFailed] = "{0}: сбой сокета: {1}",
        [ProtocolMessages.InterfaceNotFound] = "Сетевой интерфейс «{0}» не найден (кабель отключён или адаптер выключен?). Доступны: {1}.",
        [ProtocolMessages.InterfaceNotFoundNone] = "Сетевой интерфейс «{0}» не найден (кабель отключён или адаптер выключен?). Доступных интерфейсов нет.",
        [ProtocolMessages.Listening] = "Приём на UDP {0} ({1} {2})",
        [ProtocolMessages.ListeningAll] = "Приём на UDP {0} (все интерфейсы)",
        [ProtocolMessages.ArtNetListening] = "Приём на UDP {0} ({1} {2}); объявлено юниверсов: {3}, узел «{4}»",
        [ProtocolMessages.ArtNetListeningAll] = "Приём на UDP {0} (все интерфейсы); объявлено юниверсов: {1}, узел «{2}»",
        [ProtocolMessages.ArtNetUnaddressable] = "Подписанные юниверсы ({0}) нельзя выразить адресом порта Art-Net со смещением {1}; они не объявляются.",
        [ProtocolMessages.ArtNetTooManyUniverses] = "В 255 ответов ArtPollReply помещаются только первые 1020 из {0} подписанных юниверсов.",
        [ProtocolMessages.ArtNetStreamTableFull] = "Активно более {0} потоков Art-Net (отправитель, порт, юниверс); новые игнорируются.",
        [ProtocolMessages.SacnListening] = "Приём на UDP {0} ({1} {2}); multicast подключён для {3} из {4} юниверсов",
        [ProtocolMessages.SacnListeningAll] = "Приём на UDP {0} (все интерфейсы); multicast подключён для {1} из {2} юниверсов",
        [ProtocolMessages.SacnUnicast] = "Приём на UDP {0} ({1} {2}); только unicast",
        [ProtocolMessages.SacnUnicastAll] = "Приём на UDP {0} (все интерфейсы); только unicast",
        [ProtocolMessages.SacnMulticastLimit] = "Подписано юниверсов: {0}; multicast подключён только для первых {1}. Остальные передавайте по unicast.",
        [ProtocolMessages.SacnMulticastFailed] = "Не удалось подключить multicast sACN для юниверсов ({1}; всего {0}): {2}. Unicast sACN работает — " +
                                                 "настройте пульт на unicast на IP этого компьютера или проверьте сетевой интерфейс.",
        [ProtocolMessages.SacnSyncJoinFailed] = "Не удалось подключить юниверс синхронизации sACN {0}: {1}. Синхронизированные данные выводятся без синхронизации.",
        [ProtocolMessages.SacnStreamTableFull] = "Активно более {0} потоков sACN (источник, юниверс); новые игнорируются.",
        [ProtocolMessages.OscPatternIgnored] = "Шаблон OSC пропущен: {0}.",
        [ProtocolMessages.OscKeepAliveFailed] = "Сбой keep-alive OSC: {0}",
        [ProtocolMessages.OscHeldTableFull] = "OSC уже удерживает {0} юниверсов; сообщения для других юниверсов игнорируются.",
        [ProtocolMessages.PsnListening] = "Приём на UDP {0} ({1} {2}), multicast {3}",
        [ProtocolMessages.PsnListeningAll] = "Приём на UDP {0} (все интерфейсы), multicast {1}",
        [ProtocolMessages.PsnTrackers] = "Приём на UDP {0}; трекеров: {1}",
        [ProtocolMessages.PsnInvalidGroup] = "Группа PosiStageNet «{0}» не является адресом IPv4 multicast.",
        [ProtocolMessages.PsnMulticastFailed] = "Не удалось подключить multicast PosiStageNet {0}: {1}. Unicast PSN на этот компьютер работает.",
        [ProtocolMessages.EnttecReceiving] = "Приём DMX с {0} в юниверс {1}",
        [ProtocolMessages.EnttecNoPort] = "Выберите последовательный порт EntTec DMX USB Pro (доступны: {0}).",
        [ProtocolMessages.EnttecNoPortNone] = "Выберите последовательный порт EntTec DMX USB Pro (последовательные порты не найдены).",
        [ProtocolMessages.EnttecInvalidUniverse] = "Юниверс EntTec {0} недопустим; юниверсы начинаются с 1.",
        [ProtocolMessages.EnttecOpenFailed] = "Не удаётся открыть адаптер EntTec на {0}: {1} (подключён ли он и не занят ли другой программой?)",
        [ProtocolMessages.EnttecNotResponding] = "Адаптер EntTec на {0} не принимает команды: {1}",
        [ProtocolMessages.EnttecWriteFailed] = "Адаптер EntTec на {0}: ошибка записи ({1}).",
        [ProtocolMessages.EnttecDisconnected] = "Адаптер EntTec на {0} отключён: {1}",
        [ProtocolMessages.EnttecFailed] = "Сбой адаптера EntTec на {0}: {1}: {2}",
    };

    private static readonly Dictionary<ProtocolErrorCode, (string En, string Ru)> Hints = new()
    {
        [ProtocolErrorCode.Unavailable] = (
            "This protocol is planned or needs the manufacturer's SDK, so this version cannot start it.",
            "Протокол запланирован или требует SDK производителя, поэтому эта версия не может его запустить."),
        [ProtocolErrorCode.InvalidSettings] = (
            "Check the protocol's settings below (port, universe, multicast group, serial port).",
            "Проверьте настройки протокола ниже (порт, юниверс, группа multicast, последовательный порт)."),
        [ProtocolErrorCode.PortConflict] = (
            "Two enabled protocols are set to the same UDP port: change the port of one of them.",
            "Два включённых протокола настроены на один UDP-порт: измените порт одного из них."),
        [ProtocolErrorCode.PortInUse] = (
            "Another program on this computer holds the port: close it (another visualiser, console offline software, node software) or choose a specific network interface.",
            "Порт занят другой программой на этом компьютере: закройте её (другой визуализатор, офлайн-версию пульта, ПО узла) или выберите конкретный сетевой интерфейс."),
        [ProtocolErrorCode.AddressNotAvailable] = (
            "The selected address is not on this computer: choose another network interface or All interfaces.",
            "Выбранного адреса нет на этом компьютере: выберите другой сетевой интерфейс или «Все интерфейсы»."),
        [ProtocolErrorCode.InterfaceNotFound] = (
            "The selected network adapter is missing: plug the cable in, enable the adapter or choose another interface.",
            "Выбранного сетевого адаптера нет: подключите кабель, включите адаптер или выберите другой интерфейс."),
        [ProtocolErrorCode.SocketError] = (
            "The system refused to open the port: check the firewall and the network settings.",
            "Система не дала открыть порт: проверьте брандмауэр и сетевые настройки."),
        [ProtocolErrorCode.SocketFailed] = (
            "The network connection failed while receiving: check the adapter, then switch the protocol off and on.",
            "Сетевое соединение оборвалось во время приёма: проверьте адаптер, затем выключите и снова включите протокол."),
        [ProtocolErrorCode.DeviceNotFound] = (
            "The serial port could not be opened: plug the widget in, close programs that use it and choose the right port.",
            "Не удалось открыть последовательный порт: подключите адаптер, закройте программы, которые его используют, и выберите правильный порт."),
        [ProtocolErrorCode.DeviceNotResponding] = (
            "The device does not answer: replug it and check that it is an EntTec DMX USB Pro.",
            "Устройство не отвечает: переподключите его и проверьте, что это EntTec DMX USB Pro."),
        [ProtocolErrorCode.DeviceDisconnected] = (
            "The device was unplugged or failed: plug it in again, then switch the protocol off and on.",
            "Устройство отключено или неисправно: подключите его снова, затем выключите и включите протокол."),
        [ProtocolErrorCode.StartFailed] = (
            "An unexpected failure: the message above and the Console panel tell more.",
            "Непредвиденный сбой: подробности — в сообщении выше и на панели «Консоль»."),
    };

    /// <summary>The Russian templates by key (tests check completeness and placeholders).</summary>
    internal static IReadOnlyDictionary<string, string> RussianTemplates => Russian;

    /// <summary>The hints by error code in English and Russian (tests check completeness).</summary>
    internal static IReadOnlyDictionary<ProtocolErrorCode, (string En, string Ru)> ErrorHints => Hints;

    /// <summary>
    /// <paramref name="text"/> in the UI language: the English text, or the key's Russian template filled with the
    /// arguments. <paramref name="protocol"/> (optional) lets an "is not available" message give the protocol's support
    /// reason in the UI language too.
    /// </summary>
    public static string Localize(ProtocolText text, ProtocolDescriptor? protocol = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Loc.Language != "ru" || !Russian.TryGetValue(text.Key, out var template))
            return text.English;
        IReadOnlyList<string> arguments = text.Arguments;
        if (text.Key == ProtocolMessages.Unavailable && protocol is not null && arguments.Count == 2)
            arguments = new[] { arguments[0], StageLoc.ProtocolSupportReason(protocol) };
        return Fill(template, arguments) ?? text.English;
    }

    /// <summary>What to do about <paramref name="code"/> in the UI language; empty for <see cref="ProtocolErrorCode.None"/>.</summary>
    public static string ErrorHint(ProtocolErrorCode code) =>
        Hints.TryGetValue(code, out var hint) ? Loc.Language == "ru" ? hint.Ru : hint.En : "";

    /// <summary>The template filled with the arguments, or null when it does not fit them.</summary>
    internal static string? Fill(string template, IReadOnlyList<string> arguments)
    {
        var values = new object[arguments.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = arguments[i];
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, values);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
