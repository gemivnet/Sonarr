import useApiMutation from 'Helpers/Hooks/useApiMutation';

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

interface GrabPayload {
  magnetUrl: string;
  tvdbId: number;
  seasons: number[];
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
