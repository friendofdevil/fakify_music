using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FakeifyCSharp
{
    public partial class MainWindow : Window
    {
        // Storage paths. The folder is configurable in Settings; the choice is remembered in AppData/Fakeify/settings.json
        private string MUSIC_FOLDER = string.Empty;
        private string PLAYLISTS_FILE = string.Empty;
        private string LIBRARY_ORDER_FILE = string.Empty;
        private string QUEUE_FILE = string.Empty;
        private string TRACK_CACHE_FILE = string.Empty;   // remembers which downloaded file belongs to which "title|artist"

        // Icon glyphs (Segoe Fluent Icons / Segoe MDL2 Assets)
        private const string G_PLAY = "\uE768", G_PAUSE = "\uE769", G_ADD = "\uE710", G_DELETE = "\uE74D",
                             G_UP = "\uE70E", G_DOWN = "\uE70D", G_REMOVE = "\uE711", G_FOLDER = "\uE8B7",
                             G_SHUFFLE = "\uE8B1", G_NOTE = "\uE8D6",
                             G_MUTE = "\uE74F", G_VOL1 = "\uE993", G_VOL2 = "\uE994", G_VOL3 = "\uE995";
        private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        // Data Models
        public class Track
        {
            public string FilePath { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
        }

        private Dictionary<string, List<Track>> _playlists = new();
        private List<string> _libraryOrder = new();
        private List<Track> _songQueue = new();
        private List<string>? _cachedLibrary;
        private Dictionary<string, string> _trackCache = new();   // "normalized title|normalized artist" -> mp3 file name
        private string _lastDownloadError = "";

        // Playback & State Engine
        private readonly MediaPlayer _player = new();
        private readonly DispatcherTimer _timer = new();
        private readonly Random _rng = new();
        private bool _isPlaying = false;          // audio is currently running (not paused)
        private bool _isActive = false;           // a track is loaded and hasn't finished/stopped (running OR paused)
        private bool _isDraggingSlider = false;
        private bool _isShuffleMode = false;
        private bool _isRepeat = false;           // repeat current song
        private double _volumeBeforeMute = 0.7;
        private Track? _currentTrack;
        private readonly List<Track> _history = new();   // previously played tracks, for the Previous button
        private string _currentView = "library";
        private string _localSearchQuery = "";
        private int _renderVersion = 0;           // lets a newer RenderView() cancel an older one that is still adding rows
        private int _busyCount = 0;               // running downloads/imports (the saving directory can't change meanwhile)
        private TaskCompletionSource<int>? _dialogTcs;   // the in-app dialog that is currently open

        public MainWindow()
        {
            InitializeComponent();

            // Storage folder (configurable in Settings)
            string folder = LoadMusicFolderSetting();
            try { Directory.CreateDirectory(folder); }
            catch { folder = DefaultMusicFolder(); Directory.CreateDirectory(folder); }
            ApplyStoragePaths(folder);

            // dropping a song on "Queue" in the sidebar queues it
            MakeDropTarget(BtnQueueView, p => AddToQueue(p.FilePath, p.Title));

            // Audio Player Configuration
            _player.Volume = 0.7;
            _player.MediaEnded += Player_MediaEnded;
            _player.MediaOpened += Player_MediaOpened;
            _player.MediaFailed += Player_MediaFailed;

            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            // Persistent Data Load & Startup Sequence
            LoadData();
            Task.Run(() => StartupCacheCheck());
        }

        #region Startup Cache Check & Data Persistence
        private async Task StartupCacheCheck()
        {
            await Dispatcher.InvokeAsync(() => LblLoadingStatus.Text = "Scanning folder & converting media files...");
            ScanAndConvertFolderMp4s();

            await Dispatcher.InvokeAsync(() => LblLoadingStatus.Text = "Rebuilding local library cache...");
            GetOrderedLibrarySongs(forceReload: true);

            await Dispatcher.InvokeAsync(() => LblLoadingStatus.Text = "Validating playlists and queue...");
            ValidateData();

            await Task.Delay(300);

            await Dispatcher.InvokeAsync(() =>
            {
                RenderSidebarPlaylists();
                ShowLocalLibrary_Click();

                // fade the loading screen out
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
                fade.Completed += (s, e) => LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingOverlay.BeginAnimation(OpacityProperty, fade);
            });
        }

        private void LoadData()
        {
            if (File.Exists(PLAYLISTS_FILE))
                _playlists = JsonSerializer.Deserialize<Dictionary<string, List<Track>>>(File.ReadAllText(PLAYLISTS_FILE)) ?? new();
            if (File.Exists(LIBRARY_ORDER_FILE))
                _libraryOrder = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(LIBRARY_ORDER_FILE)) ?? new();
            if (File.Exists(QUEUE_FILE))
                _songQueue = JsonSerializer.Deserialize<List<Track>>(File.ReadAllText(QUEUE_FILE)) ?? new();
            if (File.Exists(TRACK_CACHE_FILE))
            {
                try { _trackCache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(TRACK_CACHE_FILE)) ?? new(); }
                catch { _trackCache = new(); }
            }
        }

        private void SaveData()
        {
            File.WriteAllText(PLAYLISTS_FILE, JsonSerializer.Serialize(_playlists));
            File.WriteAllText(LIBRARY_ORDER_FILE, JsonSerializer.Serialize(_libraryOrder));
            File.WriteAllText(QUEUE_FILE, JsonSerializer.Serialize(_songQueue));
            UpdateQueueUi();
        }

        private void SaveTrackCache()
        {
            try { File.WriteAllText(TRACK_CACHE_FILE, JsonSerializer.Serialize(_trackCache)); } catch { }
        }

        /// <summary>Updates the "Queue (n)" sidebar label and the "Up next" line in the player bar.</summary>
        private void UpdateQueueUi()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(UpdateQueueUi);
                return;
            }
            LblQueueNav.Text = $"Queue ({_songQueue.Count})";
            LblNowSub.Text = _songQueue.Count > 0 ? $"Up next: {_songQueue[0].Title}" : "Queue is empty";
        }

        private void ValidateData()
        {
            foreach (string key in _playlists.Keys.ToList())
            {
                var validTracks = new List<Track>();
                foreach (var t in _playlists[key])
                {
                    string correctedPath = t.FilePath;
                    if (!File.Exists(correctedPath))
                    {
                        string fileName = Path.GetFileName(t.FilePath);
                        string alternativePath = Path.Combine(MUSIC_FOLDER, fileName);
                        if (File.Exists(alternativePath))
                        {
                            correctedPath = alternativePath;
                        }
                    }

                    if (File.Exists(correctedPath))
                    {
                        validTracks.Add(new Track { FilePath = correctedPath, Title = t.Title });
                    }
                }
                _playlists[key] = validTracks;
            }
            _songQueue = _songQueue.Where(t => File.Exists(t.FilePath)).ToList();
            SaveData();
        }

        private List<string> GetOrderedLibrarySongs(bool forceReload = false)
        {
            if (_cachedLibrary != null && !forceReload) return _cachedLibrary;

            List<string> actualSongsList = Directory.GetFiles(MUSIC_FOLDER, "*.mp3")
                                                  .Select(Path.GetFileName)
                                                  .OfType<string>()
                                                  .ToList();
            HashSet<string> actualSongsSet = new(actualSongsList);

            List<string> ordered = _libraryOrder.Where(actualSongsSet.Contains).ToList();
            HashSet<string> orderedSet = new(ordered);

            foreach (string f in actualSongsList.OrderBy(x => x))
            {
                if (orderedSet.Add(f)) ordered.Add(f);
            }

            _libraryOrder = ordered;
            _cachedLibrary = new List<string>(_libraryOrder);
            SaveData();
            return _cachedLibrary;
        }
        #endregion

        #region UI helpers
        private Brush Res(string key) => (Brush)FindResource(key);

        /// <summary>Round glass icon button used in song rows.</summary>
        private Button MakeIconButton(string glyph, string tooltip, string styleKey = "IconButton")
        {
            return new Button
            {
                Content = glyph,
                ToolTip = tooltip,
                Style = (Style)FindResource(styleKey),
                Margin = new Thickness(2, 0, 2, 0)
            };
        }

        /// <summary>Glossy pill button with an icon + label (header actions).</summary>
        private Button MakeActionButton(string glyph, string text, string styleKey)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return new Button { Content = sp, Style = (Style)FindResource(styleKey), Height = 36, Margin = new Thickness(8, 0, 0, 0) };
        }

        private UIElement CreateEmptyState(string text)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 70, 0, 0) };
            sp.Children.Add(new TextBlock { Text = G_NOTE, FontFamily = IconFont, FontSize = 56, Foreground = Res("InkSoft"), Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = Res("InkSoft"),
                FontSize = 14,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
                Margin = new Thickness(0, 14, 0, 0)
            });
            return sp;
        }

        private static string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

        private bool IsCurrent(string filePath) =>
            _currentTrack != null && string.Equals(_currentTrack.FilePath, filePath, StringComparison.OrdinalIgnoreCase);

        /// <summary>Highlights the nav item that matches the current view.</summary>
        private void UpdateNavHighlight()
        {
            BtnLibraryView.Tag = _currentView == "library" ? "active" : null;
            BtnQueueView.Tag = _currentView == "queue" ? "active" : null;
            foreach (Button b in PlaylistsPanel.Children.OfType<Button>())
                b.Tag = (b.CommandParameter as string) == _currentView ? "active" : null;
        }

        /// <summary>Highlights the row(s) of the song that is playing, without rebuilding the list.</summary>
        private void UpdateNowPlayingHighlight()
        {
            foreach (Border row in TrackListItems.Items.OfType<Border>())
                if (row.DataContext is DragPayload d) row.Tag = IsCurrent(d.FilePath) ? "playing" : null;
        }

        private void UpdatePlayPauseIcon() => BtnPlayPause.Content = _isPlaying ? G_PAUSE : G_PLAY;
        #endregion

        #region Async View Rendering
        private async void RenderView()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RenderView);
                return;
            }

            int version = ++_renderVersion;

            TrackListItems.Items.Clear();
            HeaderActionPanel.Children.Clear();
            UpdateNavHighlight();

            List<UIElement> itemsToAdd = new();
            int totalCount = 0;
            bool filtering = !string.IsNullOrEmpty(_localSearchQuery);
            string emptyText = "Nothing here yet.";

            if (_currentView == "library")
            {
                LblViewHeader.Text = "Local Library";
                Button btnShuffle = MakeActionButton(G_SHUFFLE, "Shuffle Play", "AccentButton");
                btnShuffle.Click += (s, e) => ShuffleLocalLibrary();
                HeaderActionPanel.Children.Add(btnShuffle);

                List<string> songs = GetOrderedLibrarySongs();
                totalCount = songs.Count;
                emptyText = filtering ? "No songs match your filter."
                                      : "Your library is empty.\nSearch YouTube above, or import MP3/MP4 files and CSV playlists from the sidebar.";

                for (int i = 0; i < songs.Count; i++)
                {
                    string filename = songs[i];
                    string title = Path.GetFileNameWithoutExtension(filename);
                    if (filtering && !title.ToLower().Contains(_localSearchQuery)) continue;

                    itemsToAdd.Add(CreateTrackRow(i, title, Path.Combine(MUSIC_FOLDER, filename), "library"));
                }
            }
            else if (_currentView == "queue")
            {
                LblViewHeader.Text = "Up Next";

                Button btnClear = MakeActionButton(G_DELETE, "Clear Queue", "DangerButton");
                btnClear.Click += (s, e) => { _songQueue.Clear(); SaveData(); RenderView(); };
                btnClear.IsEnabled = _songQueue.Count > 0;
                HeaderActionPanel.Children.Add(btnClear);

                totalCount = _songQueue.Count;
                emptyText = filtering ? "No songs match your filter."
                                      : "The queue is empty.\nUse the Queue button on any song to line it up.";

                for (int i = 0; i < _songQueue.Count; i++)
                {
                    if (filtering && !_songQueue[i].Title.ToLower().Contains(_localSearchQuery)) continue;
                    itemsToAdd.Add(CreateTrackRow(i, _songQueue[i].Title, _songQueue[i].FilePath, "queue"));
                }
            }
            else if (_currentView.StartsWith("playlist:"))
            {
                string plName = _currentView.Substring("playlist:".Length);   // (Split(':') broke names that contain a colon)
                LblViewHeader.Text = plName;

                Button btnPlayAll = MakeActionButton(G_PLAY, "Play All", "AccentButton");
                btnPlayAll.Click += (s, e) =>
                {
                    if (!_playlists.TryGetValue(plName, out var pl) || pl.Count == 0) return;
                    _songQueue = _isShuffleMode ? pl.OrderBy(_ => _rng.Next()).ToList() : new List<Track>(pl);
                    SaveData();
                    SkipAudio_Click();
                };

                Button btnQueueAll = MakeActionButton(G_ADD, "Queue All", "GlassButton");
                btnQueueAll.Click += (s, e) =>
                {
                    if (!_playlists.TryGetValue(plName, out var pl) || pl.Count == 0) return;
                    _songQueue.AddRange(pl);
                    SaveData();
                    if (!_isActive) SkipAudio_Click();
                    SetStatus($"Added {pl.Count} songs to queue.");
                };

                Button btnDel = MakeActionButton(G_DELETE, "Delete", "DangerButton");
                btnDel.Click += async (s, e) =>
                {
                    if (!await ConfirmAsync("Delete playlist", $"Delete \"{plName}\"?\nThe songs stay in your library.", "Delete", danger: true)) return;
                    _playlists.Remove(plName);
                    SaveData();
                    RenderSidebarPlaylists();
                    ShowLocalLibrary_Click();
                };

                HeaderActionPanel.Children.Add(btnPlayAll);
                HeaderActionPanel.Children.Add(btnQueueAll);
                HeaderActionPanel.Children.Add(btnDel);

                List<Track> tracks = _playlists.ContainsKey(plName) ? _playlists[plName] : new List<Track>();
                totalCount = tracks.Count;
                emptyText = filtering ? "No songs match your filter."
                                      : "This playlist is empty.\nUse the folder button on a song to add it here.";

                for (int i = 0; i < tracks.Count; i++)
                {
                    if (filtering && !tracks[i].Title.ToLower().Contains(_localSearchQuery)) continue;
                    itemsToAdd.Add(CreateTrackRow(i, tracks[i].Title, tracks[i].FilePath, "playlist", plName));
                }
            }

            LblViewHeader.ToolTip = LblViewHeader.Text;
            LblViewCount.Text = filtering
                ? $"{itemsToAdd.Count} of {totalCount}"
                : $"{totalCount} song{(totalCount == 1 ? "" : "s")}";

            if (itemsToAdd.Count == 0)
            {
                TrackListItems.Items.Add(CreateEmptyState(emptyText));
                return;
            }

            for (int i = 0; i < itemsToAdd.Count; i++)
            {
                TrackListItems.Items.Add(itemsToAdd[i]);
                if (i % 50 == 0)
                {
                    await Task.Delay(1);
                    // if RenderView() was called again while we waited, the newer call owns the list now
                    if (version != _renderVersion) return;
                }
            }
        }

        private Border CreateTrackRow(int index, string title, string filepath, string context, string plName = "")
        {
            var payload = new DragPayload { FilePath = filepath, Title = title, Context = context, PlaylistName = plName, Index = index };
            var row = new Border { Style = (Style)FindResource("TrackRow"), DataContext = payload };
            row.Tag = IsCurrent(filepath) ? "playing" : null;

            var grid = new Grid { Margin = new Thickness(6, 4, 8, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // number
            grid.Children.Add(new TextBlock
            {
                Text = (index + 1).ToString(),
                Foreground = Res("InkSoft"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            // little glossy "album art" tile
            var art = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(10),
                Background = Res("BlueFill"),
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#9908476F")!,
                BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = G_NOTE, FontFamily = IconFont, FontSize = 14, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            Grid.SetColumn(art, 1);
            grid.Children.Add(art);

            // title
            var lbl = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("Ink"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 12, 0),
                ToolTip = title
            };
            Grid.SetColumn(lbl, 2);
            grid.Children.Add(lbl);

            // actions (fade in while hovering the row)
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = (row.Tag as string) == "playing" ? 1 : 0 };

            Button btnPlay = MakeIconButton(G_PLAY, "Play now");
            btnPlay.Click += (s, e) => PlayFile(filepath, title);

            Button btnQueue = MakeIconButton(G_ADD, "Add to queue");
            btnQueue.Click += (s, e) => AddToQueue(filepath, title);

            actions.Children.Add(btnPlay);
            actions.Children.Add(btnQueue);

            // Add To Playlist menu (themed popup)
            Button btnAddToPl = MakeIconButton(G_FOLDER, "Add to playlist");
            btnAddToPl.Click += (s, e) =>
            {
                var cm = new ContextMenu { Style = (Style)FindResource("AeroContextMenu"), PlacementTarget = btnAddToPl, Placement = PlacementMode.Bottom };

                foreach (string pl in _playlists.Keys)
                {
                    var mi = new MenuItem { Header = MenuLabel(pl), Icon = MenuGlyph(G_NOTE), Style = (Style)FindResource("AeroMenuItem") };
                    mi.Click += (ms, me) => AddTrackToPlaylist(pl, filepath, title);
                    cm.Items.Add(mi);
                }
                if (_playlists.Count == 0)
                    cm.Items.Add(new MenuItem { Header = MenuLabel("No playlists yet"), IsEnabled = false, Style = (Style)FindResource("AeroMenuItem") });

                var miNew = new MenuItem { Header = MenuLabel("New playlist..."), Icon = MenuGlyph(G_ADD), Style = (Style)FindResource("AeroMenuItem") };
                miNew.Click += async (ms, me) =>
                {
                    string? created = await PromptAndCreatePlaylistAsync();
                    if (created != null) AddTrackToPlaylist(created, filepath, title);
                };
                cm.Items.Add(miNew);

                btnAddToPl.ContextMenu = cm;
                cm.IsOpen = true;
            };
            actions.Children.Add(btnAddToPl);

            if (context == "library")
            {
                Button btnDel = MakeIconButton(G_DELETE, "Delete from library", "DangerIconButton");
                btnDel.Click += (s, e) => DeleteLocalFile(filepath);
                actions.Children.Add(btnDel);
            }
            else if (context == "playlist")
            {
                Button btnUp = MakeIconButton(G_UP, "Move up");
                btnUp.Click += (s, e) => MovePlaylistSong(plName, index, -1);

                Button btnDown = MakeIconButton(G_DOWN, "Move down");
                btnDown.Click += (s, e) => MovePlaylistSong(plName, index, 1);

                Button btnRm = MakeIconButton(G_REMOVE, "Remove from playlist", "DangerIconButton");
                btnRm.Click += (s, e) => { _playlists[plName].RemoveAt(index); SaveData(); RenderView(); };

                actions.Children.Add(btnUp);
                actions.Children.Add(btnDown);
                actions.Children.Add(btnRm);
            }
            else if (context == "queue")
            {
                Button btnUp = MakeIconButton(G_UP, "Move up");
                btnUp.Click += (s, e) => MoveQueueSong(index, -1);

                Button btnDown = MakeIconButton(G_DOWN, "Move down");
                btnDown.Click += (s, e) => MoveQueueSong(index, 1);

                Button btnRm = MakeIconButton(G_REMOVE, "Remove from queue", "DangerIconButton");
                btnRm.Click += (s, e) => { _songQueue.RemoveAt(index); SaveData(); RenderView(); };

                actions.Children.Add(btnUp);
                actions.Children.Add(btnDown);
                actions.Children.Add(btnRm);
            }

            Grid.SetColumn(actions, 3);
            grid.Children.Add(actions);
            row.Child = grid;

            row.MouseEnter += (s, e) => actions.Opacity = 1;
            row.MouseLeave += (s, e) => actions.Opacity = (row.Tag as string) == "playing" ? 1 : 0;
            // double-click anywhere on the row (not on a button) plays it
            row.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) PlayFile(filepath, title); };

            // ---- drag & drop: drag a row to reorder, or drop it on a playlist / Queue in the sidebar ----
            row.AllowDrop = true;
            row.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (IsInsideButton(e.OriginalSource as DependencyObject, row)) { _dragRow = null; return; }
                _dragStart = e.GetPosition(null);
                _dragRow = row;
            };
            row.PreviewMouseLeftButtonUp += (s, e) => _dragRow = null;
            row.MouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragRow != row) return;
                Point pos = e.GetPosition(null);
                if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

                _dragRow = null;
                row.Opacity = 0.5;
                try { DragDrop.DoDragDrop(row, new DataObject(DragFormat, payload), DragDropEffects.Move | DragDropEffects.Copy); }
                finally { row.Opacity = 1; }
            };
            row.DragOver += (s, e) =>
            {
                if (e.Data.GetData(DragFormat) is DragPayload src && IsFromCurrentView(src))
                {
                    ShowDropIndicator(row, e.GetPosition(row).Y > row.ActualHeight / 2);
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                }
            };
            row.DragLeave += (s, e) => ClearDropIndicator(row);
            row.Drop += (s, e) =>
            {
                ClearDropIndicator(row);
                if (e.Data.GetData(DragFormat) is DragPayload src && IsFromCurrentView(src))
                {
                    bool below = e.GetPosition(row).Y > row.ActualHeight / 2;
                    ReorderTrack(src, below ? payload.Index + 1 : payload.Index);
                    e.Handled = true;
                }
            };

            return row;
        }

        private void MovePlaylistSong(string plName, int index, int direction)
        {
            if (!_playlists.ContainsKey(plName)) return;
            var list = _playlists[plName];
            int newIndex = index + direction;
            if (newIndex >= 0 && newIndex < list.Count)
            {
                var item = list[index];
                list.RemoveAt(index);
                list.Insert(newIndex, item);
                SaveData();
                RenderView();
            }
        }

        private void MoveQueueSong(int index, int direction)
        {
            int newIndex = index + direction;
            if (newIndex >= 0 && newIndex < _songQueue.Count)
            {
                var item = _songQueue[index];
                _songQueue.RemoveAt(index);
                _songQueue.Insert(newIndex, item);
                SaveData();
                RenderView();
            }
        }
        #endregion

        #region Navigation & Sidebar
        private void ShowLocalLibrary_Click(object? sender = null, RoutedEventArgs? e = null) { _currentView = "library"; TxtLocalFilter.Text = ""; RenderView(); }
        private void ShowQueue_Click(object? sender = null, RoutedEventArgs? e = null) { _currentView = "queue"; TxtLocalFilter.Text = ""; RenderView(); }
        private void LocalFilter_TextChanged(object? sender = null, TextChangedEventArgs? e = null) { _localSearchQuery = TxtLocalFilter.Text.ToLower(); RenderView(); }

        private async void CreatePlaylist_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            string? name = await PromptAndCreatePlaylistAsync();
            if (name == null) return;

            _currentView = $"playlist:{name}";
            TxtLocalFilter.Text = "";
            RenderView();
        }

        /// <summary>Asks for a name (themed dialog) and creates the playlist if it doesn't exist yet. Returns its name, or null if cancelled.</summary>
        private async Task<string?> PromptAndCreatePlaylistAsync()
        {
            string? name = await PromptAsync("New playlist", "Give your playlist a name.", "Playlist name", "Create");
            if (string.IsNullOrWhiteSpace(name)) return null;

            if (!_playlists.ContainsKey(name))
            {
                _playlists[name] = new List<Track>();
                SaveData();
                RenderSidebarPlaylists();
            }
            return name;
        }

        private void RenderSidebarPlaylists()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RenderSidebarPlaylists);
                return;
            }

            PlaylistsPanel.Children.Clear();
            foreach (string pl in _playlists.Keys)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Text = G_NOTE, FontFamily = IconFont, FontSize = 14, Width = 28, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(new TextBlock { Text = pl, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 });

                Button btn = new Button { Content = content, CommandParameter = $"playlist:{pl}", Style = (Style)FindResource("NavButton") };
                btn.Click += (s, e) => { _currentView = $"playlist:{pl}"; TxtLocalFilter.Text = ""; RenderView(); };
                MakeDropTarget(btn, p => AddTrackToPlaylist(pl, p.FilePath, p.Title));
                PlaylistsPanel.Children.Add(btn);
            }
            UpdateNavHighlight();
        }
        #endregion

        #region Local Importing & FFmpeg Conversion
        private static readonly HashSet<string> ImportExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".mp4", ".m4a", ".mkv", ".wav", ".flac", ".ogg", ".webm", ".aac", ".mov" };

        private void ImportLocal_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Media Files|*.mp4;*.mp3;*.m4a;*.mkv;*.wav;*.flac;*.ogg;*.webm;*.aac;*.mov|All Files|*.*"
            };
            if (dlg.ShowDialog() == true) StartImport(dlg.FileNames);
        }

        private void StartImport(string[] files)
        {
            _busyCount++;
            SetStatus($"Importing {files.Length} file{(files.Length == 1 ? "" : "s")}...");
            string destFolder = MUSIC_FOLDER;
            Task.Run(() => ProcessImportedFiles(files, destFolder));
        }

        private void ProcessImportedFiles(string[] files, string destFolder)
        {
            string? error = null;
            try
            {
                foreach (string file in files)
                {
                    string baseName = Path.GetFileNameWithoutExtension(file);
                    string ext = Path.GetExtension(file).ToLower();
                    string dest = Path.Combine(destFolder, $"{baseName}.mp3");

                    if (ext == ".mp3")
                    {
                        if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                            File.Copy(file, dest, true);
                    }
                    else ExtractAudio(file, dest);
                }
            }
            catch (Exception ex) { error = ex.Message; }

            Dispatcher.Invoke(() =>
            {
                _busyCount--;
                GetOrderedLibrarySongs(forceReload: true);
                SetStatus(error == null ? "Import complete!" : $"Import stopped: {error}");
                RenderView();
            });
        }

        private void ScanAndConvertFolderMp4s()
        {
            foreach (string file in Directory.GetFiles(MUSIC_FOLDER, "*.mp4"))
            {
                string dest = Path.Combine(MUSIC_FOLDER, Path.GetFileNameWithoutExtension(file) + ".mp3");
                ExtractAudio(file, dest);
                try { File.Delete(file); } catch { }
            }
        }

        private void ExtractAudio(string source, string dest)
        {
            // uses RunProcess so ffmpeg's stderr output can't fill the pipe and freeze the conversion
            try
            {
                RunProcess("ffmpeg", new[]
                {
                    "-nostdin", "-loglevel", "error",
                    "-i", source, "-vn", "-ar", "44100", "-ac", "2", "-b:a", "192k", "-y", dest
                });
            }
            catch { }
        }

        /// <summary>
        /// Runs an external program and returns its exit code, stdout and stderr.
        /// Both output streams are read while the process runs, otherwise a chatty child process (yt-dlp, ffmpeg)
        /// fills the pipe, blocks, and WaitForExit() never returns.
        /// Arguments are passed as a list, so titles containing quotes can't break the command line.
        /// </summary>
        private static (int ExitCode, string Output, string Error) RunProcess(string fileName, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // Make yt-dlp (Python) write UTF-8 so file paths with accents/non-latin characters survive the pipe
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";

            foreach (string a in args) psi.ArgumentList.Add(a);

            using Process p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
            p.StandardInput.Close();

            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();

            return (p.ExitCode, outTask.Result, errTask.Result);
        }
        #endregion

        #region Search, yt-dlp, & CSV Importing
        private void SearchPlay_Click(object? sender = null, RoutedEventArgs? e = null) => StartSearchDownload(autoPlay: true);
        private void SearchQueue_Click(object? sender = null, RoutedEventArgs? e = null) => StartSearchDownload(autoQueue: true);

        private void TxtYoutubeSearch_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                StartSearchDownload(autoPlay: true);
                e.Handled = true;
            }
        }

        private void StartSearchDownload(bool autoPlay = false, bool autoQueue = false)
        {
            string text = TxtYoutubeSearch.Text.Trim();
            if (text.Length == 0)
            {
                SetStatus("Type a song or artist to search for.");
                TxtYoutubeSearch.Focus();
                return;
            }
            DownloadTrack(text, autoPlay, autoQueue);
        }

        private void DownloadTrack(string searchText, bool autoPlay = false, bool autoQueue = false)
        {
            SetStatus($"Downloading \"{searchText}\"...");
            _busyCount++;
            Task.Run(() =>
            {
                string? downloadedFile = DownloadTrackSync($"ytsearch1:{searchText}");
                Dispatcher.Invoke(() =>
                {
                    _busyCount--;
                    if (downloadedFile != null)
                    {
                        GetOrderedLibrarySongs(forceReload: true);
                        string title = Path.GetFileNameWithoutExtension(downloadedFile);
                        if (autoPlay) PlayFile(downloadedFile, title);
                        else if (autoQueue) AddToQueue(downloadedFile, title);
                        RenderView();
                        SetStatus("Ready");
                    }
                    else
                    {
                        SetStatus(string.IsNullOrEmpty(_lastDownloadError) ? "Download failed or aborted." : $"Download failed: {_lastDownloadError}");
                    }
                });
            });
        }

        /// <summary>
        /// Downloads the first match for a yt-dlp query as mp3 into MUSIC_FOLDER and returns the full path of the mp3.
        /// Safe to call from a background thread (touches no UI / shared state except _lastDownloadError).
        /// </summary>
        private string? DownloadTrackSync(string query)
        {
            _lastDownloadError = "";
            var beforeFiles = new HashSet<string>(Directory.GetFiles(MUSIC_FOLDER, "*.mp3"), StringComparer.OrdinalIgnoreCase);
            string outTmpl = Path.Combine(MUSIC_FOLDER, "%(title)s.%(ext)s");

            try
            {
                var (_, stdout, stderr) = RunProcess("yt-dlp", new[]
                {
                    "-x", "--audio-format", "mp3", "--audio-quality", "192K",
                    "--no-playlist",
                    "--no-simulate", "--print", "after_move:filepath",   // yt-dlp tells us the final mp3 path
                    "-o", outTmpl,
                    query
                });

                // 1. Path reported by yt-dlp (works even if the file already existed, unlike a before/after diff)
                string? printed = stdout.Split('\n')
                                        .Select(l => l.Trim())
                                        .LastOrDefault(l => l.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
                if (printed != null && File.Exists(printed)) return printed;

                // 2. Fallback: any mp3 that wasn't there before
                string? created = Directory.GetFiles(MUSIC_FOLDER, "*.mp3").FirstOrDefault(f => !beforeFiles.Contains(f));
                if (created != null) return created;

                _lastDownloadError = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "yt-dlp produced no file.";
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _lastDownloadError = "yt-dlp was not found. Make sure it is installed and on your PATH.";
                return null;
            }
            catch (Exception ex)
            {
                _lastDownloadError = ex.Message;
                return null;
            }
        }

        // ---------- CSV playlist import ----------

        private async void ImportCSV_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Title = "Select Spotify CSV Export",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                SetStatus("Parsing CSV file...");
                try { await ProcessCsvPlaylistAsync(dlg.FileName); }
                catch (Exception ex)
                {
                    DownloadProgress.Value = 0;
                    SetStatus($"Error importing CSV: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// For every track in the CSV:
        ///   1. already downloaded (remembered from an earlier import, or found in the library)? -> just add it to the playlist
        ///   2. otherwise download it with yt-dlp (this also puts it in the library) -> add it to the playlist
        /// Runs on the UI thread; only the slow work (CSV parsing, yt-dlp) is pushed to background threads, so
        /// _playlists / _songQueue / _libraryOrder are never touched from two threads at once.
        /// </summary>
        private async Task ProcessCsvPlaylistAsync(string filePath)
        {
            _busyCount++;
            try { await ProcessCsvPlaylistCoreAsync(filePath); }
            finally { _busyCount--; }
        }

        private async Task ProcessCsvPlaylistCoreAsync(string filePath)
        {
            string playlistName = Path.GetFileNameWithoutExtension(filePath);

            List<(string Title, string Artist)> tracks = await Task.Run(() => ParseSpotifyCsv(filePath));
            if (tracks.Count == 0)
            {
                SetStatus("No tracks found in that CSV.");
                return;
            }

            if (!_playlists.ContainsKey(playlistName)) _playlists[playlistName] = new List<Track>();
            SaveData();
            RenderSidebarPlaylists();

            List<string> library = GetOrderedLibrarySongs(forceReload: true);

            int total = tracks.Count, added = 0, alreadyInPlaylist = 0, downloaded = 0;
            string lastError = "";
            var failed = new List<string>();
            DownloadProgress.Maximum = total;
            DownloadProgress.Value = 0;

            for (int i = 0; i < total; i++)
            {
                (string title, string artist) = tracks[i];
                SetStatus($"Importing ({i + 1}/{total}): {title}...");

                string key = MakeTrackKey(title, artist);
                string? fileName = null;

                // 1a. Did a previous import already download this exact track?
                if (_trackCache.TryGetValue(key, out string? cached) && cached != null && File.Exists(Path.Combine(MUSIC_FOLDER, cached)))
                    fileName = cached;
                // 1b. Otherwise look for it in the library by title + artist
                else
                    fileName = FindInLibrary(library, title, artist);

                // 2. Not found locally -> download it
                if (fileName == null)
                {
                    string search = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
                    string? downloadedPath = await Task.Run(() => DownloadTrackSync($"ytsearch1:{search} official audio"));

                    if (downloadedPath != null && File.Exists(downloadedPath))
                    {
                        fileName = Path.GetFileName(downloadedPath);
                        _trackCache[key] = fileName;
                        SaveTrackCache();
                        library = GetOrderedLibrarySongs(forceReload: true);   // the new mp3 is now part of the library
                        downloaded++;
                    }
                    else
                    {
                        lastError = _lastDownloadError;
                    }
                }

                // 3. Add to the playlist
                if (fileName != null)
                {
                    if (!_playlists.TryGetValue(playlistName, out List<Track>? playlist))
                    {
                        DownloadProgress.Value = 0;
                        SetStatus($"Playlist '{playlistName}' was deleted - import stopped.");
                        return;
                    }

                    string fullPath = Path.Combine(MUSIC_FOLDER, fileName);
                    if (playlist.Any(t => t.FilePath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        alreadyInPlaylist++;
                    }
                    else
                    {
                        playlist.Add(new Track { FilePath = fullPath, Title = Path.GetFileNameWithoutExtension(fileName) });
                        added++;
                        SaveData();
                        if (_currentView == $"playlist:{playlistName}") RenderView();
                    }
                }
                else
                {
                    failed.Add(string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}");
                }

                DownloadProgress.Value = i + 1;
            }

            DownloadProgress.Value = 0;
            RenderView();   // refreshes the library / playlist view so newly downloaded songs show up
            SetStatus($"'{playlistName}': {added} added ({downloaded} downloaded), {alreadyInPlaylist} already in playlist, {failed.Count} failed.");

            if (failed.Count > 0)
            {
                string failedList = string.Join("\n", failed.Take(15)) + (failed.Count > 15 ? $"\n...and {failed.Count - 15} more" : "");
                string reason = string.IsNullOrEmpty(lastError) ? "" : $"\n\nLast error: {lastError}";
                await ShowDialogAsync("Import finished with errors", $"{failed.Count} track(s) could not be downloaded:\n\n{failedList}{reason}",
                                      null, ("OK", "AccentButton"));
            }
        }

        private static List<(string Title, string Artist)> ParseSpotifyCsv(string filePath)
        {
            var result = new List<(string Title, string Artist)>();

            using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            List<string>? headerFields = ReadCsvRecord(reader);
            if (headerFields == null) return result;

            var headers = headerFields.Select(h => h.Trim().Trim('\uFEFF').ToLowerInvariant()).ToList();
            int trackIdx = headers.FindIndex(h => h.Contains("track name") || h == "title" || h == "name" || h == "track" || h == "song");
            int artistIdx = headers.FindIndex(h => h.Contains("artist"));

            if (trackIdx == -1) trackIdx = 2;   // Default fallback

            List<string>? record;
            while ((record = ReadCsvRecord(reader)) != null)
            {
                if (record.Count <= trackIdx) continue;

                string title = record[trackIdx].Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;

                string artist = (artistIdx != -1 && record.Count > artistIdx) ? record[artistIdx] : "";
                artist = artist.Split(';')[0].Trim();   // Spotify exports join several artists with ';' - the first one is enough

                result.Add((title, artist));
            }

            return result;
        }

        /// <summary>
        /// Reads one CSV record. Handles quoted fields, commas inside quotes, "" escaped quotes,
        /// and quoted fields that continue over several lines. Returns null at end of file.
        /// </summary>
        private static List<string>? ReadCsvRecord(StreamReader reader)
        {
            string? line = reader.ReadLine();
            if (line == null) return null;

            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            while (true)
            {
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (inQuotes)
                    {
                        if (c == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                            else inQuotes = false;
                        }
                        else sb.Append(c);
                    }
                    else if (c == '"') inQuotes = true;
                    else if (c == ',') { fields.Add(sb.ToString().Trim()); sb.Clear(); }
                    else sb.Append(c);
                }

                if (!inQuotes) break;

                string? next = reader.ReadLine();   // quoted field continues on the next line
                if (next == null) break;
                sb.Append('\n');
                line = next;
            }

            fields.Add(sb.ToString().Trim());
            return fields;
        }

        // ---------- Matching helpers ----------

        private static readonly Regex FeatRegex = new(@"[\(\[]\s*(feat|ft|featuring)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NonAlnumRegex = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

        /// <summary>Lower-case, drop "(feat. X)", replace punctuation with single spaces.</summary>
        private static string Normalize(string s)
        {
            s = s.Normalize(NormalizationForm.FormC).ToLowerInvariant();
            s = FeatRegex.Replace(s, " ");
            s = NonAlnumRegex.Replace(s, " ");
            return s.Trim();
        }

        private static string MakeTrackKey(string title, string artist)
        {
            string t = Normalize(title);
            if (t.Length == 0) t = title.Trim().ToLowerInvariant();   // titles made only of symbols
            return $"{t}|{Normalize(artist)}";
        }

        /// <summary>
        /// Looks for an already-downloaded file whose name contains the whole title (as whole words)
        /// and, when the CSV gives an artist, the artist as well.
        /// </summary>
        private static string? FindInLibrary(IEnumerable<string> libraryFiles, string title, string artist)
        {
            string nTitle = Normalize(title);
            string nArtist = Normalize(artist);
            if (nTitle.Length == 0) return null;

            foreach (string file in libraryFiles)
            {
                string nFile = " " + Normalize(Path.GetFileNameWithoutExtension(file)) + " ";
                if (!nFile.Contains(" " + nTitle + " ")) continue;
                if (nArtist.Length > 0 && !nFile.Contains(" " + nArtist + " ")) continue;
                return file;
            }
            return null;
        }
        #endregion

        #region Core Playback & Transport Controls
        private void PlayFile(string filepath, string title, bool addToHistory = true)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => PlayFile(filepath, title, addToHistory));
                return;
            }

            if (!File.Exists(filepath)) { SetStatus("File missing."); return; }

            if (addToHistory && _currentTrack != null)
            {
                _history.Add(_currentTrack);
                if (_history.Count > 100) _history.RemoveAt(0);
            }

            _currentTrack = new Track { FilePath = filepath, Title = title };
            _player.Open(new Uri(filepath));
            _player.Play();
            _isPlaying = true;
            _isActive = true;

            ProgressSlider.Value = 0;
            LblCurrentTime.Text = "00:00";
            LblNowPlaying.Text = title;
            UpdatePlayPauseIcon();
            UpdateNowPlayingHighlight();
        }

        private void Player_MediaOpened(object? sender, EventArgs e)
        {
            if (_player.NaturalDuration.HasTimeSpan)
            {
                ProgressSlider.Maximum = _player.NaturalDuration.TimeSpan.TotalSeconds;
                LblTotalTime.Text = FormatTime(_player.NaturalDuration.TimeSpan);
            }
        }

        private void Player_MediaEnded(object? sender, EventArgs e)
        {
            if (_isRepeat && _currentTrack != null)
            {
                _player.Position = TimeSpan.Zero;
                _player.Play();
                _isPlaying = true;
                _isActive = true;
                UpdatePlayPauseIcon();
                return;
            }

            _isActive = false;
            SkipAudio_Click();
        }

        private void Player_MediaFailed(object? sender, ExceptionEventArgs e)
        {
            SetStatus($"Couldn't play that file: {e.ErrorException.Message}");
            _isPlaying = false;
            _isActive = false;
            UpdatePlayPauseIcon();
            if (_songQueue.Count > 0) SkipAudio_Click();   // don't get stuck on a broken file
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_isPlaying && !_isDraggingSlider && _player.NaturalDuration.HasTimeSpan)
            {
                ProgressSlider.Value = _player.Position.TotalSeconds;
                LblCurrentTime.Text = FormatTime(_player.Position);
            }
        }

        private void TogglePlayPause_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            if (_isPlaying) { PauseAudio_Click(); return; }

            if (_player.Source != null && _currentTrack != null)
            {
                // replay from the start if the song had finished
                if (_player.NaturalDuration.HasTimeSpan && _player.Position >= _player.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(300))
                    _player.Position = TimeSpan.Zero;
                PlayAudio_Click();
                return;
            }

            if (_songQueue.Count > 0) SkipAudio_Click();
            else SetStatus("Nothing to play yet - add a song to the queue or hit Shuffle Play.");
        }

        private void PlayAudio_Click(object? sender = null, RoutedEventArgs? e = null) { _player.Play(); _isPlaying = true; _isActive = true; UpdatePlayPauseIcon(); }
        private void PauseAudio_Click(object? sender = null, RoutedEventArgs? e = null) { _player.Pause(); _isPlaying = false; UpdatePlayPauseIcon(); }
        private void StopAudio_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            _player.Stop();
            _isPlaying = false;
            _isActive = false;
            ProgressSlider.Value = 0;
            LblCurrentTime.Text = "00:00";
            UpdatePlayPauseIcon();
        }

        private void SkipAudio_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            if (_songQueue.Count > 0)
            {
                Track next = _songQueue[0];
                _songQueue.RemoveAt(0);
                SaveData();
                if (_currentView == "queue") RenderView();
                PlayFile(next.FilePath, next.Title);
            }
            else { StopAudio_Click(); SetStatus("Queue finished."); }
        }

        private void PrevAudio_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            // like most players: if we're a few seconds in, restart the song first
            if (_currentTrack != null && _player.Position.TotalSeconds > 3)
            {
                _player.Position = TimeSpan.Zero;
                return;
            }
            if (_history.Count == 0)
            {
                if (_currentTrack != null) _player.Position = TimeSpan.Zero;
                return;
            }

            Track prev = _history[^1];
            _history.RemoveAt(_history.Count - 1);

            // put the current song back at the front of the queue so "Next" returns to it
            if (_currentTrack != null)
            {
                _songQueue.Insert(0, _currentTrack);
                SaveData();
                if (_currentView == "queue") RenderView();
            }
            PlayFile(prev.FilePath, prev.Title, addToHistory: false);
        }

        private void AddToQueue(string filepath, string title)
        {
            _songQueue.Add(new Track { FilePath = filepath, Title = title });
            SaveData();
            // only auto-start when nothing is loaded (previously a paused song got skipped when you queued something)
            if (!_isActive) SkipAudio_Click();
            else if (_currentView == "queue") RenderView();
        }

        private async void DeleteLocalFile(string filepath)
        {
            string name = Path.GetFileNameWithoutExtension(filepath);
            if (!await ConfirmAsync("Delete song", $"Permanently delete \"{name}\" from your library?\nThis removes the file from disk.", "Delete", danger: true)) return;

            try
            {
                // release the file if it is the one that's loaded in the player
                if (IsCurrent(filepath))
                {
                    _player.Close();
                    _currentTrack = null;
                    _isPlaying = false;
                    _isActive = false;
                    ProgressSlider.Value = 0;
                    LblCurrentTime.Text = "00:00";
                    LblTotalTime.Text = "00:00";
                    LblNowPlaying.Text = "Nothing playing";
                    UpdatePlayPauseIcon();
                }

                File.Delete(filepath);

                bool Same(Track t) => string.Equals(t.FilePath, filepath, StringComparison.OrdinalIgnoreCase);
                foreach (string key in _playlists.Keys.ToList()) _playlists[key].RemoveAll(Same);
                _songQueue.RemoveAll(Same);

                string fn = Path.GetFileName(filepath);
                foreach (var kv in _trackCache.Where(kv => string.Equals(kv.Value, fn, StringComparison.OrdinalIgnoreCase)).ToList())
                    _trackCache.Remove(kv.Key);
                SaveTrackCache();

                GetOrderedLibrarySongs(forceReload: true);   // also saves playlists/queue
                RenderView();
                SetStatus($"Deleted \"{name}\".");
            }
            catch (Exception ex)
            {
                SetStatus($"Couldn't delete the file: {ex.Message}");
            }
        }

        private void ToggleShuffle_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            _isShuffleMode = !_isShuffleMode;
            BtnShuffle.Foreground = _isShuffleMode ? Res("ToggleOn") : Res("ToggleOff");
            BtnShuffle.ToolTip = _isShuffleMode ? "Shuffle: on" : "Shuffle: off";
            if (_isShuffleMode && _songQueue.Count > 0)
            {
                _songQueue = _songQueue.OrderBy(x => _rng.Next()).ToList();
                SaveData();
                if (_currentView == "queue") RenderView();
            }
        }

        private void ToggleRepeat_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            _isRepeat = !_isRepeat;
            BtnRepeat.Foreground = _isRepeat ? Res("ToggleOn") : Res("ToggleOff");
            BtnRepeat.ToolTip = _isRepeat ? "Repeat current song: on" : "Repeat current song: off";
        }

        private void ShuffleLocalLibrary()
        {
            List<string> songs = GetOrderedLibrarySongs().OrderBy(x => _rng.Next()).ToList();
            if (songs.Count == 0) return;

            _songQueue.Clear();
            foreach (string f in songs)
            {
                _songQueue.Add(new Track { FilePath = Path.Combine(MUSIC_FOLDER, f), Title = Path.GetFileNameWithoutExtension(f) });
            }
            SaveData();
            SkipAudio_Click();
        }
        #endregion

        #region Settings, dialogs & saving directory
        public class AppSettings { public string? MusicFolder { get; set; } }

        private static readonly string SETTINGS_FILE =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fakeify", "settings.json");

        private static string DefaultMusicFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Fakeify");

        private static string LoadMusicFolderSetting()
        {
            try
            {
                if (File.Exists(SETTINGS_FILE))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SETTINGS_FILE));
                    if (!string.IsNullOrWhiteSpace(s?.MusicFolder)) return s!.MusicFolder!;
                }
            }
            catch { }
            return DefaultMusicFolder();
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SETTINGS_FILE)!);
                File.WriteAllText(SETTINGS_FILE, JsonSerializer.Serialize(new AppSettings { MusicFolder = MUSIC_FOLDER }));
            }
            catch { }
        }

        private void ApplyStoragePaths(string folder)
        {
            MUSIC_FOLDER = folder;
            PLAYLISTS_FILE = Path.Combine(folder, "playlists.json");
            LIBRARY_ORDER_FILE = Path.Combine(folder, "library_order.json");
            QUEUE_FILE = Path.Combine(folder, "queue.json");
            TRACK_CACHE_FILE = Path.Combine(folder, "track_cache.json");
        }

        // ---------- in-app dialogs (themed replacement for the standard Windows message/input boxes) ----------

        /// <summary>
        /// Shows the glass dialog. Buttons are shown left to right; the last one is the default (Enter), Esc cancels.
        /// Returns the index of the clicked button, or -1 if the dialog was cancelled/closed.
        /// </summary>
        private Task<int> ShowDialogAsync(string title, string message, UIElement? extra = null, params (string Text, string StyleKey)[] buttons)
        {
            _dialogTcs?.TrySetResult(-1);
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dialogTcs = tcs;

            DialogTitle.Text = title;
            DialogMessage.Text = message;
            DialogMessageScroll.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            DialogExtra.Content = extra;
            DialogExtra.Visibility = extra == null ? Visibility.Collapsed : Visibility.Visible;

            DialogButtons.Children.Clear();
            for (int i = 0; i < buttons.Length; i++)
            {
                int result = i;
                var b = new Button
                {
                    Content = buttons[i].Text,
                    Style = (Style)FindResource(buttons[i].StyleKey),
                    Height = 36,
                    MinWidth = 100,
                    Margin = new Thickness(10, 0, 0, 0),
                    IsDefault = i == buttons.Length - 1
                };
                b.Click += (s, e) => CloseDialog(result);
                DialogButtons.Children.Add(b);
            }
            DialogButtons.Visibility = buttons.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            DialogHost.Visibility = Visibility.Visible;
            DialogHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

            if (extra is TextBox tb && !tb.IsReadOnly)
                Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); tb.SelectAll(); }), DispatcherPriority.Loaded);

            return tcs.Task;
        }

        private void CloseDialog(int result)
        {
            var tcs = _dialogTcs;
            _dialogTcs = null;
            DialogHost.Visibility = Visibility.Collapsed;
            DialogExtra.Content = null;
            tcs?.TrySetResult(result);
        }

        private async Task<bool> ConfirmAsync(string title, string message, string okText, bool danger = false)
            => await ShowDialogAsync(title, message, null, ("Cancel", "GlassButton"), (okText, danger ? "DangerButton" : "AccentButton")) == 1;

        private async Task<string?> PromptAsync(string title, string message, string placeholder, string okText)
        {
            var tb = new TextBox { Style = (Style)FindResource("GlassTextBox"), Tag = placeholder, Margin = new Thickness(0, 16, 0, 0) };
            int r = await ShowDialogAsync(title, message, tb, ("Cancel", "GlassButton"), (okText, "AccentButton"));
            return r == 1 ? tb.Text.Trim() : null;
        }

        private static FrameworkElement MenuGlyph(string glyph) =>
            new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        private static FrameworkElement MenuLabel(string text) =>
            new TextBlock { Text = text, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };

        // ---------- settings ----------

        private async void Settings_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            while (true)
            {
                var pathBox = new TextBox
                {
                    Style = (Style)FindResource("GlassTextBox"),
                    Text = MUSIC_FOLDER,
                    IsReadOnly = true,
                    Margin = new Thickness(0, 16, 0, 0)
                };
                int r = await ShowDialogAsync("Settings",
                    "Saving directory\nYour songs, playlists and queue are stored in this folder.",
                    pathBox, ("Open folder", "GlassButton"), ("Change folder...", "GlassButton"), ("Done", "AccentButton"));

                if (r == 0)
                {
                    try { Process.Start(new ProcessStartInfo { FileName = MUSIC_FOLDER, UseShellExecute = true }); } catch { }
                }
                else if (r == 1) await ChangeSaveFolderFlowAsync();
                else break;
            }
        }

        private string? PickFolder(string initialDirectory)
        {
#if NET8_0_OR_GREATER
            var dlg = new OpenFolderDialog { Title = "Choose where Fakeify saves your music", InitialDirectory = initialDirectory };
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
#else
            // Older .NET has no folder picker in WPF: use the file dialog, then take the folder it is showing
            var dlg = new OpenFileDialog
            {
                Title = "Open the folder you want, then press Open",
                ValidateNames = false,
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "Select this folder",
                InitialDirectory = initialDirectory
            };
            if (dlg.ShowDialog(this) != true) return null;
            if (Directory.Exists(dlg.FileName)) return dlg.FileName;
            string? dir = Path.GetDirectoryName(dlg.FileName);
            return string.IsNullOrEmpty(dir) ? null : dir;
#endif
        }

        private async Task ChangeSaveFolderFlowAsync()
        {
            if (_busyCount > 0)
            {
                await ShowDialogAsync("Please wait", "A download or import is still running. Try again once it has finished.", null, ("OK", "AccentButton"));
                return;
            }

            string? picked = PickFolder(MUSIC_FOLDER);
            if (string.IsNullOrWhiteSpace(picked)) return;

            string newFolder;
            try { newFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked)); }
            catch { return; }

            if (string.Equals(newFolder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(MUSIC_FOLDER)), StringComparison.OrdinalIgnoreCase))
                return;

            int choice = await ShowDialogAsync("Change saving directory",
                $"New folder:\n{newFolder}\n\n" +
                "Move everything: your songs, playlists and queue are moved to the new folder.\n\n" +
                "Just switch: your current files stay where they are, and Fakeify uses whatever is in the new folder.",
                null, ("Cancel", "GlassButton"), ("Just switch", "GlassButton"), ("Move everything", "AccentButton"));
            if (choice < 1) return;

            await ApplySaveFolderAsync(newFolder, move: choice == 2);
        }

        private async Task ApplySaveFolderAsync(string newFolder, bool move)
        {
            string oldFolder = MUSIC_FOLDER;
            try { Directory.CreateDirectory(newFolder); }
            catch (Exception ex)
            {
                await ShowDialogAsync("Couldn't change folder", $"That folder can't be used:\n{ex.Message}", null, ("OK", "AccentButton"));
                return;
            }

            // release the file the player may be holding and forget playback state tied to the old folder
            StopAudio_Click();
            _player.Close();
            _currentTrack = null;
            _history.Clear();
            LblNowPlaying.Text = "Nothing playing";
            LblTotalTime.Text = "00:00";

            int skipped = 0, failed = 0;
            if (move)
            {
                _busyCount++;
                _ = ShowDialogAsync("Moving your library", "Please keep Fakeify open while your songs are moved...", null);
                try { (skipped, failed) = await Task.Run(() => MoveMp3Files(oldFolder, newFolder)); }
                finally { _busyCount--; CloseDialog(-1); }
            }

            ApplyStoragePaths(newFolder);
            SaveSettings();

            if (move)
            {
                // point playlists and the queue at the new location
                string oldNorm = Path.TrimEndingDirectorySeparator(oldFolder);
                string Remap(string path) =>
                    string.Equals(Path.GetDirectoryName(path), oldNorm, StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(newFolder, Path.GetFileName(path))
                        : path;

                foreach (var list in _playlists.Values) foreach (var t in list) t.FilePath = Remap(t.FilePath);
                foreach (var t in _songQueue) t.FilePath = Remap(t.FilePath);

                // our data files are rewritten in the new folder below; remove the stale copies in the old one
                foreach (string f in new[] { "playlists.json", "library_order.json", "queue.json", "track_cache.json" })
                {
                    try { File.Delete(Path.Combine(oldFolder, f)); } catch { }
                }
                _cachedLibrary = null;
                SaveTrackCache();
            }
            else
            {
                _playlists = new(); _libraryOrder = new(); _songQueue = new(); _trackCache = new(); _cachedLibrary = null;
                LoadData();   // whatever already lives in the new folder
            }

            await Task.Run(() => ScanAndConvertFolderMp4s());   // convert stray .mp4 files found in the new folder
            GetOrderedLibrarySongs(forceReload: true);
            ValidateData();
            RenderSidebarPlaylists();
            ShowLocalLibrary_Click();
            UpdateQueueUi();
            SetStatus(move ? "Library moved to the new folder." : "Now saving to the new folder.");

            if (move && skipped + failed > 0)
            {
                await ShowDialogAsync("Library moved",
                    $"Your library now lives in:\n{newFolder}\n\n{skipped} song(s) already existed there and were left in the old folder, and {failed} could not be moved.",
                    null, ("OK", "AccentButton"));
            }
        }

        private static (int Skipped, int Failed) MoveMp3Files(string from, string to)
        {
            int skipped = 0, failed = 0;
            if (!Directory.Exists(from)) return (0, 0);

            foreach (string src in Directory.GetFiles(from, "*.mp3"))
            {
                string dst = Path.Combine(to, Path.GetFileName(src));
                try
                {
                    if (File.Exists(dst)) skipped++;   // never overwrite something that is already there
                    else File.Move(src, dst);
                }
                catch { failed++; }
            }
            return (skipped, failed);
        }
        #endregion

        #region Drag & drop
        private sealed class DragPayload
        {
            public string FilePath { get; set; } = "";
            public string Title { get; set; } = "";
            public string Context { get; set; } = "";        // library / queue / playlist
            public string PlaylistName { get; set; } = "";
            public int Index { get; set; }                   // position in the underlying (unfiltered) list
        }

        private const string DragFormat = "FakeifyTrack";
        private Point _dragStart;
        private Border? _dragRow;

        /// <summary>True when the mouse went down on a button inside the row (those must not start a drag).</summary>
        private static bool IsInsideButton(DependencyObject? d, DependencyObject stopAt)
        {
            while (d != null && d != stopAt)
            {
                if (d is ButtonBase) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private bool IsFromCurrentView(DragPayload p) =>
            _currentView == "library" ? p.Context == "library"
            : _currentView == "queue" ? p.Context == "queue"
            : p.Context == "playlist" && _currentView == $"playlist:{p.PlaylistName}";

        private void ShowDropIndicator(Border row, bool below)
        {
            row.BorderBrush = Res("ToggleOn");
            row.BorderThickness = below ? new Thickness(1, 1, 1, 4) : new Thickness(1, 4, 1, 1);
        }

        private static void ClearDropIndicator(Border row)
        {
            row.ClearValue(Border.BorderBrushProperty);
            row.ClearValue(Border.BorderThicknessProperty);
        }

        private static void MoveItem<T>(List<T> list, int from, int insertPos)
        {
            if (from < 0 || from >= list.Count) return;
            insertPos = Math.Clamp(insertPos, 0, list.Count);
            T item = list[from];
            list.RemoveAt(from);
            if (from < insertPos) insertPos--;
            list.Insert(insertPos, item);
        }

        /// <summary>Moves the dragged song so it ends up at insertion position <paramref name="insertPos"/> of the list it came from.</summary>
        private void ReorderTrack(DragPayload src, int insertPos)
        {
            switch (src.Context)
            {
                case "playlist":
                    if (!_playlists.TryGetValue(src.PlaylistName, out var pl)) return;
                    MoveItem(pl, src.Index, insertPos);
                    break;
                case "queue":
                    MoveItem(_songQueue, src.Index, insertPos);
                    break;
                case "library":
                    MoveItem(_libraryOrder, src.Index, insertPos);
                    _cachedLibrary = new List<string>(_libraryOrder);
                    break;
                default:
                    return;
            }
            SaveData();
            RenderView();
        }

        private void AddTrackToPlaylist(string playlist, string filepath, string title)
        {
            if (!_playlists.TryGetValue(playlist, out var list)) return;

            if (list.Any(t => string.Equals(t.FilePath, filepath, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus($"Already in {playlist}");
                return;
            }

            list.Add(new Track { FilePath = filepath, Title = title });
            SaveData();
            SetStatus($"Added to {playlist}");
            if (_currentView == $"playlist:{playlist}") RenderView();
        }

        /// <summary>Makes a sidebar button accept dragged songs (it glows green while a song hovers over it).</summary>
        private void MakeDropTarget(Button target, Action<DragPayload> onDrop)
        {
            target.AllowDrop = true;

            void Over(object? s, DragEventArgs e)
            {
                if (e.Data.GetData(DragFormat) is DragPayload)
                {
                    target.Tag = "drop";
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
            }

            target.DragEnter += Over;
            target.DragOver += Over;
            target.DragLeave += (s, e) => UpdateNavHighlight();
            target.Drop += (s, e) =>
            {
                UpdateNavHighlight();
                if (e.Data.GetData(DragFormat) is DragPayload p)
                {
                    onDrop(p);
                    e.Handled = true;
                }
            };
        }

        // scroll the list while dragging near its top/bottom edge
        private void TrackList_PreviewDragOver(object? sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DragFormat)) return;
            double y = e.GetPosition(TrackScroll).Y;
            if (y < 40) TrackScroll.ScrollToVerticalOffset(TrackScroll.VerticalOffset - 18);
            else if (y > TrackScroll.ActualHeight - 40) TrackScroll.ScrollToVerticalOffset(TrackScroll.VerticalOffset + 18);
        }

        // dropping on the empty space under the rows moves the song to the end
        private void TrackList_DragOver(object? sender, DragEventArgs e)
        {
            if (e.Data.GetData(DragFormat) is DragPayload src && IsFromCurrentView(src))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        }

        private void TrackList_Drop(object? sender, DragEventArgs e)
        {
            if (e.Data.GetData(DragFormat) is DragPayload src && IsFromCurrentView(src))
            {
                ReorderTrack(src, int.MaxValue);
                e.Handled = true;
            }
        }

        // dragging audio/video files from Windows Explorer into the window imports them
        private void Window_DragOver(object? sender, DragEventArgs e)
        {
            e.Effects = DialogHost.Visibility != Visibility.Visible && e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object? sender, DragEventArgs e)
        {
            if (DialogHost.Visibility == Visibility.Visible) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

            string[] media = files.Where(f => ImportExtensions.Contains(Path.GetExtension(f))).ToArray();
            if (media.Length == 0)
            {
                SetStatus("Those aren't audio or video files Fakeify can import.");
                return;
            }
            StartImport(media);
        }
        #endregion

        #region Slider, Volume & Keyboard
        private void Slider_DragStarted(object? sender = null, DragStartedEventArgs? e = null) => _isDraggingSlider = true;

        private void Slider_DragCompleted(object? sender = null, DragCompletedEventArgs? e = null)
        {
            _isDraggingSlider = false;
            if (_player.Source != null) _player.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
        }

        // shows the time you're seeking to while dragging
        private void ProgressSlider_ValueChanged(object? sender = null, RoutedPropertyChangedEventArgs<double>? e = null)
        {
            if (_isDraggingSlider && LblCurrentTime != null)
                LblCurrentTime.Text = FormatTime(TimeSpan.FromSeconds(ProgressSlider.Value));
        }

        private void VolumeSlider_ValueChanged(object? sender = null, RoutedPropertyChangedEventArgs<double>? e = null)
        {
            if (_player == null || VolumeSlider == null || BtnMute == null) return;
            _player.Volume = VolumeSlider.Value;
            BtnMute.Content = _player.Volume == 0 ? G_MUTE : (_player.Volume < 0.34 ? G_VOL1 : (_player.Volume < 0.67 ? G_VOL2 : G_VOL3));
        }

        private void ToggleMute_Click(object? sender = null, RoutedEventArgs? e = null)
        {
            if (VolumeSlider.Value > 0)
            {
                _volumeBeforeMute = VolumeSlider.Value;
                VolumeSlider.Value = 0;
            }
            else
            {
                VolumeSlider.Value = _volumeBeforeMute > 0 ? _volumeBeforeMute : 0.7;
            }
        }

        // Space = play/pause, M = mute, Ctrl+Left/Right = previous/next (ignored while typing in a text box)
        private void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            // while a dialog is open: Esc cancels, nothing else triggers player shortcuts
            if (DialogHost.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape && DialogButtons.Children.Count > 0) { CloseDialog(-1); e.Handled = true; }
                return;
            }

            if (Keyboard.FocusedElement is TextBox) return;

            if (e.Key == Key.Space) { TogglePlayPause_Click(); e.Handled = true; }
            else if (e.Key == Key.M) { ToggleMute_Click(); e.Handled = true; }
            else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control) { SkipAudio_Click(); e.Handled = true; }
            else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control) { PrevAudio_Click(); e.Handled = true; }
        }

        private void SetStatus(string text)
        {
            if (Dispatcher.CheckAccess()) LblStatus.Text = text;
            else Dispatcher.Invoke(() => LblStatus.Text = text);
        }
        #endregion
    }
}
