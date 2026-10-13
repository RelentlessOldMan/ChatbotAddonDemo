namespace Demo.Domain;

/// <summary>Example design files a new workspace starts with.</summary>
public static class SeedData
{
    public const string DatasheetCsv =
        """
        # LDO-3300 3.3 V low-dropout regulator, datasheet rev A (electrical characteristics)
        # device: LDO-3300
        parameter,name,units,min,typ,max
        VOUT,Output voltage,V,3.267,3.300,3.333
        IQ,Quiescent current,µA,,45,60
        DROPOUT,Dropout voltage,mV,,180,300
        LOAD_REG,Load regulation,mV,,2,8
        PSRR,Ripple rejection at 1 kHz,dB,55,65,

        """;

    /// <summary>Tighter targets for the next silicon revision, plus a parameter the bench can't measure.</summary>
    public const string RevBTargetsJson =
        """
        {
          "device": "LDO-3300",
          "revision": "B (design targets)",
          "parameters": [
            { "parameter": "VOUT",    "name": "Output voltage",           "units": "V",  "min": 3.280, "typ": 3.300, "max": 3.320 },
            { "parameter": "IQ",      "name": "Quiescent current",        "units": "µA", "typ": 40, "max": 55 },
            { "parameter": "DROPOUT", "name": "Dropout voltage",          "units": "mV", "typ": 160, "max": 250 },
            { "parameter": "PSRR",    "name": "Ripple rejection at 1 kHz", "units": "dB", "min": 58, "typ": 66 },
            { "parameter": "NOISE",   "name": "Output noise (10 Hz-100 kHz)", "units": "µVrms", "typ": 30, "max": 45 }
          ]
        }

        """;
}
