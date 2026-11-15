#!/bin/sh
set -eu

if [ -z "${TELEGRAM_API_ID:-}" ] || [ -z "${TELEGRAM_API_HASH:-}" ]; then
    printf '%s\n' 'Local Bot API requires TELEGRAM_API_ID and TELEGRAM_API_HASH in the runtime environment.' >&2
    exit 64
fi

case "$TELEGRAM_API_ID" in
    *[!0-9]*)
        printf '%s\n' 'TELEGRAM_API_ID must contain only decimal digits.' >&2
        exit 64
        ;;
esac

exec /usr/local/bin/telegram-bot-api \
    --local \
    --http-port=8081 \
    --dir=/var/lib/telegram-bot-api/data \
    --temp-dir=/var/lib/telegram-bot-api/temp \
    --verbosity=0
