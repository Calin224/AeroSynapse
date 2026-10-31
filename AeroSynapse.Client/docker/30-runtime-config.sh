#!/bin/sh
set -eu
config=$(jq -n --arg api "${FRONTEND_API_BASE_URL:-/api}" --arg intelligence "${FRONTEND_INTELLIGENCE_BASE_URL:-/intelligence}" '{apiBaseUrl: $api, intelligenceBaseUrl: $intelligence}')
printf 'window.__AEROSYNAPSE_CONFIG__ = %s;\n' "$config" > /usr/share/nginx/html/runtime-config.js
printf '%s\n' 'Public frontend runtime configuration generated.'
