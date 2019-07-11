using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PocketSharp;
using PocketSharp.Models;
using YoutubeExplode;
using YoutubeExplode.Models.MediaStreams;

namespace PocketDownloader
{
    class Program
    {
        public class ConsoleItem
        {
            public PocketItem Item { get; set; }
            public int ConsoleLocLeft { get; set; }
            public int ConsoleLocTop { get; set; }
        }

        const string ConsumerKey = "85079-85ce76fc6f685bfd96affa74";
        static PocketClient PocketClient = null;
        static string DownloadDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        static void Main(string[] args)
        {
            AuthPocket();

            List<PocketItem> allArticles = GetPocketArticles();
            List<PocketItem> selectedArticles = GetSelectionFromList(allArticles);
            selectedArticles = ValidateItems(selectedArticles);

            Console.WriteLine(); //Line separator

            List<ConsoleItem> consoleItems = ConvertToConsoleItems(selectedArticles);
            int maxLeft = consoleItems.Max(p => p.ConsoleLocLeft);
            consoleItems.ForEach(p => p.ConsoleLocLeft = maxLeft);

            int finalCursorLeft = Console.CursorLeft;
            int finalCursorTop = Console.CursorTop;

            Console.CursorVisible = false;
            List<Task> tasksForDownloads = StartTasks(consoleItems);

            Task.WaitAll(tasksForDownloads.ToArray());
            Console.CursorVisible = true;
            Console.SetCursorPosition(finalCursorLeft, finalCursorTop);
            Console.WriteLine($"All videos downloaded to {DownloadDirectory}");
            Console.WriteLine("Press any key to exit");
            Console.Read();
        }

        public static void AuthPocket()
        {
            PocketClient = new PocketClient(ConsumerKey);
            PocketClient.CallbackUri = "https://getpocket.com/a/queue/"; //Todo: prevent this from opening
            string requestCode = PocketClient.GetRequestCode().Result;
            Uri authenticationUri = PocketClient.GenerateAuthenticationUri();
            Process.Start(authenticationUri.ToString());

            PocketUser user = null;
            while (true)
            {
                try
                {
                    user = PocketClient.GetUser(requestCode).Result;
                    break;
                }
                catch { }
                System.Threading.Thread.Sleep(500);
            }

            PocketClient = new PocketClient(ConsumerKey, user.Code);
        }

        public static List<PocketItem> GetPocketArticles()
        {
            return PocketClient.Get().Result.ToList();
        }

        public static string GetTitleForDisplay(PocketItem item, bool withQuotes = true)
        {
            string titleToDisplay = item.Title;
            if (titleToDisplay.Length > 50) //Truncate the title to fit nicely in the console
                titleToDisplay = titleToDisplay.Substring(0, 50) + "...";

            if (withQuotes)
                titleToDisplay = $"\"{titleToDisplay}\"";

            return titleToDisplay;
        }

        public static List<PocketItem> GetSelectionFromList(List<PocketItem> listToDisplay)
        {
            foreach (PocketItem item in listToDisplay)
                Console.WriteLine("(" + listToDisplay.IndexOf(item) + ") " + GetTitleForDisplay(item, false));

            Console.WriteLine(); //Line separator
            Console.WriteLine("Which # would you like? (you can choose multiple items with commas or ranges eg \"1-3,7,9\")");
            string input = Console.ReadLine();
            input = Regex.Replace(input, @"[^\d-,]", ""); //Remove any characters that are not numbers, commas, or hyphens

            List<PocketItem> result = new List<PocketItem>();
            foreach (string indexStr in input.Split(','))
            {
                if (Regex.IsMatch(indexStr, @"\d+-\d+"))
                {
                    int startOfRange = Convert.ToInt32(indexStr.Split('-')[0]);
                    int endOfRange = Convert.ToInt32(indexStr.Split('-')[1]);
                    for (int i = startOfRange; i <= endOfRange; i++)
                    {
                        if (i < 0 || i > listToDisplay.Count - 1)
                        {
                            Console.WriteLine("Could not find index " + i);
                            continue;
                        }

                        result.Add(listToDisplay[i]);
                    }
                }
                else if (Regex.IsMatch(indexStr, @"\d+"))
                {
                    int index = Convert.ToInt32(indexStr);
                    if (index < 0 || index > listToDisplay.Count - 1)
                    {
                        Console.WriteLine("Could not find index " + index);
                        continue;
                    }

                    result.Add(listToDisplay[index]);
                }
            }

            return result;
        }

        public static List<PocketItem> ValidateItems(List<PocketItem> selectedItems)
        {
            List<PocketItem> invalidItems = selectedItems.Where(p => { string id = ""; YoutubeClient.TryParseVideoId(p.Uri.ToString(), out id); return id == null; }).ToList();
            invalidItems.ForEach(p => Console.WriteLine($"Cannot download {GetTitleForDisplay(p)} because it is not a YouTube video"));
            selectedItems.RemoveAll(p => invalidItems.Contains(p));

            return selectedItems;
        }

        public static List<ConsoleItem> ConvertToConsoleItems(List<PocketItem> listToConvert)
        {
            List<ConsoleItem> consoleItems = new List<ConsoleItem>();
            foreach (PocketItem item in listToConvert)
            {
                Console.Write($"Downloading {GetTitleForDisplay(item)} ");

                ConsoleItem consoleItem = new ConsoleItem()
                {
                    Item = item,
                    ConsoleLocLeft = Console.CursorLeft,
                    ConsoleLocTop = Console.CursorTop
                };
                consoleItems.Add(consoleItem);

                Console.Write("\n");
            }

            return consoleItems;
        }

        public static List<Task> StartTasks(List<ConsoleItem> itemsToDownload)
        {
            List<Task> progressTasks = new List<Task>();
            foreach (ConsoleItem item in itemsToDownload)
            {
                Task itemTask = DownloadPocketItem(item);
                progressTasks.Add(itemTask);
            }

            return progressTasks;
        }

        public static async Task DownloadPocketItem(ConsoleItem itemToDownload)
        {
            string youTubeVideoId = YoutubeClient.ParseVideoId(itemToDownload.Item.Uri.ToString());

            YoutubeClient client = new YoutubeClient();
            var videoInfo = await client.GetVideoAsync(youTubeVideoId);
            var streamInfoSet = await client.GetVideoMediaStreamInfosAsync(youTubeVideoId);
            List<VideoStreamInfo> qualities = streamInfoSet.Video.OrderByDescending(s => s.VideoQuality).ToList();

            if (qualities.Count == 0)
            {
                ThrowDownloadError(itemToDownload);
                return;
            }

            string fileExtension = qualities[0].Container.GetFileExtension();
            string fileName = "[" + videoInfo.Author + "] " + videoInfo.Title + "." + fileExtension;

            //Remove invalid characters from filename
            string regexSearch = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
            Regex r = new Regex(string.Format("[{0}]", Regex.Escape(regexSearch)));
            fileName = r.Replace(fileName, "");

            string fullTargetPath = Path.Combine(DownloadDirectory, fileName);
            if (File.Exists(fullTargetPath))
                File.Delete(fullTargetPath);

            //Loop through qualities highest to lowest (in case high qualities fail) as suggested in https://github.com/Tyrrrz/YoutubeExplode/issues/219
            foreach (VideoStreamInfo videoQuality in qualities)
            {
                Progress<double> progress = new Progress<double>();
                progress.ProgressChanged += (s, e) => { UpdatePercentange(itemToDownload, Math.Round(e * 100, 1)); };

                try
                {
                    await client.DownloadMediaStreamAsync(qualities[0], fullTargetPath, progress);

                    return;
                }
                catch (Exception)  //Catch errors caused by https://github.com/Tyrrrz/YoutubeExplode/issues/219
                {
                    ThrowDownloadError(itemToDownload);

                    if (File.Exists(fullTargetPath))
                        File.Delete(fullTargetPath);
                }
            }
        }

        private static object _sync = new object();
        private static void UpdatePercentange(ConsoleItem item, double percentage)
        {
            lock (_sync)
            {
                Console.SetCursorPosition(item.ConsoleLocLeft, item.ConsoleLocTop);
                Console.Write($"({string.Format("{0:F1}", percentage)}%)");
            }
        }

        private static void ThrowDownloadError(ConsoleItem item, string message = "")
        {
            lock (_sync)
            {
                Console.SetCursorPosition(item.ConsoleLocLeft, item.ConsoleLocTop);
                Console.Write($"ERROR");
                if (!string.IsNullOrEmpty(message))
                    Console.Write(": " + message);
            }
        }
    }
}