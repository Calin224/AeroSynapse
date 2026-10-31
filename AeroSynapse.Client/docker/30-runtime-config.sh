#!/bin/sh
set -eu
config=$(jq -n --arg api "${FRONTEND_API_BASE_URL:-/api}" --arg intelligence "${FRONTEND_INTELLIGENCE_BASE_URL:-/intelligence}" --arg baggage "${BAGGAGE_TWIN_ENABLED:-false}" --arg airside "${AIRSIDE_TWIN_ENABLED:-false}" --arg energy "${ENERGY_ANALYTICS_ENABLED:-false}" '{apiBaseUrl: $api, intelligenceBaseUrl: $intelligence, baggageTwinEnabled: ($baggage == "true"), airsideTwinEnabled: ($airside == "true"), energyAnalyticsEnabled: ($energy == "true")}')
printf 'window.__AEROSYNAPSE_CONFIG__ = %s;\n' "$config" > /usr/share/nginx/html/runtime-config.js
if [ -f /usr/share/nginx/html/ngsw.json ]; then
    runtime_hash=$(sha1sum /usr/share/nginx/html/runtime-config.js | cut -d ' ' -f 1)
    jq --arg hash "$runtime_hash" '.hashTable["/runtime-config.js"] = $hash' /usr/share/nginx/html/ngsw.json > /usr/share/nginx/html/ngsw.json.tmp
    mv /usr/share/nginx/html/ngsw.json.tmp /usr/share/nginx/html/ngsw.json
fi
printf '%s\n' 'Public frontend runtime configuration generated.'
