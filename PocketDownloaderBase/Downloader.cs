using PocketSharp;
using PocketSharp.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Models;
using YoutubeExplode.Models.MediaStreams;

namespace PocketDownloaderBase
{
    public class Downloader //Todo: should probably rename this class
    {
        #region Private Properties
        const string POCKETCONSUMERKEY = "85079-85ce76fc6f685bfd96affa74";
        private static PocketClient pocketClient;
        #endregion Private Properties


        #region Public Properties
        public static string DownloadDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        public static IProgress<double> TotalProgress { get; set; }
        public static int FailedDownloads { get; set; } = 0;
        public static List<Item> ItemsScheduledForDownload { get; set; } = new List<Item>();
        public static List<FileDownload> FilesDownloading { get; set; } = new List<FileDownload>();
        #endregion Public Properties


        #region Private Methods
        private static void UpdateTotalProgress()
        {
            double totalDownloadSeconds = ItemsScheduledForDownload.Sum(p => p.Progress >= 0 ? p.VideoInfo.Duration.TotalSeconds : 0);
            double totalProgress = ItemsScheduledForDownload.Sum(p => p.Progress >= 0 ? p.Progress * (p.VideoInfo.Duration.TotalSeconds / totalDownloadSeconds) : 0);

            TotalProgress?.Report(totalProgress);
        }

        private static async Task<bool> DownloadWithYouTubeExplode(Item itemToDownload, string targetPath, YoutubeClient client, Progress<double> progress = null)
        {
            FileDownload fileDownload = null;

            try
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);

                string youTubeVideoId = YoutubeClient.ParseVideoId(itemToDownload.PocketItem.Uri.ToString());
                Video videoInfo = await client.GetVideoAsync(youTubeVideoId);
                MediaStreamInfoSet streamInfoSet = await client.GetVideoMediaStreamInfosAsync(youTubeVideoId);
                List<MuxedStreamInfo> qualities = streamInfoSet.Muxed.OrderByDescending(s => s.VideoQuality).ToList();

                //Loop through qualities highest to lowest (in case high qualities fail) as suggested in https://github.com/Tyrrrz/YoutubeExplode/issues/219
                foreach (MuxedStreamInfo videoQuality in qualities)
                {
                    try
                    {
                        //using (MediaStream stream = await client.GetMediaStreamAsync(videoQuality).ConfigureAwait(false))
                        {
                            fileDownload = new FileDownload(client, videoQuality, targetPath, progress);
                            FilesDownloading.Add(fileDownload);
                            await fileDownload.Start();
                        }

                        FilesDownloading.Remove(fileDownload); //Remove download once it is completed

                        return true;
                    }
                    catch (Exception ex)  //Catch errors caused by https://github.com/Tyrrrz/YoutubeExplode/issues/219
                    {
                        itemToDownload.Progress = -1;

                        if (fileDownload != null)
                            FilesDownloading.Remove(fileDownload);

                        if (File.Exists(targetPath))
                            File.Delete(targetPath);
                    }
                }
            }
            catch (Exception ex)
            {
                if (fileDownload != null)
                    FilesDownloading.Remove(fileDownload);

                return false;
            }

            return false;
        }

        private static async Task<bool> DownloadAlternate(Item itemToDownload, string targetPath, Progress<double> progress = null)
        {
            FileDownload fileDownload = null;

            try
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);

                string youTubeVideoId = YoutubeClient.ParseVideoId(itemToDownload.PocketItem.Uri.ToString());

                string saveMediaURL = $"https://dev.invidio.us/watch?v={youTubeVideoId}"; //"https://odownloader.com/download?q=" + HttpUtility.UrlEncode(itemToDownload.PocketItem.Uri.ToString());
                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("user-agent", "Mozilla/4.0 (compatible; MSIE 6.0; " +
                                          "Windows NT 5.2; .NET CLR 1.0.3705;)");
                    string html = client.DownloadString(saveMediaURL);
                    string videoSources = Regex.Match(html, @"<source.*>").Value;
                    string downloadURLWithHighestQuality = "https://www.invidio.us" + Regex.Match(videoSources, @"src=""([^""]*)""").Groups[1].Value;
                    downloadURLWithHighestQuality = Utilities.GetRedirectURL(downloadURLWithHighestQuality);

                    fileDownload = new FileDownload(downloadURLWithHighestQuality, targetPath, progress: progress);
                    FilesDownloading.Add(fileDownload);
                    await fileDownload.Start();
                }

                FilesDownloading.Remove(fileDownload); //Remove download once it is completed
                return true;
            }
            catch (Exception ex)
            {
                itemToDownload.Progress = -1;

                if (fileDownload != null)
                    FilesDownloading.Remove(fileDownload);

                if (File.Exists(targetPath))
                    File.Delete(targetPath);
            }

            return false;
        }
        #endregion Private Methods


        #region Public Methods
        public static string AuthPocket(string accessCode = null)
        {
            if (string.IsNullOrEmpty(accessCode))
            {
                pocketClient = new PocketClient(POCKETCONSUMERKEY) { CallbackUri = "https://getpocket.com/a/queue/" };
                string requestCode = pocketClient.GetRequestCode().Result;
                Process.Start(pocketClient.GenerateAuthenticationUri().ToString()); //Todo: this doesn't work on all platforms

                PocketUser user;
                while (true)
                {
                    try
                    {
                        user = pocketClient.GetUser(requestCode).Result;
                        break;
                    }
                    catch { }
                    Thread.Sleep(500);
                }

                accessCode = user.Code;
            }

            pocketClient = new PocketClient(POCKETCONSUMERKEY, accessCode);
            return accessCode;
        }

        public static async Task<List<Item>> GetPocketItems(DateTime? sinceDate = null)
        {
            IEnumerable<PocketItem> items = await pocketClient.Get();
            List<PocketItem> pocketItemsList = items.Where(p => p.Uri.ToString().Contains("youtu")).ToList();

            if (sinceDate != null)
                pocketItemsList = pocketItemsList.Where(p => p.UpdateTime.Value >= sinceDate.Value.ToUniversalTime()).ToList();

            return pocketItemsList.Select(p => new Item(p)).ToList();
        }

        public static async Task DownloadPocketItem(Item itemToDownload, Progress<double> progress = null)
        {
            //Generate download path
            YoutubeClient client = new YoutubeClient();
            string fileName;

            try
            {
                Video videoInfo = await client.GetVideoAsync(YoutubeClient.ParseVideoId(itemToDownload.PocketItem.Uri.ToString()));
                fileName = $"[{videoInfo.Author}] {videoInfo.Title}.mp4";
                fileName = Utilities.RemoveInvalidPathCharacters(fileName);
            }
            catch (Exception ex)
            {
                fileName = Utilities.RemoveInvalidPathCharacters(itemToDownload.Title + ".mp4");
            }

            string fullTargetPath = Path.Combine(DownloadDirectory, fileName);

            if (progress == null)
            {
                progress = new Progress<double>();
                progress.ProgressChanged += (s, e) => { itemToDownload.Progress = e; UpdateTotalProgress(); };
            }

            //Download video
            bool success = await DownloadWithYouTubeExplode(itemToDownload, fullTargetPath, client);
            if (!success)
            {
                itemToDownload.Progress = -1;
                success = await DownloadAlternate(itemToDownload, fullTargetPath);
            }

            if (!success)
                FailedDownloads++;
        }
        #endregion Public Methods
    }
}
