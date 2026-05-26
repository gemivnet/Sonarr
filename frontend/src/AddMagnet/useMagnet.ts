import useApiMutation from 'Helpers/Hooks/useApiMutation';

export interface MagnetEpisode {
  episode: number;
  title: string | null;
  size: number;
  quality: string | null;
  hasFile: boolean;
}

export interface MagnetSeasonPreview {
  season: number;
  title: string;
  size: number;
  fileCount: number;
  episodeCount: number;
  existingCount: number;
  satisfied: boolean;
  quality: string | null;
  approved: boolean;
  rejections: string[];
  guid: string | null;
  episodes: MagnetEpisode[];
}

export interface MagnetGrabSkip {
  season: number;
  reason: string;
}

export interface MagnetGrabResult {
  grabbed: number[];
  skipped: MagnetGrabSkip[];
}

interface PreviewPayload {
  magnetUrl: string;
  tvdbId: number;
  includeSatisfied: boolean;
}

export interface GrabEpisodeSelection {
  season: number;
  episode: number;
}

interface GrabPayload {
  magnetUrl: string;
  tvdbId: number;
  seasons: number[];
  episodes: GrabEpisodeSelection[];
}

export const useMagnetPreview = () =>
  useApiMutation<MagnetSeasonPreview[], PreviewPayload>({
    path: '/magnet/preview',
    method: 'POST',
  });

export const useMagnetGrab = () =>
  useApiMutation<MagnetGrabResult, GrabPayload>({
    path: '/magnet/grab',
    method: 'POST',
  });
