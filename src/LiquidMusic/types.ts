export interface PlaybackSnapshot {
  trackId: string | null;
  title: string;
  artist: string;
  album: string;
  artworkUrl: string | null;
  trackUrl: string | null;
  service: string;
  durationMs: number | null;
  positionMs: number;
  isPlaying: boolean;
  audioAvailable: boolean;
  serverTimeMs: number;
}

export interface SourcePlayback {
  trackId: string | null;
  title: string;
  artist: string;
  album: string;
  artworkUrl: string | null;
  trackUrl: string | null;
  service: string;
  streamUrl: string | null;
  durationMs: number | null;
  positionMs: number;
  isPlaying: boolean;
  capturedAtMs: number;
}

export interface CompanionSettings {
  sourceUrl: string;
  sourceToken: string;
}

export interface RendererState {
  addresses: string[];
  pairingKey: string;
  port: number;
  clientCount: number;
  sourceConnected: boolean;
  sourceMessage: string;
  snapshot: PlaybackSnapshot;
}

export function emptySnapshot(): PlaybackSnapshot {
  return {
    trackId: null,
    title: '',
    artist: '',
    album: '',
    artworkUrl: null,
    trackUrl: null,
    service: '',
    durationMs: null,
    positionMs: 0,
    isPlaying: false,
    audioAvailable: false,
    serverTimeMs: Date.now(),
  };
}
