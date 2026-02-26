"use client";

interface UnsavedChangesModalProps {
  isOpen: boolean;
  isSaving: boolean;
  onSaveAndNavigate: () => void;
  onDiscardAndNavigate: () => void;
  onCancel: () => void;
}

export default function UnsavedChangesModal({
  isOpen,
  isSaving,
  onSaveAndNavigate,
  onDiscardAndNavigate,
  onCancel,
}: UnsavedChangesModalProps) {
  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-md w-full p-6">
        <div className="flex items-start justify-between mb-4">
          <div className="flex items-center space-x-3">
            <div className="w-10 h-10 bg-amber-100 rounded-full flex items-center justify-center flex-shrink-0">
              <svg className="w-5 h-5 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                  d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
              </svg>
            </div>
            <div>
              <h3 className="text-lg font-bold text-gray-900">Unsaved Changes</h3>
              <p className="text-sm text-gray-500 mt-0.5">
                You have unsaved changes. What would you like to do?
              </p>
            </div>
          </div>
          <button
            onClick={onCancel}
            disabled={isSaving}
            className="ml-4 text-gray-400 hover:text-gray-600 disabled:opacity-50 transition-colors"
            aria-label="Cancel"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <div className="flex flex-col space-y-2 mt-6">
          <button
            onClick={onSaveAndNavigate}
            disabled={isSaving}
            className="w-full px-4 py-2.5 bg-indigo-600 text-white rounded-md text-sm font-medium hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center justify-center space-x-2"
          >
            {isSaving ? (
              <>
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
                <span>Saving...</span>
              </>
            ) : (
              <span>Save Now</span>
            )}
          </button>

          <button
            onClick={onDiscardAndNavigate}
            disabled={isSaving}
            className="w-full px-4 py-2.5 border border-gray-300 text-gray-700 bg-white rounded-md text-sm font-medium hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            Discard Changes
          </button>

          <button
            onClick={onCancel}
            disabled={isSaving}
            className="w-full px-4 py-2.5 text-gray-500 text-sm font-medium hover:text-gray-700 disabled:opacity-50 transition-colors"
          >
            Cancel
          </button>
        </div>
      </div>
    </div>
  );
}
