"use client";

interface DeleteConfirmModalProps {
  sessionToDelete: { sessionId: string; tenantId: string; deviceName?: string };
  onConfirm: () => void;
  onCancel: () => void;
}

export function DeleteConfirmModal({ sessionToDelete, onConfirm, onCancel }: DeleteConfirmModalProps) {
  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={onCancel}>
      <div className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4" onClick={(e) => e.stopPropagation()}>
        <div className="p-6">
          <div className="flex items-center mb-4">
            <div className="flex-shrink-0 w-12 h-12 bg-red-100 rounded-full flex items-center justify-center">
              <svg className="w-6 h-6 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
              </svg>
            </div>
            <h3 className="ml-4 text-lg font-semibold text-gray-900">Delete Session</h3>
          </div>

          <div className="mb-6">
            <p className="text-sm text-gray-700 mb-2">
              This action is <span className="font-semibold text-red-600">irreversible</span>!
            </p>
            <p className="text-sm text-gray-700 mb-2">
              The session <span className="font-mono text-xs">{sessionToDelete.sessionId}</span> for device <span className="font-semibold">{sessionToDelete.deviceName || 'Unknown'}</span> and all associated events will be permanently deleted.
            </p>
            <p className="text-sm text-gray-600">
              Do you want to continue?
            </p>
          </div>

          <div className="flex justify-end gap-3">
            <button
              onClick={onCancel}
              className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={onConfirm}
              className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 transition-colors"
            >
              Delete
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

interface BlockConfirmModalProps {
  sessionToBlock: { serialNumber: string; tenantId: string; deviceName?: string };
  blockingDevice: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export function BlockConfirmModal({ sessionToBlock, blockingDevice, onConfirm, onCancel }: BlockConfirmModalProps) {
  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={onCancel}>
      <div className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4" onClick={(e) => e.stopPropagation()}>
        <div className="p-6">
          <div className="flex items-center mb-4">
            <div className="flex-shrink-0 w-12 h-12 bg-orange-100 rounded-full flex items-center justify-center">
              <svg className="w-6 h-6 text-orange-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
              </svg>
            </div>
            <h3 className="ml-4 text-lg font-semibold text-gray-900">Block Device</h3>
          </div>

          <div className="mb-6">
            <p className="text-sm text-gray-700 mb-2">
              Device <span className="font-semibold">{sessionToBlock.deviceName || sessionToBlock.serialNumber}</span> (Serial: <span className="font-mono text-xs">{sessionToBlock.serialNumber}</span>) will be blocked for <span className="font-semibold">24 hours</span>.
            </p>
            <p className="text-sm text-gray-600">
              The agent will stop uploading data while blocked. Do you want to continue?
            </p>
          </div>

          <div className="flex justify-end gap-3">
            <button
              onClick={onCancel}
              className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={onConfirm}
              disabled={blockingDevice}
              className="px-4 py-2 bg-orange-600 text-white rounded-md hover:bg-orange-700 transition-colors disabled:opacity-50"
            >
              {blockingDevice ? 'Blocking...' : 'Block'}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
