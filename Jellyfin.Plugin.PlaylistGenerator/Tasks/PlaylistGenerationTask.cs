using Microsoft.VisualBasic.FileIO;
using System.IO;

using System.Runtime.InteropServices;
using Jellyfin.Plugin.PlaylistGenerator.Configuration;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Playlists;
using Jellyfin.Plugin.PlaylistGenerator.Objects;
using MediaBrowser.Controller;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.PlaylistGenerator.Tasks;

public class PlaylistGenerationTask(ILibraryManager libraryManager, 
                                    IUserManager userManager, 
                                    IUserDataManager userDataManager, 
                                    ILogger<PlaylistGenerationTask> logManager,
                                    IPlaylistManager playlistManager,
                                    IServerApplicationPaths applicationPaths,
                                    IFileSystem fileSystem
                                    ) : IScheduledTask
{
    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly IUserManager _userManager = userManager;
    private readonly IUserDataManager _userDataManager = userDataManager;
    private readonly IPlaylistManager _playlistManager = playlistManager;
    private readonly ILogger<PlaylistGenerationTask> _logger = logManager;
    private readonly IServerApplicationPaths _paths = applicationPaths;
    private readonly IFileSystem _fileSystem = fileSystem;
    private ActivityDatabase _activityDatabase = null!;

    public string Name => "Generate Personal Playlist";
    public string Key => "PlaylistGenerationTask";
    public string Description => "Generate a playlist based on previous listen data + similarity.";
    public string Category => "Library";

    private List<ICollectionFolder?>? GetLibraries(User currentUser)
    {
        // get all libraries and then filter for music libraries
        var allFolders = _libraryManager.GetUserRootFolder()
            .GetChildren(currentUser, true)
            .OfType<Folder>()
            .ToList();
        
        
        var musicLibraries = allFolders.Select(folder => folder as ICollectionFolder)
            .Where(collectionFolder => collectionFolder?.CollectionType == CollectionType.music).ToList();
        
        var selectedLibraries = musicLibraries
            .Where(cf => Config.SelectedLibraryIds.Contains(cf!.Id))
            .ToList();
        
        if (selectedLibraries.Count <= 0)
        {
         _logger.LogInformation($"No library found for user: {currentUser.Username}.");
         return null;
        }
        _logger.LogInformation($"Generating playlist from libraries: {string.Join(", ", selectedLibraries.Select(l => l.Name))}");
        return selectedLibraries;
    }

    private List<BaseItem> GetAllSongs(List<ICollectionFolder?> selectedLibraries, CancellationToken token)
    {
        var allAudio = new List<BaseItem>();
        foreach (var library in selectedLibraries)
        {
            token.ThrowIfCancellationRequested();
            if (library == null)
            {
                _logger.LogWarning("No library here, skipping.");
                continue;
            }
            _logger.LogInformation($"Searching for songs in library: {library.Name}");
            var songQuery = new InternalItemsQuery{
                IncludeItemTypes = [BaseItemKind.Audio], 
                ParentId = library.Id,
                Recursive = true
            };
            var audioItems = _libraryManager.GetItemList(songQuery);
            allAudio.AddRange(audioItems);
        }

        return allAudio;
    }

    private List<ScoredSong> BuildPlaylist(List<BaseItem> songs, User currentUser, CancellationToken token)
    {
        // first get all songs
        var songList = new List<ScoredSong>();
        
        foreach (var song in songs)
        {
            token.ThrowIfCancellationRequested();
            songList.Add(new ScoredSong(song, currentUser, _userDataManager, _libraryManager, _activityDatabase));
        }

        // initialise the Recommenders and get some recommendations based on our top
        var experimentalFilter = Config.ExperimentalFilter;
        Recommender playlistRecommender = new(_libraryManager, _userDataManager, _activityDatabase, Config.ExplorationCoefficient);
        
        List<ScoredSong> topSongs = [.. songList.OrderByDescending(song => song.Score).Take(20)];
        var similarBySong = playlistRecommender.RecommendSimilar(topSongs, currentUser);
        var similarByGenre = playlistRecommender.RecommendByGenre(topSongs, currentUser, experimentalFilter);
        var similarByArtist = playlistRecommender.RecommendByArtist(topSongs, currentUser, experimentalFilter);
        var favouriteSongs = playlistRecommender.RecommendByFavourite(topSongs, currentUser);

        List<ScoredSong> allSongs = [..topSongs];
        allSongs.AddRange(similarBySong);
        allSongs.AddRange(similarByGenre);
        allSongs.AddRange(similarByArtist);
        allSongs.AddRange(favouriteSongs);
        
        // prune songs that are too short or have no ParentId 
        allSongs = allSongs.Where(song => (song.Song.RunTimeTicks ?? 0 / TimeSpan.TicksPerSecond) >= Config.ExcludeTime 
                                          && song.Song.ParentId != Guid.Empty).ToList();

        _logger.LogInformation($"Highest score: {allSongs[0].Score} for song: {allSongs[0].Song.Name}");
        var assembledPlaylist = PlaylistService.AssemblePlaylist(allSongs, Config.PlaylistDuration, 
            playlistRecommender, currentUser, token);
        assembledPlaylist = PlaylistService.GentleShuffle(assembledPlaylist, 10, true);
        return assembledPlaylist;
    }

    private void HandleJellyPlaylist(List<ScoredSong> assembledPlaylist, User currentUser)
    {
        // check if playlist exists
        var playlists = _libraryManager.GetItemList(new InternalItemsQuery{IncludeItemTypes = 
            [BaseItemKind.Playlist]});
        PlaylistService playlistServer = new(_playlistManager, _libraryManager);
        var allPlaylists = playlists.Cast<Playlist>().ToList();

        if (allPlaylists.Any(p => p.Name.Equals(Config.PlaylistName) && p.OwnerUserId == currentUser.Id)) 
        {
            _logger.LogInformation($"Playlist {Config.PlaylistName} of {currentUser.Username} exists. Overwriting.");
            playlistServer.RemovePlaylist(allPlaylists, Config.PlaylistName, currentUser.Id);
        }

        // make the playlist
        playlistServer.CreatePlaylist(Config.PlaylistName, currentUser, assembledPlaylist);
    }
    
    
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        try
        {
            _activityDatabase = new ActivityDatabase(_logger, _paths, _fileSystem, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while generating the playlist.");
            return Task.CompletedTask;
        }
        
        // get user to identify listen data
        using var reader = new StringReader(Config.PlaylistUserName);
        using var parser = new TextFieldParser(reader);

        parser.SetDelimiters(";");
        parser.HasFieldsEnclosedInQuotes = true;

        var users = parser.ReadFields();
        if (users == null)
        {
            _logger.LogError("No users found.");
            return Task.CompletedTask;
        }

        foreach (var user in users)
        {
            var currentUser = _userManager.GetUserByName(user);
            
            if (currentUser == null)
            {
                _logger.LogWarning($"User: {user} not found. Aborting.");
                continue;
            }
            _logger.LogInformation($"Start generating playlist with Exploration {Config.ExplorationCoefficient} " +
                                   $"for {currentUser.Username}");

            var selectedLibraries = GetLibraries(currentUser);
            if (selectedLibraries == null)
            {
                continue;
            }

            // search for songs in the music libraries
            var allAudio = GetAllSongs(selectedLibraries, cancellationToken);

            if (allAudio.Count <= 0)
            {
                _logger.LogWarning("No music found.");
                continue;
            }
        
            // filter out theme songs and songs that are too short
            var noThemeSongs = allAudio.Where(song => song.IsThemeMedia == false).ToList();
            var songs = noThemeSongs.Where(song => (int)((long)(song.RunTimeTicks ?? 0) / TimeSpan.TicksPerSecond) > Config.ExcludeTime).ToList();
        
            if (songs.Count <= 0)
            {
                _logger.LogWarning("No music found after filtering.");
                continue;
            }
    
            _logger.LogInformation($"Found {allAudio.Count} songs, filtering out theme songs and short songs...{songs.Count} remaining.");
            var assembledPlaylist = BuildPlaylist(songs, currentUser, cancellationToken);
        
            HandleJellyPlaylist(assembledPlaylist, currentUser);
            _logger.LogInformation($"Generated personal playlist for {currentUser.Username}.");   
        }
        
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            // Example trigger: Run every day at midnight
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(0).Ticks
            }
        ];
    }
}

