"use client";

export function SettingsSidebar({ children }: { children: React.ReactNode }) {
  return (
    <>
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900">Settings</h1>
          <p className="text-sm text-gray-500 mt-1">Global platform configuration</p>
        </div>
      </header>
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        {children}
      </div>
    </>
  );
}
