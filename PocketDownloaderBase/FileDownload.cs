using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Models.MediaStreams;

namespace PocketDownloaderBase
{
    public class FileDownload
    {
        private volatile bool allowedToRun;
        private Stream sourceStream;
        private MuxedStreamInfo videoQuality;
        private YoutubeClient youtubeClient;
        private string sourceUrl;
        private string destination;
        private bool disposeOnCompletion;
        private int chunkSize;
        private IProgress<double> progress;
        private Lazy<int> contentLength;

        public int BytesWritten { get; private set; }
        public int ContentLength { get { return contentLength.Value; } }
        public bool Done { get { return ContentLength == BytesWritten; } }

        public FileDownload(YoutubeClient client, MuxedStreamInfo quality, string destination, IProgress<double> progress = null)
        {
            this.allowedToRun = true;

            this.youtubeClient = client;
            this.videoQuality = quality;
            this.sourceStream = client.GetMediaStreamAsync(quality).Result;
            this.destination = destination;
            this.disposeOnCompletion = true;
            this.chunkSize = 10000;
            this.contentLength = new Lazy<int>(() => Convert.ToInt32(GetContentLength()));
            this.progress = progress;

            this.BytesWritten = 0;
        }

        public FileDownload(Stream source, string destination, bool disposeOnCompletion = true, int chunkSizeInBytes = 10000 /*Default to 0.01 mb*/, IProgress<double> progress = null)
        {
            this.allowedToRun = true;

            this.sourceStream = source;
            this.destination = destination;
            this.disposeOnCompletion = disposeOnCompletion;
            this.chunkSize = chunkSizeInBytes;
            this.contentLength = new Lazy<int>(() => Convert.ToInt32(GetContentLength()));
            this.progress = progress;

            this.BytesWritten = 0;
        }

        public FileDownload(string source, string destination, int chunkSizeInBytes = 10000 /*Default to 0.01 mb*/, IProgress<double> progress = null)
        {
            this.allowedToRun = true;

            this.sourceUrl = source;
            this.destination = destination;
            this.chunkSize = chunkSizeInBytes;
            this.contentLength = new Lazy<int>(() => Convert.ToInt32(GetContentLength()));
            this.progress = progress;

            this.BytesWritten = 0;
        }

        private long GetContentLength()
        {
            if (sourceStream != null)
                return sourceStream.Length;
            else
            {
                var request = (HttpWebRequest)WebRequest.Create(sourceUrl);
                request.Method = "HEAD";

                using (var response = request.GetResponse())
                    return response.ContentLength;
            }
        }

        private async Task Start(int range)
        {
            if (!allowedToRun)
                throw new InvalidOperationException();

            if (sourceStream != null)
            {
                await DownloadFromStream(sourceStream);

                if (BytesWritten == ContentLength && disposeOnCompletion)
                    sourceStream?.Dispose();
            }
            else
            {
                var request = (HttpWebRequest)WebRequest.Create(sourceUrl);
                request.Method = "GET";
                request.UserAgent = "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; .NET CLR 1.0.3705;)";
                request.AddRange(range);

                using (var response = await request.GetResponseAsync())
                {
                    using (var responseStream = response.GetResponseStream())
                    {
                        await DownloadFromStream(responseStream);
                    }
                }
            }
        }

        private async Task DownloadFromStream(Stream stream)
        {
            using (var fs = new FileStream(destination, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                int retries = 5;
                while (BytesWritten < ContentLength/*allowedToRun*/)
                {
                    try
                    {
                        if (!allowedToRun)
                            continue;

                        var buffer = new byte[chunkSize];
                        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);

                        if (bytesRead == 0)
                            break;

                        await fs.WriteAsync(buffer, 0, bytesRead);
                        BytesWritten += bytesRead;
                        progress?.Report((double)BytesWritten / ContentLength);

                        retries = 0; //Reset retries
                    }
                    catch (Exception ex)
                    {
                        if (retries == 5)
                        {
                            Debug.WriteLine("Refreshing download stream...");
                            stream = await RefreshStream(stream);
                            retries = 0;

                            if (stream == null)
                                throw;
                        }

                        Debug.WriteLine("Download hit an exception. Waiting 500ms before trying again...");
                        Thread.Sleep(500);
                        retries++;
                    }
                }

                await fs.FlushAsync();
            }
        }

        private async Task<Stream> RefreshStream(Stream stream)
        {
            stream?.Dispose();

            int retries = 0;
            while (retries < 10)
            {
                try
                {
                    if (sourceStream != null && sourceStream is MediaStream)
                        return await youtubeClient.GetMediaStreamAsync(videoQuality);
                    else if (sourceUrl != null)
                    {
                        break;
                        //Todo: for some reason this doesn't work (the stream.ReadAsync in DownloadFromStream still throws an Exception)
                        //var request = (HttpWebRequest)WebRequest.Create(sourceUrl);
                        //request.Method = "GET";
                        //request.UserAgent = "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2; .NET CLR 1.0.3705;)";

                        //using (var response = await request.GetResponseAsync())
                        //{
                        //    return response.GetResponseStream();
                        //}
                    }
                }
                catch
                {
                    retries++;
                    if (retries == 10)
                        throw;
                }
            }

            return null;
        }

        public Task Start()
        {
            allowedToRun = true;

            return Start(0);
        }

        public void Resume()
        {
            allowedToRun = true;
        }

        public void Pause()
        {
            allowedToRun = false;
        }
    }
}
