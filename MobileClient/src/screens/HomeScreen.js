import React, { useState, useEffect, useCallback, useRef } from 'react';
import {
  View,
  Text,
  FlatList,
  TouchableOpacity,
  StyleSheet,
  Alert,
  ActivityIndicator,
  Image,
  InteractionManager,
} from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
import NetInfo from '@react-native-community/netinfo';
import DateTimePicker from '@react-native-community/datetimepicker';
import ApiService from '../services/api';
import DownloadService from '../services/download';
import Logger from '../services/logger';

const QUICK_RANGES = [
  { label: '1D', days: 1 },
  { label: '2D', days: 2 },
  { label: '3D', days: 3 },
  { label: '5D', days: 5 },
  { label: '1W', days: 7 },
  { label: '2W', days: 14 },
  { label: '1M', days: 30 },
];

export default function HomeScreen({ navigation }) {
  const [bookmarks, setBookmarks] = useState([]);
  const [selected, setSelected] = useState(new Set());
  const [downloading, setDownloading] = useState(false);
  const [paused, setPaused] = useState(false);
  const [progress, setProgress] = useState({});
  const [loading, setLoading] = useState(false);
  const [sinceDate, setSinceDate] = useState(new Date());
  const [activeRange, setActiveRange] = useState(null);
  const [showDatePicker, setShowDatePicker] = useState(false);
  const pausedRef = useRef(false);
  const stoppedRef = useRef(false);
  const queueRef = useRef([]);
  const progressBufferRef = useRef({});
  const progressFlushTimerRef = useRef(null);

  const fetchBookmarks = useCallback(async (filterDate) => {
    setLoading(true);
    try {
      const data = await ApiService.getBookmarks();
      let filtered = data;
      if (filterDate) {
        filtered = data.filter((item) => new Date(item.created) >= filterDate);
        Logger.log(`Filtered to ${filtered.length} items since ${filterDate.toLocaleDateString()}`);
      }
      setBookmarks(filtered);
    } catch (error) {
      Logger.error('Failed to fetch bookmarks', error);
      Alert.alert('Error', 'Failed to fetch bookmarks: ' + error.message);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchBookmarks(null);
  }, [fetchBookmarks]);

  const handleGet = () => {
    setActiveRange(null);
    fetchBookmarks(sinceDate);
  };

  const handleQuickRange = (range) => {
    const date = new Date();
    date.setDate(date.getDate() - range.days);
    setSinceDate(date);
    setActiveRange(range.label);
    fetchBookmarks(date);
  };

  const toggleSelect = (id) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const selectAll = () => {
    if (selected.size === bookmarks.length) {
      setSelected(new Set());
    } else {
      setSelected(new Set(bookmarks.map((b) => b.id)));
    }
  };

  // Compute total progress from selected items
  const getTotalProgress = () => {
    const selectedItems = bookmarks.filter((b) => selected.has(b.id));
    if (selectedItems.length === 0) return 0;
    let total = 0;
    selectedItems.forEach((item) => {
      const p = progress[item.id];
      if (p !== undefined && p >= 0) {
        total += p > 1.0 ? 1.0 : p;
      }
    });
    return total / selectedItems.length;
  };

  const startDownloads = async () => {
    const netState = await NetInfo.fetch();
    if (!netState.isWifiEnabled || netState.type !== 'wifi') {
      const proceed = await new Promise((resolve) =>
        Alert.alert(
          'No WiFi',
          'You are not on WiFi. Continue downloading?',
          [
            { text: 'Cancel', onPress: () => resolve(false) },
            { text: 'Continue', onPress: () => resolve(true) },
          ]
        )
      );
      if (!proceed) return;
    }

    setDownloading(true);
    setPaused(false);
    pausedRef.current = false;
    stoppedRef.current = false;
    setProgress({});
    const itemsToDownload = bookmarks.filter((b) => selected.has(b.id));

    let chunkSize = 3;
    try {
      const saved = await AsyncStorage.getItem('settings');
      if (saved) {
        const settings = JSON.parse(saved);
        chunkSize = settings.chunkSize || 3;
      }
    } catch (e) { /* use default */ }

    Logger.log(`Starting download of ${itemsToDownload.length} items (${chunkSize} simultaneous)`);

    // Throttled progress updates — buffer events and flush every 500ms
    progressBufferRef.current = {};
    const flushProgress = () => {
      const buffer = progressBufferRef.current;
      if (Object.keys(buffer).length > 0) {
        setProgress((prev) => {
          const updated = { ...prev };
          for (const [id, val] of Object.entries(buffer)) {
            if (updated[id] !== 1.0 && updated[id] !== -1) {
              updated[id] = val;
            }
          }
          return updated;
        });
        progressBufferRef.current = {};
      }
    };
    progressFlushTimerRef.current = setInterval(flushProgress, 500);

    const titleById = {};
    itemsToDownload.forEach((it) => { titleById[it.id] = it.title; });
    const processingItems = new Set();
    const dlMax = {};

    const progressSub = ApiService.getProgressEmitter().addListener(
      'YtDlpProgress',
      (event) => {
        const itemId = event.processId?.replace('dl_', '');
        if (!itemId) return;
        const line = event.line || '';
        const isPostProcessing = line.includes('[Merger]') || line.includes('[SponsorBlock]') ||
          line.includes('[ffmpeg]') || line.includes('[FixupM3u8]') || line.includes('[ModifyChapters]');
        if (isPostProcessing) {
          // Sentinel: 1.01 = post-processing (download finished, still merging / cutting)
          progressBufferRef.current[itemId] = 1.01;
          if (!processingItems.has(itemId)) {
            processingItems.add(itemId);
            Logger.log(`Processing "${titleById[itemId] || itemId}"… (merging / SponsorBlock — can take several minutes)`);
          }
        } else if (event.progress >= 0 && !processingItems.has(itemId)) {
          // Cap below 1.0 (exactly 1.0 means fully finished) and keep it monotonic so the
          // bar doesn't jump backwards when a second stream (audio) starts downloading.
          const capped = Math.min(event.progress, 0.999);
          const next = Math.max(capped, dlMax[itemId] || 0);
          dlMax[itemId] = next;
          progressBufferRef.current[itemId] = next;
        }
      }
    );

    // Queue-based concurrent downloads that respect pause
    queueRef.current = [...itemsToDownload];
    let active = 0;
    let activeItems = new Set();
    let failed = 0;

    await new Promise((resolveAll) => {
      const tryNext = () => {
        if (stoppedRef.current) {
          if (active === 0) resolveAll();
          return;
        }
        if (pausedRef.current) {
          setTimeout(tryNext, 300);
          return;
        }
        while (active < chunkSize && queueRef.current.length > 0 && !stoppedRef.current && !pausedRef.current) {
          const item = queueRef.current.shift();
          active++;
          activeItems.add(item.id);
          (async () => {
            try {
              const result = await DownloadService.downloadVideo(item);
              if (result.error === 'Cancelled' && pausedRef.current) {
                // Re-queue cancelled items from pause — reset their progress tracking
                dlMax[item.id] = 0;
                processingItems.delete(item.id);
                queueRef.current.unshift(item);
              } else if (!result.success) {
                setProgress((prev) => ({ ...prev, [item.id]: -1 }));
                failed++;
              } else {
                setProgress((prev) => ({ ...prev, [item.id]: 1.0 }));
              }
            } catch (error) {
              Logger.error(`Download failed for "${item.title}"`, error);
              setProgress((prev) => ({ ...prev, [item.id]: -1 }));
              failed++;
            }
            active--;
            activeItems.delete(item.id);
            if (queueRef.current.length === 0 && active === 0) {
              resolveAll();
            } else {
              tryNext();
            }
          })();
        }
        if (queueRef.current.length === 0 && active === 0) {
          resolveAll();
        }
      };
      tryNext();
    });

    progressSub.remove();
    clearInterval(progressFlushTimerRef.current);
    setDownloading(false);
    setPaused(false);
    if (stoppedRef.current) {
      Logger.log('Downloads stopped by user');
    } else if (failed > 0) {
      Alert.alert('Done', `Done downloading items (${failed} failed)`);
    } else {
      Alert.alert('Done', 'All downloads completed successfully');
    }
  };

  const handlePause = () => {
    const newPaused = !paused;
    setPaused(newPaused);
    pausedRef.current = newPaused;
    if (newPaused) {
      Logger.log('Downloads pausing...');
      // Let UI update render, then cancel active downloads
      InteractionManager.runAfterInteractions(() => {
        DownloadService.stopAll();
      });
    } else {
      Logger.log('Downloads resumed');
    }
  };

  const handleStop = () => {
    stoppedRef.current = true;
    pausedRef.current = false;
    setPaused(false);
    InteractionManager.runAfterInteractions(() => {
      DownloadService.stopAll();
    });
  };

  const renderItem = ({ item }) => {
    const isSelected = selected.has(item.id);
    const itemProgress = progress[item.id];
    const isProcessing = itemProgress === 1.01;
    const progressFraction = isProcessing ? 1.0 : (itemProgress !== undefined && itemProgress >= 0 ? itemProgress : 0);
    const isError = itemProgress !== undefined && itemProgress < 0;
    const isComplete = itemProgress === 1.0;

    return (
      <TouchableOpacity
        style={styles.item}
        onPress={() => toggleSelect(item.id)}
        disabled={downloading}
      >
        <Text style={styles.title} numberOfLines={2}>
          {item.title}
        </Text>
        <View style={styles.itemRow}>
          <View style={styles.checkboxContainer}>
            <View style={[styles.checkbox, isSelected && styles.checkboxSelected]}>
              {isSelected && <Text style={styles.checkmark}>✓</Text>}
            </View>
          </View>
          {item.cover ? (
            <Image source={{ uri: item.cover }} style={styles.thumbnail} resizeMode="cover" />
          ) : (
            <View style={[styles.thumbnail, styles.thumbnailPlaceholder]}>
              <Text style={styles.placeholderText}>No Image</Text>
            </View>
          )}
          <View style={styles.progressSide}>
            {isError && <Text style={styles.errorText}>ERROR</Text>}
            {!isError && isProcessing && (
              <Text style={styles.processingText}>Processing…</Text>
            )}
            {!isError && !isProcessing && itemProgress !== undefined && (
              <Text style={styles.progressText}>{(progressFraction * 100).toFixed(1)}%</Text>
            )}
          </View>
        </View>
        {/* Per-item progress bar */}
        {itemProgress !== undefined && (
          <View style={styles.itemProgressBarBg}>
            <View
              style={[
                styles.itemProgressBarFill,
                {
                  width: `${Math.min(progressFraction * 100, 100)}%`,
                  backgroundColor: isError ? '#ff5252' : isComplete ? '#66bb6a' : isProcessing ? '#ffa726' : '#5c6bc0',
                },
              ]}
            />
          </View>
        )}
      </TouchableOpacity>
    );
  };

  const totalProgress = getTotalProgress();

  return (
    <View style={styles.container}>
      <View style={styles.header}>
        <Text style={styles.headerTitle}>Raindrop Downloader</Text>
        <TouchableOpacity onPress={() => navigation.navigate('Settings')}>
          <Text style={styles.menuIcon}>⋮</Text>
        </TouchableOpacity>
      </View>

      {/* Date picker + GET */}
      <View style={styles.dateRow}>
        <TouchableOpacity
          onPress={() => setShowDatePicker(true)}
          style={styles.datePickerButton}
          disabled={downloading}
        >
          <Text style={styles.datePickerText}>
            {sinceDate.toLocaleDateString()}
          </Text>
        </TouchableOpacity>
        <TouchableOpacity
          style={[styles.getButton, downloading && styles.buttonDisabledOpacity]}
          onPress={handleGet}
          disabled={downloading || loading}
        >
          <Text style={styles.getButtonText}>GET</Text>
        </TouchableOpacity>
      </View>

      {showDatePicker && (
        <DateTimePicker
          value={sinceDate}
          mode="date"
          onChange={(event, date) => {
            setShowDatePicker(false);
            if (date) {
              setSinceDate(date);
              setActiveRange(null);
            }
          }}
        />
      )}

      {/* Quick range buttons */}
      <View style={styles.rangeRow}>
        {QUICK_RANGES.map((range) => (
          <TouchableOpacity
            key={range.label}
            style={[styles.rangeButton, activeRange === range.label && styles.rangeButtonActive]}
            onPress={() => handleQuickRange(range)}
            disabled={downloading || loading}
          >
            <Text style={[styles.rangeButtonText, activeRange === range.label && styles.rangeButtonTextActive]}>
              {range.label}
            </Text>
          </TouchableOpacity>
        ))}
      </View>

      {/* Select all + Download */}
      <View style={styles.actionBar}>
        <TouchableOpacity onPress={selectAll} disabled={downloading}>
          <View style={[styles.checkbox, selected.size === bookmarks.length && bookmarks.length > 0 && styles.checkboxSelected]}>
            {selected.size === bookmarks.length && bookmarks.length > 0 && <Text style={styles.checkmark}>✓</Text>}
          </View>
        </TouchableOpacity>
        <TouchableOpacity
          style={[styles.downloadButton, (downloading || selected.size === 0) && styles.downloadButtonDisabled]}
          onPress={startDownloads}
          disabled={downloading || selected.size === 0}
        >
          <Text style={styles.downloadButtonText}>DOWNLOAD SELECTED</Text>
        </TouchableOpacity>
      </View>

      {loading ? (
        <ActivityIndicator size="large" color="#5c6bc0" style={styles.loader} />
      ) : (
        <FlatList
          data={bookmarks}
          renderItem={renderItem}
          keyExtractor={(item) => String(item.id)}
          contentContainerStyle={styles.list}
        />
      )}

      {/* Bottom bar: total progress + pause/stop */}
      {downloading && (
        <View style={styles.bottomBar}>
          <View style={styles.totalProgressBarBg}>
            <View
              style={[styles.totalProgressBarFill, { width: `${Math.min(totalProgress * 100, 100)}%` }]}
            />
          </View>
          <View style={styles.bottomControls}>
            <Text style={styles.totalProgressText}>{(totalProgress * 100).toFixed(1)}%</Text>
            <View style={styles.bottomButtons}>
              <TouchableOpacity style={styles.pauseButton} onPress={handlePause}>
                <Text style={styles.pauseButtonText}>{paused ? 'RESUME' : 'PAUSE'}</Text>
              </TouchableOpacity>
              <TouchableOpacity style={styles.stopButton} onPress={handleStop}>
                <Text style={styles.stopButtonText}>STOP</Text>
              </TouchableOpacity>
            </View>
          </View>
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: '#3a3f51' },
  header: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingHorizontal: 16,
    paddingTop: 0,
    paddingBottom: 8,
    backgroundColor: '#4a5068',
  },
  headerTitle: { fontSize: 20, fontWeight: 'bold', color: '#e0e0e0' },
  menuIcon: { fontSize: 24, color: '#e0e0e0', paddingHorizontal: 8 },
  dateRow: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 16,
    paddingVertical: 8,
    gap: 12,
  },
  datePickerButton: {
    flex: 1,
    backgroundColor: '#555a6e',
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderRadius: 4,
    borderWidth: 1,
    borderColor: '#6a6f85',
    alignItems: 'center',
  },
  datePickerText: { color: '#e0e0e0', fontSize: 14 },
  getButton: {
    backgroundColor: '#5c6bc0',
    paddingHorizontal: 20,
    paddingVertical: 6,
    borderRadius: 4,
  },
  getButtonText: { color: '#fff', fontSize: 14, fontWeight: 'bold' },
  buttonDisabledOpacity: { opacity: 0.5 },
  rangeRow: {
    flexDirection: 'row',
    paddingHorizontal: 16,
    paddingBottom: 8,
    gap: 6,
  },
  rangeButton: {
    backgroundColor: '#4a5068',
    paddingHorizontal: 12,
    paddingVertical: 6,
    borderRadius: 4,
    borderWidth: 1,
    borderColor: '#5c6370',
  },
  rangeButtonActive: { backgroundColor: '#5c6bc0', borderColor: '#5c6bc0' },
  rangeButtonText: { color: '#c0c0c0', fontSize: 13, fontWeight: 'bold' },
  rangeButtonTextActive: { color: '#fff' },
  actionBar: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 16,
    paddingVertical: 8,
    gap: 12,
  },
  downloadButton: {
    flex: 1,
    backgroundColor: '#5c6bc0',
    paddingVertical: 10,
    borderRadius: 4,
    alignItems: 'center',
  },
  downloadButtonDisabled: { opacity: 0.5 },
  downloadButtonText: { color: '#fff', fontSize: 14, fontWeight: 'bold' },
  list: { paddingHorizontal: 8, paddingBottom: 80 },
  item: {
    backgroundColor: '#4a5068',
    borderRadius: 4,
    padding: 10,
    marginBottom: 8,
    borderBottomWidth: 1,
    borderBottomColor: '#5c6370',
  },
  title: { color: '#e0e0e0', fontSize: 14, marginBottom: 6 },
  itemRow: { flexDirection: 'row', alignItems: 'center' },
  checkboxContainer: { marginRight: 10 },
  checkbox: {
    width: 24,
    height: 24,
    borderWidth: 2,
    borderColor: '#7986cb',
    borderRadius: 3,
    justifyContent: 'center',
    alignItems: 'center',
    backgroundColor: 'transparent',
  },
  checkboxSelected: { backgroundColor: '#5c6bc0', borderColor: '#5c6bc0' },
  checkmark: { color: '#fff', fontSize: 16, fontWeight: 'bold' },
  thumbnail: { width: 128, height: 72, borderRadius: 3 },
  thumbnailPlaceholder: { backgroundColor: '#2c2f3a', justifyContent: 'center', alignItems: 'center' },
  placeholderText: { color: '#888', fontSize: 11 },
  progressSide: { flex: 1, alignItems: 'flex-end', paddingRight: 4 },
  progressText: { color: '#e0e0e0', fontSize: 13 },
  processingText: { color: '#ffa726', fontSize: 12, fontStyle: 'italic' },
  errorText: { color: '#ff5252', fontSize: 13, fontWeight: 'bold' },
  // Per-item progress bar
  itemProgressBarBg: {
    height: 4,
    backgroundColor: '#2c2f3a',
    borderRadius: 2,
    marginTop: 6,
    overflow: 'hidden',
  },
  itemProgressBarFill: {
    height: '100%',
    borderRadius: 2,
  },
  // Bottom bar
  bottomBar: {
    position: 'absolute',
    bottom: 0,
    left: 0,
    right: 0,
    backgroundColor: '#3a3f51',
    borderTopWidth: 1,
    borderTopColor: '#5c6370',
    paddingHorizontal: 16,
    paddingVertical: 8,
  },
  totalProgressBarBg: {
    height: 6,
    backgroundColor: '#2c2f3a',
    borderRadius: 3,
    overflow: 'hidden',
    marginBottom: 8,
  },
  totalProgressBarFill: {
    height: '100%',
    backgroundColor: '#5c6bc0',
    borderRadius: 3,
  },
  bottomControls: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  totalProgressText: { color: '#e0e0e0', fontSize: 14, fontWeight: 'bold' },
  bottomButtons: { flexDirection: 'row', gap: 10 },
  pauseButton: {
    backgroundColor: '#f9a825',
    paddingHorizontal: 16,
    paddingVertical: 8,
    borderRadius: 4,
  },
  pauseButtonText: { color: '#000', fontSize: 13, fontWeight: 'bold' },
  stopButton: {
    backgroundColor: '#c62828',
    paddingHorizontal: 16,
    paddingVertical: 8,
    borderRadius: 4,
  },
  stopButtonText: { color: '#fff', fontSize: 13, fontWeight: 'bold' },
  loader: { flex: 1, justifyContent: 'center' },
});
