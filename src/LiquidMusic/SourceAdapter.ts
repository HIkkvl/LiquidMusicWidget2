import type { CompanionSettings, PlaybackSnapshot, SourcePlayback } from './types';
import { emptySnapshot } from './types';

type SettingsProvider = () => CompanionSettings;

function record(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

function first(...values: unknown[]): unknown {
  return values.find((value) => value !== undefined && value !== null);
}

function text(value: unknown, fallback = ''): string {
  return typeof value === 'string' || typeof value === 'number' ? String(value) : fallback;
}

function finiteNumber(value: unknown): number | null {
  const parsed = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function booleanValue(value: unknown): boolean {
  if (typeof value === 'boolean') return value;
  if (typeof value === 'number') return value === 1;
  if (typeof value === 'string') return ['playing', 'play', 'true', '1'].includes(value.toLowerCase());
  return false;
}

export function normalizeSourceResponse(raw: unknown, sourceUrl: string, capturedAtMs = Date.now()): SourcePlayback {
  if (raw === null || raw === undefined) return emptySource(capturedAtMs);
  const root = record(raw);
  if (!root) throw new Error('API вернул не JSON-объект.');
  const data = record(root.data) ?? root;
  const container = record(first(data.nowPlaying, data.now_playing, data.playback)) ?? data;
  if (container.track === null || container.hasTrack === false) return emptySource(capturedAtMs);
  const track = record(container.track) ?? {};

  const title = text(first(container.title, track.title, container.name, track.name));
  const artist = text(first(container.artist, track.artist, container.artistName, track.artistName));
  const explicitId = text(first(container.trackId, container.track_id, container.id, track.id));
  const trackId = explicitId || (title ? `${title}\u0000${artist}` : '');
  if (!trackId) return emptySource(capturedAtMs);

  const positionMilliseconds = finiteNumber(first(container.positionMs, container.position_ms, container.progressMs));
  const positionSeconds = finiteNumber(first(container.positionSeconds, container.position_seconds, container.position, container.progress));
  const durationMilliseconds = finiteNumber(first(container.durationMs, container.duration_ms, track.durationMs, track.duration_ms));
  const durationSeconds = finiteNumber(first(container.durationSeconds, container.duration_seconds, container.duration, track.duration));

  const resolveUrl = (value: unknown): string | null => {
    const candidate = text(value).trim();
    if (!candidate) return null;
    try {
      const url = new URL(candidate, sourceUrl);
      return url.protocol === 'http:' || url.protocol === 'https:' ? url.toString() : null;
    } catch {
      return null;
    }
  };

  return {
    trackId,
    title: title || 'Неизвестный трек',
    artist: artist || 'Неизвестный исполнитель',
    album: text(first(container.album, track.album, container.albumTitle, track.albumTitle)),
    artworkUrl: resolveUrl(first(container.artworkUrl, container.artwork_url, container.artwork, track.artworkUrl, track.artwork)),
    trackUrl: resolveUrl(first(container.trackUrl, container.track_url, container.pageUrl, container.page_url, container.url, track.trackUrl, track.track_url, track.url)),
    service: text(first(container.service, container.source)).trim().toLowerCase(),
    streamUrl: resolveUrl(first(container.streamUrl, container.stream_url, container.audioUrl, container.audio_url, track.streamUrl, track.stream_url, track.audioUrl, track.audio_url)),
    durationMs: Math.max(0, durationMilliseconds ?? (durationSeconds !== null ? durationSeconds * 1000 : 0)) || null,
    positionMs: Math.max(0, positionMilliseconds ?? (positionSeconds !== null ? positionSeconds * 1000 : 0)),
    isPlaying: booleanValue(first(container.isPlaying, container.is_playing, container.playing, container.playbackState, container.playback_state, container.status)),
    capturedAtMs,
  };
}

function emptySource(capturedAtMs: number): SourcePlayback {
  return {
    trackId: null,
    title: '',
    artist: '',
    album: '',
    artworkUrl: null,
    trackUrl: null,
    service: '',
    streamUrl: null,
    durationMs: null,
    positionMs: 0,
    isPlaying: false,
    capturedAtMs,
  };
}

export class SourceAdapter {
  private settings: SettingsProvider;
  private current: SourcePlayback = emptySource(Date.now());

  constructor(settings: SettingsProvider) {
    this.settings = settings;
  }

  async poll(): Promise<SourcePlayback> {
    const { sourceUrl, sourceToken } = this.settings();
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (sourceToken) headers.Authorization = `Bearer ${sourceToken}`;
    const response = await fetch(sourceUrl, {
      headers,
      signal: AbortSignal.timeout(3500),
    });
    if (!response.ok) throw new Error(`API ответил HTTP ${response.status}.`);
    const raw = response.status === 204 ? null : await response.json();
    this.current = normalizeSourceResponse(raw, sourceUrl);
    return this.current;
  }

  getSnapshot(): PlaybackSnapshot {
    if (!this.current.trackId) return emptySnapshot();
    const elapsed = this.current.isPlaying ? Math.max(0, Date.now() - this.current.capturedAtMs) : 0;
    let positionMs = this.current.positionMs + elapsed;
    if (this.current.durationMs !== null) positionMs = Math.min(positionMs, this.current.durationMs);
    return {
      trackId: this.current.trackId,
      title: this.current.title,
      artist: this.current.artist,
      album: this.current.album,
      artworkUrl: this.current.artworkUrl,
      trackUrl: this.current.trackUrl,
      service: this.current.service,
      durationMs: this.current.durationMs,
      positionMs,
      isPlaying: this.current.isPlaying,
      audioAvailable: this.current.streamUrl !== null,
      serverTimeMs: Date.now(),
    };
  }

  async fetchAudio(range?: string): Promise<Response> {
    if (!this.current.streamUrl) throw new Error('Для текущего трека нет аудиопотока.');
    const { sourceToken } = this.settings();
    const headers: Record<string, string> = {};
    if (sourceToken) headers.Authorization = `Bearer ${sourceToken}`;
    if (range) headers.Range = range;
    const response = await fetch(this.current.streamUrl, { headers });
    if (!response.ok && response.status !== 206) {
      throw new Error(`Аудиопоток ответил HTTP ${response.status}.`);
    }
    return response;
  }
}
