import { NativeModules } from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
import RNFS from 'react-native-fs';
import Logger from './logger';

const { YtDlpModule } = NativeModules;

const QUALITY_FORMATS = {
  best: 'bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best',
  '1080p': 'bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/best[height<=1080][ext=mp4]/best',
  '720p': 'bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/best[height<=720][ext=mp4]/best',
  '480p': 'bestvideo[height<=480][ext=mp4]+bestaudio[ext=m4a]/best[height<=480][ext=mp4]/best',
  '360p': 'bestvideo[height<=360][ext=mp4]+bestaudio[ext=m4a]/best[height<=360][ext=mp4]/best',
  'audio': 'bestaudio[ext=m4a]/bestaudio',
};

class DownloadService {
  constructor() {
    this.activeDownloads = new Set();
  }

  async getDownloadOptions() {
    try {
      const saved = await AsyncStorage.getItem('settings');
      if (saved) {
        const settings = JSON.parse(saved);
        return {
          outputDir: settings.downloadLocation || null,
          format: QUALITY_FORMATS[settings.videoQuality || 'best'] || QUALITY_FORMATS.best,
          sponsorBlock: settings.sponsorBlock ?? false,
          preciseCuts: settings.preciseSponsorCuts ?? false,
        };
      }
    } catch (e) { /* use defaults */ }
    return {
      outputDir: null,
      format: QUALITY_FORMATS.best,
      sponsorBlock: false,
      preciseCuts: false,
    };
  }

  async downloadVideo(item, onProgress) {
    const processId = `dl_${item.id}`;
    this.activeDownloads.add(processId);

    Logger.log(`Downloading "${item.title}" via yt-dlp`);
    Logger.log(`URL: ${item.link}`);

    const options = await this.getDownloadOptions();

    try {
      const result = await YtDlpModule.download(item.link, item.title, processId, options);
      this.activeDownloads.delete(processId);

      const outLines = result.out || '';
      const hasDownloaded = outLines.includes('[download] 100%') ||
        outLines.includes('has already been downloaded') ||
        outLines.includes('[Merger] Merging formats') ||
        outLines.includes('[download] 100% of');

      if (result.success || hasDownloaded) {
        Logger.log(`Download complete: "${item.title}"`);

        const mergeMatch = outLines.match(/\[Merger\] Merging formats into "(.+)"/);
        const destMatch = outLines.match(/Destination: (.+)/);
        const alreadyMatch = outLines.match(/\[download\] (.+) has already been downloaded/);
        const filePath = mergeMatch?.[1] || destMatch?.[1] || alreadyMatch?.[1] || '';

        if (filePath) {
          Logger.log(`Saved to: ${filePath}`);
        }

        return { success: true, path: filePath };
      } else {
        const errMsg = result.err || outLines;
        Logger.error(`yt-dlp failed (exit code ${result.exitCode}): ${errMsg.substring(0, 200)}`);
        return { success: false, error: `Exit code ${result.exitCode}` };
      }
    } catch (error) {
      this.activeDownloads.delete(processId);
      if (error.code === 'YTDLP_CANCELLED') {
        Logger.log(`Download cancelled: "${item.title}"`);
        return { success: false, error: 'Cancelled' };
      }
      Logger.error(`Download exception for "${item.title}": ${error.message}`);
      return { success: false, error: error.message };
    }
  }

  async stopDownload(itemId) {
    const processId = `dl_${itemId}`;
    if (this.activeDownloads.has(processId)) {
      await YtDlpModule.cancelDownload(processId);
      this.activeDownloads.delete(processId);
    }
  }

  async stopAll() {
    const promises = [...this.activeDownloads].map((processId) =>
      YtDlpModule.cancelDownload(processId).catch(() => {})
    );
    await Promise.all(promises);
    this.activeDownloads.clear();
  }

  get activeCount() {
    return this.activeDownloads.size;
  }
}

export default new DownloadService();
