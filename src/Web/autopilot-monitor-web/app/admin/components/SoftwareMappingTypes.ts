export interface UnmatchedSoftwareEntry {
  softwareName: string;
  publisher: string;
  frequency: number;
  lastSeenAt: string;
  exampleSessionId: string;
  normalizedVendor?: string;
  normalizedVersion?: string;
}

export interface AutoResolveResult {
  resolved: Array<{ softwareName: string; cpeUri: string; confidence: number }>;
  failed: Array<{ softwareName: string; reason: string }>;
  totalProcessed: number;
  totalResolved: number;
  totalFailed: number;
}

export interface CpeMappingEntry {
  normalizedVendor: string;
  normalizedProduct: string;
  cpeVendor: string;
  cpeProduct: string;
  cpeUri: string;
  category: string;
  displayNamePatterns: string[];
  publisherPatterns: string[];
  excludePatterns: string[];
  source: string;
  createdAt: string;
}

export interface IgnoredSoftwareEntry {
  softwareName: string;
  publisher: string;
  reason: string;
  ignoredAt: string;
}

export interface TabProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
}
