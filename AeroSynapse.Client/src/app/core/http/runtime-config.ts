export interface RuntimeConfig {
  apiBaseUrl: string;
  intelligenceBaseUrl: string;
}

declare global {
  interface Window {
    __AEROSYNAPSE_CONFIG__?: RuntimeConfig;
  }
}

export function runtimeConfig(): RuntimeConfig {
  return (
    window.__AEROSYNAPSE_CONFIG__ ?? { apiBaseUrl: '/api', intelligenceBaseUrl: '/intelligence' }
  );
}
