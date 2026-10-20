using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace DeskShell.Services;

public sealed record NowPlaying(string Title, string Artist, bool IsPlaying, BitmapImage? Art);

/// <summary>Follows whatever app Windows considers the current media session (Spotify, browsers, etc.).</summary>
public sealed class MediaService
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>Raised on a background thread whenever the track or play state changes.</summary>
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => AttachSession();
            AttachSession();
        }
        catch (Exception ex)
        {
            Log.Error("Media sessions are unavailable", ex);
        }
    }

    void AttachSession()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
        }

        _session = _manager?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.PlaybackInfoChanged += OnSessionChanged;
        }
        Changed?.Invoke();
    }

    void OnSessionChanged(GlobalSystemMediaTransportControlsSession s, object e) => Changed?.Invoke();

    public async Task<NowPlaying?> GetAsync()
    {
        var session = _session;
        if (session == null) return null;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;

            var playing = session.GetPlaybackInfo()?.PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            BitmapImage? art = null;
            if (props.Thumbnail != null)
            {
                using var winStream = await props.Thumbnail.OpenReadAsync();
                var buffer = new MemoryStream();
                await winStream.AsStreamForRead().CopyToAsync(buffer);
                buffer.Position = 0;
                art = new BitmapImage();
                art.BeginInit();
                art.CacheOption = BitmapCacheOption.OnLoad;
                art.DecodePixelWidth = 128;
                art.StreamSource = buffer;
                art.EndInit();
                art.Freeze();
            }

            return new NowPlaying(props.Title, props.Artist ?? "", playing, art);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the current media", ex);
            return null;
        }
    }

    public Task PlayPauseAsync() => _session?.TryTogglePlayPauseAsync().AsTask() ?? Task.CompletedTask;
    public Task NextAsync() => _session?.TrySkipNextAsync().AsTask() ?? Task.CompletedTask;
    public Task PreviousAsync() => _session?.TrySkipPreviousAsync().AsTask() ?? Task.CompletedTask;
}
