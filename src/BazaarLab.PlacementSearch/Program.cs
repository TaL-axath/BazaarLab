using System.Text.Json;
using System.Globalization;
using BazaarLab.Combat;

if (args.Length is not (3 or 4))
{
    Console.Error.WriteLine(
        "usage: BazaarLab.PlacementSearch <catalog.jsonl> <snapshot.json> " +
        "<output.json> [options.json]");
    Environment.ExitCode = 2;
    return;
}

OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(args[0]);
PlacementSearchOptions options = args.Length == 4
    ? JsonSerializer.Deserialize<PlacementSearchOptions>(
        File.ReadAllText(args[3]), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException("invalid placement options")
    : new PlacementSearchOptions();
PlacementSearchResult result = PlacementOptimizer.Optimize(args[1], catalog, options,
    progress: value =>
    {
        string message = (value.Message ?? string.Empty).Replace('\t', ' ')
            .Replace('\r', ' ').Replace('\n', ' ');
        Console.WriteLine("BLPROGRESS\t" + value.Stage + "\t" +
            value.Fraction.ToString("0.0000", CultureInfo.InvariantCulture) + "\t" + message);
        Console.Out.Flush();
    });
string json = JsonSerializer.Serialize(result,
    new JsonSerializerOptions { WriteIndented = true });
string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(args[2]));
if (!string.IsNullOrEmpty(outputDirectory))
{
    Directory.CreateDirectory(outputDirectory);
}
File.WriteAllText(args[2], json + Environment.NewLine);
Console.WriteLine(json);
