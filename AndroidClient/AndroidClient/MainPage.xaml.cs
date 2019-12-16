using PocketDownloaderBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Essentials;
using Xamarin.Forms;
using YoutubeExplode;
using YoutubeExplode.Models;

namespace AndroidClient
{
    // Learn more about making custom code visible in the Xamarin.Forms previewer
    // by visiting https://aka.ms/xamarinforms-previewer
    [DesignTimeVisible(false)]
    public partial class MainPage : ContentPage
    {
        /*=====================================================
        * TODO:
        * (Before cruise)
        * - Setting: download shortest videos first or longest videos first
        * - Setting: download newest videos first or oldest videos first
        * 
        * 
        * (Later)
        * - Ability to Auth Pocket
        * - Proper way to select a download directory (have the user select it?)
        * - "Only download over wifi" (controlled via a setting & switch)
        * 
        *=====================================================
        */

        public ObservableCollection<Item> Items = new ObservableCollection<Item>();
        INotificationManager notificationManager;

        bool isPaused = false;

        public MainPage()
        {
            InitializeComponent();
            HideBottomBar();

            notificationManager = DependencyService.Get<INotificationManager>();
            Connectivity.ConnectivityChanged += Connectivity_ConnectivityChanged;

            DatePicker.Date = DateTime.Now;
            TimePicker.Time = DateTime.Now.TimeOfDay;

            Downloader.DownloadDirectory = "/storage/emulated/0/Download";
            Progress<double> totalProgress = new Progress<double>();
            totalProgress.ProgressChanged += (sender, args) => SetTotalProgress(args);
            Downloader.TotalProgress = totalProgress;

            ListView.ItemsSource = Items;

            AuthPocket();
        }

        private async void AuthPocket()
        {
            string authCode = await Downloader.AuthPocket(Settings.Instance.PocketAuthCode, s => Launcher.OpenAsync(new Uri(s)));
            Settings.Instance.PocketAuthCode = authCode;
        }

        private void Connectivity_ConnectivityChanged(object sender, ConnectivityChangedEventArgs e)
        {
            if (!Connectivity.ConnectionProfiles.Contains(ConnectionProfile.WiFi) && !isPaused && Downloader.FilesDownloading.Count > 0)
            {
                PauseDownloads(null, null);
                Device.BeginInvokeOnMainThread(() => DisplayAlert("", "Wifi disconnected. Downloads paused automatically.", "Ok"));
            }
        }

        private DateTime SelectedDateTime
        {
            get { return DatePicker.Date.Add(TimePicker.Time); }
        }

        private void GetNewItems(object sender, EventArgs e)
        {
            Items.Clear();

            _ = GetPocketItems();
        }

        private async Task GetPocketItems(DateTime? sinceDate = null)
        {
            SetGetButtonsEnabled(false);

            if (sinceDate == null)
                sinceDate = SelectedDateTime;

            List<Item> pocketItem = await Downloader.GetPocketItems(sinceDate);
            pocketItem.ToList().ForEach(p => Items.Add(p));

            if (Settings.Instance.DownloadAllOnGet)
            {
                foreach (Item item in Items)
                    item.IsChecked = true;

                DownloadItems(null, null);
            }
            else
                Device.BeginInvokeOnMainThread(() => DisplayAlert("", "Done retrieving items", "Ok"));

            SetGetButtonsEnabled(true);
        }

        private void PauseDownloads(object sender, EventArgs e)
        {
            if (Downloader.FilesDownloading.Count > 0)
            {
                isPaused = true;
                SetPauseResumeButton("resume");

                foreach (FileDownload fileDownload in Downloader.FilesDownloading)
                    fileDownload.Pause();

                if (sender != null) //Coming from button
                    Device.BeginInvokeOnMainThread(() => DisplayAlert("", "Downloads paused", "Ok"));
            }
        }

        private async void ResumeDownloads(object sender, EventArgs e)
        {
            if (Downloader.FilesDownloading.Count > 0)
            {
                if (!Connectivity.ConnectionProfiles.Contains(ConnectionProfile.WiFi))
                {
                    bool continueDownloading = await DisplayAlert("", "You are not currently connected to Wifi - do you want to continue?", "Yes", "No");
                    if (!continueDownloading)
                        return;
                }

                isPaused = false;
                SetPauseResumeButton("pause");

                foreach (FileDownload fileDownload in Downloader.FilesDownloading)
                    fileDownload.Resume();
            }
        }

        private async void DownloadItems(object sender, EventArgs e)
        {
            if (!Connectivity.ConnectionProfiles.Contains(ConnectionProfile.WiFi))
            {
                bool continueDownloading = await DisplayAlert("", "You are not currently connected to Wifi - do you want to continue?", "Yes", "No");
                if (!continueDownloading)
                    return;
            }

            ShowBottomBar();
            DownloadButton.IsEnabled = false;
            SetGetButtonsEnabled(false);
            Downloader.FailedDownloads = 0;

            Downloader.ItemsScheduledForDownload = Items.Where(p => p.IsChecked).OrderBy(p => p.PocketItem.UpdateTime).ToList();
            Dictionary<Item, Video> itemsWithVideoInfo = new Dictionary<Item, Video>();

            try
            {
                //"Pre-generate" all video infos
                YoutubeClient client = new YoutubeClient();
                foreach (Item item in Downloader.ItemsScheduledForDownload)
                    await item.GetOrGenerateVideoInfo();
            }
            catch (Exception ex)
            {
                Device.BeginInvokeOnMainThread(() => DisplayAlert("", "An error was encountered when trying to get video information\n\n" + ex.Message, "Ok"));
                HideBottomBar();
                DownloadButton.IsEnabled = true;
                SetGetButtonsEnabled(true);
                return;
            }


            int chunkSize = Settings.Instance.ChunkSize;
            List<List<Item>> chunkedItemsToDownload = new List<List<Item>>();

            if (chunkSize > 0)
            {
                chunkedItemsToDownload = Downloader.ItemsScheduledForDownload.Select((x, i) => new { Index = i, Value = x })
                                                        .GroupBy(x => x.Index / chunkSize)
                                                        .Select(x => x.Select(v => v.Value).ToList())
                                                        .ToList();
            }
            else
                chunkedItemsToDownload = new List<List<Item>> { Downloader.ItemsScheduledForDownload };

            List<Task> progressTasks = new List<Task>();
            foreach (List<Item> chunk in chunkedItemsToDownload)
            {
                progressTasks.Clear();
                Downloader.FilesDownloading.Clear();
                foreach (Item item in chunk)
                {
                    Task itemTask = Downloader.DownloadPocketItem(item);
                    progressTasks.Add(itemTask);
                }

                await Task.WhenAll(progressTasks.ToArray());
            }

            Device.BeginInvokeOnMainThread(() =>
            {
                if (Downloader.FailedDownloads > 0)
                    DisplayAlert("", $"Done downloading items ({Downloader.FailedDownloads} failed)", "Ok");
                else
                    DisplayAlert("", "Done downloading items", "Ok");

                //notificationManager.ScheduleNotification("Done downloading items", "");

                HideBottomBar();
                DownloadButton.IsEnabled = true;
                SetGetButtonsEnabled(true);
            });
        }

        private async void SelectMissing(object sender, EventArgs e)
        {
            Dictionary<Item, string> itemsWithGeneratedFileNames = new Dictionary<Item, string>();

            try
            {
                YoutubeClient client = new YoutubeClient();
                foreach (Item item in Items)
                {
                    Video videoInfo = await client.GetVideoAsync(YoutubeClient.ParseVideoId(item.PocketItem.Uri.ToString()));

                    string fileName = $"[{videoInfo.Author}] {videoInfo.Title}.mp4";
                    fileName = Utilities.RemoveInvalidPathCharacters(fileName);
                    itemsWithGeneratedFileNames.Add(item, fileName);
                }
            }
            catch (Exception ex)
            {
                Device.BeginInvokeOnMainThread(() => DisplayAlert("", "An error was encountered when trying to get video information\n\n" + ex.Message, "Ok"));
                return;
            }

            List<FileInfo> files = new DirectoryInfo(Downloader.DownloadDirectory).GetFiles().ToList();
            List<Item> missingItems = itemsWithGeneratedFileNames.Where(p => files.Find(f => f.Name == p.Value) == null).Select(p => p.Key).ToList(); //Todo: also somehow check incomplete downloads

            if (missingItems.Count > 0)
            {
                Items.ToList().ForEach(p => p.IsChecked = false);
                missingItems.ForEach(p => p.IsChecked = true);

                Device.BeginInvokeOnMainThread(() => DisplayAlert("", $"{missingItems.Count} missing items selected for download", "Ok"));
            }
            else
                Device.BeginInvokeOnMainThread(() => DisplayAlert("", "No missing items found", "Ok"));
        }

        private void OpenSettings(object sender, EventArgs e)
        {
            Navigation.PushAsync(new SettingsPage());
        }

        private void GetWithRange(object sender, EventArgs e)
        {
            Items.Clear();
            string buttonText = (sender as Button).Text;
            switch (buttonText)
            {
                case "1D":
                    _ = GetPocketItems(DateTime.Now.AddDays(-1));
                    break;
                case "2D":
                    _ = GetPocketItems(DateTime.Now.AddDays(-2));
                    break;
                case "3D":
                    _ = GetPocketItems(DateTime.Now.AddDays(-3));
                    break;
                case "5D":
                    _ = GetPocketItems(DateTime.Now.AddDays(-5));
                    break;
                case "1W":
                    _ = GetPocketItems(DateTime.Now.AddDays(-7));
                    break;
                case "2W":
                    _ = GetPocketItems(DateTime.Now.AddDays(-14));
                    break;
                case "1M":
                    _ = GetPocketItems(DateTime.Now.AddMonths(-1));
                    break;
            }
        }

        private void SelectAll(object sender, EventArgs e)
        {
            bool selectAll = !Items.All(p => p.IsChecked);

            foreach (Item item in Items)
                item.IsChecked = selectAll;
        }

        private void ListView_ItemTapped(object sender, ItemTappedEventArgs e)
        {
            (e.Item as Item).IsChecked = !(e.Item as Item).IsChecked;
        }

        private void ShowBottomBar()
        {
            BottomBar.IsVisible = true;
            SetPauseResumeButton("pause");
            SetTotalProgress(0);
        }

        private void HideBottomBar()
        {
            BottomBar.IsVisible = false;
        }

        private void SetTotalProgress(double progress)
        {
            TotalProgressBar.Progress = progress;
            TotalProgressLabel.Text = progress < 0 ? " ERR" : $"{Math.Round(progress * 100, 2),5:0.0}%";
        }

        private void SetPauseResumeButton(string pauseOrResume)
        {
            //Remove any already existing event handlers
            PauseResumeButton.Clicked -= PauseDownloads;
            PauseResumeButton.Clicked -= ResumeDownloads;

            if (pauseOrResume == "pause")
            {
                PauseResumeButton.Text = "Pause";
                PauseResumeButton.Clicked += PauseDownloads;
            }
            else if (pauseOrResume == "resume")
            {
                PauseResumeButton.Text = "Resume";
                PauseResumeButton.Clicked += ResumeDownloads;
            }
        }

        private void SetGetButtonsEnabled(bool enabled)
        {
            GetButton.IsEnabled = enabled;

            //We can't just disable the parent StackLayout (known bug: https://github.com/xamarin/Xamarin.Forms/issues/2047)
            Button1D.IsEnabled = enabled;
            Button2D.IsEnabled = enabled;
            Button3D.IsEnabled = enabled;
            Button5D.IsEnabled = enabled;
            Button1W.IsEnabled = enabled;
            Button2W.IsEnabled = enabled;
            Button1M.IsEnabled = enabled;
        }
    }
}
