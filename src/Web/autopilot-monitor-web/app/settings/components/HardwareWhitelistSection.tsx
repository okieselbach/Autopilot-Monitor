"use client";

interface HardwareWhitelistSectionProps {
  manufacturerWhitelist: string;
  setManufacturerWhitelist: (value: string) => void;
  modelWhitelist: string;
  setModelWhitelist: (value: string) => void;
}

export default function HardwareWhitelistSection({
  manufacturerWhitelist,
  setManufacturerWhitelist,
  modelWhitelist,
  setModelWhitelist,
}: HardwareWhitelistSectionProps) {
  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200">
        <h2 className="text-xl font-semibold text-gray-900">Hardware Whitelist</h2>
        <p className="text-sm text-gray-500 mt-1">Restrict which device manufacturers and models can enroll</p>
      </div>
      <div className="p-6 space-y-6">
        {/* Manufacturer Whitelist */}
        <div>
          <label className="block">
            <span className="text-gray-700 font-medium">Allowed Manufacturers</span>
            <p className="text-sm text-gray-500 mb-2">
              Comma-separated list. Supports wildcards: <code className="bg-gray-100 px-1 rounded">*</code> = all,
              <code className="bg-gray-100 px-1 rounded ml-1">Dell*</code> = starts with "Dell"
            </p>
            <input
              type="text"
              value={manufacturerWhitelist}
              onChange={(e) => setManufacturerWhitelist(e.target.value)}
              placeholder="Dell*,HP*,Lenovo*,Microsoft Corporation"
              className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
            />
          </label>
        </div>

        {/* Model Whitelist */}
        <div>
          <label className="block">
            <span className="text-gray-700 font-medium">Allowed Models</span>
            <p className="text-sm text-gray-500 mb-2">
              Comma-separated list. Use <code className="bg-gray-100 px-1 rounded">*</code> to allow all models.
              Examples: <code className="bg-gray-100 px-1 rounded">Latitude*</code>, <code className="bg-gray-100 px-1 rounded">EliteBook*</code>
            </p>
            <input
              type="text"
              value={modelWhitelist}
              onChange={(e) => setModelWhitelist(e.target.value)}
              placeholder="*"
              className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
            />
          </label>
        </div>
      </div>
    </div>
  );
}
