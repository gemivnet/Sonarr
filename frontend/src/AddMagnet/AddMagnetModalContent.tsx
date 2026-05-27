import React, { useCallback, useEffect, useMemo, useState } from 'react';
import Alert from 'Components/Alert';
import Icon from 'Components/Icon';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import Popover from 'Components/Tooltip/Popover';
import { icons, kinds, tooltipPositions } from 'Helpers/Props';
import useSeries from 'Series/useSeries';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import {
  GrabEpisodeSelection,
  MagnetSeasonPreview,
  useMagnetGrab,
  useMagnetPreview,
} from './useMagnet';

interface AddMagnetModalContentProps {
  onModalClose: () => void;
  seriesTvdbId?: number;
}

const labelStyle: React.CSSProperties = {
  display: 'block',
  marginBottom: '5px',
  fontWeight: 'bold',
};

const fieldStyle: React.CSSProperties = { marginBottom: '15px' };
const controlStyle: React.CSSProperties = { width: '100%', padding: '6px' };
const cellStyle: React.CSSProperties = {
  padding: '4px 8px',
  borderBottom: '1px solid rgba(128,128,128,0.2)',
};
const checkAllStyle: React.CSSProperties = {
  display: 'flex',
  gap: '8px',
  alignItems: 'center',
  flexWrap: 'wrap',
  marginTop: '15px',
};

function gb(bytes: number) {
  return `${(bytes / 1073741824).toFixed(2)} GB`;
}

function pad2(n: number) {
  return n < 10 ? `0${n}` : `${n}`;
}

function epKey(season: number, episode: number) {
  return `${season}:${episode}`;
}

function hasEpisodes(row: MagnetSeasonPreview) {
  return row.episodes.length > 0;
}

function selectRowFully(
  row: MagnetSeasonPreview,
  seasons: Set<number>,
  eps: Set<string>
) {
  if (hasEpisodes(row)) {
    row.episodes.forEach((e) => eps.add(epKey(row.season, e.episode)));
  } else {
    seasons.add(row.season);
  }
}

function AddMagnetModalContent({
  onModalClose,
  seriesTvdbId,
}: AddMagnetModalContentProps) {
  const { data: series } = useSeries();
  const sortedSeries = useMemo(
    () => [...series].sort((a, b) => a.sortTitle.localeCompare(b.sortTitle)),
    [series]
  );

  // Launched from a series page: lock to that series and hide the picker.
  const lockedSeriesTitle = seriesTvdbId
    ? series.find((s) => s.tvdbId === seriesTvdbId)?.title
    : undefined;

  const [tvdbId, setTvdbId] = useState(seriesTvdbId ?? 0);
  const [magnetUrl, setMagnetUrl] = useState('');
  const [showAll, setShowAll] = useState(false);
  const [expanded, setExpanded] = useState<Set<number>>(new Set());

  // Selection is two-tier: whole seasons (for rows with no episode breakdown,
  // or fully-ticked seasons) and individual episodes (partial seasons).
  const [selectedSeasons, setSelectedSeasons] = useState<Set<number>>(
    new Set()
  );
  const [selectedEpisodes, setSelectedEpisodes] = useState<Set<string>>(
    new Set()
  );

  const {
    mutate: preview,
    data: rows,
    isPending: isPreviewing,
    error: previewError,
    reset: resetPreview,
  } = useMagnetPreview();

  const {
    mutate: grab,
    data: grabResult,
    isPending: isGrabbing,
    error: grabError,
  } = useMagnetGrab();

  const visibleRows = useMemo(() => {
    if (!rows) {
      return [];
    }

    return showAll ? rows : rows.filter((r) => !r.satisfied);
  }, [rows, showAll]);

  const hiddenCount = (rows?.length ?? 0) - visibleRows.length;

  const seasonState = useCallback(
    (row: MagnetSeasonPreview) => {
      if (!hasEpisodes(row)) {
        return {
          checked: selectedSeasons.has(row.season),
          indeterminate: false,
        };
      }

      const total = row.episodes.length;
      const selected = row.episodes.filter((e) =>
        selectedEpisodes.has(epKey(row.season, e.episode))
      ).length;

      return {
        checked: selected > 0 && selected === total,
        indeterminate: selected > 0 && selected < total,
      };
    },
    [selectedSeasons, selectedEpisodes]
  );

  // Default-check the approved (wanted) seasons in full.
  useEffect(() => {
    if (!rows) {
      return;
    }

    const seasons = new Set<number>();
    const eps = new Set<string>();

    rows
      .filter((r) => r.approved)
      .forEach((r) => {
        if (hasEpisodes(r)) {
          // Only pre-select episodes we don't already have, so a partially-owned
          // season starts with just its missing episodes ticked (owned ones are
          // left unchecked - the user can still tick them to re-grab/upgrade).
          r.episodes
            .filter((e) => !e.hasFile)
            .forEach((e) => eps.add(epKey(r.season, e.episode)));
        } else {
          seasons.add(r.season);
        }
      });

    setSelectedSeasons(seasons);
    setSelectedEpisodes(eps);
  }, [rows]);

  const canPreview = tvdbId > 0 && magnetUrl.trim().length > 0;

  const onPreviewPress = useCallback(() => {
    if (canPreview) {
      setShowAll(false);
      setExpanded(new Set());
      preview({ magnetUrl: magnetUrl.trim(), tvdbId, includeSatisfied: true });
    }
  }, [canPreview, preview, magnetUrl, tvdbId]);

  const onMagnetChange = useCallback(
    (event: React.ChangeEvent<HTMLTextAreaElement>) => {
      setMagnetUrl(event.target.value);
      resetPreview();
    },
    [resetPreview]
  );

  const toggleExpanded = useCallback((season: number) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(season)) {
        next.delete(season);
      } else {
        next.add(season);
      }
      return next;
    });
  }, []);

  const toggleSeason = useCallback(
    (row: MagnetSeasonPreview) => {
      if (!hasEpisodes(row)) {
        setSelectedSeasons((prev) => {
          const next = new Set(prev);
          if (next.has(row.season)) {
            next.delete(row.season);
          } else {
            next.add(row.season);
          }
          return next;
        });
        return;
      }

      const { checked } = seasonState(row);
      setSelectedEpisodes((prev) => {
        const next = new Set(prev);
        row.episodes.forEach((e) => {
          const key = epKey(row.season, e.episode);
          if (checked) {
            next.delete(key);
          } else {
            next.add(key);
          }
        });
        return next;
      });
    },
    [seasonState]
  );

  const toggleEpisode = useCallback((season: number, episode: number) => {
    setSelectedEpisodes((prev) => {
      const next = new Set(prev);
      const key = epKey(season, episode);
      if (next.has(key)) {
        next.delete(key);
      } else {
        next.add(key);
      }
      return next;
    });
  }, []);

  // Check-all helpers build fresh selections from the visible rows.
  const applySelection = useCallback(
    (
      builder: (
        row: MagnetSeasonPreview,
        seasons: Set<number>,
        eps: Set<string>
      ) => void
    ) => {
      const seasons = new Set<number>();
      const eps = new Set<string>();
      visibleRows.forEach((row) => builder(row, seasons, eps));
      setSelectedSeasons(seasons);
      setSelectedEpisodes(eps);
    },
    [visibleRows]
  );

  const onCheckAll = useCallback(
    () => applySelection(selectRowFully),
    [applySelection]
  );

  const onCheckUpgradable = useCallback(
    () =>
      applySelection((row, seasons, eps) => {
        if (row.approved && row.existingCount > 0) {
          selectRowFully(row, seasons, eps);
        }
      }),
    [applySelection]
  );

  const onCheckNotInLibrary = useCallback(
    () =>
      applySelection((row, seasons, eps) => {
        if (hasEpisodes(row)) {
          row.episodes
            .filter((e) => !e.hasFile)
            .forEach((e) => eps.add(epKey(row.season, e.episode)));
        } else if (row.existingCount === 0) {
          seasons.add(row.season);
        }
      }),
    [applySelection]
  );

  const onCheckNone = useCallback(() => {
    setSelectedSeasons(new Set());
    setSelectedEpisodes(new Set());
  }, []);

  const hasSelection = selectedSeasons.size > 0 || selectedEpisodes.size > 0;

  const onGrabPress = useCallback(() => {
    const seasons: number[] = [];
    const episodes: GrabEpisodeSelection[] = [];

    (rows ?? []).forEach((row) => {
      if (!hasEpisodes(row)) {
        if (selectedSeasons.has(row.season)) {
          seasons.push(row.season);
        }
        return;
      }

      const selected = row.episodes.filter((e) =>
        selectedEpisodes.has(epKey(row.season, e.episode))
      );

      if (selected.length === 0) {
        return;
      }

      if (selected.length === row.episodes.length) {
        seasons.push(row.season);
      } else {
        selected.forEach((e) =>
          episodes.push({ season: row.season, episode: e.episode })
        );
      }
    });

    grab(
      { magnetUrl: magnetUrl.trim(), tvdbId, seasons, episodes },
      {
        onSuccess: (result) => {
          if (result.skipped.length === 0) {
            onModalClose();
          }
        },
      }
    );
  }, [
    grab,
    magnetUrl,
    tvdbId,
    rows,
    selectedSeasons,
    selectedEpisodes,
    onModalClose,
  ]);

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>{translate('AddMagnet')}</ModalHeader>

      <ModalBody>
        <div style={fieldStyle}>
          <label style={labelStyle}>{translate('Series')}</label>
          {seriesTvdbId ? (
            <div style={{ fontWeight: 'bold' }}>{lockedSeriesTitle}</div>
          ) : (
            <select
              style={controlStyle}
              value={tvdbId}
              onChange={(e) => setTvdbId(Number(e.target.value))}
            >
              <option value={0}>{translate('AddMagnetSelectSeries')}</option>
              {sortedSeries.map((s) => (
                <option key={s.id} value={s.tvdbId}>
                  {s.title}
                </option>
              ))}
            </select>
          )}
        </div>

        <div style={fieldStyle}>
          <label style={labelStyle}>{translate('AddMagnetMagnetLink')}</label>
          <textarea
            style={{
              ...controlStyle,
              minHeight: '70px',
              fontFamily: 'monospace',
            }}
            value={magnetUrl}
            placeholder="magnet:?xt=urn:btih:..."
            onChange={onMagnetChange}
          />
        </div>

        <Button
          kind={kinds.PRIMARY}
          isDisabled={!canPreview || isPreviewing}
          onPress={onPreviewPress}
        >
          {translate('AddMagnetPreview')}
        </Button>

        {isPreviewing ? <LoadingIndicator /> : null}

        {previewError ? (
          <Alert kind={kinds.DANGER}>{getErrorMessage(previewError)}</Alert>
        ) : null}

        {rows && visibleRows.length === 0 && !isPreviewing ? (
          <Alert kind={kinds.INFO}>
            {hiddenCount > 0
              ? translate('AddMagnetAllOwned', { count: hiddenCount })
              : translate('AddMagnetNoSeasons')}
          </Alert>
        ) : null}

        {visibleRows.length > 0 ? (
          <>
            <div style={checkAllStyle}>
              <span style={{ fontWeight: 'bold' }}>
                {translate('AddMagnetCheck')}:
              </span>
              <Button onPress={onCheckAll}>{translate('All')}</Button>
              <Button onPress={onCheckUpgradable}>
                {translate('AddMagnetUpgradable')}
              </Button>
              <Button onPress={onCheckNotInLibrary}>
                {translate('AddMagnetNotInLibrary')}
              </Button>
              <Button onPress={onCheckNone}>
                {translate('AddMagnetNone')}
              </Button>
            </div>

            <table
              style={{
                width: '100%',
                marginTop: '10px',
                borderCollapse: 'collapse',
              }}
            >
              <thead>
                <tr>
                  <th style={cellStyle} />
                  <th style={cellStyle} />
                  <th style={cellStyle}>{translate('Season')}</th>
                  <th style={cellStyle}>{translate('Quality')}</th>
                  <th style={cellStyle}>{translate('Size')}</th>
                  <th style={cellStyle}>{translate('AddMagnetLibrary')}</th>
                  <th style={cellStyle}>{translate('Status')}</th>
                </tr>
              </thead>
              <tbody>
                {visibleRows.map((row) => {
                  const state = seasonState(row);
                  const isExpanded = expanded.has(row.season);

                  return (
                    <React.Fragment key={row.season}>
                      <tr style={{ opacity: row.approved ? 1 : 0.7 }}>
                        <td style={cellStyle}>
                          <input
                            type="checkbox"
                            checked={state.checked}
                            ref={(el) => {
                              if (el) {
                                el.indeterminate = state.indeterminate;
                              }
                            }}
                            onChange={() => toggleSeason(row)}
                          />
                        </td>
                        <td style={cellStyle}>
                          {hasEpisodes(row) ? (
                            <Button onPress={() => toggleExpanded(row.season)}>
                              <Icon
                                name={
                                  isExpanded ? icons.COLLAPSE : icons.EXPAND
                                }
                              />
                            </Button>
                          ) : null}
                        </td>
                        <td style={cellStyle}>S{pad2(row.season)}</td>
                        <td style={cellStyle}>{row.quality ?? '—'}</td>
                        <td style={cellStyle}>{gb(row.size)}</td>
                        <td style={cellStyle}>
                          {row.existingCount}/{row.episodeCount}
                        </td>
                        <td style={cellStyle}>
                          {row.approved ? (
                            <Icon
                              name={icons.CHECK}
                              kind={kinds.SUCCESS}
                              title={translate('AddMagnetWanted')}
                            />
                          ) : row.rejections.length > 0 ? (
                            <Popover
                              anchor={
                                <Icon name={icons.DANGER} kind={kinds.DANGER} />
                              }
                              title={translate('ReleaseRejected')}
                              body={
                                <ul>
                                  {row.rejections.map((rejection, index) => (
                                    <li key={index}>{rejection}</li>
                                  ))}
                                </ul>
                              }
                              position={tooltipPositions.LEFT}
                            />
                          ) : null}
                        </td>
                      </tr>

                      {isExpanded
                        ? row.episodes.map((ep) => (
                            <tr
                              key={`${row.season}-${ep.episode}`}
                              style={{ opacity: ep.hasFile ? 0.6 : 1 }}
                            >
                              <td style={cellStyle}>
                                <input
                                  type="checkbox"
                                  checked={selectedEpisodes.has(
                                    epKey(row.season, ep.episode)
                                  )}
                                  onChange={() =>
                                    toggleEpisode(row.season, ep.episode)
                                  }
                                />
                              </td>
                              <td style={cellStyle} />
                              <td style={{ ...cellStyle, paddingLeft: '24px' }}>
                                S{pad2(row.season)}E{pad2(ep.episode)}
                                {ep.title ? ` · ${ep.title}` : ''}
                              </td>
                              <td style={cellStyle}>{ep.quality ?? '—'}</td>
                              <td style={cellStyle}>{gb(ep.size)}</td>
                              <td style={cellStyle}>
                                {ep.hasFile
                                  ? translate('AddMagnetInLibrary')
                                  : translate('AddMagnetMissing')}
                              </td>
                              <td style={cellStyle} />
                            </tr>
                          ))
                        : null}
                    </React.Fragment>
                  );
                })}
              </tbody>
            </table>
          </>
        ) : null}

        {hiddenCount > 0 || (showAll && rows && rows.length > 0) ? (
          <label
            style={{
              display: 'block',
              marginTop: '10px',
              cursor: 'pointer',
            }}
          >
            <input
              type="checkbox"
              checked={showAll}
              onChange={(e) => setShowAll(e.target.checked)}
            />{' '}
            {translate('AddMagnetShowOwned', { count: hiddenCount })}
          </label>
        ) : null}

        {grabError ? (
          <Alert kind={kinds.DANGER}>{getErrorMessage(grabError)}</Alert>
        ) : null}

        {grabResult && grabResult.skipped.length > 0 ? (
          <Alert kind={kinds.WARNING}>
            {translate('AddMagnetGrabbedSeasons', {
              grabbed: grabResult.grabbed.length,
            })}
            {` — ${grabResult.skipped
              .map((s) => `S${s.season}: ${s.reason}`)
              .join('; ')}`}
          </Alert>
        ) : null}
      </ModalBody>

      <ModalFooter>
        <Button onPress={onModalClose}>{translate('Close')}</Button>

        <SpinnerButton
          kind={kinds.PRIMARY}
          isSpinning={isGrabbing}
          isDisabled={!rows || !hasSelection || isGrabbing}
          onPress={onGrabPress}
        >
          {translate('AddMagnetGrabSelected')}
        </SpinnerButton>
      </ModalFooter>
    </ModalContent>
  );
}

export default AddMagnetModalContent;
