"use client";

import React from "react";

interface FormJsonToggleProps {
  jsonMode: boolean;
  onToggleMode: (jsonMode: boolean) => void;
  jsonText: string;
  onJsonTextChange: (text: string) => void;
  jsonError: string | null;
  onApplyJson: () => void;
  children: React.ReactNode;
  textareaRows?: number;
  description?: string;
}

export function FormJsonToggle({
  jsonMode,
  onToggleMode,
  jsonText,
  onJsonTextChange,
  jsonError,
  onApplyJson,
  children,
  textareaRows = 20,
  description,
}: FormJsonToggleProps) {
  return (
    <>
      {jsonMode ? (
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            {description && (
              <p className="text-sm text-gray-600" dangerouslySetInnerHTML={{ __html: description }} />
            )}
            <button
              type="button"
              onClick={onApplyJson}
              className="text-xs text-indigo-600 hover:text-indigo-800 font-medium whitespace-nowrap ml-4"
            >
              Apply JSON &rarr;
            </button>
          </div>
          {jsonError && (
            <p className="text-xs text-red-600 bg-red-50 border border-red-200 rounded px-3 py-2">{jsonError}</p>
          )}
          <textarea
            value={jsonText}
            onChange={(e) => onJsonTextChange(e.target.value)}
            rows={textareaRows}
            spellCheck={false}
            className="w-full px-4 py-3 border border-gray-300 rounded-lg text-sm font-mono text-gray-900 bg-gray-50 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 resize-y"
          />
        </div>
      ) : (
        children
      )}
    </>
  );
}

/** The toggle buttons for switching between Form and JSON mode. */
export function JsonModeToggleButtons({
  jsonMode,
  onToggleMode,
}: {
  jsonMode: boolean;
  onToggleMode: (jsonMode: boolean) => void;
}) {
  return (
    <div className="flex items-center bg-gray-100 rounded-lg p-1">
      <button
        onClick={() => onToggleMode(false)}
        className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${
          !jsonMode ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"
        }`}
      >
        Form
      </button>
      <button
        onClick={() => onToggleMode(true)}
        className={`px-3 py-1.5 text-xs font-medium rounded-md transition-colors ${
          jsonMode ? "bg-white text-gray-900 shadow-sm" : "text-gray-500 hover:text-gray-700"
        }`}
      >
        JSON
      </button>
    </div>
  );
}
