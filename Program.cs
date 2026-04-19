using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Npgsql;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.S3;
using Amazon.S3.Model;

class Program
{
  static async Task Main(string[] args)
  {
    var configuration = new ConfigurationBuilder()
      .SetBasePath(Directory.GetCurrentDirectory())
      .AddJsonFile("appsettings.json")
      .Build();

    Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║         Keith Long Archive - Get Special Images            ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
    Console.WriteLine();

    var secretArn   = configuration["PostgreSQL:SecretArn"]  ?? throw new Exception("PostgreSQL:SecretArn not configured");
    var host        = configuration["PostgreSQL:Host"]        ?? throw new Exception("PostgreSQL:Host not configured");
    var database    = configuration["PostgreSQL:Database"]    ?? throw new Exception("PostgreSQL:Database not configured");
    var port        = configuration["PostgreSQL:Port"] ?? "5432";
    var bucketName  = configuration["S3:BucketName"]  ?? "keithlong-art-photos";
    var s3Prefix    = configuration["S3:Prefix"]      ?? "jpg/";
    var outputDir   = configuration["Output:Directory"] ?? "images";

    Console.WriteLine("Retrieving database credentials from AWS Secrets Manager...");
    var (username, password) = await GetDatabaseCredentials(secretArn);
    Console.WriteLine("✓ Credentials retrieved\n");

    var connectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true";

    Console.WriteLine("Querying database...");
    var rows = await GetImageList(connectionString);
    Console.WriteLine($"✓ Found {rows.Count} images to process\n");

    if (rows.Count == 0)
    {
      Console.WriteLine("Nothing to do.");
      return;
    }

    Directory.CreateDirectory(outputDir);

    var region = Amazon.RegionEndpoint.USEast1;
    using var s3Client = new AmazonS3Client(region);

    int downloaded = 0;
    int skipped    = 0;
    int failed     = 0;

    for (int i = 0; i < rows.Count; i++)
    {
      var (sourceFilename, newName) = rows[i];
      var s3Key      = s3Prefix + sourceFilename;
      var outputPath = Path.Combine(outputDir, newName);

      if (File.Exists(outputPath))
      {
        skipped++;
        Console.Write($"\r  Skipped (already exist): {skipped}   ");
        continue;
      }

      try
      {
        var request = new GetObjectRequest
        {
          BucketName = bucketName,
          Key = s3Key
        };

        using var response = await s3Client.GetObjectAsync(request);
        await using var fileStream = File.Create(outputPath);
        await response.ResponseStream.CopyToAsync(fileStream);

        downloaded++;
        Console.WriteLine($"  ✓ [{i + 1}/{rows.Count}] {sourceFilename} → {newName}");
      }
      catch (Exception ex)
      {
        failed++;
        Console.WriteLine($"  ✗ [{i + 1}/{rows.Count}] {sourceFilename}: {ex.Message}");
      }
    }

    if (skipped > 0) Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine("─────────────────────────────────────");
    Console.WriteLine($"  Total:      {rows.Count}");
    Console.WriteLine($"  Downloaded: {downloaded}");
    Console.WriteLine($"  Skipped:    {skipped}");
    Console.WriteLine($"  Failed:     {failed}");
    Console.WriteLine("─────────────────────────────────────");
  }

  static async Task<List<(string sourceFilename, string newName)>> GetImageList(string connectionString)
  {
    var results = new List<(string, string)>();

    const string sql = """
with img as 
(
  select ai.id_field, ai.artwork_id, LEFT(ai.view , 4) as lview 
  from artwork_image ai
  WHERE ai.view LIKE 'Front%' and URL is null 
), imgGroup as 
(
  select array_to_string(array_agg(id_field), ', ') as ids, artwork_id, lview
  from img 
  group by artwork_id, img.lview 
)
SELECT
  --EXTRACT(YEAR FROM A.CREATE_Dt)::text, A.TYPE_NUMBER,  
  CONCAT(A.FILENAME,'.jpg') AS OLD_NAME, CONCAT(a.id_field::text,'_',EXTRACT(YEAR FROM CREATE_Dt)::text,T.code,a.type_number::text,'.jpg') AS NEW_NAME
FROM artwork a
LEFT JOIN artwork_type t ON a.type_id ->> 0 = t.airtable_id
LEFT JOIN imgGroup ai_front ON a.airtable_id = ai_front.artwork_id AND ai_front.lview    ='Fron'
WHERE  A.FILENAME IS NOT NULL AND ai_front.ids IS NULL 
-- order by  EXTRACT(YEAR FROM CREATE_Dt), type_number
     
EXCEPT
      
SELECT
  --EXTRACT(YEAR FROM CREATE_Dt)::text, TYPE_NUMBER,  
  CONCAT(FILENAME,'.jpg') AS OLD_NAME, CONCAT(id_field::text,'_',EXTRACT(YEAR FROM CREATE_Dt)::text,T.code,type_number::text,'.jpg') AS NEW_NAME
FROM ARTWORK A 
LEFT join artwork_type t on a.type_id ->> 0 = t.airtable_id
WHERE A.REFERENCE_IMAGE IS NULL AND A.FILENAME IS NOT NULL 
--order by  EXTRACT(YEAR FROM A.CREATE_Dt), type_number
""";

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();

    await using var cmd = new NpgsqlCommand(sql, connection);
    await using var reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
      if (reader.IsDBNull(0) || reader.IsDBNull(1))
      {
        Console.WriteLine("  ⚠ Skipping row: null source filename or new name (artwork_type may be missing)");
        continue;
      }

      results.Add((reader.GetString(0), reader.GetString(1)));
    }

    return results;
  }

  static async Task<(string username, string password)> GetDatabaseCredentials(string secretArn)
  {
    var client = new AmazonSecretsManagerClient(Amazon.RegionEndpoint.USEast1);

    var response = await client.GetSecretValueAsync(new GetSecretValueRequest
    {
      SecretId = secretArn
    });

    var secret   = JObject.Parse(response.SecretString);
    var username = secret["username"]?.ToString() ?? throw new Exception("username not found in secret");
    var password = secret["password"]?.ToString() ?? throw new Exception("password not found in secret");

    return (username, password);
  }
}
