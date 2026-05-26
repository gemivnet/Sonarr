import React, { useCallback, useState } from 'react';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import { icons } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import AddMagnetModal from './AddMagnetModal';

function AddMagnetToolbarButton() {
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

      <AddMagnetModal isOpen={isOpen} onModalClose={onModalClose} />
    </>
  );
}

export default AddMagnetToolbarButton;
