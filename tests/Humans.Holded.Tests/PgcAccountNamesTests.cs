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

    [HumansFact]
    public void Entries_are_digit_prefixes_with_both_names()
    {
        PgcAccountNames.Entries.Should().OnlyContain(e =>
            e.Prefix.Length >= 1 && e.Prefix.Length <= 4 && e.Prefix.All(char.IsAsciiDigit)
            && !string.IsNullOrWhiteSpace(e.Spanish) && !string.IsNullOrWhiteSpace(e.English));
    }

    [HumansTheory]
    // Names exactly as Holded's chart has them (screenshot of /Holded, 30 Oct 2026), misspellings included.
    [Xunit.InlineData(10000000, "Dotación fundacional", "Founding endowment")]
    [Xunit.InlineData(11000000, "Prima de emisión o asunción", "Share premium")]
    [Xunit.InlineData(11200000, "Reserva legal", "Legal reserve")]
    [Xunit.InlineData(12000000, "Remanente", "Retained earnings")]
    [Xunit.InlineData(12100000, "Excendentes negativos de ejercicios anteriores", "Prior years' deficits")]
    [Xunit.InlineData(12900000, "Excendente del ejercicio", "Surplus for the year")]
    [Xunit.InlineData(17000000, "Deudas a largo plazo con entidades de crédito", "Long-term bank debt")]
    [Xunit.InlineData(17100000, "Deudas a largo plazo", "Long-term debt")]
    [Xunit.InlineData(17300000, "Proveedores de inmovilizado a largo plazo", "Long-term fixed-asset suppliers")]
    [Xunit.InlineData(17500000, "Efectos a pagar a largo plazo", "Long-term bills payable")]
    [Xunit.InlineData(18000000, "Fianzas recibidas a largo plazo", "Long-term guarantees received")]
    [Xunit.InlineData(18100000, "Anticipos recibidos por ventas o prestaciones de servicios a largo plazo", "Long-term advances received for sales or services")]
    [Xunit.InlineData(57000000, "Caja, euros", "Cash, euros")]
    [Xunit.InlineData(57200000, "BANCOS E INSTITUCIONES DE CREDITO C/C VISTA, EUROS", "Banks, current accounts, euros")]
    [Xunit.InlineData(47200000, "Hacienda Pública, IVA soportado", "Input VAT")]
    [Xunit.InlineData(47200021, "Hacienda Pública, IVA soportado 21%", "Input VAT 21%")]
    [Xunit.InlineData(47510000, "Hacienda Pública, acreedora por retenciones practicadas", "Tax authority, withholdings payable")]
    [Xunit.InlineData(47500000, "Hacienda Pública, acreedora por conceptos fiscales", "Tax authority, payables")]
    [Xunit.InlineData(72000000, "Cuotas de asociados y afiliados", "Membership fees")]
    [Xunit.InlineData(65000000, "Ayudas monetarias", "Monetary grants made")]
    [Xunit.InlineData(65000000, "Pérdidas de créditos comerciales incobrables", "Bad trade debts")]
    [Xunit.InlineData(609, "«Rappels» por compras", "Volume discounts on purchases")]
    public void Standard_spanish_names_translate(int number, string holded, string english) =>
        PgcAccountNames.EnglishName(number, holded).Should().Be(english);

    [HumansTheory]
    [Xunit.InlineData(57200001, "Banco Sabadell")]
    [Xunit.InlineData(57200001, "Bancos e instituciones de crédito c/c vista, euros Sabadell")]
    [Xunit.InlineData(40000004, "Proveedores")]
    [Xunit.InlineData(41000012, "Acreedores por prestaciones de servicios")]
    [Xunit.InlineData(62900000, "Otros servicios")]
    [Xunit.InlineData(62900128, "Camp infrastructure")]
    [Xunit.InlineData(57000000, "")]
    public void Names_the_association_chose_and_member_and_category_accounts_keep_theirs(int number, string holded) =>
        PgcAccountNames.EnglishName(number, holded).Should().BeNull();
}
