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
  MagnetSeasonPreview,
  useMagnetGrab,
  useMagnetPreview,
} from './useMagnet';

interface AddMagnetModalContentProps {
  onModalClose: () => void;
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

function AddMagnetModalContent({ onModalClose }: AddMagnetModalContentProps) {
  const { data: series } = useSeries();
  const sortedSeries = useMemo(
    () => [...series].sort((a, b) => a.sortTitle.localeCompare(b.sortTitle)),
    [series]
  );

  const [tvdbId, setTvdbId] = useState(0);
  const [magnetUrl, setMagnetUrl] = useState('');
  const [checked, setChecked] = useState<Set<number>>(new Set());
  const [showAll, setShowAll] = useState(false);

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

  // We always fetch everything (incl. already-owned seasons, flagged
  // `satisfied`) so the "show all" toggle is instant client-side - no second RD
  // probe. Default view hides what we already have at >= this quality.
  const visibleRows = useMemo(() => {
    if (!rows) {
      return [];
    }

    return showAll ? rows : rows.filter((r) => !r.satisfied);
  }, [rows, showAll]);

  const hiddenCount = (rows?.length ?? 0) - visibleRows.length;

  // Default-check the seasons the decision engine approved (matches interactive
  // search: already-have / not-an-upgrade come back unchecked but overridable).
  // Approved rows are never `satisfied`, so they're always visible.
  useEffect(() => {
    if (rows) {
      setChecked(new Set(rows.filter((r) => r.approved).map((r) => r.season)));
    }
  }, [rows]);

  const canPreview = tvdbId > 0 && magnetUrl.trim().length > 0;

  const onPreviewPress = useCallback(() => {
    if (canPreview) {
      setShowAll(false);
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

  const toggle = useCallback((season: number) => {
    setChecked((prev) => {
      const next = new Set(prev);
      if (next.has(season)) {
        next.delete(season);
      } else {
        next.add(season);
      }
      return next;
    });
  }, []);

  // Check-all helpers operate on the currently-visible rows only.
  const checkMatching = useCallback(
    (predicate: (row: MagnetSeasonPreview) => boolean) => {
      setChecked(new Set(visibleRows.filter(predicate).map((r) => r.season)));
    },
    [visibleRows]
  );

  const onGrabPress = useCallback(() => {
    grab(
      { magnetUrl: magnetUrl.trim(), tvdbId, seasons: [...checked] },
      {
        onSuccess: (result) => {
          // Clean finish: close on a full grab. Keep the modal open only when
          // something got skipped (e.g. over its size limit) so the user sees why.
          if (result.skipped.length === 0) {
            onModalClose();
          }
        },
      }
    );
  }, [grab, magnetUrl, tvdbId, checked, onModalClose]);

  return (
    <ModalContent onModalClose={onModalClose}>
      <ModalHeader>{translate('AddMagnet')}</ModalHeader>

      <ModalBody>
        <div style={fieldStyle}>
          <label style={labelStyle}>{translate('Series')}</label>
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
              <Button onPress={() => checkMatching(() => true)}>
                {translate('All')}
              </Button>
              <Button
                onPress={() =>
                  checkMatching((r) => r.approved && r.existingCount > 0)
                }
              >
                {translate('AddMagnetUpgradable')}
              </Button>
              <Button
                onPress={() => checkMatching((r) => r.existingCount === 0)}
              >
                {translate('AddMagnetNotInLibrary')}
              </Button>
              <Button onPress={() => setChecked(new Set())}>
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
                  <th style={cellStyle}>{translate('Season')}</th>
                  <th style={cellStyle}>{translate('Quality')}</th>
                  <th style={cellStyle}>{translate('Size')}</th>
                  <th style={cellStyle}>{translate('AddMagnetLibrary')}</th>
                  <th style={cellStyle}>{translate('Status')}</th>
                </tr>
              </thead>
              <tbody>
                {visibleRows.map((row) => (
                  <tr
                    key={row.season}
                    style={{ opacity: row.approved ? 1 : 0.7 }}
                  >
                    <td style={cellStyle}>
                      <input
                        type="checkbox"
                        checked={checked.has(row.season)}
                        onChange={() => toggle(row.season)}
                      />
                    </td>
                    <td style={cellStyle}>
                      S{row.season < 10 ? `0${row.season}` : row.season}
                    </td>
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
                ))}
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
          isDisabled={!rows || checked.size === 0 || isGrabbing}
          onPress={onGrabPress}
        >
          {translate('AddMagnetGrabSelected')}
        </SpinnerButton>
      </ModalFooter>
    </ModalContent>
  );
}

export default AddMagnetModalContent;
