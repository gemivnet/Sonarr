import React from 'react';
import Modal from 'Components/Modal/Modal';
import AddMagnetModalContent from './AddMagnetModalContent';

interface AddMagnetModalProps {
  isOpen: boolean;
  onModalClose: () => void;
}

function AddMagnetModal({ isOpen, onModalClose }: AddMagnetModalProps) {
  return (
    <Modal isOpen={isOpen} onModalClose={onModalClose}>
      <AddMagnetModalContent onModalClose={onModalClose} />
    </Modal>
  );
}

export default AddMagnetModal;
