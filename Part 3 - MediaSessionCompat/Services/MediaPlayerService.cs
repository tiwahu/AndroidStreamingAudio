using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Android.Support.V4.App;
using Android.Support.V4.Media;
using Android.Support.V4.Media.Session;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BackgroundStreamingAudio.Services
{
    [Service]
    [IntentFilter(new[] { ActionPlay, ActionPause, ActionStop, ActionTogglePlayback, ActionNext, ActionPrevious })]
    public class MediaPlayerService : Service, AudioManager.IOnAudioFocusChangeListener,
    MediaPlayer.IOnBufferingUpdateListener,
    MediaPlayer.IOnCompletionListener,
    MediaPlayer.IOnErrorListener,
    MediaPlayer.IOnPreparedListener,
    MediaPlayer.IOnSeekCompleteListener
    {
        //Actions
        public const string ActionPlay = "com.xamarin.action.PLAY";
        public const string ActionPause = "com.xamarin.action.PAUSE";
        public const string ActionStop = "com.xamarin.action.STOP";
        public const string ActionTogglePlayback = "com.xamarin.action.TOGGLEPLAYBACK";
        public const string ActionNext = "com.xamarin.action.NEXT";
        public const string ActionPrevious = "com.xamarin.action.PREVIOUS";

        private const string audioUrl = @"https://archive.org/download/pmb2001-04-06.sr77.shnf/pmb2001-04-06d2/pmb2001-04-06d2t06.mp3";

        public MediaPlayer mediaPlayer;
        private AudioManager audioManager;

        private MediaSessionCompat mediaSessionCompat;
        public MediaControllerCompat mediaControllerCompat;

        public int MediaPlayerState
        {
            get
            {
                return (mediaControllerCompat.PlaybackState != null ?
                    mediaControllerCompat.PlaybackState.State :
                    PlaybackStateCompat.StateNone);
            }
        }


        private WifiManager wifiManager;
        private WifiManager.WifiLock wifiLock;
        private ComponentName remoteComponentName;

        private const int NotificationId = 1;

        public event StatusChangedEventHandler StatusChanged;

        public event CoverReloadedEventHandler CoverReloaded;

        public event PlayingEventHandler Playing;

        public event BufferingEventHandler Buffering;

        private Handler PlayingHandler;
        private Java.Lang.Runnable PlayingHandlerRunnable;

        public MediaPlayerService()
        {
            // Create an instance for a runnable-handler
            PlayingHandler = new Handler();

            // Create a runnable, restarting itself if the status still is "playing"
            PlayingHandlerRunnable = new Java.Lang.Runnable(() =>
            {
                this.OnPlaying(EventArgs.Empty);

                if (this.MediaPlayerState == PlaybackStateCompat.StatePlaying)
                {
                    PlayingHandler.PostDelayed(PlayingHandlerRunnable, 250);
                }
            });

            // On Status changed to PLAYING, start raising the Playing event
            StatusChanged += (object sender, EventArgs e) =>
            {
                if (this.MediaPlayerState == PlaybackStateCompat.StatePlaying)
                {
                    PlayingHandler.PostDelayed(PlayingHandlerRunnable, 0);
                }
            };
        }

        protected virtual void OnStatusChanged(EventArgs e)
        {
            if (StatusChanged != null)
                StatusChanged(this, e);
        }

        protected virtual void OnCoverReloaded(EventArgs e)
        {
            if (CoverReloaded != null)
            {
                CoverReloaded(this, e);
                this.StartNotification();
                this.UpdateMediaMetadataCompat();
            }
        }

        protected virtual void OnPlaying(EventArgs e)
        {
            if (Playing != null)
                Playing(this, e);
        }

        protected virtual void OnBuffering(EventArgs e)
        {
            if (Buffering != null)
                Buffering(this, e);
        }

        /// <summary>
        /// On create simply detect some of our managers
        /// </summary>
        public override void OnCreate()
        {
            base.OnCreate();
            //Find our audio and notificaton managers
            audioManager = (AudioManager)this.GetSystemService(AudioService);
            wifiManager = (WifiManager)this.GetSystemService(WifiService);

            remoteComponentName = new ComponentName(this.PackageName, new RemoteControlBroadcastReceiver().ComponentName);
        }

        /// <summary>
        /// Will register for the remote control client commands in audio manager
        /// </summary>
        private void InitMediaSession()
        {
            try
            {
                if (mediaSessionCompat == null)
                {
                    Intent nIntent = new Intent(this.ApplicationContext, typeof(MainActivity));
                    PendingIntent pIntent = PendingIntent.GetActivity(this.ApplicationContext, 0, nIntent, 0);

                    remoteComponentName = new ComponentName(this.PackageName, new RemoteControlBroadcastReceiver().ComponentName);

                    mediaSessionCompat = new MediaSessionCompat(this.ApplicationContext, "XamarinStreamingAudio", remoteComponentName, pIntent);
                    mediaControllerCompat = new MediaControllerCompat(this.ApplicationContext, mediaSessionCompat.SessionToken);
                }

                mediaSessionCompat.Active = true;
                mediaSessionCompat.SetCallback(new MediaSessionCallback((MediaPlayerServiceBinder)binder));

                mediaSessionCompat.SetFlags(MediaSessionCompat.FlagHandlesMediaButtons | MediaSessionCompat.FlagHandlesTransportControls);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// Intializes the player.
        /// </summary>
        private void InitializePlayer()
        {
            mediaPlayer = new MediaPlayer();

            //Tell our player to stream music
            //x mediaPlayer.SetAudioStreamType(Stream.Music);
            var aab = new AudioAttributes.Builder();
            aab.SetUsage(AudioUsageKind.Media);
            aab.SetContentType(AudioContentType.Music);
            mediaPlayer.SetAudioAttributes(aab.Build());

            //Wake mode will be partial to keep the CPU still running under lock screen
            mediaPlayer.SetWakeMode(this.ApplicationContext, WakeLockFlags.Partial);

            mediaPlayer.SetOnBufferingUpdateListener(this);
            mediaPlayer.SetOnCompletionListener(this);
            mediaPlayer.SetOnErrorListener(this);
            mediaPlayer.SetOnPreparedListener(this);
        }


        public void OnBufferingUpdate(MediaPlayer mp, int percent)
        {
            int duration = 0;
            if (this.MediaPlayerState == PlaybackStateCompat.StatePlaying || this.MediaPlayerState == PlaybackStateCompat.StatePaused)
                duration = mp.Duration;

            int newBufferedTime = duration * percent / 100;
            if (newBufferedTime != this.Buffered)
            {
                this.Buffered = newBufferedTime;
            }
        }

        public async void OnCompletion(MediaPlayer mp)
        {
            await this.PlayNext();
        }

        public bool OnError(MediaPlayer mp, MediaError what, int extra)
        {

            this.UpdatePlaybackState(PlaybackStateCompat.StateError);
            Task.Run(async () => await this.Stop());
            return true;
        }

        public void OnSeekComplete(MediaPlayer mp)
        {
            //TODO: Implement buffering on seeking
        }

        public void OnPrepared(MediaPlayer mp)
        {
            //Mediaplayer is prepared start track playback
            mp.Start();
            this.UpdatePlaybackState(PlaybackStateCompat.StatePlaying);
        }

        public int Position
        {
            get
            {
                if (mediaPlayer == null
                    || (this.MediaPlayerState != PlaybackStateCompat.StatePlaying
                        && this.MediaPlayerState != PlaybackStateCompat.StatePaused))
                    return -1;
                else
                    return mediaPlayer.CurrentPosition;
            }
        }

        public int Duration
        {
            get
            {
                if (mediaPlayer == null
                    || (this.MediaPlayerState != PlaybackStateCompat.StatePlaying
                        && this.MediaPlayerState != PlaybackStateCompat.StatePaused))
                    return 0;
                else
                    return mediaPlayer.Duration;
            }
        }

        private int buffered = 0;

        public int Buffered
        {
            get
            {
                if (mediaPlayer == null)
                    return 0;
                else
                    return buffered;
            }
            private set
            {
                buffered = value;
                this.OnBuffering(EventArgs.Empty);
            }
        }

        private Bitmap cover;

        public object Cover
        {
            get
            {
                if (cover == null)
                    cover = BitmapFactory.DecodeResource(this.Resources, Resource.Drawable.album_art);
                return cover;
            }
            private set
            {
                cover = value as Bitmap;
                this.OnCoverReloaded(EventArgs.Empty);
            }
        }

        /// <summary>
        /// Intializes the player.
        /// </summary>
        public async Task Play()
        {
            if (mediaPlayer != null && this.MediaPlayerState == PlaybackStateCompat.StatePaused)
            {
                //We are simply paused so just start again
                mediaPlayer.Start();
                this.UpdatePlaybackState(PlaybackStateCompat.StatePlaying);
                this.StartNotification();

                //Update the metadata now that we are playing
                this.UpdateMediaMetadataCompat();
                return;
            }

            if (mediaPlayer == null)
                this.InitializePlayer();

            if (mediaSessionCompat == null)
                this.InitMediaSession();

            if (mediaPlayer.IsPlaying)
            {
                this.UpdatePlaybackState(PlaybackStateCompat.StatePlaying);
                return;
            }

            try
            {
                MediaMetadataRetriever metaRetriever = new MediaMetadataRetriever();

                await mediaPlayer.SetDataSourceAsync(this.ApplicationContext, Android.Net.Uri.Parse(audioUrl));

                await metaRetriever.SetDataSourceAsync(audioUrl, new Dictionary<string, string>());

                //x var focusResult = audioManager.RequestAudioFocus(this, Stream.Music, AudioFocus.Gain);
                var afrcb = new AudioFocusRequestClass.Builder(AudioFocus.Gain);
                afrcb.SetOnAudioFocusChangeListener(this);

                var focusResult = audioManager.RequestAudioFocus(afrcb.Build());

                if (focusResult != AudioFocusRequest.Granted)
                {
                    //could not get audio focus
                    Console.WriteLine("Could not get audio focus");
                }

                this.UpdatePlaybackState(PlaybackStateCompat.StateBuffering);
                mediaPlayer.PrepareAsync();

                this.AquireWifiLock();
                this.UpdateMediaMetadataCompat(metaRetriever);
                this.StartNotification();

                byte[] imageByteArray = metaRetriever.GetEmbeddedPicture();
                if (imageByteArray == null)
                    this.Cover = await BitmapFactory.DecodeResourceAsync(this.Resources, Resource.Drawable.album_art);
                else
                    this.Cover = await BitmapFactory.DecodeByteArrayAsync(imageByteArray, 0, imageByteArray.Length);
            }
            catch (Exception ex)
            {
                this.UpdatePlaybackState(PlaybackStateCompat.StateStopped);

                mediaPlayer.Reset();
                mediaPlayer.Release();
                mediaPlayer = null;

                //unable to start playback log error
                Console.WriteLine(ex);
            }
        }

        public async Task Seek(int position)
        {
            await Task.Run(() =>
            {
                if (mediaPlayer != null)
                {
                    mediaPlayer.SeekTo(position);
                }
            });
        }

        public async Task PlayNext()
        {
            if (mediaPlayer != null)
            {
                mediaPlayer.Reset();
                mediaPlayer.Release();
                mediaPlayer = null;
            }

            this.UpdatePlaybackState(PlaybackStateCompat.StateSkippingToNext);

            await this.Play();
        }

        public async Task PlayPrevious()
        {
            // Start current track from beginning if it's the first track or the track has played more than 3sec and you hit "playPrevious".
            if (this.Position > 3000)
            {
                await this.Seek(0);
            }
            else
            {
                if (mediaPlayer != null)
                {
                    mediaPlayer.Reset();
                    mediaPlayer.Release();
                    mediaPlayer = null;
                }

                this.UpdatePlaybackState(PlaybackStateCompat.StateSkippingToPrevious);

                await this.Play();
            }
        }

        public async Task PlayPause()
        {
            if (mediaPlayer == null || (mediaPlayer != null && this.MediaPlayerState == PlaybackStateCompat.StatePaused))
            {
                await this.Play();
            }
            else
            {
                await this.Pause();
            }
        }

        public async Task Pause()
        {
            await Task.Run(() =>
            {
                if (mediaPlayer == null)
                    return;

                if (mediaPlayer.IsPlaying)
                    mediaPlayer.Pause();

                this.UpdatePlaybackState(PlaybackStateCompat.StatePaused);
            });
        }

        public async Task Stop()
        {
            await Task.Run(() =>
            {
                if (mediaPlayer == null)
                    return;

                if (mediaPlayer.IsPlaying)
                {
                    mediaPlayer.Stop();
                }

                this.UpdatePlaybackState(PlaybackStateCompat.StateStopped);
                mediaPlayer.Reset();
                this.StopNotification();
                this.StopForeground(true);
                this.ReleaseWifiLock();
                this.UnregisterMediaSessionCompat();
            });
        }

        private void UpdatePlaybackState(int state)
        {
            if (mediaSessionCompat == null || mediaPlayer == null)
                return;

            try
            {
                PlaybackStateCompat.Builder stateBuilder = new PlaybackStateCompat.Builder()
                    .SetActions(
                        PlaybackStateCompat.ActionPause |
                        PlaybackStateCompat.ActionPlay |
                        PlaybackStateCompat.ActionPlayPause |
                        PlaybackStateCompat.ActionSkipToNext |
                        PlaybackStateCompat.ActionSkipToPrevious |
                        PlaybackStateCompat.ActionStop
                    )
                    .SetState(state, this.Position, 1.0f, SystemClock.ElapsedRealtime());

                mediaSessionCompat.SetPlaybackState(stateBuilder.Build());

                //Used for backwards compatibility
                //if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop)
                //{
                //    if (mediaSessionCompat.RemoteControlClient != null && mediaSessionCompat.RemoteControlClient.Equals(typeof(RemoteControlClient)))
                //    {
                //        RemoteControlClient remoteControlClient = (RemoteControlClient)mediaSessionCompat.RemoteControlClient;

                //        RemoteControlFlags flags = RemoteControlFlags.Play
                //            | RemoteControlFlags.Pause
                //            | RemoteControlFlags.PlayPause
                //            | RemoteControlFlags.Previous
                //            | RemoteControlFlags.Next
                //            | RemoteControlFlags.Stop;

                //        remoteControlClient.SetTransportControlFlags(flags);
                //    }
                //}

                this.OnStatusChanged(EventArgs.Empty);

                if (state == PlaybackStateCompat.StatePlaying || state == PlaybackStateCompat.StatePaused)
                {
                    this.StartNotification();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// When we start on the foreground we will present a notification to the user
        /// When they press the notification it will take them to the main page so they can control the music
        /// </summary>
        private void StartNotification()
        {
            if (mediaSessionCompat == null)
                return;

            var pendingIntent = PendingIntent.GetActivity(this.ApplicationContext, 0, new Intent(this.ApplicationContext, typeof(MainActivity)), PendingIntentFlags.UpdateCurrent);
            MediaMetadataCompat currentTrack = mediaControllerCompat.Metadata;

            Android.Support.V7.App.NotificationCompat.MediaStyle style = new Android.Support.V7.App.NotificationCompat.MediaStyle();
            style.SetMediaSession(mediaSessionCompat.SessionToken);

            Intent intent = new Intent(this.ApplicationContext, typeof(MediaPlayerService));
            intent.SetAction(ActionStop);
            PendingIntent pendingCancelIntent = PendingIntent.GetService(this.ApplicationContext, 1, intent, PendingIntentFlags.CancelCurrent);

            style.SetShowCancelButton(true);
            style.SetCancelButtonIntent(pendingCancelIntent);

            NotificationCompat.Builder builder = new NotificationCompat.Builder(this.ApplicationContext)
                .SetStyle(style)
                .SetContentTitle(currentTrack.GetString(MediaMetadata.MetadataKeyTitle))
                .SetContentText(currentTrack.GetString(MediaMetadata.MetadataKeyArtist))
                .SetContentInfo(currentTrack.GetString(MediaMetadata.MetadataKeyAlbum))
                .SetSmallIcon(Resource.Drawable.album_art)
                .SetLargeIcon(this.Cover as Bitmap)
                .SetContentIntent(pendingIntent)
                .SetShowWhen(false)
                .SetOngoing(this.MediaPlayerState == PlaybackStateCompat.StatePlaying)
                .SetVisibility(NotificationCompat.VisibilityPublic);

            builder.AddAction(this.GenerateActionCompat(Android.Resource.Drawable.IcMediaPrevious, "Previous", ActionPrevious));
            this.AddPlayPauseActionCompat(builder);
            builder.AddAction(this.GenerateActionCompat(Android.Resource.Drawable.IcMediaNext, "Next", ActionNext));
            style.SetShowActionsInCompactView(0, 1, 2);

            NotificationManagerCompat.From(this.ApplicationContext).Notify(NotificationId, builder.Build());
        }

        private NotificationCompat.Action GenerateActionCompat(int icon, String title, String intentAction)
        {
            Intent intent = new Intent(this.ApplicationContext, typeof(MediaPlayerService));
            intent.SetAction(intentAction);

            PendingIntentFlags flags = PendingIntentFlags.UpdateCurrent;
            if (intentAction.Equals(ActionStop))
                flags = PendingIntentFlags.CancelCurrent;

            PendingIntent pendingIntent = PendingIntent.GetService(this.ApplicationContext, 1, intent, flags);

            return new NotificationCompat.Action.Builder(icon, title, pendingIntent).Build();
        }

        private void AddPlayPauseActionCompat(NotificationCompat.Builder builder)
        {
            if (this.MediaPlayerState == PlaybackStateCompat.StatePlaying)
                builder.AddAction(this.GenerateActionCompat(Android.Resource.Drawable.IcMediaPause, "Pause", ActionPause));
            else
                builder.AddAction(this.GenerateActionCompat(Android.Resource.Drawable.IcMediaPlay, "Play", ActionPlay));
        }

        public void StopNotification()
        {
            NotificationManagerCompat nm = NotificationManagerCompat.From(this.ApplicationContext);
            nm.CancelAll();
        }

        /// <summary>
        /// Updates the metadata on the lock screen
        /// </summary>
        private void UpdateMediaMetadataCompat(MediaMetadataRetriever metaRetriever = null)
        {
            if (mediaSessionCompat == null)
                return;

            MediaMetadataCompat.Builder builder = new MediaMetadataCompat.Builder();

            if (metaRetriever != null)
            {
                builder
                .PutString(MediaMetadata.MetadataKeyAlbum, metaRetriever.ExtractMetadata(MetadataKey.Album))
                .PutString(MediaMetadata.MetadataKeyArtist, metaRetriever.ExtractMetadata(MetadataKey.Artist))
                .PutString(MediaMetadata.MetadataKeyTitle, metaRetriever.ExtractMetadata(MetadataKey.Title));
            }
            else
            {
                builder
                    .PutString(MediaMetadata.MetadataKeyAlbum, mediaSessionCompat.Controller.Metadata.GetString(MediaMetadata.MetadataKeyAlbum))
                    .PutString(MediaMetadata.MetadataKeyArtist, mediaSessionCompat.Controller.Metadata.GetString(MediaMetadata.MetadataKeyArtist))
                    .PutString(MediaMetadata.MetadataKeyTitle, mediaSessionCompat.Controller.Metadata.GetString(MediaMetadata.MetadataKeyTitle));
            }
            builder.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, this.Cover as Bitmap);

            mediaSessionCompat.SetMetadata(builder.Build());
        }

        [Obsolete("deprecated")]
        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            this.HandleIntent(intent);
            return base.OnStartCommand(intent, flags, startId);
        }

        private void HandleIntent(Intent intent)
        {
            if (intent == null || intent.Action == null)
                return;

            String action = intent.Action;

            if (action.Equals(ActionPlay))
            {
                mediaControllerCompat.GetTransportControls().Play();
            }
            else if (action.Equals(ActionPause))
            {
                mediaControllerCompat.GetTransportControls().Pause();
            }
            else if (action.Equals(ActionPrevious))
            {
                mediaControllerCompat.GetTransportControls().SkipToPrevious();
            }
            else if (action.Equals(ActionNext))
            {
                mediaControllerCompat.GetTransportControls().SkipToNext();
            }
            else if (action.Equals(ActionStop))
            {
                mediaControllerCompat.GetTransportControls().Stop();
            }
        }

        /// <summary>
        /// Lock the wifi so we can still stream under lock screen
        /// </summary>
        private void AquireWifiLock()
        {
            if (wifiLock == null)
            {
                wifiLock = wifiManager.CreateWifiLock(WifiMode.Full, "xamarin_wifi_lock");
            }
            wifiLock.Acquire();
        }

        /// <summary>
        /// This will release the wifi lock if it is no longer needed
        /// </summary>
        private void ReleaseWifiLock()
        {
            if (wifiLock == null)
                return;

            wifiLock.Release();
            wifiLock = null;
        }

        private void UnregisterMediaSessionCompat()
        {
            try
            {
                if (mediaSessionCompat != null)
                {
                    mediaSessionCompat.Dispose();
                    mediaSessionCompat = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        IBinder binder;

        public override IBinder OnBind(Intent intent)
        {
            binder = new MediaPlayerServiceBinder(this);
            return binder;
        }

        public override bool OnUnbind(Intent intent)
        {
            this.StopNotification();
            return base.OnUnbind(intent);
        }

        /// <summary>
        /// Properly cleanup of your player by releasing resources
        /// </summary>
        public override void OnDestroy()
        {
            base.OnDestroy();
            if (mediaPlayer != null)
            {
                mediaPlayer.Release();
                mediaPlayer = null;

                this.StopNotification();
                this.StopForeground(true);
                this.ReleaseWifiLock();
                this.UnregisterMediaSessionCompat();
            }
        }

        /// <summary>
        /// For a good user experience we should account for when audio focus has changed.
        /// There is only 1 audio output there may be several media services trying to use it so
        /// we should act correctly based on this.  "duck" to be quiet and when we gain go full.
        /// All applications are encouraged to follow this, but are not enforced.
        /// </summary>
        /// <param name="focusChange"></param>
        public async void OnAudioFocusChange(AudioFocus focusChange)
        {
            switch (focusChange)
            {
                case AudioFocus.Gain:
                    if (mediaPlayer == null)
                        this.InitializePlayer();

                    if (!mediaPlayer.IsPlaying)
                    {
                        mediaPlayer.Start();
                    }

                    mediaPlayer.SetVolume(1.0f, 1.0f);//Turn it up!
                    break;
                case AudioFocus.Loss:
                    //We have lost focus stop!
                    await this.Stop();
                    break;
                case AudioFocus.LossTransient:
                    //We have lost focus for a short time, but likely to resume so pause
                    await this.Pause();
                    break;
                case AudioFocus.LossTransientCanDuck:
                    //We have lost focus but should till play at a muted 10% volume
                    if (mediaPlayer.IsPlaying)
                        mediaPlayer.SetVolume(.1f, .1f);//turn it down!
                    break;

            }
        }

        public class MediaSessionCallback : MediaSessionCompat.Callback
        {

            private MediaPlayerServiceBinder mediaPlayerService;

            public MediaSessionCallback(MediaPlayerServiceBinder service)
            {
                mediaPlayerService = service;
            }

            public override async void OnPause()
            {
                await mediaPlayerService.GetMediaPlayerService().Pause();
                base.OnPause();
            }

            public override async void OnPlay()
            {
                await mediaPlayerService.GetMediaPlayerService().Play();
                base.OnPlay();
            }

            public override async void OnSkipToNext()
            {
                await mediaPlayerService.GetMediaPlayerService().PlayNext();
                base.OnSkipToNext();
            }

            public override async void OnSkipToPrevious()
            {
                await mediaPlayerService.GetMediaPlayerService().PlayPrevious();
                base.OnSkipToPrevious();
            }

            public override async void OnStop()
            {
                await mediaPlayerService.GetMediaPlayerService().Stop();
                base.OnStop();
            }
        }
    }

    public class MediaPlayerServiceBinder : Binder
    {
        private MediaPlayerService service;

        public MediaPlayerServiceBinder(MediaPlayerService service)
        {
            this.service = service;
        }

        public MediaPlayerService GetMediaPlayerService()
        {
            return service;
        }
    }
}
