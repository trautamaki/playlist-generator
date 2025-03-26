using MediaBrowser.Controller;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using SQLitePCL.pretty;

namespace Jellyfin.Plugin.PlaylistGenerator.Objects;

public class ActivityDatabase
{
    //private static readonly string DATE_TIME_FORMAT = "yyyy-MM-dd HH:mm:ss";
    private readonly ILogger _logger;
    protected IFileSystem FileSystem { get; private set; }
    private string DbFilePath { get; set; }
    private readonly SQLiteDatabaseConnection _dbConnection;
    
    public readonly int MaxSevenDays;
    
    
    public ActivityDatabase(ILogger logger, IServerApplicationPaths appPaths, IFileSystem fileSystem, 
        CancellationToken cancellationToken)
    {
        DbFilePath = Path.Combine(appPaths.DataPath, "playback_reporting.db");
        FileSystem = fileSystem;
        _logger = logger;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _dbConnection = SQLite3.Open(DbFilePath, ConnectionFlags.ReadOnly, null);
            _logger.LogInformation("Opened Playback Reporting database: {DbFilePath}", DbFilePath);
            MaxSevenDays = MaxPlaysSevenDays();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Playback Reporting database at {DbFilePath}", DbFilePath);
            throw;
        }
    }

    public void Dispose()
    {
        _dbConnection?.Dispose();
    }

    public List<Dictionary<string, string>> ExecuteQuery(string sql)
    {
        try
        {
            var queryResult = _dbConnection.Query(sql);
            var resultList = new List<Dictionary<string, string>>();

            foreach (var row in queryResult)
            {
                var rowDict = new Dictionary<string, string>();

                for (var i = 0; i < row.Count; i++)
                {
                    rowDict[$"Column{i}"] = row[i].ToString();
                }
                resultList.Add(rowDict);
            }

            _logger.LogInformation($"Got {resultList.Count} records from playback reporting database.");
            return resultList;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing SQL query: {Sql}", sql);
            return new List<Dictionary<string, string>>(); // Return empty list on failure
        }
    }
    
    private int MaxPlaysSevenDays()
    {
        var sql = """
                  SELECT ItemId, COUNT(*) AS ItemCount
                  FROM PlaybackActivity
                  WHERE ItemType = 'Audio' 
                  AND DateCreated >= datetime('now', '-7 days')
                  GROUP BY ItemId
                  ORDER BY ItemCount DESC;
                  """;
        var output = ExecuteQuery(sql);
        if (output.Count == 0)
        {
            return 0;
        }
        return int.Parse(output[0]["Column1"]);
    }
}