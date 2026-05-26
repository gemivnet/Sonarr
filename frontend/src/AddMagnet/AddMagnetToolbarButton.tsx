import React, { useCallback, useState } from 'react';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import AddMagnetModal from './AddMagnetModal';

interface AddMagnetToolbarButtonProps {
  seriesTvdbId?: number;
}

function AddMagnetToolbarButton({ seriesTvdbId }: AddMagnetToolbarButtonProps) {
  const [isOpen, setIsOpen] = useState(false);

  const onOpenPress = useCallback(() => {
    setIsOpen(true);
  }, []);

  const onModalClose = useCallback(() => {
    setIsOpen(false);
  }, []);

  return (
    <>
      <PageToolbarButton
        label={translate('AddMagnet')}
        iconName={icons.ADD}
        onPress={onOpenPress}
      />

      <AddMagnetModal
        isOpen={isOpen}
        onModalClose={onModalClose}
        seriesTvdbId={seriesTvdbId}
      />
    </>
  );
}

export default AddMagnetToolbarButton;
