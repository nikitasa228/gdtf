namespace Candela.App.Ui;

/// <summary>
/// Unit symbols and ImGui number formats of the engine-backed panels in the UI language: the international SI symbols in
/// English, the Cyrillic ones in Russian (кд, лм, Вт …). One immutable table per language, so drawing code reads ready
/// strings without allocating.
/// </summary>
internal sealed class StageUnits
{
    private static readonly StageUnits English = new(
        candela: "cd", megacandela: "Mcd", lumen: "lm", kilogram: "kg", watt: "W", volt: "V", ampere: "A", hertz: "Hz",
        decibelA: "dB(A)", metre: "m", second: "s", metrePerSecond: "m/s", nanometre: "nm", candelaPerSquareMetre: "cd/m²",
        framesPerSecond: "fps", lux: "lx", decibel: "dB");

    private static readonly StageUnits Russian = new(
        candela: "кд", megacandela: "Мкд", lumen: "лм", kilogram: "кг", watt: "Вт", volt: "В", ampere: "А", hertz: "Гц",
        decibelA: "дБ(А)", metre: "м", second: "с", metrePerSecond: "м/с", nanometre: "нм", candelaPerSquareMetre: "кд/м²",
        framesPerSecond: "к/с", lux: "лк", decibel: "дБ");

    private StageUnits(string candela, string megacandela, string lumen, string kilogram, string watt, string volt, string ampere,
        string hertz, string decibelA, string metre, string second, string metrePerSecond, string nanometre,
        string candelaPerSquareMetre, string framesPerSecond, string lux, string decibel)
    {
        Lux = lux;
        Decibel = decibel;
        FramesPerSecond = framesPerSecond;
        Candela = candela;
        Megacandela = megacandela;
        Lumen = lumen;
        Kilogram = kilogram;
        Watt = watt;
        Volt = volt;
        Ampere = ampere;
        Hertz = hertz;
        DecibelA = decibelA;
        Metre = metre;
        Second = second;
        FormatHertz = "%.2f " + hertz;
        FormatMetres = "%.2f " + metre;
        FormatSeconds = "%.2f " + second;
        FormatMetresPerSecond = "%.2f " + metrePerSecond;
        FormatNanometres = "%.0f " + nanometre;
        FormatCandela = "%.0f " + candela;
        FormatPerMetre = "%.3f /" + metre;
        FormatLuminance = "%.3f " + candelaPerSquareMetre;
        FormatFramesPerSecond = "%.2f " + framesPerSecond;
    }

    /// <summary>The table of the current UI language.</summary>
    public static StageUnits Current => Loc.Language == "ru" ? Russian : English;

    /// <summary>Frames per second (timecode and image sequences).</summary>
    public string FramesPerSecond { get; }

    /// <summary>Lux (illuminance).</summary>
    public string Lux { get; }

    /// <summary>Decibel (sound level; the overlay legend's values).</summary>
    public string Decibel { get; }

    /// <summary>Candela (luminous intensity).</summary>
    public string Candela { get; }

    /// <summary>Megacandela.</summary>
    public string Megacandela { get; }

    /// <summary>Lumen (luminous flux).</summary>
    public string Lumen { get; }

    /// <summary>Kilogram.</summary>
    public string Kilogram { get; }

    /// <summary>Watt.</summary>
    public string Watt { get; }

    /// <summary>Volt.</summary>
    public string Volt { get; }

    /// <summary>Ampere.</summary>
    public string Ampere { get; }

    /// <summary>Hertz.</summary>
    public string Hertz { get; }

    /// <summary>A-weighted decibels (sound level).</summary>
    public string DecibelA { get; }

    /// <summary>Metre.</summary>
    public string Metre { get; }

    /// <summary>Second.</summary>
    public string Second { get; }

    /// <summary>"%.2f Hz".</summary>
    public string FormatHertz { get; }

    /// <summary>"%.2f m".</summary>
    public string FormatMetres { get; }

    /// <summary>"%.2f s".</summary>
    public string FormatSeconds { get; }

    /// <summary>"%.2f m/s".</summary>
    public string FormatMetresPerSecond { get; }

    /// <summary>"%.0f nm".</summary>
    public string FormatNanometres { get; }

    /// <summary>"%.0f cd".</summary>
    public string FormatCandela { get; }

    /// <summary>"%.3f /m" (scattering coefficient).</summary>
    public string FormatPerMetre { get; }

    /// <summary>"%.3f cd/m²" (luminance).</summary>
    public string FormatLuminance { get; }

    /// <summary>"%.2f fps".</summary>
    public string FormatFramesPerSecond { get; }
}
