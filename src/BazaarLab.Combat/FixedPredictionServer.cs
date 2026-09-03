using System.Text.Json;

namespace BazaarLab.Combat;

public static class FixedPredictionServer
{
    private sealed class Request
    {
        public string InputPath { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
    }

    public static void Run(string catalogPath, int baseSeed, int samples, int maximumTicks)
    {
        OfficialCardCatalog catalog = OfficialCardCatalog.LoadJsonLines(catalogPath);
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                Request request = JsonSerializer.Deserialize<Request>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("prediction request is empty");
                string snapshotJson = File.ReadAllText(request.InputPath);
                BppPredictionResult prediction = BppMonteCarloDifferential.PredictJson(
                    snapshotJson, Path.GetFileNameWithoutExtension(request.InputPath),
                    catalog, baseSeed, samples, maximumTicks);
                string resultJson = JsonSerializer.Serialize(prediction,
                    new JsonSerializerOptions { WriteIndented = true });
                string? directory = Path.GetDirectoryName(
                    Path.GetFullPath(request.OutputPath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(request.OutputPath, resultJson + Environment.NewLine);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    outputPath = request.OutputPath,
                }));
            }
            catch (Exception exception)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = exception.GetType().Name + ": " + exception.Message,
                }));
            }
            Console.Out.Flush();
        }
    }

}
