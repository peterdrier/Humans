using AwesomeAssertions;
using Humans.Holded.Services;

namespace Humans.Holded.Tests;

public sealed class PgcAccountNamesTests
{
    /// <summary>The PGC PYMES chart's account numbers (three digits), transcribed independently of the dictionary's
    /// names, plus the non-profit adaptation's own. A standard account missing from the dictionary fails here.</summary>
    private static readonly string[] StandardAccounts =
    [
        "100", "101", "102", "103", "104", "108", "109", "110", "111", "112", "113", "114", "118", "119",
        "120", "121", "129", "130", "131", "132", "137", "141", "142", "143", "145", "146", "150", "153", "154",
        "160", "161", "162", "163", "170", "171", "172", "173", "174", "175", "176", "177", "179",
        "180", "181", "185", "189", "190", "192", "194", "195", "197", "199",
        "200", "201", "202", "203", "204", "205", "206", "209", "210", "211", "212", "213", "214", "215", "216",
        "217", "218", "219", "220", "221", "230", "231", "232", "233", "237", "239", "240", "241", "242", "249",
        "250", "251", "252", "253", "254", "255", "257", "258", "259", "260", "265", "280", "281", "282",
        "290", "291", "292", "293", "294", "295", "297", "298",
        "300", "310", "320", "321", "322", "325", "326", "327", "328", "390", "391", "392", "393", "394", "395", "396",
        "400", "401", "403", "404", "405", "406", "407", "410", "411", "419", "430", "431", "432", "433", "434",
        "435", "436", "437", "438", "440", "441", "446", "449", "460", "465", "466", "470", "471", "472", "473",
        "474", "475", "476", "477", "479", "480", "485", "490", "493", "499",
        "500", "502", "505", "506", "507", "510", "511", "512", "513", "514", "520", "521", "522", "523", "524",
        "525", "526", "527", "528", "529", "530", "531", "532", "533", "534", "535", "539", "540", "541", "542",
        "543", "544", "545", "546", "547", "548", "549", "550", "551", "552", "554", "555", "556", "557", "558",
        "559", "560", "561", "565", "566", "567", "568", "569", "570", "571", "572", "573", "574", "575", "576",
        "593", "594", "595", "597", "598", "599",
        "600", "601", "602", "606", "607", "608", "609", "610", "611", "612", "620", "621", "622", "623", "624",
        "625", "626", "627", "628", "629", "630", "631", "633", "634", "636", "638", "639", "640", "641", "642",
        "643", "649", "650", "651", "653", "654", "659", "660", "661", "662", "663", "664", "665", "666", "667",
        "668", "669", "670", "671", "672", "673", "675", "678", "680", "681", "682", "690", "691", "692", "693",
        "694", "695", "696", "697", "698", "699",
        "700", "701", "702", "703", "704", "705", "706", "708", "709", "710", "711", "712", "713", "720", "721",
        "722", "723", "728", "730", "731", "732", "733", "740", "746", "747", "751", "752", "753", "754", "755",
        "759", "760", "761", "762", "763", "766", "768", "769", "770", "771", "772", "773", "774", "775", "778",
        "790", "791", "792", "793", "794", "795", "796", "797", "798", "799",
        "840", "841", "842", "940", "941", "942",
    ];

    [HumansFact]
    public void Every_standard_account_and_its_subgroup_and_group_have_an_entry()
    {
        var prefixes = PgcAccountNames.Entries.Select(e => e.Prefix).ToHashSet(StringComparer.Ordinal);
        var missing = StandardAccounts
            .SelectMany(a => new[] { a, a[..2], a[..1] })
            .Distinct(StringComparer.Ordinal)
            .Where(p => !prefixes.Contains(p))
            .ToList();

        missing.Should().BeEmpty();
    }

    /// <summary>Standard-named accounts in the association's live Holded chart (30 Oct 2026) whose wording differs
    /// from the PGC text. A name Holded uses with no entry fails here.</summary>
    private static readonly (int Number, string Name)[] LiveChartNames =
    [
        (20400000, "Fondo comercio"), (20700000, "Derechos sobre activos cedidos en uso"),
        (26000000, "Inversiones financieras a largo plazo en instrumentos de patrimonio"),
        (27000000, "Fianzas constituidas a largo plazo"),
        (28000000, "Amortización acumulada de investigación"), (28010000, "Amortización acumulada de desarrollo"),
        (28020000, "Amortización acumulada de concesiones administrativas"),
        (28030000, "Amortización acumulada de propiedad industrial"),
        (28040000, "Amortización acumulada de fondo de comercio"),
        (28050000, "Amortización acumulada de derechos de traspaso"),
        (28060000, "Amortización acumulada de aplicaciones informáticas"),
        (28070000, "Amortización acumulada de derechos de sobre activos cedidos en uso"),
        (28140000, "Amortización acumulada de utillaje"), (28150000, "Amortización acumulada de otras instalaciones"),
        (28160000, "Amortización acumulada de mobiliario"),
        (28170000, "Amortización acumulada de equipos para procesos de información"),
        (28180000, "Amortización acumulada de elementos de transporte"),
        (28190000, "Amortización acumulada de otro inmovilizado material"),
        (30000000, "Mercaderías A"), (31000000, "Materias primas A"), (33000000, "Productos en curso A"),
        (34000000, "Productos semiterminados A"), (35000000, "Productos terminados A"),
        (39000000, "Deterioro de valor de bienes destinados a la actividad"),
        (43040000, "Clientes (moneda extranjera)"), (43090000, "Clientes, facturas pendientes de formalizar"),
        (43100000, "Efectos comerciales en cartera"), (44000000, "Deudores (euros)"),
        (44040000, "Deudores (moneda extranjera)"), (44090000, "Deudores, facturas pendientes de formalizar"),
        (44100000, "Deudores, efectos comerciales en cartera"), (44800000, "Patrocinadores"),
        (44820000, "Afiliados"), (44890000, "Otros deudores"), (46400000, "Entregas para gastos a justificar"),
        (52000000, "Préstamos a corto plazo de entidades de crédito"),
        (52090000, "Deudas por operaciones de factoring"), (55100000, "Cuenta corriente con patronos y otros"),
        (57200000, "Bancos, euros"), (57400000, "Bancos, cuentas de ahorro, euros"),
        (60000000, "Compras de bienes destinados a la actividad"),
        (60800000, "Devoluciones de compras de bienes destinados a la actividad"),
        (61000000, "Variación de existencias de bienes destinados a la actividad"),
        (63000000, "Impuesto corriente"), (63010000, "Impuesto diferido"),
        (66230000, "Intereses de deudas con entidades de crédito"), (66240000, "Intereses de deudas, otras empresas"),
        (72300000, "Patrocinio"), (72310000, "Patrocinio publicitario"), (72330000, "Colaboraciones empresariales"),
        (74700000, " Donaciones y legados transferidos al excedente del ejercicio"), // leading space is Holded's
    ];

    [HumansFact]
    public void Every_standard_name_in_the_live_chart_translates()
    {
        LiveChartNames.Where(a => PgcAccountNames.EnglishName(a.Number, a.Name) is null)
            .Select(a => a.Number).Should().BeEmpty();
    }

    [HumansFact]
    public void Entries_are_digit_prefixes_with_both_names()
    {
        PgcAccountNames.Entries.Should().OnlyContain(e =>
            e.Prefix.Length >= 1 && e.Prefix.Length <= 4 && e.Prefix.All(char.IsAsciiDigit)
            && !string.IsNullOrWhiteSpace(e.Spanish) && !string.IsNullOrWhiteSpace(e.English));
    }

    [HumansTheory]
    [Xunit.InlineData(57000000, "Caja, euros", "Cash, euros")]
    [Xunit.InlineData(57200000, "BANCOS E INSTITUCIONES DE CREDITO C/C VISTA, EUROS", "Banks, current accounts, euros")]
    [Xunit.InlineData(12900000, "Excendente del ejercicio", "Surplus for the year")] // Holded's own misspelling
    [Xunit.InlineData(47200021, "Hacienda Pública, IVA soportado 21%", "Input VAT 21%")]
    public void Standard_spanish_names_translate(int number, string holded, string english) =>
        PgcAccountNames.EnglishName(number, holded).Should().Be(english);

    [HumansTheory]
    [Xunit.InlineData(57200001, "Banco Sabadell")]
    [Xunit.InlineData(41000012, "Acreedores por prestaciones de servicios")]
    [Xunit.InlineData(62900000, "Otros servicios")]
    public void Names_the_association_chose_and_member_and_category_accounts_keep_theirs(int number, string holded) =>
        PgcAccountNames.EnglishName(number, holded).Should().BeNull();
}
