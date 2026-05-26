import React from 'react';
import Modal from 'Components/Modal/Modal';
import AddMagnetModalContent from './AddMagnetModalContent';

interface AddMagnetModalProps {
  isOpen: boolean;
  onModalClose: () => void;
  seriesTvdbId?: number;
}

function AddMagnetModal({
  isOpen,
  onModalClose,
  seriesTvdbId,
}: AddMagnetModalProps) {
  return (
    <Modal isOpen={isOpen} onModalClose={onModalClose}>
      <AddMagnetModalContent
        onModalClose={onModalClose}
        seriesTvdbId={seriesTvdbId}
      />
    </Modal>
  );
}

export default AddMagnetModal;
