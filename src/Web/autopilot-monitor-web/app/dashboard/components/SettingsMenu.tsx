"use client";

import { useEffect, useState, useRef } from "react";

export function SettingsMenu({
  adminMode,
  onAdminModeChange,
  globalAdminMode,
  onGlobalAdminModeChange,
  user,
}: {
  adminMode: boolean;
  onAdminModeChange: (enabled: boolean) => void;
  globalAdminMode: boolean;
  onGlobalAdminModeChange: (enabled: boolean) => void;
  user: { displayName: string; email: string; isGlobalAdmin?: boolean } | null;
}) {
  const [isOpen, setIsOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  // Close dropdown when clicking outside
  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) {
        setIsOpen(false);
      }
    }

    if (isOpen) {
      document.addEventListener("mousedown", handleClickOutside);
      return () => {
        document.removeEventListener("mousedown", handleClickOutside);
      };
    }
  }, [isOpen]);

  return (
    <div className="relative" ref={menuRef}>
      {/* Settings Button */}
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="p-2 rounded-full hover:bg-gray-100 transition-colors"
        aria-label="Settings"
        title="Settings"
      >
        <svg className="h-5 w-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
        </svg>
      </button>

      {/* Dropdown Menu */}
      {isOpen && (
        <div className="absolute right-0 mt-2 w-72 bg-white rounded-lg shadow-lg border border-gray-200 z-50">
          <div className="p-4">
            <h3 className="text-sm font-semibold text-gray-900 mb-3">Settings</h3>

            {/* Admin Mode Toggle */}
            <div className="mb-3">
              <div className="flex items-center justify-between p-3 rounded-lg bg-gray-50">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium text-gray-700">Admin Mode</span>
                  {adminMode && <span className="text-xs text-red-600 font-semibold">AKTIV</span>}
                </div>
                <button
                  onClick={() => onAdminModeChange(!adminMode)}
                  className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                    adminMode ? 'bg-red-600' : 'bg-gray-300'
                  }`}
                >
                  <span
                    className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                      adminMode ? 'translate-x-6' : 'translate-x-1'
                    }`}
                  />
                </button>
              </div>
              {adminMode && (
                <p className="mt-2 text-xs text-red-600 px-3 whitespace-nowrap">
                  ⚠️ Allows deleting sessions
                </p>
              )}
            </div>

            {/* Global Admin Toggle - Only visible to actual global admins */}
            {user?.isGlobalAdmin && (
              <div className="mb-3">
                <div className="flex items-center justify-between p-3 rounded-lg bg-purple-50">
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium text-gray-700">Global Admin</span>
                    {globalAdminMode && <span className="text-xs text-purple-700 font-semibold">ACTIVE</span>}
                  </div>
                  <button
                    onClick={() => onGlobalAdminModeChange(!globalAdminMode)}
                    className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                      globalAdminMode ? 'bg-purple-600' : 'bg-gray-300'
                    }`}
                  >
                    <span
                      className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                        globalAdminMode ? 'translate-x-6' : 'translate-x-1'
                      }`}
                    />
                  </button>
                </div>
                {globalAdminMode && (
                  <p className="mt-2 text-xs text-purple-700 px-3">
                    Shows ALL sessions across ALL tenants
                  </p>
                )}
              </div>
            )}

          </div>
        </div>
      )}
    </div>
  );
}
